namespace Builder.Domain.Agents;

public sealed class Agent
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
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

    public Agent(string name, DateTimeOffset now)
    {
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

    public bool Matches(IReadOnlyCollection<string> requiredLabels) =>
        requiredLabels.All(l => Labels.Contains(l.ToLowerInvariant()));
}
