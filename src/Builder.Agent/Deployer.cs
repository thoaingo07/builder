using Builder.Contracts;

namespace Builder.Agent;

/// <summary>
/// Prepares credentials for a deploy target (exposed to the task's commands as DEPLOY_* / KUBECONFIG)
/// and runs the built-in deploy and teardown actions.
/// </summary>
public sealed class Deployer(DeployTarget target, string tempDir, string workRoot)
{
    private string? _keyFile;
    private string? _kubeconfig;
    private readonly Dictionary<string, string> _env = new();

    public IReadOnlyDictionary<string, string> Environment => _env;

    public async Task PrepareAsync(JobLog log, CancellationToken ct)
    {
        Directory.CreateDirectory(tempDir);
        _env["DEPLOY_ENVIRONMENT"] = target.EnvironmentName;
        if (target.Project is { } project) _env["DEPLOY_PROJECT"] = project;
        if (target.Url is { } url) _env["DEPLOY_URL"] = url;

        if (target.Type == DeployTargetType.SshDocker)
        {
            if (string.IsNullOrWhiteSpace(target.Host)) throw new InvalidOperationException($"Environment '{target.EnvironmentName}' has no host.");
            _env["DEPLOY_HOST"] = target.Host;
            _env["DEPLOY_PORT"] = target.Port.ToString();
            _env["DEPLOY_USER"] = target.Username ?? "root";
            if (target.PrivateKey is { } key)
            {
                _keyFile = Path.Combine(tempDir, $"id_{Guid.NewGuid():N}");
                await File.WriteAllTextAsync(_keyFile, key.ReplaceLineEndings("\n").TrimEnd() + "\n", ct);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                _env["DEPLOY_SSH_KEY"] = _keyFile;
            }
            _env["DEPLOY_SSH"] = "ssh " + string.Join(' ', SshOptions().Select(Quote)) + $" {Destination}";
            return;
        }

        _kubeconfig = Path.Combine(tempDir, $"kubeconfig-{Guid.NewGuid():N}");
        if (target.Kubeconfig is { } kubeconfig)
        {
            await File.WriteAllTextAsync(_kubeconfig, kubeconfig, ct);
        }
        else if (target.AksClusterName is not null)
        {
            var azDir = Path.Combine(tempDir, $"az-{Guid.NewGuid():N}");
            var azEnv = new Dictionary<string, string> { ["AZURE_CONFIG_DIR"] = azDir };
            log.System($"az login (service principal) and get-credentials for AKS cluster {target.AksClusterName}");
            await ProcessRunner.CaptureAsync("az", ["login", "--service-principal", "-u", target.AksClientId ?? "",
                "-p", target.AksClientSecret ?? "", "--tenant", target.AksTenantId ?? "", "--output", "none"], tempDir, azEnv, ct);
            if (target.AksSubscriptionId is { } sub)
                await ProcessRunner.CaptureAsync("az", ["account", "set", "--subscription", sub], tempDir, azEnv, ct);
            List<string> getCreds = ["aks", "get-credentials", "-g", target.AksResourceGroup ?? "", "-n", target.AksClusterName,
                "--file", _kubeconfig, "--overwrite-existing", "--output", "none"];
            if (target.AksAdmin) getCreds.Add("--admin");
            await ProcessRunner.CaptureAsync("az", getCreds, tempDir, azEnv, ct);
            if (!target.AksAdmin && ProcessRunner.Exists("kubelogin"))
            {
                await ProcessRunner.CaptureAsync("kubelogin", ["convert-kubeconfig", "-l", "spn", "--kubeconfig", _kubeconfig], tempDir, null, ct);
                _env["AAD_SERVICE_PRINCIPAL_CLIENT_ID"] = target.AksClientId ?? "";
                _env["AAD_SERVICE_PRINCIPAL_CLIENT_SECRET"] = target.AksClientSecret ?? "";
                _env["AZURE_TENANT_ID"] = target.AksTenantId ?? "";
            }
        }
        else
        {
            throw new InvalidOperationException($"Environment '{target.EnvironmentName}' has neither a kubeconfig nor AKS settings.");
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_kubeconfig, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _env["KUBECONFIG"] = _kubeconfig;
        if (target.Namespace is { } ns) _env["DEPLOY_NAMESPACE"] = ns;
    }

    /// <summary>The built-in deploy: docker compose over SSH, or kubectl apply. No-op when neither is configured.</summary>
    public async Task<int> DeployAsync(string sourceDir, JobLog log, CancellationToken ct)
    {
        if (target.Type == DeployTargetType.SshDocker)
        {
            if (target.ComposeFile is null) return 0;
            var project = target.Project!;
            var compose = Path.GetFullPath(Path.Combine(sourceDir, target.ComposeFile));
            if (!File.Exists(compose)) { log.Err($"Compose file '{target.ComposeFile}' not found"); return 1; }
            var remoteDir = $"builder/{project}";

            log.System($"Deploying '{project}' to {Destination} with docker compose");
            var code = await Ssh($"mkdir -p {remoteDir}", log, ct);
            if (code != 0) return code;
            code = await ProcessRunner.RunAsync("scp", [.. ScpOptions(), compose, $"{Destination}:{remoteDir}/docker-compose.yml"],
                sourceDir, null, log.Write, ct);
            if (code != 0) return code;
            return await Ssh($"cd {remoteDir} && docker compose -p {project} up -d --pull always --remove-orphans", log, ct);
        }

        if (target.Manifests is null) return 0;
        var ns = target.Namespace!;
        var manifests = Path.GetFullPath(Path.Combine(sourceDir, target.Manifests));
        log.System($"Applying {target.Manifests} to namespace '{ns}'");
        var env = new Dictionary<string, string>(_env);
        var nsYaml = await ProcessRunner.CaptureAsync("kubectl", ["create", "namespace", ns, "--dry-run=client", "-o", "yaml"], sourceDir, env, ct);
        var rc = await ProcessRunner.RunAsync("kubectl", ["apply", "-f", "-"], sourceDir, env, log.Write, ct, stdin: nsYaml);
        if (rc != 0) return rc;
        List<string> apply = ["apply", "-n", ns, "-f", manifests];
        if (Directory.Exists(manifests)) apply.Add("-R");
        return await ProcessRunner.RunAsync("kubectl", apply, sourceDir, env, log.Write, ct);
    }

    public async Task<(bool Ok, string Output)> TeardownAsync(CancellationToken ct)
    {
        var lines = new List<string>();
        var log = new JobLog(_ => Task.CompletedTask, [target.PrivateKey, target.AksClientSecret]);
        void Collect(LogStream _, string l) => lines.Add(l);
        int code;
        await PrepareAsync(log, ct);
        if (target.Type == DeployTargetType.SshDocker)
        {
            code = await ProcessRunner.RunAsync("ssh", [.. SshOptions(), Destination,
                $"docker compose -p {target.Project} down --remove-orphans && rm -rf builder/{target.Project}"], tempDir, null, Collect, ct);
        }
        else
        {
            code = await ProcessRunner.RunAsync("kubectl", ["delete", "namespace", target.Namespace!, "--wait=false", "--ignore-not-found"],
                tempDir, new Dictionary<string, string>(_env), Collect, ct);
        }
        await log.DisposeAsync();
        Cleanup();
        return (code == 0, string.Join("\n", lines.TakeLast(50)));
    }

    public void Cleanup()
    {
        foreach (var f in new[] { _keyFile, _kubeconfig })
            if (f is not null && File.Exists(f)) File.Delete(f);
    }

    private string Destination => $"{target.Username ?? "root"}@{target.Host}";

    private Task<int> Ssh(string command, JobLog log, CancellationToken ct) =>
        ProcessRunner.RunAsync("ssh", [.. SshOptions(), Destination, command], tempDir, null, log.Write, ct);

    private List<string> CommonOptions()
    {
        var o = new List<string>
        {
            "-o", "BatchMode=yes",
            "-o", "StrictHostKeyChecking=accept-new",
            "-o", $"UserKnownHostsFile={Path.Combine(workRoot, "known_hosts")}",
            "-o", "ConnectTimeout=20",
        };
        if (_keyFile is not null) o.AddRange(["-i", _keyFile, "-o", "IdentitiesOnly=yes"]);
        return o;
    }

    private List<string> SshOptions() => [.. CommonOptions(), "-p", target.Port.ToString()];
    private List<string> ScpOptions() => [.. CommonOptions(), "-P", target.Port.ToString()];

    private static string Quote(string s) => s.Contains(' ') ? $"'{s}'" : s;
}
