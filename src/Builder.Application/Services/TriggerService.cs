using System.Security.Cryptography;
using System.Text;
using Builder.Application.Abstractions;
using Builder.Application.Dtos;
using Builder.Domain;
using Builder.Domain.Builds;
using Builder.Domain.Connections;
using Builder.Domain.Organizations;
using Builder.Domain.Pipelines;
using Builder.Domain.Repositories;
using Builder.Domain.Triggers;
using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Builder.Application.Services;

/// <summary>
/// Starts builds from outside events: pushes and pull requests (Azure DevOps service hooks) and schedules. The
/// runner file at the pushed commit decides (x-builder.triggers), so trigger changes take effect with the commit
/// that makes them. Schedules are read from the default branch and refreshed on every push there.
/// </summary>
public sealed class TriggerService(
    IAppDbContext db,
    IClock clock,
    ICurrentOrg current,
    IGitService git,
    ITaskfilePlanner planner,
    GitRemotes remotes,
    BuildService builds,
    IAzureDevOpsClient azureDevOps,
    BuilderLinks links,
    ILogger<TriggerService> log)
{
    public const string HookHeader = "X-Builder-Hook";

    // ---------------------------------------------------------------- webhooks: secret + installation

    /// <summary>A new webhook secret for the repository (shown once) and where Azure DevOps must post.</summary>
    public async Task<HookSetupDto> NewHookSecretAsync(Guid repositoryId, string? origin, CancellationToken ct)
    {
        current.RequireRole(OrgRole.Admin);
        var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Id == repositoryId, ct) ?? throw new NotFoundException("Repository");
        var secret = "bhk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        repo.SetHookSecretHash(Hash(secret));
        await db.SaveChangesAsync(ct);
        return new HookSetupDto(HookUrl(repositoryId, origin), HookHeader, secret, null);
    }

    /// <summary>Rotates the secret and creates the service hooks in Azure DevOps through the repository's connection.</summary>
    public async Task<HookSetupDto> InstallHooksAsync(Guid repositoryId, string? origin, CancellationToken ct)
    {
        current.RequireRole(OrgRole.Admin);
        var repo = await db.Repositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == repositoryId, ct) ?? throw new NotFoundException("Repository");
        var connection = repo.ConnectionId is { } cid ? await db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct) : null;
        if (connection is not { Type: ConnectionType.AzureDevOps })
            throw new DomainException("Automatic installation needs an Azure DevOps connection; set the webhook up by hand instead.");
        var coords = AzureDevOpsCoordinates(repo.Url) ?? throw new DomainException("The repository URL is not an Azure DevOps Git URL.");
        var url = HookUrl(repositoryId, origin);
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new DomainException($"Azure DevOps can only reach a public https URL; Builder is at {url}. Set Builder:PublicUrl.");

        var setup = await NewHookSecretAsync(repositoryId, origin, ct);
        var authorization = await remotes.AuthorizationAsync(connection, ct) ?? throw new DomainException("The connection has no credentials.");
        var count = await azureDevOps.InstallWebhooksAsync(connection.Url, authorization, coords.Project, coords.Repository, url, HookHeader, setup.Secret!, ct);
        return setup with { Secret = null, Installed = count };
    }

    /// <summary>Checks a webhook call's secret against the repository (constant time).</summary>
    public async Task<Repository?> VerifyHookAsync(Guid repositoryId, string? presented, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(presented)) return null;
        var repo = await db.Repositories.AsNoTracking().IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
        if (repo?.HookSecretHash is null) return null;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(repo.HookSecretHash), Encoding.ASCII.GetBytes(Hash(presented))) ? repo : null;
    }

    // ---------------------------------------------------------------- push / pull request

    public async Task<List<Guid>> OnPushAsync(Repository repo, string branch, string before, string after, string pushedBy, CancellationToken ct)
    {
        var remote = await remotes.ForRepositoryAsync(repo, ct);
        var changed = await ChangedAsync(remote, branch, before, after, ct);
        var started = new List<Guid>();
        foreach (var runner in await RunnersAsync(repo, ct))
        {
            var spec = await TriggersAtAsync(remote, runner, branch, after, ct);
            if (branch == repo.DefaultBranch) await StoreTriggersAsync(runner, spec, after, ct);
            if (spec.Spec?.Push is not { } push || !Glob.Matches(push.Branches, branch) || !new Filter([], push.Paths).MatchesPaths(changed))
                continue;
            if (await AlreadyBuiltAsync(runner.Id, after, branch, BuildReason.Push, ct)) continue;
            started.Add(await QueueAsync(runner, branch, after, push.Vars, $"push by {pushedBy}", BuildReason.Push, null, null, ct));
        }
        return started;
    }

    public async Task<List<Guid>> OnPullRequestAsync(Repository repo, int pullRequestId, string sourceBranch, string targetBranch,
        string sourceCommit, string? mergeCommit, string author, CancellationToken ct)
    {
        var remote = await remotes.ForRepositoryAsync(repo, ct);
        var commit = mergeCommit ?? sourceCommit;
        IReadOnlyList<string>? changed = null;
        try
        {
            var targetHead = await git.ResolveBranchAsync(remote, targetBranch, ct);
            changed = await ChangedAsync(remote, sourceBranch, targetHead, sourceCommit, ct);
        }
        catch (Exception ex) when (ex is ExternalServiceException or DomainException) { /* no path filtering */ }

        var started = new List<Guid>();
        foreach (var runner in await RunnersAsync(repo, ct))
        {
            var spec = await TriggersAtAsync(remote, runner, sourceBranch, sourceCommit, ct);
            if (spec.Spec?.PullRequest is not { } pr || !Glob.Matches(pr.Branches, targetBranch) || !new Filter([], pr.Paths).MatchesPaths(changed))
                continue;
            if (await AlreadyBuiltAsync(runner.Id, commit, sourceBranch, BuildReason.PullRequest, ct)) continue; // PR updated without new commits
            started.Add(await QueueAsync(runner, sourceBranch, commit, pr.Vars, $"PR !{pullRequestId} by {author}", BuildReason.PullRequest,
                mergeCommit is null ? null : $"refs/pull/{pullRequestId}/merge", pullRequestId, ct));
        }
        return started;
    }

    // ---------------------------------------------------------------- schedules and trigger refresh

    /// <summary>Re-reads the runner file on the default branch and replaces its schedules.</summary>
    public async Task<PipelineTriggersDto> RefreshAsync(Guid pipelineId, CancellationToken ct)
    {
        var runner = await db.Pipelines.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == pipelineId
            && (current.OrgId == null || p.OrgId == current.OrgId), ct) ?? throw new NotFoundException("Runner");
        var repo = await db.Repositories.AsNoTracking().IgnoreQueryFilters().FirstAsync(r => r.Id == runner.RepositoryId, ct);
        var remote = await remotes.ForRepositoryAsync(repo, ct);
        var head = await git.ResolveBranchAsync(remote, repo.DefaultBranch, ct);
        await StoreTriggersAsync(runner, await TriggersAtAsync(remote, runner, repo.DefaultBranch, head, ct), head, ct);
        return await TriggersOfAsync(pipelineId, ct);
    }

    public async Task<PipelineTriggersDto> TriggersOfAsync(Guid pipelineId, CancellationToken ct)
    {
        var runner = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pipelineId, ct) ?? throw new NotFoundException("Runner");
        var schedules = await db.RunnerSchedules.AsNoTracking().Where(s => s.PipelineId == pipelineId).OrderBy(s => s.NextRunAt).ToListAsync(ct);
        return new PipelineTriggersDto(runner.Triggers ?? TriggerSpec.None, runner.TriggersCommit, runner.TriggersError,
            schedules.Select(s => new ScheduleDto(s.Cron, s.TimeZone, s.Branch, s.NextRunAt, s.LastRunAt)).ToList());
    }

    /// <summary>Queues every schedule that is due; returns how many builds were started.</summary>
    public async Task<int> RunDueSchedulesAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var due = await db.RunnerSchedules.IgnoreQueryFilters().Where(s => s.NextRunAt != null && s.NextRunAt <= now).ToListAsync(ct);
        var started = 0;
        foreach (var schedule in due)
        {
            var runner = await db.Pipelines.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == schedule.PipelineId, ct);
            schedule.Ran(now, Next(schedule.Cron, schedule.TimeZone, now));
            await db.SaveChangesAsync(ct);
            if (runner is null) continue;
            var repo = await db.Repositories.AsNoTracking().IgnoreQueryFilters().FirstAsync(r => r.Id == runner.RepositoryId, ct);
            try
            {
                await QueueAsync(runner, string.IsNullOrEmpty(schedule.Branch) ? repo.DefaultBranch : schedule.Branch, null, schedule.Vars,
                    $"schedule {schedule.Cron}", BuildReason.Schedule, null, null, ct);
                started++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Scheduled build of {Runner} failed to queue", runner.Name);
            }
        }
        return started;
    }

    /// <summary>Hourly safety net: re-read triggers of every runner (catches pushes made while webhooks were not set up).</summary>
    public async Task RefreshAllAsync(CancellationToken ct)
    {
        foreach (var id in await db.Pipelines.IgnoreQueryFilters().Select(p => p.Id).ToListAsync(ct))
        {
            try { await RefreshAsync(id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug(ex, "Refreshing triggers of runner {Runner} failed", id); }
        }
    }

    public static DateTimeOffset? Next(string cron, string timeZone, DateTimeOffset after)
    {
        var expression = CronExpression.Parse(cron, cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
        // in UTC: Cronos answers in the schedule's own offset, and timestamptz columns only take UTC
        return expression.GetNextOccurrence(after, TimeZoneInfo.FindSystemTimeZoneById(timeZone))?.ToUniversalTime();
    }

    // ---------------------------------------------------------------- helpers

    private async Task<(TriggerSpec? Spec, string? Error)> TriggersAtAsync(GitRemote remote, Pipeline runner, string branch, string commit, CancellationToken ct)
    {
        var yaml = await git.ReadFileAsync(remote, branch, commit, runner.TaskfilePath, ct);
        if (yaml is null) return (null, $"{runner.TaskfilePath} does not exist on {branch}");
        try { return (planner.Triggers(yaml), null); }
        catch (TaskfileException ex) { return (null, ex.Message); }
    }

    private async Task StoreTriggersAsync(Pipeline runner, (TriggerSpec? Spec, string? Error) found, string commit, CancellationToken ct)
    {
        runner.TriggersRead(found.Spec, commit, found.Error);
        var old = await db.RunnerSchedules.IgnoreQueryFilters().Where(s => s.PipelineId == runner.Id).ToListAsync(ct);
        db.RunnerSchedules.RemoveRange(old);
        foreach (var s in found.Spec?.Schedules ?? [])
            db.RunnerSchedules.Add(new RunnerSchedule(runner.OrgId, runner.Id, s, Next(s.Cron, s.TimeZone, clock.UtcNow)));
        await db.SaveChangesAsync(ct);
    }

    private async Task<IReadOnlyList<string>?> ChangedAsync(GitRemote remote, string branch, string before, string after, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(before) || before.Trim('0').Length == 0) return null; // new branch
        try { return await git.ChangedFilesAsync(remote, branch, before, after, ct); }
        catch (Exception ex) when (ex is ExternalServiceException or InvalidOperationException) { return null; }
    }

    private async Task<List<Pipeline>> RunnersAsync(Repository repo, CancellationToken ct) =>
        await db.Pipelines.IgnoreQueryFilters().Where(p => p.RepositoryId == repo.Id && p.OrgId == repo.OrgId).ToListAsync(ct);

    private Task<bool> AlreadyBuiltAsync(Guid runnerId, string commit, string branch, BuildReason reason, CancellationToken ct) =>
        db.Builds.IgnoreQueryFilters().AnyAsync(b => b.PipelineId == runnerId && b.Commit == commit && b.Branch == branch && b.Reason == reason, ct);

    private async Task<Guid> QueueAsync(Pipeline runner, string branch, string? commit, Dictionary<string, string> vars, string requestedBy,
        BuildReason reason, string? sourceRef, int? pullRequestId, CancellationToken ct)
    {
        var build = await builds.QueueAsync(runner.Id, new QueueBuildInput(branch, null, vars), requestedBy, ct, commit, reason, sourceRef, pullRequestId);
        log.LogInformation("{Reason} build #{Number} of {Runner} on {Branch} ({By})", reason, build.Number, runner.Name, branch, requestedBy);
        return build.Id;
    }

    private string HookUrl(Guid repositoryId, string? origin) =>
        $"{(links.PublicUrl ?? origin ?? "").TrimEnd('/')}/hooks/azure-devops/{repositoryId}";

    private static AzureDevOpsRepo? AzureDevOpsCoordinates(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var parts = u.AbsolutePath.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        var git = Array.IndexOf(parts, "_git");
        if (git < 1 || git + 1 >= parts.Length) return null;
        var repo = parts[git + 1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[git + 1][..^4] : parts[git + 1];
        var project = u.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) ? (git >= 2 ? parts[git - 1] : repo) : parts[git - 1];
        return new AzureDevOpsRepo(project, repo);
    }

    private sealed record AzureDevOpsRepo(string Project, string Repository);

    private static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
