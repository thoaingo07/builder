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
    CredentialService credentials,
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

        var pipeline = await db.Pipelines.AsNoTracking().IgnoreQueryFilters().FirstAsync(p => p.Id == build.PipelineId, ct);
        string commit;
        List<BuildJob> jobs;
        string entry;
        try
        {
            var (remote, _) = await remotes.ForPipelineAsync(pipeline, ct);
            commit = build.Commit ?? await git.ResolveBranchAsync(remote, build.Branch, ct);
            var yaml = await git.ReadFileAsync(remote, build.SourceRef ?? build.Branch, commit, pipeline.TaskfilePath, ct)
                ?? throw new TaskfileException($"'{pipeline.TaskfilePath}' was not found at {commit[..Math.Min(8, commit.Length)]}.");
            var plan = planner.Plan(yaml, string.IsNullOrEmpty(build.EntryTask) ? pipeline.EntryTask : build.EntryTask);
            entry = plan.EntryTask;
            CheckInputs(plan, build.Variables);
            jobs = await ToJobsAsync(plan, build.OrgId, pipeline.RepositoryId, ct);
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

    private async Task<bool> RepositoryUsesAzureDevOpsAsync(Guid orgId, Guid repositoryId, CancellationToken ct) =>
        await db.Repositories.AsNoTracking().IgnoreQueryFilters().Where(r => r.Id == repositoryId && r.OrgId == orgId)
            .Join(db.Connections.IgnoreQueryFilters(), r => r.ConnectionId, c => c.Id, (r, c) => c.Type)
            .AnyAsync(t => t == Domain.Connections.ConnectionType.AzureDevOps, ct);

    /// <summary>Every requires.vars input must be given (and, with an enum, be one of its values).</summary>
    public static void CheckInputs(TaskfilePlan plan, IReadOnlyDictionary<string, string> variables)
    {
        var problems = new List<string>();
        foreach (var (input, requiredBy) in plan.Inputs)
        {
            if (!variables.TryGetValue(input.Name, out var value) || string.IsNullOrEmpty(value))
                problems.Add($"{input.Name} (required by {string.Join(", ", requiredBy)})");
            else if (input.Enum is { } allowed && !allowed.Contains(value))
                problems.Add($"{input.Name}='{value}' is not one of: {string.Join(", ", allowed)}");
        }
        if (problems.Count > 0) throw new TaskfileException("Missing or invalid variables: " + string.Join("; ", problems));
    }

    private async Task<List<BuildJob>> ToJobsAsync(TaskfilePlan plan, Guid orgId, Guid repositoryId, CancellationToken ct)
    {
        var envNames = plan.Jobs.Where(j => j.Deploy is not null).Select(j => j.Deploy!.Environment).Distinct().ToList();
        var envs = await db.Environments.AsNoTracking().IgnoreQueryFilters().Where(e => e.OrgId == orgId && envNames.Contains(e.Name)).ToListAsync(ct);
        var missing = envNames.Except(envs.Select(e => e.Name), StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
            throw new TaskfileException($"Unknown deploy environment(s): {string.Join(", ", missing)}. Create them under Environments.");

        var wanted = plan.Jobs.SelectMany(j => j.Secrets).Distinct().ToList();
        var known = await db.Secrets.AsNoTracking().IgnoreQueryFilters().Where(s => s.OrgId == orgId && wanted.Contains(s.Name))
            .Select(s => s.Name).ToListAsync(ct);
        var unknown = wanted.Except(known).ToList();
        if (unknown.Count > 0)
            throw new TaskfileException($"Unknown secret(s): {string.Join(", ", unknown)}. Add them under Secrets.");

        // registries need an Azure container registry and a resolvable Azure connection; Azure Artifacts an Azure DevOps repo
        foreach (var spec in plan.Jobs.SelectMany(j => j.Registries).DistinctBy(r => (r.Registry, r.Connection)))
        {
            try
            {
                if (CredentialService.IsAzureContainerRegistry(spec.Registry)) await credentials.AzureConnectionAsync(orgId, spec, ct);
                else await credentials.RegistryConnectionAsync(orgId, spec, ct);
            }
            catch (Domain.DomainException ex)
            {
                throw new TaskfileException(ex.Message);
            }
        }
        if (plan.Jobs.Any(j => j.AzureArtifacts) && !await RepositoryUsesAzureDevOpsAsync(orgId, repositoryId, ct))
            throw new TaskfileException("x-azure-artifacts needs the repository to be added with an Azure DevOps connection.");

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
                labels.Distinct().ToList(), p.Artifacts, p.HasCommands, approval, p.Deploy, p.Secrets, p.Registries, p.AzureArtifacts, p.Steps);
        }).ToList();
    }
}
