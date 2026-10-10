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
            if (!IsAzureContainerRegistry(spec.Registry))
            {
                // Docker Hub, GHCR, …: the registry connection's user name + access token
                var registry = await RegistryConnectionAsync(build.OrgId, build.ProjectId, spec, ct);
                registries.Add(new RegistryLogin(spec.Registry, registry.Username ?? "",
                    protector.Unprotect(registry.TokenProtected ?? throw new DomainException($"Connection '{registry.Name}' has no token."))));
                continue;
            }
            var azure = await AzureConnectionAsync(build.OrgId, build.ProjectId, spec, ct);
            var arm = await remotes.ArmTokenAsync(azure, ct);
            var refresh = await acr.ExchangeAsync(spec.Registry, azure.TenantId!, arm.Token, ct);
            registries.Add(new RegistryLogin(spec.Registry, IAcrTokens.DockerUser, refresh.Token));
            expiries.Add(refresh.ExpiresOn);
        }

        DeploySecrets? deploy = null;
        if (job.Deploy is { } deploySpec)
        {
            var env = await ProjectLookup.EnvironmentAsync(db, build.OrgId, build.ProjectId, deploySpec.Environment, ct)
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
        if (d is null || d.TeardownAgentId != agentId
            || d.Status is not (Domain.Deployments.DeploymentStatus.Destroying or Domain.Deployments.DeploymentStatus.RollingBack))
            throw new ForbiddenException("This agent is not tearing down or rolling back that deployment.");
        var env = await db.Environments.AsNoTracking().IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == d.EnvironmentId, ct)
            ?? throw new DomainException("The environment of this deployment no longer exists.");
        return Reveal(env);
    }

    /// <summary>An environment's secrets in clear text (for the agent running a job, a teardown or a test).</summary>
    public DeploySecrets Reveal(Domain.Deployments.DeployEnvironment e) => new(
        Unprotect(e.PrivateKeyProtected), Unprotect(e.KubeconfigProtected), Unprotect(e.AksClientSecretProtected),
        Unprotect(e.PasswordProtected));

    private string? Unprotect(string? value) => value is null ? null : protector.Unprotect(value);

    /// <summary>
    /// The Azure connection a registry login uses: the one named (the project's own before a shared one), else the
    /// project's only Azure connection, else the organization's only shared one.
    /// </summary>
    public async Task<GitConnection> AzureConnectionAsync(Guid orgId, Guid projectId, RegistrySpec spec, CancellationToken ct)
    {
        var all = await ProjectLookup.ConnectionsAsync(db, orgId, projectId, ConnectionType.Azure, ct);
        if (spec.Connection is { } name)
            return Domain.Projects.ProjectScope.Pick(all, projectId, name)
                ?? throw new DomainException($"Registry {spec.Registry}: there is no Azure connection named '{name}'.");
        var own = all.Where(c => c.ProjectId == projectId).ToList();
        var azure = own.Count > 0 ? own : all;
        return azure.Count switch
        {
            1 => azure[0],
            0 => throw new DomainException($"Registry {spec.Registry}: add an Azure connection (service principal with AcrPush) first."),
            _ => throw new DomainException($"Registry {spec.Registry}: several Azure connections exist; name one (x-registries: [{{ registry: {spec.Registry}, connection: <name> }}])."),
        };
    }

    /// <summary>
    /// The registry connection for a non-Azure registry: the one named, or the one whose URL is that host; the
    /// project's own before a shared one.
    /// </summary>
    public async Task<GitConnection> RegistryConnectionAsync(Guid orgId, Guid projectId, RegistrySpec spec, CancellationToken ct)
    {
        var all = (await ProjectLookup.ConnectionsAsync(db, orgId, projectId, ConnectionType.Registry, ct))
            .OrderBy(c => c.ProjectId == projectId ? 0 : 1).ToList();
        var match = spec.Connection is { } name
            ? Domain.Projects.ProjectScope.Pick(all, projectId, name)
            : all.FirstOrDefault(c => string.Equals(RegistryHost(c.Url), spec.Registry, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new DomainException(
            $"Registry {spec.Registry}: add a Container registry connection for it (user name + access token).");
    }

    /// <summary>docker.io, index.docker.io and registry-1.docker.io are the same registry.</summary>
    public static string RegistryHost(string url)
    {
        var host = Uri.TryCreate(url.Contains("://") ? url : "https://" + url, UriKind.Absolute, out var u) ? u.Host : url;
        return host is "index.docker.io" or "registry-1.docker.io" or "hub.docker.com" ? "docker.io" : host.ToLowerInvariant();
    }

    public static bool IsAzureContainerRegistry(string host) =>
        host.EndsWith(".azurecr.io", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".azurecr.cn", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".azurecr.us", StringComparison.OrdinalIgnoreCase);
}
