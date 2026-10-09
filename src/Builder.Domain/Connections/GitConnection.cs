namespace Builder.Domain.Connections;

public enum ConnectionType { AzureDevOps, Git }

/// <summary>Credentials for a git host. <see cref="TokenProtected"/> holds ciphertext.</summary>
public sealed class GitConnection
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public string Name { get; private set; } = "";
    public ConnectionType Type { get; private set; }
    /// <summary>Azure DevOps organization URL (https://dev.azure.com/org) or a git host base URL.</summary>
    public string Url { get; private set; } = "";
    public string? Username { get; private set; }
    public string? TokenProtected { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private GitConnection() { }

    public GitConnection(string name, ConnectionType type, string url, DateTimeOffset now)
    {
        CreatedAt = now;
        Update(name, type, url, null, null);
    }

    public void Update(string name, ConnectionType type, string url, string? username, string? tokenProtected)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Connection name is required.");
        Name = name.Trim();
        Type = type;
        Url = url.Trim().TrimEnd('/');
        Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        if (tokenProtected is not null) TokenProtected = tokenProtected;
    }
}
