using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Testing;

namespace Builder.Tests;

/// <summary>Projects: what a runner's names resolve to, what lists show, and testing environments from an agent.</summary>
[Collection(AspireCollection.Name)]
public sealed class ProjectTests(AspireFixture aspire) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "builder-prj-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task A_project_uses_its_own_settings_before_the_shared_ones()
    {
        using var http = await E2E.LoginAsync(aspire);
        var shop = await E2E.PostAsync(http, "/api/projects", new { name = "Shop" });
        var blog = await E2E.PostAsync(http, "/api/projects", new { name = "Blog", description = "the other one" });
        string Id(JsonNode n) => n["id"]!.GetValue<string>();

        // GREETING: shared, and overridden in Shop; ONLY_SHOP exists only in Shop
        await E2E.PostAsync(http, "/api/secrets", new { name = "GREETING", value = "shared" });
        await E2E.PostAsync(http, "/api/secrets", new { name = "GREETING", value = "shop-own-value", projectId = Id(shop) });
        await E2E.PostAsync(http, "/api/secrets", new { name = "ONLY_SHOP", value = "x", projectId = Id(shop) });
        var dup = await http.PostAsJsonAsync("/api/secrets", new { name = "GREETING", value = "again", projectId = Id(shop) });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        var runner = """
            version: '3'
            tasks:
              ci:
                x-secrets: [GREETING]
                cmds:
                  - echo "length=${#GREETING}"
            """;
        var shopRepo = E2E.CreateRepository(Path.Combine(_root, "shop"), new Dictionary<string, string> { [".builder/runners/ci.yml"] = runner });
        var blogRepo = E2E.CreateRepository(Path.Combine(_root, "blog"), new Dictionary<string, string>
        {
            [".builder/runners/ci.yml"] = runner,
            [".builder/runners/other.yml"] = "version: '3'\ntasks:\n  ci:\n    x-secrets: [ONLY_SHOP]\n    cmds: [echo hi]\n",
        });
        // the same runner name in two projects
        var shopCi = await E2E.MapRunnerAsync(http, shopRepo, ".builder/runners/ci.yml", "ci", projectId: Id(shop));
        var blogCi = await E2E.MapRunnerAsync(http, blogRepo, ".builder/runners/ci.yml", "ci", projectId: Id(blog));
        var blogOther = await E2E.MapRunnerAsync(http, blogRepo, ".builder/runners/other.yml", "ci");
        var pipelines = (await E2E.GetAsync(http, "/api/pipelines")).AsArray();
        Assert.Equal(["ci", "ci"], pipelines.Where(p => p!["id"]!.GetValue<string>() is var i && (i == shopCi || i == blogCi))
            .Select(p => p!["name"]!.GetValue<string>()));

        Assert.Contains("length=14", await LogOfAsync(http, await E2E.RunBuildAsync(http, shopCi, null, 120)));
        Assert.Contains("length=6", await LogOfAsync(http, await E2E.RunBuildAsync(http, blogCi, null, 120)));
        var other = await E2E.RunBuildAsync(http, blogOther, null, 120);
        Assert.Equal("Failed", other["status"]!.GetValue<string>());
        Assert.Contains("ONLY_SHOP", other["error"]!.GetValue<string>());

        // lists: a project sees its own plus the shared ones
        var shopSecrets = (await E2E.GetAsync(http, $"/api/secrets?project={Id(shop)}")).AsArray();
        Assert.Equal(3, shopSecrets.Count);
        var blogSecrets = (await E2E.GetAsync(http, $"/api/secrets?project={Id(blog)}")).AsArray();
        Assert.Equal("GREETING", blogSecrets.Single()!["name"]!.GetValue<string>());
        Assert.Null(blogSecrets.Single()!["projectId"]);
        Assert.Single((await E2E.GetAsync(http, $"/api/repositories?project={Id(shop)}")).AsArray());
        Assert.All((await E2E.GetAsync(http, $"/api/builds?project={Id(blog)}")).AsArray(),
            b => Assert.Equal(Id(blog), b!["projectId"]!.GetValue<string>()));
        Assert.Equal(2, (await E2E.GetAsync(http, $"/api/dashboard?project={Id(blog)}"))["recentBuilds"]!.AsArray().Count);

        // a repository needs a project once there are several; moving it takes its runners and builds along
        var third = await http.PostAsJsonAsync("/api/repositories", new { url = Path.Combine(_root, "none.git") });
        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        var repoId = (await E2E.GetAsync(http, $"/api/repositories?project={Id(blog)}")).AsArray().Single()!["id"]!.GetValue<string>();
        var move = await http.PutAsJsonAsync($"/api/repositories/{repoId}", new { url = blogRepo, projectId = Id(shop) });
        Assert.Equal(HttpStatusCode.Conflict, move.StatusCode); // Shop already has a runner named ci
        await E2E.PutAsync(http, $"/api/pipelines/{blogCi}", new { name = "blog-ci" });
        await E2E.PutAsync(http, $"/api/repositories/{repoId}", new { url = blogRepo, projectId = Id(shop) });
        Assert.Equal(3, (await E2E.GetAsync(http, $"/api/builds?project={Id(shop)}")).AsArray().Count);
        var p = await E2E.GetAsync(http, $"/api/projects/{Id(shop)}");
        Assert.Equal(2, p["repositoryCount"]!.GetValue<int>());

        // only an empty project can go
        Assert.Equal(HttpStatusCode.Conflict, (await http.DeleteAsync($"/api/projects/{Id(shop)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/api/projects/{Id(blog)}")).StatusCode);
    }

    [Fact]
    public async Task Environments_are_tested_from_an_agent()
    {
        var vps = aspire.App.GetEndpoint("vps", "ssh");
        using var http = await E2E.LoginAsync(aspire);
        var good = await E2E.PostAsync(http, "/api/environments", new
        {
            name = "vps", type = "SshDocker", requiresApproval = false,
            host = vps.Host, port = vps.Port, username = "root", privateKey = aspire.VpsPrivateKey,
        });
        var result = await E2E.PostAsync(http, $"/api/environments/{good["id"]}/test", new { });
        Assert.True(result["ok"]!.GetValue<bool>(), result["message"]!.GetValue<string>());
        Assert.Matches("SSH works as root .*docker \\d", result["message"]!.GetValue<string>());

        var badKey = await E2E.PostAsync(http, "/api/environments", new
        {
            name = "wrong-key", type = "SshDocker", requiresApproval = false,
            host = vps.Host, port = vps.Port, username = "root", privateKey = WrongKey(),
        });
        var refused = await E2E.PostAsync(http, $"/api/environments/{badKey["id"]}/test", new { });
        Assert.False(refused["ok"]!.GetValue<bool>());
        Assert.Contains("SSH to root@", refused["message"]!.GetValue<string>());

        // a password instead of a key: the Test button, and $DEPLOY_SSH in a task's own commands
        var byPassword = await E2E.PostAsync(http, "/api/environments", new
        {
            name = "vps-password", type = "SshDocker", requiresApproval = false,
            host = vps.Host, port = vps.Port, username = "root", password = aspire.VpsPassword,
        });
        Assert.True(byPassword["hasPassword"]!.GetValue<bool>());
        Assert.False(byPassword["hasPrivateKey"]!.GetValue<bool>());
        var viaPassword = await E2E.PostAsync(http, $"/api/environments/{byPassword["id"]}/test", new { });
        Assert.True(viaPassword["ok"]!.GetValue<bool>(), viaPassword["message"]!.GetValue<string>());
        var wrongPassword = await E2E.PostAsync(http, "/api/environments", new
        {
            name = "vps-wrong-password", type = "SshDocker", requiresApproval = false,
            host = vps.Host, port = vps.Port, username = "root", password = "not-it",
        });
        Assert.False((await E2E.PostAsync(http, $"/api/environments/{wrongPassword["id"]}/test", new { }))["ok"]!.GetValue<bool>());

        var repo = E2E.CreateRepository(Path.Combine(_root, "pw"), new Dictionary<string, string>
        {
            [".builder/runners/ssh.yml"] = "version: '3'\ntasks:\n  ssh:\n    x-deploy: { environment: vps-password }\n    cmds:\n      - $DEPLOY_SSH \"echo remote-$((40+2))\"\n",
        });
        var runner = await E2E.MapRunnerAsync(http, repo, ".builder/runners/ssh.yml", "ssh");
        var build = await E2E.RunBuildAsync(http, runner, null, 120);
        var log = await LogOfAsync(http, build);
        Assert.Contains("remote-42", log);
        Assert.DoesNotContain(aspire.VpsPassword, log);

        var nobody = await E2E.PostAsync(http, "/api/environments", new
        {
            name = "no-agent", type = "SshDocker", requiresApproval = false, agentLabels = new[] { "no-such-label" },
            host = vps.Host, port = vps.Port, username = "root", privateKey = aspire.VpsPrivateKey,
        });
        var none = await http.PostAsJsonAsync($"/api/environments/{nobody["id"]}/test", new { });
        Assert.Equal(HttpStatusCode.Conflict, none.StatusCode);
        Assert.Contains("No online agent", await none.Content.ReadAsStringAsync());
    }

    private static async Task<string> LogOfAsync(HttpClient http, JsonNode build)
    {
        Assert.True(build["jobs"]!.AsArray().Count > 0, $"{build["status"]} {build["error"]}");
        var log = await http.GetStringAsync($"/api/builds/{build["id"]}/jobs/{build["jobs"]![0]!["id"]}/logs");
        Assert.True(build["status"]!.GetValue<string>() == "Succeeded", $"{build["status"]} {build["error"]}\n{log}");
        return log;
    }

    private string WrongKey()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "wrong");
        using var p = System.Diagnostics.Process.Start("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-f", file])!;
        p.WaitForExit();
        return File.ReadAllText(file);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }
}
