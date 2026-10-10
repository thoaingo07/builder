using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Builder.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Builder.Agent;

/// <summary>Keeps the SignalR connection to the server, reports metrics and runs assigned jobs.</summary>
public sealed class AgentWorker : BackgroundService, IServerChannel
{
    private readonly AgentOptions _options;
    private readonly ILogger<AgentWorker> _log;
    private readonly HubConnection _hub;
    private readonly JobRunner _runner;
    private readonly SystemMetrics _metrics;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly CancellationTokenSource _stopping = new();

    public AgentWorker(IOptions<AgentOptions> options, IHttpClientFactory httpFactory, ILogger<AgentWorker> log, ILogger<JobRunner> runnerLog)
    {
        _options = options.Value;
        _log = log;
        Directory.CreateDirectory(_options.WorkRoot);
        if (JobSandbox.SweepLeftovers(_options.WorkRoot) is > 0 and var swept)
            log.LogInformation("Removed {Count} job sandbox folder(s) left by an earlier run", swept);
        _metrics = new SystemMetrics(_options.WorkRoot);
        _runner = new JobRunner(_options, httpFactory.CreateClient("server"), this, runnerLog);

        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(_options.ServerUrl), AgentHubNames.Route), o =>
                o.Headers[AgentHubNames.TokenHeader] = _options.Token)
            .WithAutomaticReconnect(new ForeverRetry())
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        _hub.ServerTimeout = TimeSpan.FromSeconds(60);

        _hub.On<JobAssignment, bool>(AgentHubNames.AssignJob, Accept);
        _hub.On<Guid>(AgentHubNames.CancelJob, jobId =>
        {
            if (_running.TryGetValue(jobId, out var cts)) { _log.LogInformation("Canceling job {Job}", jobId); cts.Cancel(); }
        });
        _hub.On<CleanupRequest>(AgentHubNames.Cleanup, request => _ = Task.Run(() => CleanupAsync(request)));
        _hub.On<TeardownRequest>(AgentHubNames.Teardown, request => _ = Task.Run(() => TeardownAsync(request)));
        _hub.On<Guid>(AgentHubNames.ReleaseBuild, ReleaseBuild);
        _hub.Reconnected += async _ => await RegisterAsync(_stopping.Token);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        ct.Register(_stopping.Cancel);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _hub.StartAsync(ct);
                await RegisterAsync(ct);
                break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning("Cannot reach {Server}: {Error}. Retrying in 5 s", _options.ServerUrl, ex.Message);
                await Task.Delay(5000, ct);
            }
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (_hub.State != HubConnectionState.Connected) continue;
            try
            {
                var (memTotal, memUsed) = _metrics.Memory();
                var (diskTotal, diskUsed) = _metrics.Disk();
                await _hub.SendAsync(AgentHubNames.Heartbeat, new AgentMetrics(_metrics.CpuPercent(), Environment.ProcessorCount,
                    memTotal, memUsed, diskTotal, diskUsed, SystemMetrics.LoadAverage(), _running.Count, _running.Keys.ToArray()), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogDebug(ex, "Heartbeat failed");
            }
        }
    }

    private async Task RegisterAsync(CancellationToken ct)
    {
        var hello = new AgentHello(
            _options.EffectiveName, Environment.MachineName, RuntimeInformation.OSDescription,
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
            _options.Capacity, [.. _options.Labels, PlatformLabel],
            ProcessRunner.Exists(_options.TaskBinary), ProcessRunner.Exists("docker"), ProcessRunner.Exists("kubectl"),
            ProcessRunner.Exists("az"), ProcessRunner.Exists("ssh"), _running.Keys.ToArray());
        var welcome = await _hub.InvokeAsync<AgentWelcome>(AgentHubNames.Register, hello, ct);
        foreach (var jobId in welcome.JobsToAbort)
            if (_running.TryGetValue(jobId, out var cts)) cts.Cancel();
        _log.LogInformation("Registered with {Server} as {Name} ({Id})", _options.ServerUrl, hello.Name, welcome.AgentId);
    }

    private static string PlatformLabel =>
        OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "other";

    private readonly ConcurrentDictionary<Guid, Guid> _jobBuilds = new();

    /// <summary>The server says a build is over: remove its checkout (source code) from this machine.</summary>
    private void ReleaseBuild(Guid buildId)
    {
        if (_options.KeepWorkspaces || _jobBuilds.Values.Contains(buildId)) return;
        var dir = new Workspace(_options.WorkRoot, buildId).Root;
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Workspace.Release(buildId);
            _log.LogInformation("Removed the workspace of build {Build}", buildId);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not remove the workspace of build {Build}: {Error}", buildId, ex.Message);
        }
    }

    private bool Accept(JobAssignment job)
    {
        if (_running.Count >= _options.Capacity) return false;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        if (!_running.TryAdd(job.JobId, cts)) return false;
        _jobBuilds[job.JobId] = job.BuildId;
        _log.LogInformation("Job {Task} ({Job}) of build #{Number}", job.TaskName, job.JobId, job.BuildNumber);
        _ = Task.Run(async () =>
        {
            JobResult result;
            try
            {
                result = await _runner.RunAsync(job, cts.Token);
            }
            finally
            {
                _running.TryRemove(job.JobId, out _);
                _jobBuilds.TryRemove(job.JobId, out _);
                cts.Dispose();
            }
            await ReportAsync(result);
        });
        return true;
    }

    /// <summary>The result must reach the server, so retry across reconnects.</summary>
    private async Task ReportAsync(JobResult result)
    {
        for (var attempt = 0; attempt < 60 && !_stopping.IsCancellationRequested; attempt++)
        {
            try
            {
                if (_hub.State == HubConnectionState.Connected)
                {
                    await _hub.InvokeAsync(AgentHubNames.JobCompleted, result);
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("Reporting job {Job} failed: {Error}", result.JobId, ex.Message);
            }
            await Task.Delay(2000);
        }
    }

    public Task JobStartedAsync(Guid jobId) => _hub.InvokeAsync(AgentHubNames.JobStarted, jobId);

    public Task StepStartedAsync(Guid jobId, int index) => _hub.State == HubConnectionState.Connected
        ? _hub.InvokeAsync(AgentHubNames.JobStepStarted, jobId, index)
        : Task.CompletedTask;

    public Task<JobCredentials> GetJobCredentialsAsync(Guid jobId) =>
        _hub.InvokeAsync<JobCredentials>(AgentHubNames.GetJobCredentials, jobId);

    public Task<Dictionary<string, string>> GetJobSecretsAsync(Guid jobId) =>
        _hub.InvokeAsync<Dictionary<string, string>>(AgentHubNames.GetJobSecrets, jobId);

    public Task SendLogAsync(Guid jobId, List<LogChunk> lines) => _hub.State == HubConnectionState.Connected
        ? _hub.InvokeAsync(AgentHubNames.JobLog, jobId, lines)
        : Task.CompletedTask;

    private async Task CleanupAsync(CleanupRequest request)
    {
        long freed = 0;
        var output = new List<string>();
        try
        {
            if (request.RemoveWorkspaces)
            {
                var builds = Path.Combine(_options.WorkRoot, "builds");
                var busy = _running.Keys.ToHashSet();
                if (Directory.Exists(builds))
                    foreach (var dir in Directory.GetDirectories(builds))
                    {
                        if (!Guid.TryParseExact(Path.GetFileName(dir), "N", out var buildId)) continue;
                        if (request.KeepBuildIds.Contains(buildId)) continue;
                        var size = DirectorySize(dir);
                        Directory.Delete(dir, true);
                        Workspace.Release(buildId);
                        freed += size;
                        output.Add($"Removed workspace {buildId} ({size / 1024 / 1024} MB)");
                    }
                _ = busy;
            }
            if (request.DockerPrune && ProcessRunner.Exists("docker"))
                output.Add(await ProcessRunner.CaptureAsync("docker", ["system", "prune", "-af", "--filter", "until=24h"],
                    _options.WorkRoot, null, CancellationToken.None));
        }
        catch (Exception ex)
        {
            output.Add("Cleanup error: " + ex.Message);
        }
        _log.LogInformation("Cleanup freed {MB} MB", freed / 1024 / 1024);
        await _hub.SendAsync(AgentHubNames.CleanupCompleted, new CleanupResult(request.RequestId, freed, string.Join("\n", output)));
    }

    private async Task TeardownAsync(TeardownRequest request)
    {
        TeardownResult result;
        try
        {
            var temp = Path.Combine(_options.WorkRoot, "tmp", request.DeploymentId.ToString("N"));
            try
            {
                var secrets = await _hub.InvokeAsync<DeploySecrets>(AgentHubNames.GetTeardownCredentials, request.DeploymentId);
                var deployer = new Deployer(request.Target, secrets, temp, _options.WorkRoot);
                var (ok, output) = await deployer.ActionAsync(request.Action, _stopping.Token);
                result = new TeardownResult(request.DeploymentId, ok, output, request.Action);
            }
            finally
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, true); // key, kubeconfig, az login state
            }
        }
        catch (Exception ex)
        {
            result = new TeardownResult(request.DeploymentId, false, ex.Message, request.Action);
        }
        await _hub.InvokeAsync(AgentHubNames.TeardownCompleted, result);
    }

    private static long DirectorySize(string dir) =>
        new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => { try { return f.Length; } catch { return 0L; } });

    public override async Task StopAsync(CancellationToken ct)
    {
        await _stopping.CancelAsync();
        await base.StopAsync(ct);
        await _hub.DisposeAsync();
    }

    private sealed class ForeverRetry : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context) =>
            TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(context.PreviousRetryCount, 5))));
    }
}
