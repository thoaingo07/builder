using Builder.Application.Abstractions;
using Builder.Contracts;
using Builder.Domain;
using Builder.Domain.Builds;
using Builder.Domain.Connections;
using Microsoft.EntityFrameworkCore;

namespace Builder.Application.Services;

/// <summary>
/// Mints the short-lived credentials a running job needs, for the agent running it: git access to the repository,
/// docker logins for the registries the job declared (x-registries), and an Azure DevOps token for Azure Artifacts
/// feeds (x-azure-artifacts). Nothing long-lived leaves the API for service-principal connections.
/// </summary>
public sealed class CredentialService(IAppDbContext db, GitRemotes remotes, IAcrTokens acr, ISecretProtector protector)
{
    public async Task<JobCredentials> ForJobAsync(Guid agentId, Guid jobId, CancellationToken ct)
    {
        var job = await db.BuildJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.AgentId != agentId || job.Status is not (JobStatus.Assigned or JobStatus.Running))
            throw new ForbiddenException("This agent is not running that job.");

        var build = await db.Builds.AsNoTracking().IgnoreQueryFilters().FirstAsync(b => b.Id == job.BuildId, ct);
        var pipeline = await db.Pipelines.AsNoTracking().IgnoreQueryFilters().FirstAsync(p => p.Id == build.PipelineId, ct);
        var repository = await db.Repositories.AsNoTracking().IgnoreQueryFilters().FirstAsync(r => r.Id == pipeline.RepositoryId, ct);
        var repoConnection = repository.ConnectionId is { } cid
            ? await db.Connections.AsNoTracking().IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == cid && c.OrgId == build.OrgId, ct)
            : null;
        var expiries = new List<DateTimeOffset>();

        var git = (await remotes.ForRepositoryAsync(repository, ct)).AuthorizationHeader;

        string? azureDevOps = null;
        if (job.AzureArtifacts)
        {
            if (repoConnection is not { Type: ConnectionType.AzureDevOps })
                throw new DomainException("x-azure-artifacts needs the repository to use an Azure DevOps connection.");
            var token = await remotes.AzureDevOpsTokenAsync(repoConnection, ct);
            azureDevOps = token.Token;
            expiries.Add(token.ExpiresOn);
        }

        var registries = new List<RegistryLogin>();
        foreach (var spec in job.Registries)
        {
            var azure = await AzureConnectionAsync(build.OrgId, spec, ct);
            var arm = await remotes.ArmTokenAsync(azure, ct);
            var refresh = await acr.ExchangeAsync(spec.Registry, azure.TenantId!, arm.Token, ct);
            registries.Add(new RegistryLogin(spec.Registry, IAcrTokens.DockerUser, refresh.Token));
            expiries.Add(refresh.ExpiresOn);
        }

        DeploySecrets? deploy = null;
        if (job.Deploy is { } deploySpec)
        {
            var env = await db.Environments.AsNoTracking().IgnoreQueryFilters()
                .FirstOrDefaultAsync(e => e.OrgId == build.OrgId && e.Name == deploySpec.Environment, ct)
                ?? throw new DomainException($"Environment '{deploySpec.Environment}' no longer exists.");
            deploy = Reveal(env);
        }

        return new JobCredentials(git, registries.ToArray(), azureDevOps,
            expiries.Where(e => e != DateTimeOffset.MaxValue).DefaultIfEmpty().Min() is var min && min != default ? min : null,
            deploy);
    }

    /// <summary>The environment's secrets for the agent asked to tear this deployment down, while it is doing so.</summary>
    public async Task<DeploySecrets> ForTeardownAsync(Guid agentId, Guid deploymentId, CancellationToken ct)
    {
        var d = await db.Deployments.AsNoTracking().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == deploymentId, ct);
        if (d is null || d.TeardownAgentId != agentId || d.Status != Domain.Deployments.DeploymentStatus.Destroying)
            throw new ForbiddenException("This agent is not tearing that deployment down.");
        var env = await db.Environments.AsNoTracking().IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == d.EnvironmentId, ct)
            ?? throw new DomainException("The environment of this deployment no longer exists.");
        return Reveal(env);
    }

    private DeploySecrets Reveal(Domain.Deployments.DeployEnvironment e) => new(
        Unprotect(e.PrivateKeyProtected), Unprotect(e.KubeconfigProtected), Unprotect(e.AksClientSecretProtected));

    private string? Unprotect(string? value) => value is null ? null : protector.Unprotect(value);

    /// <summary>The Azure connection a registry login uses: the one named, or the organization's only Azure connection.</summary>
    public async Task<GitConnection> AzureConnectionAsync(Guid orgId, RegistrySpec spec, CancellationToken ct)
    {
        var azure = await db.Connections.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.OrgId == orgId && c.Type == ConnectionType.Azure).ToListAsync(ct);
        if (spec.Connection is { } name)
            return azure.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new DomainException($"Registry {spec.Registry}: there is no Azure connection named '{name}'.");
        return azure.Count switch
        {
            1 => azure[0],
            0 => throw new DomainException($"Registry {spec.Registry}: add an Azure connection (service principal with AcrPush) first."),
            _ => throw new DomainException($"Registry {spec.Registry}: several Azure connections exist; name one (x-registries: [{{ registry: {spec.Registry}, connection: <name> }}])."),
        };
    }

    public static bool IsAzureContainerRegistry(string host) =>
        host.EndsWith(".azurecr.io", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".azurecr.cn", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".azurecr.us", StringComparison.OrdinalIgnoreCase);
}
