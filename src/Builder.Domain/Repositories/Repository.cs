using Builder.Domain.Organizations;

namespace Builder.Domain.Repositories;

/// <summary>A git repository added to an organization. Its runner files live under <see cref="RunnersFolder"/>.</summary>
public sealed class Repository : IOrgScoped
{
    public const string RunnersFolder = ".builder/runners";

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid OrgId { get; private set; }
    public Guid? ConnectionId { get; private set; }
    public string Name { get; private set; } = "";
    public string Url { get; private set; } = "";
    public string DefaultBranch { get; private set; } = "main";
    public DateTimeOffset CreatedAt { get; private set; }

    private Repository() { }

    public Repository(Guid orgId, Guid? connectionId, string? name, string url, string? defaultBranch, DateTimeOffset now)
    {
        OrgId = orgId;
        CreatedAt = now;
        Update(connectionId, name, url, defaultBranch);
    }

    public void Update(Guid? connectionId, string? name, string url, string? defaultBranch)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new DomainException("Repository URL is required.");
        Url = url.Trim();
        ConnectionId = connectionId;
        Name = string.IsNullOrWhiteSpace(name) ? NameFromUrl(Url) : name.Trim();
        DefaultBranch = string.IsNullOrWhiteSpace(defaultBranch) ? "main" : defaultBranch.Trim();
    }

    public static string NameFromUrl(string url)
    {
        var last = url.TrimEnd('/').Split('/', ':').Last();
        return last.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? last[..^4] : last;
    }

    /// <summary>True for a path like <c>.builder/runners/ci.yml</c> (directly in the runners folder).</summary>
    public static bool IsRunnerFile(string path) =>
        path.StartsWith(RunnersFolder + "/", StringComparison.Ordinal)
        && !path[(RunnersFolder.Length + 1)..].Contains('/')
        && (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
}
