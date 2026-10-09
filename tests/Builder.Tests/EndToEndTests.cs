using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Testing;
using Npgsql;

namespace Builder.Tests;

/// <summary>
/// Drives the real stack the way the UI does: browser → BFF (cookie, CSRF header, X-Org) → API → two agents running
/// go-task. Needs `git` and `task` on PATH (the agents run as local processes under Aspire).
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class EndToEndTests(AspireFixture aspire) : IDisposable
{
    private readonly string _repoDir = Path.Combine(Path.GetTempPath(), "builder-e2e-" + Guid.NewGuid().ToString("N")[..8]);

    private const string CiRunner = """
        version: '3'
        x-builder:
          entry: ci
        tasks:
          ci:
            deps: [approve]
          restore:
            cmds:
              - echo restore
          build-a:
            deps: [restore]
            x-artifacts: [out/a/**]
            cmds:
              - mkdir -p out/a
              - echo "made by a {{.BUILDER_BUILD_NUMBER}}" > out/a/result.txt
              - sleep 2
          build-b:
            deps: [restore]
            cmds:
              - sleep 2
              - echo b
          test:
            deps: [build-a, build-b]
            cmds:
              - cat out/a/result.txt
              - echo "GREETING={{.GREETING}}"
          approve:
            deps: [test]
            x-approval:
              message: Ship it?
        """;

    [Fact]
    public async Task Runs_a_runner_across_agents_through_an_approval_gate()
    {
        var repo = CreateRepository(new() { [".builder/runners/ci.yml"] = CiRunner });
        using var http = await LoginAsync();

        await WaitUntilAsync(async () =>
            (await http.GetFromJsonAsync<JsonArray>("/api/agents"))!.Count(a => a!["online"]!.GetValue<bool>()) >= 2,
            "two agents online");

        var runner = await E2E.MapRunnerAsync(http, repo, ".builder/runners/ci.yml");
        var build = await PostAsync(http, $"/api/pipelines/{runner}/builds",
            new { variables = new Dictionary<string, string> { ["GREETING"] = "hello" } });
        var buildUrl = $"/api/builds/{build["id"]}";

        JsonNode detail = null!;
        await WaitUntilAsync(async () =>
        {
            detail = (await http.GetFromJsonAsync<JsonNode>(buildUrl))!;
            Assert.NotEqual("Failed", detail["status"]!.GetValue<string>());
            return StatusOf(detail, "approve") == "WaitingApproval";
        }, "the approval gate");

        Assert.Equal(["Succeeded"], new[] { "restore", "build-a", "build-b", "test" }
            .Select(k => Job(detail, k)["status"]!.GetValue<string>()).Distinct());
        Assert.Single(detail["artifacts"]!.AsArray());

        var testJob = Job(detail, "test");
        var logs = await http.GetFromJsonAsync<JsonArray>($"{buildUrl}/jobs/{testJob["id"]}/logs");
        var text = string.Join("\n", logs!.Select(l => l!["text"]!.GetValue<string>()));
        Assert.Contains("made by a", text);
        Assert.Contains("GREETING=hello", text);

        await PostAsync(http, $"{buildUrl}/jobs/{Job(detail, "approve")["id"]}/approval", new { approved = true, comment = "ok" });
        await WaitUntilAsync(async () =>
            (await http.GetFromJsonAsync<JsonNode>(buildUrl))!["status"]!.GetValue<string>() == "Succeeded", "build success");
    }

    [Fact]
    public async Task Lists_runner_files_per_branch_maps_the_picked_ones_and_runs_on_another_branch()
    {
        const string Hello = """
            version: '3'
            tasks:
              default:
                cmds:
                  - echo "hello from $(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo detached) at $(basename $PWD)"
                  - test -f README.md   # runners run from the repository root
            """;
        var repo = CreateRepository(
            new() { ["README.md"] = "# demo", [".builder/runners/hello.yml"] = Hello, [".builder/runners/broken.yml"] = "tasks: [" },
            new() { ["feature"] = new Dictionary<string, string> { [".builder/runners/extra.yaml"] = Hello, ["FEATURE.md"] = "x" } });
        using var http = await LoginAsync();
        var repository = await PostAsync(http, "/api/repositories", new { url = repo, defaultBranch = "main" });
        var id = repository["id"]!.GetValue<string>();

        Assert.Equal(["main", "feature"], (await E2E.GetAsync(http, $"/api/repositories/{id}/branches")).AsArray().Select(b => b!.GetValue<string>()));

        var main = await E2E.GetAsync(http, $"/api/repositories/{id}/runner-files");
        Assert.Equal([".builder/runners/broken.yml", ".builder/runners/hello.yml"], Paths(main));
        Assert.NotNull(main["files"]![0]!["error"]);                      // broken file is listed with its error
        Assert.Equal("default", main["files"]![1]!["entryTask"]!.GetValue<string>());

        var feature = await E2E.GetAsync(http, $"/api/repositories/{id}/runner-files?branch=feature");
        Assert.Contains(".builder/runners/extra.yaml", Paths(feature));

        // map only the picked file; mapping twice is a no-op
        await PostAsync(http, $"/api/repositories/{id}/runners", new { runners = new[] { new { path = ".builder/runners/hello.yml" } } });
        var again = await PostAsync(http, $"/api/repositories/{id}/runners", new { runners = new[] { new { path = ".builder/runners/hello.yml" } } });
        Assert.Empty(again.AsArray());
        var runners = (await E2E.GetAsync(http, "/api/pipelines")).AsArray();
        var hello = Assert.Single(runners)!;
        Assert.Equal(("hello", ".builder/runners/hello.yml"), (hello["name"]!.GetValue<string>(), hello["taskfilePath"]!.GetValue<string>()));
        Assert.Equal(hello["id"]!.GetValue<string>(), (await E2E.GetAsync(http, $"/api/repositories/{id}/runner-files"))["files"]![1]!["mappedRunnerId"]!.GetValue<string>());

        // run the same runner on the feature branch
        var build = await E2E.RunBuildAsync(http, hello["id"]!.GetValue<string>(), new { branch = "feature" });
        Assert.Equal(("Succeeded", "feature"), (build["status"]!.GetValue<string>(), build["branch"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Organizations_are_isolated()
    {
        var repo = CreateRepository(new() { [".builder/runners/ci.yml"] = "version: '3'\ntasks:\n  default:\n    cmds:\n      - echo hi\n" });
        using var orgA = await LoginAsync();
        var runner = await E2E.MapRunnerAsync(orgA, repo, ".builder/runners/ci.yml");
        var build = await E2E.RunBuildAsync(orgA, runner);

        using var orgB = await LoginAsync(); // same person, another organization
        Assert.Empty((await E2E.GetAsync(orgB, "/api/pipelines")).AsArray());
        Assert.Empty((await E2E.GetAsync(orgB, "/api/builds")).AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await orgB.GetAsync($"/api/pipelines/{runner}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await orgB.GetAsync($"/api/builds/{build["id"]}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await orgB.PostAsJsonAsync($"/api/pipelines/{runner}/builds", new { })).StatusCode);
        var job = build["jobs"]![0]!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NotFound, (await orgB.GetAsync($"/api/builds/{build["id"]}/jobs/{job}/logs")).StatusCode);

        // an organization the user does not belong to is invisible
        var foreign = Guid.CreateVersion7();
        await using (var db = new NpgsqlConnection(aspire.ConnectionString))
        {
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name, slug, created_by, created_at) VALUES (@id, 'Foreign', @slug, 'someone', now())", db);
            cmd.Parameters.AddWithValue("id", foreign);
            cmd.Parameters.AddWithValue("slug", "foreign-" + foreign.ToString("N")[..8]);
            await cmd.ExecuteNonQueryAsync();
        }
        using var stranger = await LoginAsync(withOrganization: false);
        stranger.DefaultRequestHeaders.Add("X-Org", foreign.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync("/api/pipelines")).StatusCode);
        stranger.DefaultRequestHeaders.Remove("X-Org");
        Assert.Equal(HttpStatusCode.BadRequest, (await stranger.GetAsync("/api/pipelines")).StatusCode);
        var me = await E2E.GetAsync(stranger, "/api/me");
        Assert.DoesNotContain(me["orgs"]!.AsArray(), o => o!["id"]!.GetValue<string>() == foreign.ToString());
    }

    [Fact]
    public async Task Members_can_be_invited_by_email_and_roles_are_enforced()
    {
        using var http = await LoginAsync();
        var email = $"dev-{Guid.NewGuid():N}@example.com";
        var member = await PostAsync(http, "/api/org/members", new { email, role = "Member" });
        Assert.True(member["canSignInWithGoogle"]!.GetValue<bool>());
        Assert.Equal(2, (await E2E.GetAsync(http, "/api/org/members")).AsArray().Count);

        // the last owner cannot be removed or demoted
        var me = (await E2E.GetAsync(http, "/api/org/members")).AsArray().Single(m => m!["role"]!.GetValue<string>() == "Owner")!;
        var demote = await http.PutAsJsonAsync($"/api/org/members/{me["userId"]}", new { role = "Member" });
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);

        var token = await PostAsync(http, "/api/org/agent-token", new { });
        Assert.StartsWith("bldr_", token["agentToken"]!.GetValue<string>());
    }

    [Fact]
    public async Task Cancel_stops_a_running_build()
    {
        var repo = CreateRepository(new() { [".builder/runners/slow.yml"] = "version: '3'\ntasks:\n  default:\n    cmds:\n      - sleep 120\n" });
        using var http = await LoginAsync();
        var runner = await E2E.MapRunnerAsync(http, repo, ".builder/runners/slow.yml");
        var build = await PostAsync(http, $"/api/pipelines/{runner}/builds", new { });
        var buildUrl = $"/api/builds/{build["id"]}";

        await WaitUntilAsync(async () => StatusOf((await http.GetFromJsonAsync<JsonNode>(buildUrl))!, "default") == "Running",
            "the job to start");
        await PostAsync(http, $"{buildUrl}/cancel", new { });
        await WaitUntilAsync(async () =>
            (await http.GetFromJsonAsync<JsonNode>(buildUrl))!["status"]!.GetValue<string>() == "Canceled", "cancellation");
    }

    [Fact]
    public async Task Bff_rejects_anonymous_and_csrf_less_requests()
    {
        using var anonymous = new HttpClient { BaseAddress = aspire.App.GetEndpoint("bff", "http") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/pipelines")).StatusCode);

        using var http = await LoginAsync();
        http.DefaultRequestHeaders.Remove("X-CSRF");
        var response = await http.PostAsJsonAsync("/api/orgs", new { name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/agent/artifacts/" + Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task Google_sign_in_is_hidden_until_configured()
    {
        using var anonymous = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = aspire.App.GetEndpoint("bff", "http"),
        };
        var providers = await anonymous.GetFromJsonAsync<JsonNode>("/bff/providers");
        Assert.True(providers!["password"]!.GetValue<bool>());
        Assert.False(providers["google"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/bff/login/google")).StatusCode);
    }

    private static List<string> Paths(JsonNode files) =>
        files["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()).ToList();

    private Task<HttpClient> LoginAsync(bool withOrganization = true) => E2E.LoginAsync(aspire, withOrganization);
    private static Task<JsonNode> PostAsync(HttpClient http, string url, object body) => E2E.PostAsync(http, url, body);
    private static JsonNode Job(JsonNode build, string key) => E2E.Job(build, key);
    private static string? StatusOf(JsonNode build, string key) => E2E.StatusOf(build, key);
    private static Task WaitUntilAsync(Func<Task<bool>> condition, string what, int seconds = 90) => E2E.WaitUntilAsync(condition, what, seconds);

    private int _repos;
    private string CreateRepository(Dictionary<string, string> files, Dictionary<string, IReadOnlyDictionary<string, string>>? branches = null) =>
        E2E.CreateRepository(Path.Combine(_repoDir, (++_repos).ToString()), files,
            branches?.ToDictionary(b => b.Key, b => b.Value));

    public void Dispose()
    {
        try { Directory.Delete(_repoDir, true); } catch { /* best effort */ }
    }
}
