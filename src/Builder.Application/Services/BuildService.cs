using Builder.Application.Abstractions;
using Builder.Application.Dtos;
using Builder.Domain.Builds;
using Microsoft.EntityFrameworkCore;

namespace Builder.Application.Services;

public sealed class BuildService(
    IAppDbContext db,
    IClock clock,
    IBuildLock buildLock,
    IAgentGateway agents,
    IArtifactStore artifactStore,
    ISchedulerSignal scheduler,
    BuildEvents events)
{
    public async Task<BuildSummaryDto> QueueAsync(Guid pipelineId, QueueBuildInput input, string user, CancellationToken ct,
        string? commit = null, BuildReason reason = BuildReason.Manual, string? sourceRef = null, int? pullRequestId = null)
    {
        var pipeline = await db.Pipelines.FirstOrDefaultAsync(p => p.Id == pipelineId, ct)
            ?? throw new NotFoundException("Runner");
        var defaultBranch = await db.Repositories.Where(r => r.Id == pipeline.RepositoryId).Select(r => r.DefaultBranch).FirstAsync(ct);
        var vars = (input.Variables ?? new())
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value ?? "");
        var build = Build.Queue(pipeline.OrgId, pipeline.ProjectId, pipeline.Id, pipeline.NextBuildNumber(),
            string.IsNullOrWhiteSpace(input.Branch) ? defaultBranch : input.Branch.Trim(),
            string.IsNullOrWhiteSpace(input.EntryTask) ? null : input.EntryTask.Trim(),
            vars, user, clock.UtcNow, commit, reason, sourceRef, pullRequestId);
        db.Builds.Add(build);
        await db.SaveChangesAsync(ct);
        scheduler.Wake();
        await events.PublishAsync(build, [], ct);
        return build.ToSummary(pipeline.Name);
    }

    public async Task<BuildSummaryDto> RerunAsync(Guid buildId, string user, CancellationToken ct)
    {
        var old = await db.Builds.AsNoTracking().FirstOrDefaultAsync(b => b.Id == buildId, ct)
            ?? throw new NotFoundException("Build");
        return await QueueAsync(old.PipelineId, new QueueBuildInput(old.Branch, old.EntryTask, old.Variables), user, ct, old.Commit,
            BuildReason.Rerun, old.SourceRef, old.PullRequestId);
    }

    public async Task<List<BuildSummaryDto>> ListAsync(Guid? pipelineId, BuildStatus? status, int take, CancellationToken ct, Guid? projectId = null)
    {
        var q = db.Builds.AsNoTracking().Include(b => b.Jobs).AsQueryable();
        if (projectId is { } prj) q = q.Where(b => b.ProjectId == prj);
        if (pipelineId is { } pid) q = q.Where(b => b.PipelineId == pid);
        if (status is { } s) q = q.Where(b => b.Status == s);
        var builds = await q.OrderByDescending(b => b.QueuedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        var names = await PipelineNamesAsync(ct);
        return builds.Select(b => b.ToSummary(names.GetValueOrDefault(b.PipelineId, "?"))).ToList();
    }

    public async Task<BuildDetailDto> GetAsync(Guid buildId, CancellationToken ct)
    {
        var build = await db.Builds.AsNoTracking().Include(b => b.Jobs).FirstOrDefaultAsync(b => b.Id == buildId, ct)
            ?? throw new NotFoundException("Build");
        var pipelineName = await db.Pipelines.Where(p => p.Id == build.PipelineId).Select(p => p.Name).FirstOrDefaultAsync(ct) ?? "?";
        var artifacts = await db.Artifacts.AsNoTracking().Where(a => a.BuildId == buildId).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        var deployments = await db.Deployments.AsNoTracking().Where(d => d.BuildId == buildId).OrderBy(d => d.CreatedAt).ToListAsync(ct);
        return build.ToDetail(pipelineName, artifacts.Select(a => a.ToDto()).ToList(),
            deployments.Select(d => d.ToDto(pipelineName)).ToList());
    }

    public async Task<BuildSummaryDto> CancelAsync(Guid buildId, CancellationToken ct)
    {
        Build build;
        List<BuildJob> running;
        using (await buildLock.AcquireAsync(ct))
        {
            build = await db.Builds.Include(b => b.Jobs).FirstOrDefaultAsync(b => b.Id == buildId, ct)
                ?? throw new NotFoundException("Build");
            var before = build.Jobs.ToDictionary(j => j.Id, j => j.Status);
            running = build.Cancel(clock.UtcNow);
            await db.SaveChangesAsync(ct);
            await events.PublishAsync(build, build.Jobs.Where(j => before[j.Id] != j.Status), ct);
        }
        foreach (var job in running.Where(j => j.AgentId is not null))
            await agents.CancelJobAsync(job.AgentId!.Value, job.Id, ct);
        var name = await db.Pipelines.Where(p => p.Id == build.PipelineId).Select(p => p.Name).FirstAsync(ct);
        return build.ToSummary(name);
    }

    public async Task<JobDto> DecideApprovalAsync(Guid buildId, Guid jobId, ApprovalInput input, string user, CancellationToken ct)
    {
        using (await buildLock.AcquireAsync(ct))
        {
            var build = await db.Builds.Include(b => b.Jobs).FirstOrDefaultAsync(b => b.Id == buildId, ct)
                ?? throw new NotFoundException("Build");
            var before = build.Jobs.ToDictionary(j => j.Id, j => j.Status);
            build.Approve(jobId, user, input.Approved, input.Comment, clock.UtcNow);
            await db.SaveChangesAsync(ct);
            await events.PublishAsync(build, build.Jobs.Where(j => before[j.Id] != j.Status), ct);
            scheduler.Wake();
            return build.Job(jobId).ToDto();
        }
    }

    public async Task DeleteAsync(Guid buildId, CancellationToken ct)
    {
        var build = await db.Builds.FirstOrDefaultAsync(b => b.Id == buildId, ct) ?? throw new NotFoundException("Build");
        if (!build.IsFinished) throw new Domain.DomainException("Cancel the build before deleting it.");
        await DeleteBuildsAsync([buildId], ct);
    }

    /// <summary>Deletes builds with their jobs, logs and artifacts (DB cascade + files).</summary>
    public async Task<(int Artifacts, long Bytes)> DeleteBuildsAsync(IReadOnlyCollection<Guid> buildIds, CancellationToken ct)
    {
        if (buildIds.Count == 0) return (0, 0);
        var artifacts = await db.Artifacts.Where(a => buildIds.Contains(a.BuildId)).ToListAsync(ct);
        await db.LogLines.Where(l => buildIds.Contains(l.BuildId)).ExecuteDeleteAsync(ct);
        await db.Artifacts.Where(a => buildIds.Contains(a.BuildId)).ExecuteDeleteAsync(ct);
        await db.BuildJobs.Where(j => buildIds.Contains(j.BuildId)).ExecuteDeleteAsync(ct);
        await db.Builds.Where(b => buildIds.Contains(b.Id)).ExecuteDeleteAsync(ct);
        foreach (var id in buildIds) artifactStore.DeleteBuild(id);
        return (artifacts.Count, artifacts.Sum(a => a.SizeBytes));
    }

    public async Task<List<LogLineDto>> LogsAsync(Guid buildId, Guid jobId, long after, CancellationToken ct) =>
        !await db.Builds.AnyAsync(b => b.Id == buildId, ct) ? throw new NotFoundException("Build") : // org check
        (await db.LogLines.AsNoTracking()
            .Where(l => l.BuildId == buildId && l.JobId == jobId && l.Id > after)
            .OrderBy(l => l.Id).Take(5000).ToListAsync(ct))
        .Select(l => l.ToDto()).ToList();

    public async Task<(Stream Content, string FileName)> OpenArtifactAsync(Guid artifactId, CancellationToken ct)
    {
        var artifact = await db.Artifacts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == artifactId, ct)
            ?? throw new NotFoundException("Artifact");
        if (!await db.Builds.AnyAsync(b => b.Id == artifact.BuildId, ct)) throw new NotFoundException("Artifact"); // org check
        return (artifactStore.OpenRead(artifact.StoragePath), artifact.Name + ".tar.gz");
    }

    private Task<Dictionary<Guid, string>> PipelineNamesAsync(CancellationToken ct) =>
        db.Pipelines.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
}
