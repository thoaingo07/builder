using Builder.Domain.Organizations;

namespace Builder.Domain.Connections;

public enum ConnectionType { AzureDevOps, Git, Azure, Registry }

/// <summary>Pat: a stored token. ServicePrincipal: Entra client credentials; the API mints short-lived tokens.</summary>
public enum ConnectionAuthKind { Pat, ServicePrincipal }

/// <summary>
/// Credentials for a git host (Azure DevOps, plain git) or for Azure (container registries).
/// <see cref="TokenProtected"/> holds ciphertext: the PAT, or the service principal's client secret.
/// </summary>
public sealed class GitConnection : IOrgScoped
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid OrgId { get; private set; }
    public string Name { get; private set; } = "";
    public ConnectionType Type { get; private set; }
    /// <summary>Azure DevOps organization URL (https://dev.azure.com/org) or a git host base URL.</summary>
    public string Url { get; private set; } = "";
    public string? Username { get; private set; }
    public string? TokenProtected { get; private set; }
    public ConnectionAuthKind AuthKind { get; private set; } = ConnectionAuthKind.Pat;
    public string? TenantId { get; private set; }
    public string? ClientId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private GitConnection() { }

    public GitConnection(Guid orgId, string name, ConnectionType type, string url, DateTimeOffset now)
    {
        OrgId = orgId;
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

    /// <summary>Use an Entra service principal (client credentials) instead of a PAT.</summary>
    public void UseServicePrincipal(string tenantId, string clientId)
    {
        if (Type is ConnectionType.Git or ConnectionType.Registry) throw new DomainException("Plain git and container registry connections use a token.");
        if (!Guid.TryParse(tenantId, out _) && !tenantId.Contains('.'))
            throw new DomainException("Tenant must be the directory (tenant) id or its domain.");
        if (!Guid.TryParse(clientId, out _)) throw new DomainException("Client id must be the application (client) id GUID.");
        AuthKind = ConnectionAuthKind.ServicePrincipal;
        TenantId = tenantId.Trim();
        ClientId = clientId.Trim();
    }

    public void UsePat()
    {
        if (Type == ConnectionType.Azure) throw new DomainException("Azure connections use a service principal.");
        AuthKind = ConnectionAuthKind.Pat;
        TenantId = null;
        ClientId = null;
    }
}
