using Builder.Application.Abstractions;
using Builder.Domain.Connections;
using Builder.Domain.Deployments;
using Builder.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace Builder.Application.Services;

/// <summary>
/// Resolves the names a runner uses (environments, secrets, connections) for a build: the project's own first,
/// then the ones shared by the organization. Runs outside requests too, so it ignores the query filters.
/// </summary>
public static class ProjectLookup
{
    public static async Task<List<DeployEnvironment>> EnvironmentsAsync(IAppDbContext db, Guid orgId, Guid projectId,
        IReadOnlyCollection<string> names, CancellationToken ct)
    {
        var lower = names.Select(n => n.ToLower()).ToList();
        var candidates = await db.Environments.AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.OrgId == orgId && (e.ProjectId == projectId || e.ProjectId == null) && lower.Contains(e.Name.ToLower()))
            .ToListAsync(ct);
        return ProjectScope.Visible(candidates, projectId);
    }

    public static async Task<DeployEnvironment?> EnvironmentAsync(IAppDbContext db, Guid orgId, Guid projectId, string name, CancellationToken ct) =>
        (await EnvironmentsAsync(db, orgId, projectId, [name], ct)).FirstOrDefault();

    public static async Task<List<string>> SecretNamesAsync(IAppDbContext db, Guid orgId, Guid projectId,
        IReadOnlyCollection<string> names, CancellationToken ct) =>
        await db.Secrets.AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.OrgId == orgId && (s.ProjectId == projectId || s.ProjectId == null) && names.Contains(s.Name))
            .Select(s => s.Name).Distinct().ToListAsync(ct);

    /// <summary>Connections of one type a project can use: its own when it has any of that type, else the shared ones.</summary>
    public static async Task<List<GitConnection>> ConnectionsAsync(IAppDbContext db, Guid orgId, Guid projectId,
        ConnectionType type, CancellationToken ct) =>
        await db.Connections.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.OrgId == orgId && c.Type == type && (c.ProjectId == projectId || c.ProjectId == null))
            .ToListAsync(ct);
}
