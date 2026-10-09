using Builder.Application.Abstractions;
using Builder.Application.Dtos;
using Builder.Contracts;
using Builder.Domain;
using Builder.Domain.Agents;
using Builder.Domain.Builds;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Builder.Application.Services;

/// <summary>Handles everything agents report, plus agent management from the UI.</summary>
public sealed class AgentService(
    IAppDbContext db,
    IClock clock,
    IBuildLock buildLock,
    IAgentGateway gateway,
    ICurrentOrg current,
    IAgentMetricsStore metrics,
    IArtifactStore artifactStore,
    IUiNotifier ui,
    ISchedulerSignal scheduler,
    BuildEvents events,
    ILogger<AgentService> log)
{
    /// <summary>How long an agent may be disconnected before its jobs are failed.</summary>
    public static readonly TimeSpan LostAfter = TimeSpan.FromSeconds(60);

    /// <param name="orgId">The organization of the agent's token; null for the shared (system) token.</param>
    public async Task<AgentWelcome> RegisterAsync(string connectionId, Guid? orgId, AgentHello hello, CancellationToken ct)
    {
        var name = hello.Name.Trim();
        if (name.Length == 0) throw new DomainException("Agent name is required.");

        var labels = hello.Labels.ToList();
        if (hello.HasTask) labels.Add("task");
        if (hello.HasDocker) labels.Add("docker");
        if (hello.HasKubectl) labels.Add("kubectl");
        if (hello.HasAz) labels.Add("az");
        if (hello.HasSsh) labels.Add("ssh");

        Guid[] abort;
        Agent agent;
        using (await buildLock.AcquireAsync(ct))
        {
            agent = await db.Agents.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Name == name && a.OrgId == orgId, ct) ?? AddAgent(name);
            agent.Connected(connectionId, hello.HostName, hello.Os, hello.Version, hello.Capacity, labels, clock.UtcNow);

            // jobs the server thinks run on this agent but the agent no longer knows about
            var orphaned = await db.BuildJobs
                .Where(j => j.AgentId == agent.Id && (j.Status == JobStatus.Assigned || j.Status == JobStatus.Running))
                .Select(j => j.Id).ToListAsync(ct);
            var lost = orphaned.Except(hello.RunningJobIds).ToList();
            await db.SaveChangesAsync(ct);
            foreach (var jobId in lost)
                await CompleteJobLockedAsync(new JobResult(jobId, false, -1, "Agent restarted while the job was running", false), ct);

            // jobs the agent still runs but the server finished (e.g. canceled while disconnected)
            var known = await db.BuildJobs
                .Where(j => hello.RunningJobIds.Contains(j.Id) && j.AgentId == agent.Id
                            && (j.Status == JobStatus.Assigned || j.Status == JobStatus.Running))
                .Select(j => j.Id).ToListAsync(ct);
            abort = hello.RunningJobIds.Except(known).ToArray();
        }
        log.LogInformation("Agent {Agent} connected ({Host}, {Os}) labels=[{Labels}]", name, hello.HostName, hello.Os, string.Join(",", agent.Labels));
        scheduler.Wake();
        await PublishAgentsAsync(ct);
        return new AgentWelcome(agent.Id, abort);

        Agent AddAgent(string n)
        {
            var a = new Agent(orgId, n, clock.UtcNow);
            db.Agents.Add(a);
            return a;
        }
    }

    public async Task DisconnectedAsync(Guid agentId, string connectionId, CancellationToken ct)
    {
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (agent is null || agent.ConnectionId != connectionId) return; // already reconnected
        agent.Disconnected(clock.UtcNow);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Agent {Agent} disconnected", agent.Name);
        await PublishAgentsAsync(ct);
    }

    /// <summary>Fails jobs of agents that have been gone for longer than <see cref="LostAfter"/>.</summary>
    public async Task FailJobsOfLostAgentsAsync(CancellationToken ct)
    {
        var cutoff = clock.UtcNow - LostAfter;
        var jobIds = await db.BuildJobs
            .Where(j => (j.Status == JobStatus.Assigned || j.Status == JobStatus.Running) && j.AgentId != null)
            .Join(db.Agents.Where(a => !a.Online && a.LastSeenAt < cutoff), j => j.AgentId, a => a.Id, (j, a) => j.Id)
            .ToListAsync(ct);
        if (jobIds.Count == 0) return;
        using (await buildLock.AcquireAsync(ct))
            foreach (var id in jobIds)
                await CompleteJobLockedAsync(new JobResult(id, false, -1, "Lost connection to the agent", false), ct);
    }

    public async Task HeartbeatAsync(Guid agentId, AgentMetrics m, CancellationToken ct)
    {
        metrics.Record(agentId, new AgentMetricsDto(clock.UtcNow, Math.Round(m.CpuPercent, 1), m.CpuCount,
            m.MemoryTotalBytes, m.MemoryUsedBytes, m.DiskTotalBytes, m.DiskUsedBytes, m.LoadAverage1, m.RunningJobs));
        await db.Agents.Where(a => a.Id == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSeenAt, clock.UtcNow), ct);
    }

    public async Task JobStartedAsync(Guid jobId, CancellationToken ct)
    {
        using var _ = await buildLock.AcquireAsync(ct);
        var build = await BuildOfJobAsync(jobId, ct);
        if (build is null) return;
        build.JobStarted(jobId, clock.UtcNow);
        await db.SaveChangesAsync(ct);
        await events.PublishAsync(build, [build.Job(jobId)], ct);
    }

    public async Task JobLogAsync(Guid jobId, IReadOnlyList<LogChunk> chunks, CancellationToken ct)
    {
        var buildId = await db.BuildJobs.Where(j => j.Id == jobId).Select(j => (Guid?)j.BuildId).FirstOrDefaultAsync(ct);
        if (buildId is null || chunks.Count == 0) return;
        var lines = chunks.Select(c => new LogLine(buildId.Value, jobId, c.Timestamp, (LogStreamKind)c.Stream,
            c.Text.Length > 8000 ? c.Text[..8000] : c.Text)).ToList();
        db.LogLines.AddRange(lines);
        await db.SaveChangesAsync(ct);
        await ui.Log(buildId.Value, lines.Select(l => l.ToDto()).ToList());
    }

    public async Task JobCompletedAsync(JobResult result, CancellationToken ct)
    {
        using (await buildLock.AcquireAsync(ct))
            await CompleteJobLockedAsync(result, ct);
        scheduler.Wake();
    }

    private async Task CompleteJobLockedAsync(JobResult result, CancellationToken ct)
    {
        var build = await BuildOfJobAsync(result.JobId, ct);
        if (build is null) return;
        var before = build.Jobs.ToDictionary(j => j.Id, j => j.Status);
        if (result.Error is { } error && !result.Succeeded)
            db.LogLines.Add(new LogLine(build.Id, result.JobId, clock.UtcNow, LogStreamKind.System, error));
        build.JobCompleted(result.JobId, result.Succeeded, result.Canceled, result.ExitCode, result.Error, clock.UtcNow);

        var deployment = await db.Deployments.FirstOrDefaultAsync(d => d.JobId == result.JobId, ct);
        if (deployment is not null)
        {
            deployment.Completed(result.Succeeded, clock.UtcNow);
            if (result.Succeeded)
            {
                var older = await db.Deployments.Where(d => d.Id != deployment.Id && d.EnvironmentId == deployment.EnvironmentId
                    && d.Name == deployment.Name && d.Status == Domain.Deployments.DeploymentStatus.Active).ToListAsync(ct);
                older.ForEach(d => d.Superseded(clock.UtcNow));
            }
        }
        await db.SaveChangesAsync(ct);
        await events.PublishAsync(build, build.Jobs.Where(j => before[j.Id] != j.Status), ct);
        if (deployment is not null)
        {
            var name = await db.Pipelines.Where(p => p.Id == deployment.PipelineId).Select(p => p.Name).FirstAsync(ct);
            await ui.DeploymentUpdated(deployment.OrgId, deployment.ToDto(name));
        }
    }

    public async Task ArtifactUploadedAsync(Guid jobId, string name, Stream content, CancellationToken ct)
    {
        var job = await db.BuildJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw new NotFoundException("Job");
        var (path, size) = await artifactStore.SaveAsync(job.BuildId, jobId, name, content, ct);
        db.Artifacts.Add(new Artifact(job.BuildId, jobId, name, size, path, clock.UtcNow));
        await db.SaveChangesAsync(ct);
    }

    public async Task<Stream> OpenArtifactForAgentAsync(Guid artifactId, CancellationToken ct)
    {
        var path = await db.Artifacts.Where(a => a.Id == artifactId).Select(a => a.StoragePath).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Artifact");
        return artifactStore.OpenRead(path);
    }

    public async Task TeardownCompletedAsync(TeardownResult result, CancellationToken ct)
    {
        var d = await db.Deployments.FirstOrDefaultAsync(x => x.Id == result.DeploymentId, ct);
        if (d is null) return;
        d.Destroyed(result.Succeeded, result.Output, clock.UtcNow);
        await db.SaveChangesAsync(ct);
        var name = await db.Pipelines.Where(p => p.Id == d.PipelineId).Select(p => p.Name).FirstOrDefaultAsync(ct) ?? "?";
        await ui.DeploymentUpdated(d.OrgId, d.ToDto(name));
    }

    // ---- UI ----

    /// <summary>The current organization's agents plus the shared pool.</summary>
    public Task<List<AgentDto>> ListAsync(CancellationToken ct) => ListForOrgAsync(current.RequireOrgId(), ct);

    /// <summary>Agents an organization can use, with only that organization's running jobs (shared agents may run others').</summary>
    public async Task<List<AgentDto>> ListForOrgAsync(Guid orgId, CancellationToken ct)
    {
        var agents = await db.Agents.AsNoTracking().IgnoreQueryFilters()
            .Where(a => a.OrgId == null || a.OrgId == orgId).OrderBy(a => a.OrgId == null).ThenBy(a => a.Name).ToListAsync(ct);
        var running = await db.BuildJobs.AsNoTracking()
            .Where(j => j.AgentId != null && (j.Status == JobStatus.Assigned || j.Status == JobStatus.Running))
            .Join(db.Builds.IgnoreQueryFilters().Where(b => b.OrgId == orgId), j => j.BuildId, b => b.Id,
                (j, b) => new { j.AgentId, j.Id, j.TaskName, b.Number, BuildId = b.Id, b.PipelineId })
            .Join(db.Pipelines.IgnoreQueryFilters(), x => x.PipelineId, p => p.Id, (x, p) => new { x.AgentId, x.Id, x.TaskName, x.Number, x.BuildId, p.Name })
            .ToListAsync(ct);
        return agents.Select(a => a.ToDto(metrics.Latest(a.Id),
            running.Where(r => r.AgentId == a.Id)
                .Select(r => new AgentRunningJobDto(r.BuildId, r.Number, r.Name, r.Id, r.TaskName)).ToList())).ToList();
    }

    /// <summary>Sends every organization its agent list (metrics included).</summary>
    public async Task PublishAgentsAsync(CancellationToken ct)
    {
        foreach (var orgId in await db.Organizations.AsNoTracking().Select(o => o.Id).ToListAsync(ct))
            await ui.AgentsUpdated(orgId, await ListForOrgAsync(orgId, ct));
    }

    public async Task<AgentDto> UpdateAsync(Guid agentId, AgentUpdateInput input, CancellationToken ct)
    {
        var agent = await OwnAgentAsync(agentId, ct);
        agent.SetEnabled(input.Enabled);
        await db.SaveChangesAsync(ct);
        if (input.Enabled) scheduler.Wake();
        await PublishAgentsAsync(ct);
        return (await ListAsync(ct)).First(a => a.Id == agentId);
    }

    public async Task DeleteAsync(Guid agentId, CancellationToken ct)
    {
        var agent = await OwnAgentAsync(agentId, ct);
        if (agent.Online) throw new DomainException("Stop the agent before removing it.");
        db.Agents.Remove(agent);
        await db.SaveChangesAsync(ct);
        metrics.Forget(agentId);
        await PublishAgentsAsync(ct);
    }

    /// <summary>An agent the current organization manages (admins only; shared agents are managed by whoever runs them).</summary>
    private async Task<Agent> OwnAgentAsync(Guid agentId, CancellationToken ct)
    {
        current.RequireRole(Domain.Organizations.OrgRole.Admin);
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct) ?? throw new NotFoundException("Agent");
        if (agent.OrgId is null) throw new ForbiddenException("Shared agents are managed by the people who run them.");
        return agent;
    }

    public IReadOnlyList<AgentMetricsDto> MetricsHistory(Guid agentId) => metrics.History(agentId);

    public async Task<bool> RequestCleanupAsync(Guid agentId, AgentCleanupInput input, CancellationToken ct)
    {
        await OwnAgentAsync(agentId, ct);
        return await CleanupAgentAsync(agentId, input, ct);
    }

    /// <summary>Asks an agent to clean up; workspaces of active builds (any organization) are kept.</summary>
    public async Task<bool> CleanupAgentAsync(Guid agentId, AgentCleanupInput input, CancellationToken ct)
    {
        var activeBuilds = await db.Builds.IgnoreQueryFilters().Where(b => b.Status == BuildStatus.Running || b.Status == BuildStatus.Canceling)
            .Select(b => b.Id).ToArrayAsync(ct);
        return await gateway.CleanupAsync(agentId,
            new CleanupRequest(Guid.NewGuid(), activeBuilds, input.RemoveWorkspaces, input.DockerPrune), ct);
    }

    private async Task<Build?> BuildOfJobAsync(Guid jobId, CancellationToken ct)
    {
        var buildId = await db.BuildJobs.Where(j => j.Id == jobId).Select(j => (Guid?)j.BuildId).FirstOrDefaultAsync(ct);
        return buildId is null ? null : await db.Builds.Include(b => b.Jobs).FirstAsync(b => b.Id == buildId, ct);
    }
}
