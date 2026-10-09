using Builder.Application.Abstractions;
using Builder.Contracts;
using Builder.Domain.Agents;
using Builder.Domain.Builds;
using Builder.Domain.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Builder.Application.Services;

/// <summary>Assigns queued jobs to online agents with matching labels and free capacity.</summary>
public sealed class SchedulerService(
    IAppDbContext db,
    IClock clock,
    IAgentGateway gateway,
    IBuildLock buildLock,
    GitRemotes remotes,
    ISecretProtector secrets,
    BuildEvents events,
    ILogger<SchedulerService> log)
{
    public async Task<int> AssignQueuedJobsAsync(CancellationToken ct)
    {
        var assigned = 0;
        using var _ = await buildLock.AcquireAsync(ct);

        var queued = await db.BuildJobs
            .Where(j => j.Status == JobStatus.Queued)
            .Join(db.Builds.Where(b => b.Status == BuildStatus.Running), j => j.BuildId, b => b.Id, (j, b) => new { Job = j, b.QueuedAt })
            .OrderBy(x => x.QueuedAt).ThenBy(x => x.Job.Order)
            .Select(x => x.Job).ToListAsync(ct);
        if (queued.Count == 0) return 0;

        var agents = (await db.Agents.Where(a => a.Online && a.Enabled).ToListAsync(ct))
            .Where(a => gateway.IsConnected(a.Id)).ToList();
        if (agents.Count == 0) return 0;

        var busy = await db.BuildJobs
            .Where(j => j.AgentId != null && (j.Status == JobStatus.Assigned || j.Status == JobStatus.Running))
            .GroupBy(j => j.AgentId!.Value).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        foreach (var job in queued)
        {
            var orgId = await db.Builds.IgnoreQueryFilters().Where(b => b.Id == job.BuildId).Select(b => b.OrgId).FirstAsync(ct);
            var agent = PickAgent(job, orgId, agents, busy);
            if (agent is null) continue;

            var build = await db.Builds.Include(b => b.Jobs).FirstAsync(b => b.Id == job.BuildId, ct);
            JobAssignment assignment;
            try
            {
                assignment = await BuildAssignmentAsync(build, job, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Cannot prepare job {Job}", job.Id);
                build.JobCompleted(job.Id, false, false, null, ex.Message, clock.UtcNow);
                await db.SaveChangesAsync(ct);
                await events.PublishAsync(build, build.Jobs, ct);
                continue;
            }

            job.AssignTo(agent.Id, agent.Name);
            await RecordDeploymentAsync(build, job, ct);
            await db.SaveChangesAsync(ct);

            if (await gateway.AssignJobAsync(agent.Id, assignment, ct))
            {
                busy[agent.Id] = busy.GetValueOrDefault(agent.Id) + 1;
                assigned++;
                log.LogInformation("Assigned {Task} of build #{Number} to {Agent}", job.TaskName, build.Number, agent.Name);
            }
            else
            {
                job.Requeue();
                await db.SaveChangesAsync(ct);
            }
            await events.PublishAsync(build, [job], ct);
        }
        return assigned;
    }

    private static Agent? PickAgent(BuildJob job, Guid orgId, List<Agent> agents, Dictionary<Guid, int> busy) =>
        agents
            .Where(a => a.Serves(orgId) && a.Matches(job.Labels) && busy.GetValueOrDefault(a.Id) < a.Capacity)
            // an organization's own agents first, then the shared pool
            .OrderBy(a => a.OrgId is null ? 1 : 0)
            .ThenBy(a => (double)busy.GetValueOrDefault(a.Id) / a.Capacity)
            .ThenBy(a => a.Name)
            .FirstOrDefault();

    private async Task<JobAssignment> BuildAssignmentAsync(Build build, BuildJob job, CancellationToken ct)
    {
        var pipeline = await db.Pipelines.AsNoTracking().IgnoreQueryFilters().FirstAsync(p => p.Id == build.PipelineId, ct);
        var (remote, _) = await remotes.ForPipelineAsync(pipeline, ct);

        // artifacts produced by any upstream job
        var upstream = Upstream(build, job);
        var upstreamIds = build.Jobs.Where(j => upstream.Contains(j.Key)).Select(j => j.Id).ToList();
        var artifacts = await db.Artifacts.AsNoTracking().Where(a => upstreamIds.Contains(a.JobId)).ToListAsync(ct);

        var env = new Dictionary<string, string>
        {
            ["BUILDER_BUILD_ID"] = build.Id.ToString(),
            ["BUILDER_BUILD_NUMBER"] = build.Number.ToString(),
            ["BUILDER_PIPELINE"] = pipeline.Name,
            ["BUILDER_BRANCH"] = build.Branch,
            ["BUILDER_COMMIT"] = build.Commit ?? "",
            ["BUILDER_TASK"] = job.TaskName,
            ["BUILDER_REQUESTED_BY"] = build.RequestedBy,
        };
        foreach (var (k, v) in build.Variables) env[k] = v;

        var taskVars = new Dictionary<string, string>(build.Variables);
        foreach (var (k, v) in job.TaskVars) taskVars[k] = v;

        DeployTarget? deploy = null;
        if (job.Deploy is { } spec)
        {
            var e = await db.Environments.AsNoTracking().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.OrgId == build.OrgId && x.Name == spec.Environment, ct)
                ?? throw new InvalidOperationException($"Environment '{spec.Environment}' no longer exists.");
            deploy = ToTarget(e, spec, build);
        }

        return new JobAssignment(job.Id, build.Id, build.Number, pipeline.Name, job.TaskName, taskVars,
            new GitSource(remote.Url, build.Branch, build.Commit!, remote.AuthorizationHeader),
            pipeline.TaskfilePath, env, job.Artifacts.ToArray(),
            artifacts.Select(a => new ArtifactRef(a.Id, a.Name, $"/api/agent/artifacts/{a.Id}")).ToArray(),
            deploy, job.Secrets.ToArray());
    }

    public DeployTarget ToTarget(DeployEnvironment e, DeploySpec spec, Build? build) => new(
        e.Type == EnvironmentType.Kubernetes ? DeployTargetType.Kubernetes : DeployTargetType.SshDocker,
        e.Name, e.Host, e.Port, e.Username, Reveal(e.PrivateKeyProtected),
        Reveal(e.KubeconfigProtected), e.AksTenantId, e.AksClientId, Reveal(e.AksClientSecretProtected),
        e.AksSubscriptionId, e.AksResourceGroup, e.AksClusterName, e.AksAdmin,
        spec.Compose, DeploymentName(spec, build), spec.Manifests, DeploymentName(spec, build), spec.Url);

    public static string DeploymentName(DeploySpec spec, Build? build) =>
        Sanitize(spec.Project ?? spec.Namespace ?? $"builder-{build?.PipelineId.ToString()[..8]}");

    private static string Sanitize(string name) =>
        new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-').ToArray()).Trim('-');

    private string? Reveal(string? protectedValue) => protectedValue is null ? null : secrets.Unprotect(protectedValue);

    private async Task RecordDeploymentAsync(Build build, BuildJob job, CancellationToken ct)
    {
        if (job.Deploy is not { } spec) return;
        if (await db.Deployments.AnyAsync(d => d.JobId == job.Id, ct)) return;
        var env = await db.Environments.AsNoTracking().IgnoreQueryFilters().FirstAsync(e => e.OrgId == build.OrgId && e.Name == spec.Environment, ct);
        db.Deployments.Add(new Deployment(env, build.PipelineId, build.Id, build.Number, job.Id,
            DeploymentName(spec, build), spec.Compose, spec.Manifests, spec.Url, clock.UtcNow));
    }

    private static HashSet<string> Upstream(Build build, BuildJob job)
    {
        var byKey = build.Jobs.ToDictionary(j => j.Key);
        var seen = new HashSet<string>();
        var stack = new Stack<string>(job.DependsOn);
        while (stack.Count > 0)
        {
            var key = stack.Pop();
            if (!seen.Add(key) || !byKey.TryGetValue(key, out var dep)) continue;
            foreach (var d in dep.DependsOn) stack.Push(d);
        }
        return seen;
    }
}
