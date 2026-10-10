using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Testing;
using Npgsql;

namespace Builder.Tests;

/// <summary>Azure DevOps-shaped service hook calls against a local repository: push, pull request and schedules.</summary>
[Collection(AspireCollection.Name)]
public sealed class TriggerEndToEndTests(AspireFixture aspire) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "builder-triggers-" + Guid.NewGuid().ToString("N")[..8]);

    private const string Runner = """
        version: '3'
        x-builder:
          triggers:
            push:
              branches: [main, release/*]
              paths: [src/**]
            pull-request:
              branches: [main]
            schedule:
              - cron: "0 3 * * *"
                branch: main
                vars: { MODE: nightly }
        tasks:
          default:
            cmds:
              - echo "built $(git rev-parse --short HEAD 2>/dev/null || echo ?) MODE={{.MODE}}"
        """;

    [Fact]
    public async Task Pushes_pull_requests_and_schedules_start_builds()
    {
        var repo = E2E.CreateRepository(_root, new Dictionary<string, string> { [".builder/runners/ci.yml"] = Runner, ["src/app.txt"] = "v1" });
        using var http = await E2E.LoginAsync(aspire);
        var runner = await E2E.MapRunnerAsync(http, repo, ".builder/runners/ci.yml");
        var repoId = (await E2E.GetAsync(http, "/api/repositories")).AsArray().Single()!["id"]!.GetValue<string>();

        // mapping read the triggers from the default branch, including the schedule
        var triggers = await E2E.GetAsync(http, $"/api/pipelines/{runner}/triggers");
        Assert.Equal("0 3 * * *", triggers["schedules"]![0]!["cron"]!.GetValue<string>());
        Assert.NotNull(triggers["schedules"]![0]!["nextRunAt"]);

        var hook = await E2E.PostAsync(http, $"/api/repositories/{repoId}/hook", new { });
        var secret = hook["secret"]!.GetValue<string>();
        Assert.EndsWith($"/hooks/azure-devops/{repoId}", hook["url"]!.GetValue<string>());
        using var azure = new HttpClient { BaseAddress = aspire.App.GetEndpoint("bff", "http") };  // the public endpoint
        azure.DefaultRequestHeaders.Add("X-Builder-Hook", secret);

        // push that changes src/ on main → build
        var (before, after) = E2E.Push(_root, "main", new Dictionary<string, string> { ["src/app.txt"] = "v2" });
        var started = await HookAsync(azure, repoId, Push("main", before, after));
        var push = await WaitForBuildAsync(http, started.Single());
        Assert.Equal(("Push", "Succeeded", after), (push["reason"]!.GetValue<string>(), push["status"]!.GetValue<string>(), push["commit"]!.GetValue<string>()));

        // the same push again (redelivery) → nothing new
        Assert.Empty(await HookAsync(azure, repoId, Push("main", before, after)));
        // a push that only touches docs → filtered out by paths
        var (b2, a2) = E2E.Push(_root, "main", new Dictionary<string, string> { ["docs/readme.md"] = "x" });
        Assert.Empty(await HookAsync(azure, repoId, Push("main", b2, a2)));
        // a branch that isn't listed → nothing
        var (b3, a3) = E2E.Push(_root, "feature/x", new Dictionary<string, string> { ["src/app.txt"] = "feature" });
        Assert.Empty(await HookAsync(azure, repoId, Push("feature/x", b3, a3)));

        // pull request feature/x → main: builds the merge commit, which only exists at refs/pull/7/merge
        var (_, merge) = E2E.Push(_root, "pr-merge", new Dictionary<string, string> { ["src/merged.txt"] = "merge of feature/x" }, "refs/pull/7/merge");
        var prStarted = await HookAsync(azure, repoId, PullRequest(7, "feature/x", "main", a3, merge));
        var pr = await WaitForBuildAsync(http, prStarted.Single());
        Assert.Equal(("PullRequest", "Succeeded", merge, 7), (pr["reason"]!.GetValue<string>(), pr["status"]!.GetValue<string>(),
            pr["commit"]!.GetValue<string>(), pr["pullRequestId"]!.GetValue<int>()));
        // PR updated without new commits (e.g. a vote) → no new build
        Assert.Empty(await HookAsync(azure, repoId, PullRequest(7, "feature/x", "main", a3, merge)));

        // wrong secret → 401
        using var intruder = new HttpClient { BaseAddress = aspire.App.GetEndpoint("bff", "http") };
        intruder.DefaultRequestHeaders.Add("X-Builder-Hook", "bhk_wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await intruder.PostAsJsonAsync($"/hooks/azure-devops/{repoId}", Push("main", before, after))).StatusCode);

        // the schedule comes due → a scheduled build with the schedule's vars
        await using (var db = new NpgsqlConnection(aspire.ConnectionString))
        {
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand("UPDATE runner_schedules SET next_run_at = now() - interval '1 minute' WHERE pipeline_id = @id", db);
            cmd.Parameters.AddWithValue("id", Guid.Parse(runner));
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }
        JsonNode? scheduled = null;
        await E2E.WaitUntilAsync(async () =>
        {
            scheduled = (await E2E.GetAsync(http, $"/api/builds?pipelineId={runner}")).AsArray().FirstOrDefault(b => b!["reason"]!.GetValue<string>() == "Schedule");
            return scheduled is not null;
        }, "the scheduled build", 60);
        var done = await WaitForBuildAsync(http, scheduled!["id"]!.GetValue<string>());
        Assert.Equal("Succeeded", done["status"]!.GetValue<string>());
        var logs = await http.GetStringAsync($"/api/builds/{done["id"]}/jobs/{done["jobs"]![0]!["id"]}/logs");
        Assert.Contains("MODE=nightly", logs);
    }

    private static object Push(string branch, string before, string after) => new
    {
        eventType = "git.push",
        resource = new
        {
            refUpdates = new[] { new { name = $"refs/heads/{branch}", oldObjectId = before, newObjectId = after } },
            pushedBy = new { displayName = "Dev Example" },
        },
    };

    private static object PullRequest(int id, string source, string target, string sourceCommit, string mergeCommit) => new
    {
        eventType = "git.pullrequest.updated",
        resource = new
        {
            pullRequestId = id,
            status = "active",
            sourceRefName = $"refs/heads/{source}",
            targetRefName = $"refs/heads/{target}",
            lastMergeSourceCommit = new { commitId = sourceCommit },
            lastMergeCommit = new { commitId = mergeCommit },
            createdBy = new { displayName = "Dev Example" },
        },
    };

    private static async Task<List<string>> HookAsync(HttpClient azure, string repoId, object payload)
    {
        var response = await azure.PostAsJsonAsync($"/hooks/azure-devops/{repoId}", payload);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!["builds"]!.AsArray().Select(b => b!.GetValue<string>()).ToList();
    }

    private static async Task<JsonNode> WaitForBuildAsync(HttpClient http, string id)
    {
        JsonNode build = null!;
        await E2E.WaitUntilAsync(async () =>
        {
            build = await E2E.GetAsync(http, $"/api/builds/{id}");
            return build["status"]!.GetValue<string>() is "Succeeded" or "Failed" or "Canceled";
        }, $"build {id}", 120);
        return build;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }
}
