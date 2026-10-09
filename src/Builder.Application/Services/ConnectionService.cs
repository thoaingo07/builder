using Builder.Application.Abstractions;
using Builder.Application.Dtos;
using Builder.Domain;
using Builder.Domain.Connections;
using Builder.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Builder.Application.Services;

/// <summary>
/// Git host credentials of the current organization. Azure DevOps connections (organization URL + PAT) also
/// browse projects, repositories and branches so repositories can be picked instead of typed.
/// </summary>
public sealed class ConnectionService(
    IAppDbContext db,
    IClock clock,
    ICurrentOrg current,
    ISecretProtector secrets,
    IAzureDevOpsClient azureDevOps,
    IGitService git,
    GitRemotes remotes)
{
    public async Task<List<ConnectionDto>> ListAsync(CancellationToken ct) =>
        (await db.Connections.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct)).Select(c => c.ToDto()).ToList();

    public async Task<ConnectionDto> CreateAsync(ConnectionInput input, CancellationToken ct)
    {
        current.RequireRole(OrgRole.Admin);
        var c = new GitConnection(current.RequireOrgId(), input.Name, input.Type, Normalize(input.Type, input.Url), clock.UtcNow);
        c.Update(input.Name, input.Type, Normalize(input.Type, input.Url), input.Username, Protect(input.Token));
        if (c.Type == ConnectionType.AzureDevOps && c.TokenProtected is null)
            throw new DomainException("An Azure DevOps connection needs a personal access token (scope: Code → Read; Read & write to commit Taskfile edits).");
        db.Connections.Add(c);
        await db.SaveChangesAsync(ct);
        return c.ToDto();
    }

    public async Task<ConnectionDto> UpdateAsync(Guid id, ConnectionInput input, CancellationToken ct)
    {
        current.RequireRole(OrgRole.Admin);
        var c = await FindAsync(id, ct);
        c.Update(input.Name, input.Type, Normalize(input.Type, input.Url), input.Username, Protect(input.Token));
        await db.SaveChangesAsync(ct);
        return c.ToDto();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        current.RequireRole(OrgRole.Admin);
        var c = await FindAsync(id, ct);
        if (await db.Repositories.AnyAsync(r => r.ConnectionId == id, ct))
            throw new DomainException("Repositories still use this connection.");
        db.Connections.Remove(c);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Verifies the credentials: Azure DevOps lists projects; plain git can only be checked against a repository.</summary>
    public async Task<ConnectionTestDto> TestAsync(Guid id, CancellationToken ct)
    {
        var c = await FindAsync(id, ct);
        if (c.Type != ConnectionType.AzureDevOps)
            return new ConnectionTestDto(true, "Saved. Plain git credentials are checked when a repository is added.");
        var projects = await azureDevOps.TestAsync(c.Url, Authorization(c), ct);
        return new ConnectionTestDto(true, $"Connected to {c.Url}: {projects} project(s) visible.");
    }

    public async Task<IReadOnlyList<string>> ProjectsAsync(Guid id, CancellationToken ct)
    {
        var c = await AzureDevOpsAsync(id, ct);
        return await azureDevOps.ListProjectsAsync(c.Url, Authorization(c), ct);
    }

    public async Task<IReadOnlyList<RemoteRepositoryDto>> RepositoriesAsync(Guid id, string? project, CancellationToken ct)
    {
        var c = await AzureDevOpsAsync(id, ct);
        return await azureDevOps.ListRepositoriesAsync(c.Url, project, Authorization(c), ct);
    }

    /// <summary>Branches of a repository URL through this connection (before the repository is added).</summary>
    public async Task<IReadOnlyList<string>> BranchesAsync(Guid id, string url, CancellationToken ct)
    {
        var c = await FindAsync(id, ct);
        return await git.ListBranchesAsync(new GitRemote(url, remotes.AuthorizationHeader(c)), ct);
    }

    private async Task<GitConnection> FindAsync(Guid id, CancellationToken ct) =>
        await db.Connections.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Connection");

    private async Task<GitConnection> AzureDevOpsAsync(Guid id, CancellationToken ct)
    {
        var c = await FindAsync(id, ct);
        if (c.Type != ConnectionType.AzureDevOps) throw new DomainException("This is only available for Azure DevOps connections.");
        if (c.TokenProtected is null) throw new DomainException("The connection has no personal access token.");
        return c;
    }

    private string Authorization(GitConnection c) => remotes.AuthorizationHeader(c) ?? throw new DomainException("The connection has no token.");

    private string? Protect(string? token) => string.IsNullOrWhiteSpace(token) ? null : secrets.Protect(token.Trim());

    /// <summary>Accepts https://dev.azure.com/org, https://dev.azure.com/org/project/..., or org.visualstudio.com.</summary>
    internal static string Normalize(ConnectionType type, string url)
    {
        url = url.Trim().TrimEnd('/');
        if (type != ConnectionType.AzureDevOps || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        if (u.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            var org = u.AbsolutePath.Trim('/').Split('/')[0];
            if (org.Length == 0) throw new DomainException("Use the organization URL, e.g. https://dev.azure.com/<organization>.");
            return $"https://dev.azure.com/{org}";
        }
        if (u.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
            return $"https://{u.Host}";
        return url; // Azure DevOps Server (on-premises): keep the collection URL as given
    }
}
