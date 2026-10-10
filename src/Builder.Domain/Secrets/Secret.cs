using System.Text.RegularExpressions;
using Builder.Domain.Organizations;

namespace Builder.Domain.Secrets;

/// <summary>
/// A named value of an organization (token, password, connection string). Runner tasks ask for it with
/// <c>x-secrets: [NAME]</c>; the agent running the job fetches it at run time. <see cref="ValueProtected"/> is ciphertext.
/// </summary>
public sealed partial class Secret : Projects.IProjectScoped
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid OrgId { get; private set; }
    /// <summary>Null: shared by the whole organization.</summary>
    public Guid? ProjectId { get; private set; }
    public string Name { get; private set; } = "";
    public string ValueProtected { get; private set; } = "";
    public string? Description { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public string UpdatedBy { get; private set; } = "";

    private Secret() { }

    public Secret(Guid orgId, Guid? projectId, string name, string valueProtected, string? description, string user, DateTimeOffset now)
    {
        OrgId = orgId;
        ProjectId = projectId;
        Name = ValidName(name);
        CreatedAt = now;
        Update(valueProtected, description, user, now);
    }

    public void Update(string? valueProtected, string? description, string user, DateTimeOffset now)
    {
        if (valueProtected is not null) ValueProtected = valueProtected;
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        UpdatedBy = user;
        UpdatedAt = now;
    }

    /// <summary>Null: shared by the whole organization.</summary>
    public void MoveTo(Guid? projectId) => ProjectId = projectId;

    public static string ValidName(string name)
    {
        var n = name.Trim();
        if (!NamePattern().IsMatch(n))
            throw new DomainException($"'{name}' is not a valid secret name: use UPPER_SNAKE_CASE (A-Z, 0-9, _), starting with a letter.");
        return n;
    }

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,99}$")]
    private static partial Regex NamePattern();
}
