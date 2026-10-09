using Builder.Application.Abstractions;
using Builder.Domain.Builds;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Builder.Application.Services;

/// <summary>
/// Turns a queued build into a job graph: resolves the commit, reads the Taskfile at that commit,
/// plans the graph from the entry task and applies environment approval policies.
/// </summary>
public sealed class BuildPlanningService(
    IAppDbContext db,
    IClock clock,
    IGitService git,
    ITaskfilePlanner planner,
    GitRemotes remotes,
    IBuildLock buildLock,
    BuildEvents events,
    ILogger<BuildPlanningService> log)
{
    /// <summary>Plans the oldest build waiting for planning. Returns false when there was nothing to do.</summary>
    public async Task<bool> PlanNextAsync(CancellationToken ct)
    {
        var build = await db.Builds.Include(b => b.Jobs)
            .Where(b => b.Status == BuildStatus.Planning)
            .OrderBy(b => b.QueuedAt).FirstOrDefaultAsync(ct);
        if (build is null) return false;

        var pipeline = await db.Pipelines.AsNoTracking().FirstAsync(p => p.Id == build.PipelineId, ct);
        string commit;
        List<BuildJob> jobs;
        string entry;
        try
        {
            var remote = await remotes.ForPipelineAsync(pipeline, ct);
            commit = build.Commit ?? await git.ResolveBranchAsync(remote, build.Branch, ct);
            var yaml = await git.ReadFileAsync(remote, build.Branch, commit, pipeline.TaskfilePath, ct)
                ?? throw new TaskfileException($"'{pipeline.TaskfilePath}' was not found at {commit[..Math.Min(8, commit.Length)]}.");
            var plan = planner.Plan(yaml, string.IsNullOrEmpty(build.EntryTask) ? pipeline.EntryTask : build.EntryTask);
            entry = plan.EntryTask;
            jobs = await ToJobsAsync(plan, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Planning build {Build} failed", build.Id);
            using (await buildLock.AcquireAsync(ct))
            {
                build.PlanningFailed(ex.Message, clock.UtcNow);
                await db.SaveChangesAsync(ct);
            }
            await events.PublishAsync(build, [], ct);
            return true;
        }

        using (await buildLock.AcquireAsync(ct))
        {
            // the build may have been canceled while we were cloning
            await db.Builds.Entry(build).ReloadAsync(ct);
            if (build.Status != BuildStatus.Planning) return true;
            build.Planned(commit, entry, jobs, clock.UtcNow);
            await db.SaveChangesAsync(ct);
        }
        await events.PublishAsync(build, build.Jobs, ct);
        return true;
    }

    private async Task<List<BuildJob>> ToJobsAsync(TaskfilePlan plan, CancellationToken ct)
    {
        var envNames = plan.Jobs.Where(j => j.Deploy is not null).Select(j => j.Deploy!.Environment).Distinct().ToList();
        var envs = await db.Environments.AsNoTracking().Where(e => envNames.Contains(e.Name)).ToListAsync(ct);
        var missing = envNames.Except(envs.Select(e => e.Name), StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
            throw new TaskfileException($"Unknown deploy environment(s): {string.Join(", ", missing)}. Create them under Environments.");

        return plan.Jobs.Select(p =>
        {
            var approval = p.Approval;
            var labels = p.Labels.ToList();
            if (p.Deploy is { } deploy)
            {
                var env = envs.First(e => string.Equals(e.Name, deploy.Environment, StringComparison.OrdinalIgnoreCase));
                if (env.RequiresApproval)
                    approval = approval is null
                        ? new ApprovalSpec($"Deploy to {env.Name}?", env.Approvers)
                        : approval with { Approvers = approval.Approvers.Union(env.Approvers).ToList() };
                labels.AddRange(env.AgentLabels);
                labels.Add(env.Type == Domain.Deployments.EnvironmentType.Kubernetes ? "kubectl" : "ssh");
            }
            if (p.HasCommands) labels.Add("task");
            return new BuildJob(p.Key, p.TaskName, p.Description, p.Order, p.DependsOn, p.TaskVars,
                labels.Distinct().ToList(), p.Artifacts, p.HasCommands, approval, p.Deploy);
        }).ToList();
    }
}
