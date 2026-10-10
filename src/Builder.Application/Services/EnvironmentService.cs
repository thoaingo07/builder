using Builder.Contracts;
using Builder.Application.Abstractions;
using Builder.Application.Dtos;
using Builder.Domain;
using Builder.Domain.Builds;
using Builder.Domain.Deployments;
using Microsoft.EntityFrameworkCore;

namespace Builder.Application.Services;

public sealed class EnvironmentService(IAppDbContext db, IClock clock, ICurrentOrg current, ISecretProtector secrets, ProjectService projects,
    IAgentGateway gateway, CredentialService credentials)
{
    /// <summary>
    /// Checks the environment's credentials from an agent that could deploy there (same labels a deploy job gets):
    /// SSH login + docker on the host, or the Kubernetes API.
    /// </summary>
    public async Task<ConnectionTestDto> TestAsync(Guid id, CancellationToken ct)
    {
        current.RequireRole(Domain.Organizations.OrgRole.Admin);
        var env = await db.Environments.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException("Environment");
        var orgId = current.RequireOrgId();
        List<string> labels = [.. env.AgentLabels, env.Type == EnvironmentType.Kubernetes ? "kubectl" : "ssh"];
        var agents = await db.Agents.AsNoTracking().Where(a => a.Enabled).ToListAsync(ct);
        var agent = agents.Where(a => a.Serves(orgId) && a.Matches(labels) && gateway.IsConnected(a.Id))
            .OrderBy(a => a.OrgId == null ? 1 : 0).FirstOrDefault()
            ?? throw new DomainException($"No online agent can reach this environment (labels: {string.Join(", ", labels)}).");
        var result = await gateway.TestEnvironmentAsync(agent.Id, new EnvironmentTestRequest(SchedulerService.ToTarget(env), credentials.Reveal(env)), ct);
        return result is null
            ? new ConnectionTestDto(false, $"Agent {agent.Name} did not answer in time.")
            : new ConnectionTestDto(result.Ok, $"{result.Message} (from agent {agent.Name})");
    }

    /// <summary>All of the organization's, or what one project sees: its own and the shared ones.</summary>
    public async Task<List<EnvironmentDto>> ListAsync(CancellationToken ct, Guid? projectId = null) =>
        (await db.Environments.AsNoTracking().Where(e => projectId == null || e.ProjectId == projectId || e.ProjectId == null)
            .OrderBy(e => e.Name).ToListAsync(ct)).Select(e => e.ToDto()).ToList();

    public async Task<EnvironmentDto> CreateAsync(EnvironmentInput input, CancellationToken ct)
    {
        current.RequireRole(Domain.Organizations.OrgRole.Admin);
        var projectId = await projects.CheckAsync(input.ProjectId, ct);
        await EnsureFreeNameAsync(input.Name, projectId, null, ct);
        var env = new DeployEnvironment(current.RequireOrgId(), projectId, input.Name, input.Type, clock.UtcNow);
        Apply(env, input);
        db.Environments.Add(env);
        await db.SaveChangesAsync(ct);
        return env.ToDto();
    }

    public async Task<EnvironmentDto> UpdateAsync(Guid id, EnvironmentInput input, CancellationToken ct)
    {
        current.RequireRole(Domain.Organizations.OrgRole.Admin);
        var env = await db.Environments.FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException("Environment");
        var projectId = await projects.CheckAsync(input.ProjectId, ct);
        await EnsureFreeNameAsync(input.Name, projectId, id, ct);
        env.Rename(input.Name);
        env.MoveTo(projectId);
        Apply(env, input);
        await db.SaveChangesAsync(ct);
        return env.ToDto();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        current.RequireRole(Domain.Organizations.OrgRole.Admin);
        var env = await db.Environments.FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException("Environment");
        if (await db.Deployments.AnyAsync(d => d.EnvironmentId == id && d.Status == DeploymentStatus.Active, ct))
            throw new DomainException("Destroy the active deployments of this environment first.");
        db.Environments.Remove(env);
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureFreeNameAsync(string name, Guid? projectId, Guid? except, CancellationToken ct)
    {
        var n = name.Trim();
        if (await db.Environments.AnyAsync(e => e.Id != except && e.ProjectId == projectId && e.Name == n, ct))
            throw new DomainException($"An environment named '{n}' already exists{(projectId is null ? " (shared)" : " in this project")}.");
    }

    private void Apply(DeployEnvironment env, EnvironmentInput i)
    {
        env.SetPolicy(i.RequiresApproval, i.Approvers ?? [], i.AgentLabels ?? []);
        if (i.Type == EnvironmentType.SshDocker)
            env.SetSsh(Blank(i.Host), i.Port ?? 22, Blank(i.Username), ProtectIfSet(i.PrivateKey),
                string.IsNullOrEmpty(i.Password) ? null : secrets.Protect(i.Password)); // passwords as typed: no trimming
        else
            env.SetKubernetes(ProtectIfSet(i.Kubeconfig), Blank(i.AksTenantId), Blank(i.AksClientId), ProtectIfSet(i.AksClientSecret),
                Blank(i.AksSubscriptionId), Blank(i.AksResourceGroup), Blank(i.AksClusterName), i.AksAdmin);
    }

    private string? ProtectIfSet(string? value) => string.IsNullOrWhiteSpace(value) ? null : secrets.Protect(value.Replace("\r\n", "\n"));
    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

public sealed class DeploymentService(
    IAppDbContext db,
    IClock clock,
    IAgentGateway gateway,
    IUiNotifier ui,
    SchedulerService scheduler)
{
    public async Task<List<DeploymentDto>> ListAsync(Guid? environmentId, bool activeOnly, CancellationToken ct, Guid? projectId = null)
    {
        var q = db.Deployments.AsNoTracking().AsQueryable();
        if (projectId is { } pid) q = q.Where(d => d.ProjectId == pid);
        if (environmentId is { } eid) q = q.Where(d => d.EnvironmentId == eid);
        if (activeOnly) q = q.Where(d => d.Status == DeploymentStatus.Active || d.Status == DeploymentStatus.Deploying);
        var items = await q.OrderByDescending(d => d.CreatedAt).Take(200).ToListAsync(ct);
        var names = await db.Pipelines.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return items.Select(d => d.ToDto(names.GetValueOrDefault(d.PipelineId, "?"))).ToList();
    }

    public Task<DeploymentDto> DestroyAsync(Guid id, CancellationToken ct) => ActAsync(id, DeploymentAction.Teardown, ct);

    /// <summary>Blue-green / recreate deployments: make the previous (kept) container live again.</summary>
    public Task<DeploymentDto> RollbackAsync(Guid id, CancellationToken ct) => ActAsync(id, DeploymentAction.Rollback, ct);

    private async Task<DeploymentDto> ActAsync(Guid id, DeploymentAction action, CancellationToken ct)
    {
        var d = await db.Deployments.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Deployment");
        var env = await db.Environments.AsNoTracking().FirstOrDefaultAsync(e => e.Id == d.EnvironmentId, ct)
            ?? throw new DomainException("The environment of this deployment no longer exists.");
        if (action == DeploymentAction.Rollback && d.Container is null)
            throw new DomainException("Only blue-green / recreate container deployments can be rolled back.");

        var required = new List<string>(env.AgentLabels) { env.Type == EnvironmentType.Kubernetes ? "kubectl" : "ssh" };
        var agent = (await db.Agents.AsNoTracking().Where(a => a.Online && a.Enabled).ToListAsync(ct))
            .FirstOrDefault(a => gateway.IsConnected(a.Id) && a.Serves(d.OrgId) && a.Matches(required))
            ?? throw new DomainException($"No online agent with labels [{string.Join(", ", required)}] can reach this environment.");

        if (action == DeploymentAction.Rollback) d.RollingBack(agent.Id, clock.UtcNow);
        else d.Destroying(agent.Id, clock.UtcNow);
        await db.SaveChangesAsync(ct);

        var target = scheduler.ToTarget(env, new DeploySpec(env.Name, d.Compose, d.Name, d.Manifests, d.Name, d.Url, d.Container), null)
            with { Project = d.Name, Namespace = d.Name };
        await gateway.TeardownAsync(agent.Id, new TeardownRequest(d.Id, target, action), ct);

        var name = await db.Pipelines.Where(p => p.Id == d.PipelineId).Select(p => p.Name).FirstOrDefaultAsync(ct) ?? "?";
        var dto = d.ToDto(name);
        await ui.DeploymentUpdated(d.OrgId, dto);
        return dto;
    }
}
