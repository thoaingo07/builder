using Builder.Application.Abstractions;
using Builder.Application.Dtos;
using Builder.Domain.Builds;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Builder.Application.Services;

/// <summary>Pushes build/job changes to the UI after they are saved.</summary>
public sealed class BuildEvents(IAppDbContext db, IUiNotifier ui, IAgentGateway agents, GitRemotes remotes, IGitHostStatus status,
    BuilderLinks links, ReportedStatuses reported, ILogger<BuildEvents> log)
{
    public async Task PublishAsync(Build build, IEnumerable<BuildJob> changedJobs, CancellationToken ct = default)
    {
        var pipelineName = await db.Pipelines.AsNoTracking().IgnoreQueryFilters()
            .Where(p => p.Id == build.PipelineId).Select(p => p.Name).FirstOrDefaultAsync(ct) ?? "?";
        foreach (var job in changedJobs.DistinctBy(j => j.Id))
            await ui.JobUpdated(job.ToDto());
        await ui.BuildUpdated(build.OrgId, build.ToSummary(pipelineName));

        await ReportStatusAsync(build, pipelineName, ct);

        // finished: the agents that ran it delete its checkout
        if (build.IsFinished)
            foreach (var agentId in build.Jobs.Where(j => j.AgentId is not null).Select(j => j.AgentId!.Value).Distinct())
                await agents.ReleaseBuildAsync(agentId, build.Id, ct);
    }

    /// <summary>Pending once the commit is known, then the final result, each reported once to the git host.</summary>
    private async Task ReportStatusAsync(Build build, string runner, CancellationToken ct)
    {
        if (build.Commit is null) return;
        var (state, description) = build.Status switch
        {
            BuildStatus.Succeeded => ("succeeded", $"#{build.Number} succeeded"),
            BuildStatus.Failed => ("failed", $"#{build.Number} failed{(build.Error is null ? "" : ": " + build.Error)}"),
            BuildStatus.Canceled => ("error", $"#{build.Number} canceled"),
            _ => ("pending", $"#{build.Number} running"),
        };
        if (!reported.TryMark(build.Id, state)) return;
        try
        {
            var pipeline = await db.Pipelines.AsNoTracking().IgnoreQueryFilters().FirstAsync(p => p.Id == build.PipelineId, ct);
            var (remote, _) = await remotes.ForPipelineAsync(pipeline, ct);
            await status.ReportAsync(remote, new BuildStatusReport(build.Commit, build.PullRequestId, $"builder/{runner}", state,
                description.Length > 200 ? description[..200] : description, links.BuildUrl(build.Id)), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Could not report status of build {Build} to the git host: {Error}", build.Id, ex.Message);
        }
    }
}

/// <summary>Which (build, state) pairs were already reported, so each state is sent once per API process.</summary>
public sealed class ReportedStatuses
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _states = new();
    public bool TryMark(Guid buildId, string state) =>
        _states.TryGetValue(buildId, out var old) ? old != state && _states.TryUpdate(buildId, state, old) : _states.TryAdd(buildId, state);
}
