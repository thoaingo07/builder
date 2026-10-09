using Aspire.Hosting.Testing;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Builder.Tests;

/// <summary>
/// Drives the real stack the way the UI does: browser → BFF (cookie, CSRF header) → API → two agents running
/// go-task. Needs `git` and `task` on PATH (the agents run as local processes under Aspire).
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class EndToEndTests(AspireFixture aspire) : IDisposable
{
    private readonly string _repoDir = Path.Combine(Path.GetTempPath(), "builder-e2e-" + Guid.NewGuid().ToString("N")[..8]);

    private const string Taskfile = """
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
    public async Task Runs_a_pipeline_across_agents_through_an_approval_gate()
    {
        var repo = CreateRepository();
        using var http = await LoginAsync();

        await WaitUntilAsync(async () =>
            (await http.GetFromJsonAsync<JsonArray>("/api/agents"))!.Count(a => a!["online"]!.GetValue<bool>()) >= 2,
            "two agents online");

        var pipeline = await PostAsync(http, "/api/pipelines",
            new { name = "e2e-" + Guid.NewGuid().ToString("N")[..6], repositoryUrl = repo, defaultBranch = "main" });
        var build = await PostAsync(http, $"/api/pipelines/{pipeline["id"]}/builds",
            new { variables = new Dictionary<string, string> { ["GREETING"] = "hello" } });
        var buildUrl = $"/api/builds/{build["id"]}";

        JsonNode detail = null!;
        await WaitUntilAsync(async () =>
        {
            detail = (await http.GetFromJsonAsync<JsonNode>(buildUrl))!;
            var status = detail["status"]!.GetValue<string>();
            Assert.NotEqual("Failed", status);
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
    public async Task Artifacts_are_relative_to_a_nested_taskfile()
    {
        var repo = CreateRepository("""
            version: '3'
            tasks:
              produce:
                x-artifacts: [out/**]
                cmds:
                  - mkdir -p out
                  - echo nested > out/file.txt
              consume:
                deps: [produce]
                cmds:
                  - test "$(cat out/file.txt)" = nested
            """, path: "ci/Taskfile.yml");
        using var http = await LoginAsync();
        var pipeline = await PostAsync(http, "/api/pipelines", new
        {
            name = "nested-" + Guid.NewGuid().ToString("N")[..6], repositoryUrl = repo, defaultBranch = "main",
            taskfilePath = "ci/Taskfile.yml", entryTask = "consume",
        });
        var build = await PostAsync(http, $"/api/pipelines/{pipeline["id"]}/builds", new { });
        var buildUrl = $"/api/builds/{build["id"]}";
        var status = "";
        await WaitUntilAsync(async () =>
        {
            status = (await http.GetFromJsonAsync<JsonNode>(buildUrl))!["status"]!.GetValue<string>();
            return status is "Succeeded" or "Failed";
        }, "the build to finish");
        Assert.Equal("Succeeded", status);
    }

    [Fact]
    public async Task Cancel_stops_a_running_build()
    {
        var repo = CreateRepository("""
            version: '3'
            tasks:
              default:
                cmds:
                  - sleep 120
            """);
        using var http = await LoginAsync();
        var pipeline = await PostAsync(http, "/api/pipelines",
            new { name = "cancel-" + Guid.NewGuid().ToString("N")[..6], repositoryUrl = repo, defaultBranch = "main" });
        var build = await PostAsync(http, $"/api/pipelines/{pipeline["id"]}/builds", new { });
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
        var response = await http.PostAsJsonAsync("/api/pipelines", new { name = "x", repositoryUrl = "y" });
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

    private Task<HttpClient> LoginAsync() => E2E.LoginAsync(aspire);
    private static Task<JsonNode> PostAsync(HttpClient http, string url, object body) => E2E.PostAsync(http, url, body);
    private static JsonNode Job(JsonNode build, string key) => E2E.Job(build, key);
    private static string? StatusOf(JsonNode build, string key) => E2E.StatusOf(build, key);
    private static Task WaitUntilAsync(Func<Task<bool>> condition, string what, int seconds = 90) => E2E.WaitUntilAsync(condition, what, seconds);

    private string CreateRepository(string taskfile = Taskfile, string path = "Taskfile.yml") =>
        E2E.CreateRepository(_repoDir, new Dictionary<string, string> { [path] = taskfile });

    public void Dispose()
    {
        try { Directory.Delete(_repoDir, true); } catch { /* best effort */ }
    }
}
