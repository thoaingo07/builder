using System.Net.Http.Headers;
using Builder.Contracts;

namespace Builder.Agent;

/// <summary>Runs one job: checkout → download artifacts → go-task → built-in deploy → upload artifacts.</summary>
public sealed class JobRunner(AgentOptions options, HttpClient http, IServerChannel server, ILogger<JobRunner> logger)
{
    public async Task<JobResult> RunAsync(JobAssignment job, CancellationToken ct)
    {
        var workspace = new Workspace(options.WorkRoot, job.BuildId);
        var deployer = job.Deploy is { } target ? new Deployer(target, Path.Combine(workspace.Temp, job.JobId.ToString("N")), options.WorkRoot) : null;
        await using var log = new JobLog(lines => server.SendLogAsync(job.JobId, lines),
            [job.Source.AuthorizationHeader, job.Deploy?.PrivateKey, job.Deploy?.AksClientSecret, job.Deploy?.Kubeconfig]);
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

            await workspace.CheckoutAsync(job.Source, log, ct);
            // runners run from the repository root (go-task --dir), so artifact globs are repository-relative too
            var taskDir = workspace.Source;

            foreach (var artifact in job.DownloadArtifacts)
            {
                log.System($"Downloading artifact '{artifact.Name}'");
                await using var stream = await http.GetStreamAsync(artifact.DownloadPath, ct);
                await workspace.UnpackAsync(stream, taskDir, ct);
            }

            var env = new Dictionary<string, string>(job.Env);
            if (deployer is not null)
            {
                await deployer.PrepareAsync(log, ct);
                foreach (var (k, v) in deployer.Environment) env[k] = v;
            }

            derivedTaskfile = workspace.PrepareTaskfile(job.TaskfilePath, job.TaskName, job.JobId);
            List<string> args = ["--dir", workspace.Source, "--taskfile", derivedTaskfile, "--yes", "--color=false", job.TaskName];
            args.AddRange(job.TaskVars.Select(kv => $"{kv.Key}={kv.Value}"));
            // secrets: go-task vars ({{.NAME}}) and environment variables ($NAME)
            args.AddRange(secrets.Select(kv => $"{kv.Key}={kv.Value}"));
            foreach (var (k, v) in secrets) env[k] = v;
            env["NO_COLOR"] = "1";

            // go-task echoes each command as "task: [name] cmd" on stderr; show those as step markers, not errors
            var exit = await ProcessRunner.RunAsync(options.TaskBinary, args, workspace.Source, env,
                (stream, line) => log.Write(line.StartsWith("task: ", StringComparison.Ordinal) ? LogStream.System : stream, line), ct);
            if (exit != 0) return Fail(log, job, exit, $"task '{job.TaskName}' exited with code {exit}");

            if (deployer is not null)
            {
                exit = await deployer.DeployAsync(workspace.Source, log, ct);
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
}
