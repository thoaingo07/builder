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
    public async Task Secrets_reach_the_task_as_vars_and_env_and_never_appear_in_logs()
    {
        const string Value = "s3cr3t-value-123";
        var repo = CreateRepository(new()
        {
            [".builder/runners/secret.yml"] = """
                version: '3'
                tasks:
                  default:
                    x-secrets: [API_TOKEN]
                    cmds:
                      - echo "env=$API_TOKEN"
                      - echo "var={{.API_TOKEN}}"
                      - test "$API_TOKEN" = "s3cr3t-value-123"
                """,
            [".builder/runners/missing.yml"] = "version: '3'\ntasks:\n  default:\n    x-secrets: [NOPE]\n    cmds:\n      - echo hi\n",
        });
        using var http = await LoginAsync();
        var secret = await PostAsync(http, "/api/secrets", new { name = "API_TOKEN", value = Value, description = "test" });
        Assert.Null(secret["value"]);                                     // never returned
        Assert.DoesNotContain(Value, await http.GetStringAsync("/api/secrets"));
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsJsonAsync("/api/secrets", new { name = "bad-name", value = "x" })).StatusCode);

        var runner = await E2E.MapRunnerAsync(http, repo, ".builder/runners/secret.yml");
        var build = await E2E.RunBuildAsync(http, runner);
        Assert.Equal("Succeeded", build["status"]!.GetValue<string>());
        Assert.Equal(["API_TOKEN"], build["jobs"]![0]!["secrets"]!.AsArray().Select(x => x!.GetValue<string>()));
        var logs = await http.GetStringAsync($"/api/builds/{build["id"]}/jobs/{build["jobs"]![0]!["id"]}/logs");
        Assert.Contains("env=***", logs);
        Assert.Contains("var=***", logs);
        Assert.DoesNotContain(Value, logs);

        var missing = await E2E.MapRunnerAsync(http, repo, ".builder/runners/missing.yml");
        var failed = await E2E.RunBuildAsync(http, missing);
        Assert.Equal("Failed", failed["status"]!.GetValue<string>());
        Assert.Contains("Unknown secret(s): NOPE", failed["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Jobs_leave_nothing_behind_on_the_daemon()
    {
        var repo = CreateRepository(new()
        {
            [".builder/runners/clean.yml"] = """
                version: '3'
                tasks:
                  default:
                    x-secrets: [LEAK_CHECK]
                    cmds:
                      - mkdir -p "$DOCKER_CONFIG" && echo '{"auths":{"r.example":{"auth":"x"}}}' > "$DOCKER_CONFIG/config.json"
                      - echo "SANDBOX=$HOME"
                      - echo "CHECKOUT=$PWD"
                      # the secret must not be on any process's command line (shell builtins only, so this check can't leak it)
                      - |
                        all=$(cat /proc/[0-9]*/cmdline 2>/dev/null | tr '\0' ' ')
                        case "$all" in *"$LEAK_CHECK"*) echo "LEAKED ON A COMMAND LINE"; exit 1;; esac
                      - echo "home is sandboxed"; test "$HOME" != "$USER_HOME"
                """,
        });
        using var http = await LoginAsync();
        await PostAsync(http, "/api/secrets", new { name = "LEAK_CHECK", value = "only-in-env-" + Guid.NewGuid().ToString("N") });
        var runner = await E2E.MapRunnerAsync(http, repo, ".builder/runners/clean.yml");
        var build = await E2E.RunBuildAsync(http, runner);
        var logs = await http.GetFromJsonAsync<JsonArray>($"/api/builds/{build["id"]}/jobs/{build["jobs"]![0]!["id"]}/logs");
        var lines = logs!.Select(l => l!["text"]!.GetValue<string>()).ToList();
        Assert.True(build["status"]!.GetValue<string>() == "Succeeded", string.Join("\n", lines));

        var sandbox = lines.Single(l => l.StartsWith("SANDBOX=")).Split('=', 2)[1];
        var checkout = lines.Single(l => l.StartsWith("CHECKOUT=")).Split('=', 2)[1];
        Assert.Contains("/jobs/", sandbox);
        Assert.False(Directory.Exists(sandbox), "the job sandbox (HOME, DOCKER_CONFIG, ...) is deleted when the job ends");
        await WaitUntilAsync(() => Task.FromResult(!Directory.Exists(checkout)), "the checkout to be deleted after the build", 30);
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
        // agent endpoints pass through the BFF but need an agent token, never a browser session
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/agent/artifacts/" + Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task Daemons_can_reach_the_api_through_the_public_endpoint()
    {
        using var http = await LoginAsync();
        var token = (await PostAsync(http, "/api/org/agent-token", new { }))["agentToken"]!.GetValue<string>();
        using var agent = new HttpClient { BaseAddress = aspire.App.GetEndpoint("bff", "http") };
        agent.DefaultRequestHeaders.Add("X-Agent-Token", token);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync("/api/agent/artifacts/" + Guid.NewGuid())).StatusCode); // authenticated, no such artifact
        var negotiate = await agent.PostAsync("/hubs/agent/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
        agent.DefaultRequestHeaders.Remove("X-Agent-Token");
        agent.DefaultRequestHeaders.Add("X-Agent-Token", "bldr_wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.PostAsync("/hubs/agent/negotiate?negotiateVersion=1", null)).StatusCode);
    }

    [Fact]
    public async Task Users_can_change_their_password()
    {
        using var http = await LoginAsync(withOrganization: false);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsJsonAsync("/api/me/password", new { currentPassword = "wrong", newPassword = "a-long-new-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsJsonAsync("/api/me/password", new { currentPassword = E2E.AdminPassword, newPassword = "short" })).StatusCode);

        const string NewPassword = "a-long-new-password-1";
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsJsonAsync("/api/me/password", new { currentPassword = E2E.AdminPassword, newPassword = NewPassword })).StatusCode);
        try
        {
            using var fresh = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = aspire.App.GetEndpoint("bff", "http") };
            fresh.DefaultRequestHeaders.Add("X-CSRF", "1");
            Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.PostAsJsonAsync("/bff/login", new { userName = "admin", password = E2E.AdminPassword })).StatusCode);
            Assert.True((await fresh.PostAsJsonAsync("/bff/login", new { userName = "admin", password = NewPassword })).IsSuccessStatusCode);
        }
        finally
        {
            // the other tests sign in with the bootstrap password
            var restored = await http.PostAsJsonAsync("/api/me/password", new { currentPassword = NewPassword, newPassword = E2E.AdminPassword });
            Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
        }
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
