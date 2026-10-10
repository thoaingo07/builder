using System.Diagnostics;
using System.Text.Json.Nodes;
using Aspire.Hosting.Testing;

namespace Builder.Tests;

/// <summary>
/// Blue-green container deploys over SSH, for real: the test VPS runs the deploy script against this machine's
/// container engine. Traffic is checked the way a reverse proxy sees it: by the service alias on the network.
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class BlueGreenTests(AspireFixture aspire) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "builder-bg-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..6];
    private string Service => $"bg-web-{_suffix}";
    private string Network => $"bg-net-{_suffix}";

    [Fact]
    public async Task Switches_only_to_healthy_containers_and_rolls_back()
    {
        var vps = aspire.App.GetEndpoint("vps", "ssh");
        using var http = await E2E.LoginAsync(aspire);
        await E2E.PostAsync(http, "/api/environments", new
        {
            name = $"vps-{_suffix}", type = "SshDocker", requiresApproval = false,
            host = vps.Host, port = vps.Port, username = "root", privateKey = aspire.VpsPrivateKey,
        });
        var repo = E2E.CreateRepository(_root, new Dictionary<string, string>
        {
            [".builder/runners/deploy.yml"] = $$"""
                version: '3'
                tasks:
                  deploy:
                    requires:
                      vars: [VERSION]
                    x-deploy:
                      environment: vps-{{_suffix}}
                      strategy: blue-green
                      service: {{Service}}
                      image: docker.io/library/nginx:1.27-alpine
                      network: {{Network}}
                      args: [-e, "VERSION={{"{{"}}.VERSION{{"}}"}}", --health-cmd, 'test "$VERSION" != bad', --health-interval, 2s, --health-retries, "2"]
                      command: [sh, -c, "echo $VERSION > /usr/share/nginx/html/index.html && exec nginx -g 'daemon off;'"]
                      health: { timeout: 25 }
                      keep: 1
                """,
        });
        var runner = await E2E.MapRunnerAsync(http, repo, ".builder/runners/deploy.yml", entryTask: "deploy");

        try
        {
            var v1 = await DeployAsync(http, runner, "v1");
            Assert.Equal("v1", await ServedAsync());
            Assert.True((await E2E.GetAsync(http, "/api/deployments")).AsArray()
                .Single(d => d!["id"]!.GetValue<string>() == v1)!["isContainer"]!.GetValue<bool>());
            var v2 = await DeployAsync(http, runner, "v2");
            Assert.Equal("v2", await ServedAsync());
            Assert.Equal("Superseded", await StatusAsync(http, v1));

            // an unhealthy candidate is removed; the live one keeps serving
            var bad = await E2E.RunBuildAsync(http, runner, new { variables = new Dictionary<string, string> { ["VERSION"] = "bad" } }, 180);
            Assert.Equal("Failed", bad["status"]!.GetValue<string>());
            var badLog = await http.GetStringAsync($"/api/builds/{bad["id"]}/jobs/{bad["jobs"]![0]!["id"]}/logs");
            Assert.Contains("keeping the current container", badLog);
            Assert.Equal("v2", await ServedAsync());
            Assert.Equal("Active", await StatusAsync(http, v2));
            Assert.Equal(2, Containers().Count); // v2 live + v1 kept; the bad candidate is gone

            // rollback: v1 is live again
            await E2E.PostAsync(http, $"/api/deployments/{v2}/rollback", new { });
            await E2E.WaitUntilAsync(async () => await StatusAsync(http, v2) == "RolledBack", "the rollback", 90);
            Assert.Equal("Active", await StatusAsync(http, v1));
            Assert.Equal("v1", await ServedAsync());

            // destroy removes Builder's containers of the service
            await E2E.PostAsync(http, $"/api/deployments/{v1}/destroy", new { });
            await E2E.WaitUntilAsync(async () => await StatusAsync(http, v1) == "Destroyed", "the teardown", 90);
            Assert.Empty(Containers());
        }
        finally
        {
            foreach (var c in Containers()) Cli("rm", "-f", c);
            Cli("network", "rm", Network);
        }
    }

    [Fact]
    public async Task Recreate_stops_first_and_restarts_the_old_container_on_failure()
    {
        var vps = aspire.App.GetEndpoint("vps", "ssh");
        using var http = await E2E.LoginAsync(aspire);
        await E2E.PostAsync(http, "/api/environments", new
        {
            name = $"vps-{_suffix}", type = "SshDocker", requiresApproval = false,
            host = vps.Host, port = vps.Port, username = "root", privateKey = aspire.VpsPrivateKey,
        });
        var repo = E2E.CreateRepository(_root, new Dictionary<string, string>
        {
            [".builder/runners/worker.yml"] = $$"""
                version: '3'
                tasks:
                  deploy:
                    requires:
                      vars: [VERSION]
                    x-deploy:
                      environment: vps-{{_suffix}}
                      strategy: recreate
                      service: {{Service}}
                      image: docker.io/library/nginx:1.27-alpine
                      network: {{Network}}
                      args: [-e, "VERSION={{"{{"}}.VERSION{{"}}"}}", --health-cmd, 'test "$VERSION" != bad', --health-interval, 2s, --health-retries, "2"]
                      command: [sh, -c, "echo $VERSION > /usr/share/nginx/html/index.html && exec nginx -g 'daemon off;'"]
                      health: { timeout: 25 }
                """,
        });
        var runner = await E2E.MapRunnerAsync(http, repo, ".builder/runners/worker.yml", entryTask: "deploy");
        try
        {
            await DeployAsync(http, runner, "w1");
            Assert.Equal("w1", await ServedAsync());
            var bad = await E2E.RunBuildAsync(http, runner, new { variables = new Dictionary<string, string> { ["VERSION"] = "bad" } }, 180);
            Assert.Equal("Failed", bad["status"]!.GetValue<string>());
            var log = await http.GetStringAsync($"/api/builds/{bad["id"]}/jobs/{bad["jobs"]![0]!["id"]}/logs");
            Assert.Contains("stopping", log);
            Assert.Contains("starting", log);              // the old one is started again
            Assert.Equal("w1", await ServedAsync());
            Assert.Single(Containers());
        }
        finally
        {
            foreach (var c in Containers()) Cli("rm", "-f", c);
            Cli("network", "rm", Network);
        }
    }

    private static async Task<string> DeployAsync(HttpClient http, string runner, string version)
    {
        var build = await E2E.RunBuildAsync(http, runner, new { variables = new Dictionary<string, string> { ["VERSION"] = version } }, 180);
        if (build["status"]!.GetValue<string>() != "Succeeded")
        {
            var logs = await http.GetStringAsync($"/api/builds/{build["id"]}/jobs/{build["jobs"]![0]!["id"]}/logs");
            Assert.Fail($"deploy {version}: {build["status"]} {build["error"]}\n{logs}");
        }
        return build["deployments"]!.AsArray().Single()!["id"]!.GetValue<string>();
    }

    private static async Task<string> StatusAsync(HttpClient http, string deploymentId) =>
        (await E2E.GetAsync(http, "/api/deployments")).AsArray()
            .Single(d => d!["id"]!.GetValue<string>() == deploymentId)!["status"]!.GetValue<string>();

    /// <summary>What a proxy on the network gets from http://&lt;service&gt;/ (retries briefly while the alias settles).</summary>
    private async Task<string> ServedAsync()
    {
        var body = "";
        await E2E.WaitUntilAsync(() =>
        {
            body = Cli("run", "--rm", "--network", Network, "docker.io/curlimages/curl:8.10.1", "-s", "--max-time", "5", $"http://{Service}/").Trim();
            return Task.FromResult(body.Length > 0);
        }, $"{Service} to answer", 30);
        return body;
    }

    private List<string> Containers() =>
        Cli("ps", "-a", "--filter", $"label=builder.service={Service}", "--format", "{{.Names}}")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>The container CLI of the engine the test VPS drives (Podman when installed, like the AppHost).</summary>
    private static string Cli(params string[] args)
    {
        var tool = Environment.GetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME")
            ?? ((Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, "podman"))) ? "podman" : "docker");
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }
}
