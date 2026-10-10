using Builder.Domain.Organizations;

namespace Builder.Domain.Projects;

/// <summary>
/// A group of repositories inside an organization (e.g. one product). Repositories, runners, builds and deployments
/// belong to one project; connections, environments and secrets belong to a project or are shared by the
/// organization (no project). A name a runner asks for resolves in its project first, then in the shared ones.
/// </summary>
public sealed class Project : IOrgScoped
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid OrgId { get; private set; }
    public string Name { get; private set; } = "";
    public string Slug { get; private set; } = "";
    public string? Description { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private Project() { }

    public Project(Guid orgId, string name, string? description, DateTimeOffset now)
    {
        OrgId = orgId;
        CreatedAt = now;
        Update(name, description);
        Slug = Organization.Slugify(Name);
    }

    public void Update(string name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Project name is required.");
        if (name.Trim().Length > 100) throw new DomainException("Project name is too long (max 100).");
        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    /// <summary>Used when the slug is taken: keep the name, make the slug unique.</summary>
    public void UseSlug(string slug) => Slug = slug;
}

/// <summary>Connections, environments and secrets: a project's own, or shared by the organization (ProjectId null).</summary>
public interface IProjectScoped : IOrgScoped
{
    Guid? ProjectId { get; }
    string Name { get; }
}

public static class ProjectScope
{
    /// <summary>Of the candidates with one name, the project's own wins over the shared one.</summary>
    public static T? Pick<T>(IEnumerable<T> candidates, Guid projectId, string name) where T : class, IProjectScoped =>
        candidates.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.ProjectId == projectId ? 0 : 1).FirstOrDefault(c => c.ProjectId == projectId || c.ProjectId is null);

    /// <summary>What a project sees: its own, plus shared ones it does not override by name.</summary>
    public static List<T> Visible<T>(IEnumerable<T> candidates, Guid projectId) where T : class, IProjectScoped =>
        candidates.Where(c => c.ProjectId == projectId || c.ProjectId is null)
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(c => c.ProjectId == projectId ? 0 : 1).First()).ToList();
}
