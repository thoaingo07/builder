using System.Net.Http.Headers;
using Builder.Contracts;

namespace Builder.Agent;

/// <summary>Runs one job: checkout → download artifacts → go-task → built-in deploy → upload artifacts.</summary>
public sealed class JobRunner(AgentOptions options, HttpClient http, IServerChannel server, ILogger<JobRunner> logger)
{
    public async Task<JobResult> RunAsync(JobAssignment job, CancellationToken ct)
    {
        var workspace = new Workspace(options.WorkRoot, job.BuildId);
        using var sandbox = JobSandbox.Create(workspace, job.JobId, options.WorkRoot, options.SharedPackageCaches);
        Deployer? deployer = null;
        await using var log = new JobLog(lines => server.SendLogAsync(job.JobId, lines),
            []);
        var secrets = new Dictionary<string, string>();
        string? derivedTaskfile = null;
        try
        {
            await server.JobStartedAsync(job.JobId);
            log.System($"Agent {options.EffectiveName} running '{job.TaskName}' of {job.PipelineName} #{job.BuildNumber}");
            if (job.Secrets.Length > 0)
            {
                // fetched at run time, only for this job; masked in everything we log from here on
                secrets = await server.GetJobSecretsAsync(job.JobId);
                log.AddSecrets(secrets.Values);
                log.System($"Secrets: {string.Join(", ", secrets.Keys.Order())}");
            }

            // short-lived credentials for this job only: git, registries, Azure DevOps (kept in memory, masked)
            var credentials = await server.GetJobCredentialsAsync(job.JobId);
            log.AddSecrets([credentials.GitAuthorization ?? "", credentials.AzureDevOpsToken ?? "",
                .. credentials.Registries.Select(r => r.Password),
                credentials.Deploy?.PrivateKey ?? "", credentials.Deploy?.Kubeconfig ?? "", credentials.Deploy?.AksClientSecret ?? ""]);
            if (job.Deploy is { } target)
                deployer = new Deployer(target, credentials.Deploy ?? new DeploySecrets(null, null, null), Path.Combine(sandbox.Root, "deploy"), options.WorkRoot);
            if (credentials.ExpiresAt is { } expires)
                log.System($"Credentials valid until {expires:u}");

            await workspace.CheckoutAsync(job.Source, credentials.GitAuthorization, log, ct);
            // runners run from the repository root (go-task --dir), so artifact globs are repository-relative too
            var taskDir = workspace.Source;

            foreach (var artifact in job.DownloadArtifacts)
            {
                log.System($"Downloading artifact '{artifact.Name}'");
                await using var stream = await http.GetStreamAsync(artifact.DownloadPath, ct);
                await workspace.UnpackAsync(stream, taskDir, ct);
            }

            var env = new Dictionary<string, string>(sandbox.Environment);
            foreach (var (k, v) in job.Env) env[k] = v;
            if (deployer is not null)
            {
                await deployer.PrepareAsync(log, ct);
                foreach (var (k, v) in deployer.Environment) env[k] = v;
            }

            foreach (var registry in credentials.Registries)
            {
                // into the job sandbox's DOCKER_CONFIG, password on stdin: gone when the job ends
                log.System($"docker login {registry.Server}");
                var login = await ProcessRunner.RunAsync("docker", ["login", registry.Server, "--username", registry.Username, "--password-stdin"],
                    workspace.Source, env, log.Write, ct, stdin: registry.Password);
                if (login != 0) return Fail(log, job, login, $"docker login {registry.Server} failed");
            }
            if (credentials.AzureDevOpsToken is { } adoToken)
            {
                env["AZURE_DEVOPS_TOKEN"] = adoToken;      // for npm/pip/your own scripts
                env["VSS_NUGET_ACCESSTOKEN"] = adoToken;   // Azure Artifacts Credential Provider (NuGet restore)
                env["SYSTEM_ACCESSTOKEN"] = adoToken;      // same name Azure Pipelines uses
            }

            // per-job random marker: a script printing "::builder-step::" can't move the step pointer
            // step reports go out in order and are all delivered before the job's result
            var stepReports = Task.CompletedTask;
            var marker = $"::builder-step::{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8))}::";
            derivedTaskfile = workspace.PrepareTaskfile(job.TaskfilePath, job.TaskName, job.JobId, marker);
            List<string> args = ["--dir", workspace.Source, "--taskfile", derivedTaskfile, "--yes", "--color=false", job.TaskName];
            args.AddRange(job.TaskVars.Select(kv => $"{kv.Key}={kv.Value}"));
            // secrets only through the environment: go-task exposes them as {{.NAME}} too, and they never show up in `ps`
            foreach (var (k, v) in secrets) env[k] = v;
            env["NO_COLOR"] = "1";

            // go-task echoes each command as "task: [name] cmd" on stderr; show those as step markers, not errors
            var exit = await ProcessRunner.RunAsync(options.TaskBinary, args, workspace.Source, env,
                (stream, line) =>
                {
                    if (stream == LogStream.Out && line.StartsWith(marker, StringComparison.Ordinal)
                        && int.TryParse(line.AsSpan(marker.Length), out var step))
                    {
                        log.Step = step;
                        stepReports = stepReports.ContinueWith(_ => server.StepStartedAsync(job.JobId, step), TaskScheduler.Default).Unwrap();
                        return;
                    }
                    log.Write(line.StartsWith("task: ", StringComparison.Ordinal) ? LogStream.System : stream, line);
                }, ct);
            try { await stepReports; } catch { /* best effort: the build still finishes */ }
            if (exit != 0) return Fail(log, job, exit, $"task '{job.TaskName}' exited with code {exit}");

            if (deployer is not null)
            {
                var deployVars = new Dictionary<string, string>(job.TaskVars);
                foreach (var (k, v) in env) deployVars[k] = v;
                exit = await deployer.DeployAsync(workspace.Source, log, ct, deployVars);
                if (exit != 0) return Fail(log, job, exit, $"Deploy to '{job.Deploy!.EnvironmentName}' failed (exit {exit})");
                if (job.Deploy!.Url is { } url) log.System($"Deployed: {url}");
            }

            if (job.UploadArtifacts.Length > 0)
            {
                var name = string.Concat(job.TaskName.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
                var (path, count) = await workspace.PackAsync(job.UploadArtifacts, name, taskDir, ct);
                if (count == 0) log.Err($"No files matched artifacts [{string.Join(", ", job.UploadArtifacts)}]");
                else
                {
                    log.System($"Uploading artifact '{name}' ({count} files)");
                    await using var file = File.OpenRead(path);
                    using var content = new StreamContent(file);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
                    using var response = await http.PostAsync($"api/agent/jobs/{job.JobId}/artifacts?name={Uri.EscapeDataString(name)}", content, ct);
                    response.EnsureSuccessStatusCode();
                }
                File.Delete(path);
            }

            log.System($"'{job.TaskName}' succeeded");
            return new JobResult(job.JobId, true, 0, null, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log.System("Canceled");
            return new JobResult(job.JobId, false, -1, "Canceled", true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Job {Job} failed", job.JobId);
            return Fail(log, job, -1, ex.Message);
        }
        finally
        {
            deployer?.Cleanup();
            if (derivedTaskfile is not null) try { File.Delete(derivedTaskfile); } catch { /* ignore */ }
        }
    }

    private static JobResult Fail(JobLog log, JobAssignment job, int exitCode, string error)
    {
        log.Err(error);
        return new JobResult(job.JobId, false, exitCode, error, false);
    }
}

/// <summary>What the job runner needs from the server connection.</summary>
public interface IServerChannel
{
    Task JobStartedAsync(Guid jobId);
    Task SendLogAsync(Guid jobId, List<LogChunk> lines);
    Task<Dictionary<string, string>> GetJobSecretsAsync(Guid jobId);
    Task<JobCredentials> GetJobCredentialsAsync(Guid jobId);
    Task StepStartedAsync(Guid jobId, int index);
}
