using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Builder.Tests;

/// <summary>Each agent's work folder is set in Builder and applied by the running agent.</summary>
[Collection(AspireCollection.Name)]
public sealed class AgentSettingsTests(AspireFixture aspire) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "builder-wd-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task The_work_folder_is_set_per_agent_and_used_by_new_jobs()
    {
        using var http = await E2E.LoginAsync(aspire);
        List<JsonNode?> agents = [];
        await E2E.WaitUntilAsync(async () =>
        {
            agents = (await E2E.GetAsync(http, "/api/agents")).AsArray()
                .Where(a => a!["online"]!.GetValue<bool>() && a["effectiveWorkDirectory"] is not null).ToList();
            return agents.Count >= 2;
        }, "two agents online", 60);
        try
        {
            // a bad path is refused by the API; a path the agent cannot create is reported, and the agent keeps its folder
            var first = agents[0]!["id"]!.GetValue<string>();
            var before = agents[0]!["effectiveWorkDirectory"]!.GetValue<string>();
            Assert.Equal(before, agents[0]!["defaultWorkDirectory"]!.GetValue<string>());
            Assert.Equal(HttpStatusCode.Conflict, (await http.PutAsJsonAsync($"/api/agents/{first}", new { enabled = true, workDirectory = "relative/dir" })).StatusCode);
            var refused = await E2E.PutAsync(http, $"/api/agents/{first}", new { enabled = true, workDirectory = "/proc/builder-cannot-write-here" });
            Assert.Contains("Cannot use /proc/builder-cannot-write-here", refused["workDirectoryError"]!.GetValue<string>());
            Assert.Equal(before, refused["effectiveWorkDirectory"]!.GetValue<string>());

            // every agent gets its own new folder; a job then runs there
            foreach (var a in agents)
            {
                var folder = Path.Combine(_root, a!["name"]!.GetValue<string>());
                var updated = await E2E.PutAsync(http, $"/api/agents/{a["id"]}", new { enabled = true, workDirectory = folder });
                Assert.Equal(folder, updated["workDirectory"]!.GetValue<string>());
                Assert.Equal(folder, updated["effectiveWorkDirectory"]!.GetValue<string>());
                Assert.Null(updated["workDirectoryError"]);
            }
            var repo = E2E.CreateRepository(Path.Combine(_root, "repo"), new Dictionary<string, string>
            {
                [".builder/runners/ci.yml"] = "version: '3'\ntasks:\n  ci:\n    cmds:\n      - echo \"cwd=$(pwd)\"\n",
            });
            var build = await E2E.RunBuildAsync(http, await E2E.MapRunnerAsync(http, repo, ".builder/runners/ci.yml", "ci"), null, 120);
            var log = await http.GetStringAsync($"/api/builds/{build["id"]}/jobs/{build["jobs"]![0]!["id"]}/logs");
            Assert.True(build["status"]!.GetValue<string>() == "Succeeded", log);
            Assert.Contains($"cwd={_root}/", log);
        }
        finally
        {
            // back to each agent's own folder (other tests share these agents)
            foreach (var a in agents)
            {
                var reset = await E2E.PutAsync(http, $"/api/agents/{a!["id"]}", new { enabled = true, workDirectory = "" });
                Assert.Null(reset["workDirectory"]);
                Assert.Equal(a["defaultWorkDirectory"]!.GetValue<string>(), reset["effectiveWorkDirectory"]!.GetValue<string>());
            }
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }
}
