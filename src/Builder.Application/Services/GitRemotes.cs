using System.Text;
using Builder.Application.Abstractions;
using Builder.Domain.Connections;
using Builder.Domain.Pipelines;
using Builder.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Builder.Application.Services;

/// <summary>Builds the authenticated <see cref="GitRemote"/> for a repository from its connection.</summary>
public sealed class GitRemotes(IAppDbContext db, ISecretProtector secrets, IEntraTokens entra)
{
    public async Task<GitRemote> ForRepositoryAsync(Repository repository, CancellationToken ct)
    {
        GitConnection? connection = repository.ConnectionId is { } id
            ? await db.Connections.AsNoTracking().IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == id && c.OrgId == repository.OrgId, ct)
            : null;
        return new GitRemote(repository.Url, await AuthorizationAsync(connection, ct));
    }

    public async Task<(GitRemote Remote, Repository Repository)> ForPipelineAsync(Pipeline pipeline, CancellationToken ct)
    {
        var repository = await db.Repositories.AsNoTracking().IgnoreQueryFilters().FirstAsync(r => r.Id == pipeline.RepositoryId, ct);
        return (await ForRepositoryAsync(repository, ct), repository);
    }

    /// <summary>
    /// The Authorization header for a git connection: a PAT as Basic auth (Azure DevOps and GitHub accept any user
    /// name), or for a service principal a freshly minted Entra token for Azure DevOps (valid ~1 h).
    /// </summary>
    public async Task<string?> AuthorizationAsync(GitConnection? connection, CancellationToken ct)
    {
        if (connection?.TokenProtected is null) return null;
        if (connection.AuthKind == ConnectionAuthKind.ServicePrincipal)
            return "Bearer " + (await AzureDevOpsTokenAsync(connection, ct)).Token;
        var token = secrets.Unprotect(connection.TokenProtected);
        var user = connection.Username ?? "builder";
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}"));
    }

    /// <summary>A token for Azure DevOps (git, REST, Azure Artifacts): Entra for service principals, else the PAT itself.</summary>
    public async Task<AccessToken> AzureDevOpsTokenAsync(GitConnection connection, CancellationToken ct)
    {
        var secret = secrets.Unprotect(connection.TokenProtected ?? throw new Domain.DomainException($"Connection '{connection.Name}' has no credentials."));
        return connection.AuthKind == ConnectionAuthKind.ServicePrincipal
            ? await entra.GetAsync(connection.TenantId!, connection.ClientId!, secret, IEntraTokens.AzureDevOpsScope, ct)
            : new AccessToken(secret, DateTimeOffset.MaxValue);
    }

    /// <summary>An Azure Resource Manager token for an Azure (service principal) connection.</summary>
    public Task<AccessToken> ArmTokenAsync(GitConnection connection, CancellationToken ct) =>
        entra.GetAsync(connection.TenantId!, connection.ClientId!,
            secrets.Unprotect(connection.TokenProtected ?? throw new Domain.DomainException($"Connection '{connection.Name}' has no client secret.")),
            IEntraTokens.ArmScope, ct);
}
