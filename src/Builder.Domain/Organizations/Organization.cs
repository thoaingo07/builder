using System.Text.RegularExpressions;

namespace Builder.Domain.Organizations;

public enum OrgRole { Member, Admin, Owner }

/// <summary>Anything that belongs to exactly one organization.</summary>
public interface IOrgScoped
{
    Guid OrgId { get; }
}

public sealed partial class Organization
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public string Name { get; private set; } = "";
    public string Slug { get; private set; } = "";
    /// <summary>SHA-256 (hex) of the organization's agent registration token; the token itself is shown once.</summary>
    public string? AgentTokenHash { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }

    private Organization() { }

    public Organization(string name, string createdBy, DateTimeOffset now)
    {
        Rename(name);
        Slug = Slugify(Name);
        CreatedBy = createdBy;
        CreatedAt = now;
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Organization name is required.");
        if (name.Trim().Length > 100) throw new DomainException("Organization name is too long (max 100).");
        Name = name.Trim();
    }

    /// <summary>Used when the slug is taken: keep the name, make the slug unique.</summary>
    public void UseSlug(string slug) => Slug = slug;

    public void SetAgentTokenHash(string hash) => AgentTokenHash = hash;

    public static string Slugify(string name)
    {
        var slug = NonSlug().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        if (slug.Length == 0) slug = "org";
        return slug.Length > 50 ? slug[..50].Trim('-') : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();
}

public sealed class Membership
{
    public Guid OrgId { get; private set; }
    public Guid UserId { get; private set; }
    public OrgRole Role { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private Membership() { }

    public Membership(Guid orgId, Guid userId, OrgRole role, DateTimeOffset now)
    {
        OrgId = orgId;
        UserId = userId;
        Role = role;
        CreatedAt = now;
    }

    public void ChangeRole(OrgRole role) => Role = role;
}
