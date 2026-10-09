using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Aspire.Hosting.Testing;

namespace Builder.Tests;

/// <summary>
/// The SSH + compose deploy, for real: agents ssh into the test VPS container, copy the compose file and run
/// `docker compose up` there, which starts the app as a container on this machine's Podman/Docker.
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class DeployTests(AspireFixture aspire) : IDisposable
{
    private readonly string _repoDir = Path.Combine(Path.GetTempPath(), "builder-deploy-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task Deploys_over_ssh_redeploys_and_destroys()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var appPort = FreePort();
        var appUrl = $"http://127.0.0.1:{appPort}/";
        var project = $"e2e-app-{suffix}";
        var vps = aspire.App.GetEndpoint("vps", "ssh");

        using var http = await E2E.LoginAsync(aspire);
        var env = await E2E.PostAsync(http, "/api/environments", new
        {
            name = $"vps-{suffix}", type = "SshDocker", requiresApproval = false,
            host = vps.Host, port = vps.Port, username = "root", privateKey = aspire.VpsPrivateKey,
        });
        Assert.True(env["hasPrivateKey"]!.GetValue<bool>());
        Assert.Null(env["privateKey"]); // secrets are write-only

        var repo = E2E.CreateRepository(_repoDir, new Dictionary<string, string>
        {
            ["Taskfile.yml"] = $$"""
                version: '3'
                tasks:
                  deploy:
                    x-deploy:
                      environment: vps-{{suffix}}
                      compose: deploy/compose.yml
                      project: {{project}}
                      url: {{appUrl}}
                    cmds:
                      # stamp the build number into the compose file before it is shipped
                      - sed -i "s/BUILD_MARK/{{"{{"}}.BUILDER_BUILD_NUMBER{{"}}"}}/" deploy/compose.yml
                """,
            ["deploy/compose.yml"] = $$"""
                services:
                  web:
                    image: docker.io/library/nginx:1.27-alpine
                    ports: ["{{appPort}}:80"]
                    command: ["sh", "-c", "echo build-BUILD_MARK > /usr/share/nginx/html/index.html && exec nginx -g 'daemon off;'"]
                """,
        });
        var pipeline = await E2E.PostAsync(http, "/api/pipelines", new
        {
            name = $"deploy-{suffix}", repositoryUrl = repo, defaultBranch = "main", entryTask = "deploy",
        });
        var pipelineId = pipeline["id"]!.GetValue<string>();

        try
        {
            // 1st deploy
            var first = await E2E.RunBuildAsync(http, pipelineId, seconds: 240);
            await AssertSucceededAsync(http, first);
            Assert.Equal("build-1", await ReadAppAsync(appUrl));
            var firstDeployment = first["deployments"]!.AsArray().Single()!;
            Assert.Equal(appUrl, firstDeployment["url"]!.GetValue<string>());

            // 2nd deploy replaces the app and supersedes the first deployment
            var second = await E2E.RunBuildAsync(http, pipelineId, seconds: 240);
            await AssertSucceededAsync(http, second);
            Assert.Equal("build-2", await ReadAppAsync(appUrl));
            var deployments = (await E2E.GetAsync(http, "/api/deployments"))!.AsArray()
                .Where(d => d!["name"]!.GetValue<string>() == project).ToList();
            Assert.Equal(["Active", "Destroyed"], deployments.Select(d => d!["status"]!.GetValue<string>()));

            // destroy tears the app down on the host
            var active = deployments[0]!["id"]!.GetValue<string>();
            await E2E.PostAsync(http, $"/api/deployments/{active}/destroy", new { });
            await E2E.WaitUntilAsync(async () =>
                (await E2E.GetAsync(http, "/api/deployments")).AsArray()
                    .Single(d => d!["id"]!.GetValue<string>() == active)!["status"]!.GetValue<string>() == "Destroyed",
                "the deployment to be destroyed", 120);
            await E2E.WaitUntilAsync(async () => !await AnswersAsync(appUrl), "the app to stop answering", 30);
        }
        finally
        {
            // never leave a test app running on the host
            foreach (var d in (await E2E.GetAsync(http, "/api/deployments?active=true")).AsArray()
                         .Where(d => d!["name"]!.GetValue<string>() == project))
                await http.PostAsync($"/api/deployments/{d!["id"]}/destroy", null);
        }
    }

    private static async Task AssertSucceededAsync(HttpClient http, JsonNode build)
    {
        if (build["status"]!.GetValue<string>() == "Succeeded") return;
        var job = E2E.Job(build, "deploy");
        var logs = await E2E.GetAsync(http, $"/api/builds/{build["id"]}/jobs/{job["id"]}/logs");
        Assert.Fail($"Build {build["status"]}: {job["error"]}\n" + string.Join("\n", logs.AsArray().Select(l => l!["text"]!.GetValue<string>())));
    }

    private static async Task<string> ReadAppAsync(string url)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var body = "";
        await E2E.WaitUntilAsync(async () =>
        {
            body = (await client.GetStringAsync(url)).Trim();
            return body.StartsWith("build-", StringComparison.Ordinal);
        }, $"the app at {url}", 60);
        return body;
    }

    private static async Task<bool> AnswersAsync(string url)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try { return (await client.GetAsync(url)).StatusCode == HttpStatusCode.OK; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return false; }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoDir, true); } catch { /* best effort */ }
    }
}
