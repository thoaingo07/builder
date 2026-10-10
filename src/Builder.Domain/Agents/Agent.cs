namespace Builder.Domain.Agents;

public sealed class Agent
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    /// <summary>Owning organization; null for a shared agent that serves every organization.</summary>
    public Guid? OrgId { get; private set; }
    public string Name { get; private set; } = "";
    public string HostName { get; private set; } = "";
    public string Os { get; private set; } = "";
    public string Version { get; private set; } = "";
    public int Capacity { get; private set; } = 2;
    public List<string> Labels { get; private set; } = new();
    public bool Enabled { get; private set; } = true;
    public bool Online { get; private set; }
    public string? ConnectionId { get; private set; }
    public DateTimeOffset? LastSeenAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    /// <summary>Work folder set in Builder (null: the agent's own default).</summary>
    public string? WorkDirectory { get; private set; }
    /// <summary>The work folder from the agent's configuration, as it reported when connecting.</summary>
    public string? DefaultWorkDirectory { get; private set; }
    /// <summary>The work folder the agent uses now, and why it could not switch to <see cref="WorkDirectory"/>.</summary>
    public string? EffectiveWorkDirectory { get; private set; }
    public string? WorkDirectoryError { get; private set; }

    private Agent() { }

    public Agent(Guid? orgId, string name, DateTimeOffset now)
    {
        OrgId = orgId;
        Name = name;
        CreatedAt = now;
    }

    public void Connected(string connectionId, string hostName, string os, string version, int capacity,
        IEnumerable<string> labels, DateTimeOffset now)
    {
        ConnectionId = connectionId;
        HostName = hostName;
        Os = os;
        Version = version;
        Capacity = Math.Max(1, capacity);
        Labels = labels.Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length > 0).Distinct().ToList();
        Online = true;
        LastSeenAt = now;
    }

    public void Seen(DateTimeOffset now) => LastSeenAt = now;

    public void Disconnected(DateTimeOffset now)
    {
        Online = false;
        ConnectionId = null;
        LastSeenAt = now;
    }

    public void SetEnabled(bool enabled) => Enabled = enabled;

    /// <summary>Empty or whitespace: back to the agent's own default.</summary>
    public void SetWorkDirectory(string? path)
    {
        var p = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        if (p is not null && !(p.StartsWith('/') || p.StartsWith('~') || (p.Length > 2 && p[1] == ':')))
            throw new DomainException("The work folder must be an absolute path (e.g. /srv/builder or D:\\builder).");
        if (p is "/" or "~" or "~/") throw new DomainException("Pick a dedicated folder, not / or the home folder.");
        WorkDirectory = p;
    }

    public void ReportedDefaultWorkDirectory(string? path) => DefaultWorkDirectory = path;

    public void WorkDirectoryApplied(string effective, string? error)
    {
        EffectiveWorkDirectory = effective;
        WorkDirectoryError = error;
    }

    /// <summary>Shared agents serve every organization; others only their own.</summary>
    public bool Serves(Guid orgId) => OrgId is null || OrgId == orgId;

    public bool Matches(IReadOnlyCollection<string> requiredLabels) =>
        requiredLabels.All(l => Labels.Contains(l.ToLowerInvariant()));
}
