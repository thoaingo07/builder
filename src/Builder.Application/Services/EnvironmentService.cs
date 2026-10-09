using Builder.Application.Abstractions;
using Builder.Application.Dtos;
using Builder.Domain;
using Builder.Domain.Builds;
using Builder.Domain.Deployments;
using Microsoft.EntityFrameworkCore;

namespace Builder.Application.Services;

public sealed class EnvironmentService(IAppDbContext db, IClock clock, ICurrentOrg current, ISecretProtector secrets)
{
    public async Task<List<EnvironmentDto>> ListAsync(CancellationToken ct) =>
        (await db.Environments.AsNoTracking().OrderBy(e => e.Name).ToListAsync(ct)).Select(e => e.ToDto()).ToList();

    public async Task<EnvironmentDto> CreateAsync(EnvironmentInput input, CancellationToken ct)
    {
        current.RequireRole(Domain.Organizations.OrgRole.Admin);
        if (await db.Environments.AnyAsync(e => e.Name == input.Name.Trim(), ct))
            throw new DomainException($"An environment named '{input.Name}' already exists.");
        var env = new DeployEnvironment(current.RequireOrgId(), input.Name, input.Type, clock.UtcNow);
        Apply(env, input);
        db.Environments.Add(env);
        await db.SaveChangesAsync(ct);
        return env.ToDto();
    }

    public async Task<EnvironmentDto> UpdateAsync(Guid id, EnvironmentInput input, CancellationToken ct)
    {
        current.RequireRole(Domain.Organizations.OrgRole.Admin);
        var env = await db.Environments.FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException("Environment");
        env.Rename(input.Name);
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

    private void Apply(DeployEnvironment env, EnvironmentInput i)
    {
        env.SetPolicy(i.RequiresApproval, i.Approvers ?? [], i.AgentLabels ?? []);
        if (i.Type == EnvironmentType.SshDocker)
            env.SetSsh(Blank(i.Host), i.Port ?? 22, Blank(i.Username), ProtectIfSet(i.PrivateKey));
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
    public async Task<List<DeploymentDto>> ListAsync(Guid? environmentId, bool activeOnly, CancellationToken ct)
    {
        var q = db.Deployments.AsNoTracking().AsQueryable();
        if (environmentId is { } eid) q = q.Where(d => d.EnvironmentId == eid);
        if (activeOnly) q = q.Where(d => d.Status == DeploymentStatus.Active || d.Status == DeploymentStatus.Deploying);
        var items = await q.OrderByDescending(d => d.CreatedAt).Take(200).ToListAsync(ct);
        var names = await db.Pipelines.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return items.Select(d => d.ToDto(names.GetValueOrDefault(d.PipelineId, "?"))).ToList();
    }

    public async Task<DeploymentDto> DestroyAsync(Guid id, CancellationToken ct)
    {
        var d = await db.Deployments.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Deployment");
        var env = await db.Environments.AsNoTracking().FirstOrDefaultAsync(e => e.Id == d.EnvironmentId, ct)
            ?? throw new DomainException("The environment of this deployment no longer exists.");

        var required = new List<string>(env.AgentLabels) { env.Type == EnvironmentType.Kubernetes ? "kubectl" : "ssh" };
        var agent = (await db.Agents.AsNoTracking().Where(a => a.Online && a.Enabled).ToListAsync(ct))
            .FirstOrDefault(a => gateway.IsConnected(a.Id) && a.Serves(d.OrgId) && a.Matches(required))
            ?? throw new DomainException($"No online agent with labels [{string.Join(", ", required)}] can tear this deployment down.");

        d.Destroying(agent.Id, clock.UtcNow);
        await db.SaveChangesAsync(ct);

        var target = scheduler.ToTarget(env, new DeploySpec(env.Name, d.Compose, d.Name, d.Manifests, d.Name, d.Url), null)
            with { Project = d.Name, Namespace = d.Name };
        await gateway.TeardownAsync(agent.Id, new Contracts.TeardownRequest(d.Id, target), ct);

        var name = await db.Pipelines.Where(p => p.Id == d.PipelineId).Select(p => p.Name).FirstOrDefaultAsync(ct) ?? "?";
        var dto = d.ToDto(name);
        await ui.DeploymentUpdated(d.OrgId, dto);
        return dto;
    }
}
