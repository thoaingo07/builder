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

    /// <summary>Shared agents serve every organization; others only their own.</summary>
    public bool Serves(Guid orgId) => OrgId is null || OrgId == orgId;

    public bool Matches(IReadOnlyCollection<string> requiredLabels) =>
        requiredLabels.All(l => Labels.Contains(l.ToLowerInvariant()));
}
