using Builder.Domain.Organizations;

namespace Builder.Domain.Deployments;

public enum EnvironmentType { SshDocker, Kubernetes }

/// <summary>A deploy target. Secret fields hold ciphertext produced by the application's secret protector.</summary>
public sealed class DeployEnvironment : IOrgScoped
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid OrgId { get; private set; }
    public string Name { get; private set; } = "";
    public EnvironmentType Type { get; private set; }
    public bool RequiresApproval { get; private set; }
    public List<string> Approvers { get; private set; } = new();
    public List<string> AgentLabels { get; private set; } = new();

    // ssh-docker
    public string? Host { get; private set; }
    public int Port { get; private set; } = 22;
    public string? Username { get; private set; }
    public string? PrivateKeyProtected { get; private set; }

    // kubernetes
    public string? KubeconfigProtected { get; private set; }
    public string? AksTenantId { get; private set; }
    public string? AksClientId { get; private set; }
    public string? AksClientSecretProtected { get; private set; }
    public string? AksSubscriptionId { get; private set; }
    public string? AksResourceGroup { get; private set; }
    public string? AksClusterName { get; private set; }
    public bool AksAdmin { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private DeployEnvironment() { }

    public DeployEnvironment(Guid orgId, string name, EnvironmentType type, DateTimeOffset now)
    {
        OrgId = orgId;
        Rename(name);
        Type = type;
        CreatedAt = now;
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Environment name is required.");
        Name = name.Trim();
    }

    public void SetPolicy(bool requiresApproval, IEnumerable<string> approvers, IEnumerable<string> agentLabels)
    {
        RequiresApproval = requiresApproval;
        Approvers = approvers.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList();
        AgentLabels = agentLabels.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim().ToLowerInvariant()).ToList();
    }

    public void SetSsh(string? host, int port, string? username, string? privateKeyProtected)
    {
        Type = EnvironmentType.SshDocker;
        Host = host;
        Port = port <= 0 ? 22 : port;
        Username = username;
        if (privateKeyProtected is not null) PrivateKeyProtected = privateKeyProtected;
    }

    public void SetKubernetes(string? kubeconfigProtected, string? tenantId, string? clientId, string? clientSecretProtected,
        string? subscriptionId, string? resourceGroup, string? clusterName, bool admin)
    {
        Type = EnvironmentType.Kubernetes;
        if (kubeconfigProtected is not null) KubeconfigProtected = kubeconfigProtected;
        AksTenantId = tenantId;
        AksClientId = clientId;
        if (clientSecretProtected is not null) AksClientSecretProtected = clientSecretProtected;
        AksSubscriptionId = subscriptionId;
        AksResourceGroup = resourceGroup;
        AksClusterName = clusterName;
        AksAdmin = admin;
    }
}

public enum DeploymentStatus { Deploying, Active, Failed, Destroying, Destroyed }

public sealed class Deployment : IOrgScoped
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid OrgId { get; private set; }
    public Guid EnvironmentId { get; private set; }
    public string EnvironmentName { get; private set; } = "";
    public Guid PipelineId { get; private set; }
    public Guid BuildId { get; private set; }
    public int BuildNumber { get; private set; }
    public Guid JobId { get; private set; }
    public string Name { get; private set; } = "";   // compose project or k8s namespace
    public string? Compose { get; private set; }
    public string? Manifests { get; private set; }
    public string? Url { get; private set; }
    public DeploymentStatus Status { get; private set; } = DeploymentStatus.Deploying;
    public string? Output { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    /// <summary>The agent asked to tear this deployment down (the only one allowed to fetch its credentials).</summary>
    public Guid? TeardownAgentId { get; private set; }

    private Deployment() { }

    public Deployment(DeployEnvironment env, Guid pipelineId, Guid buildId, int buildNumber, Guid jobId,
        string name, string? compose, string? manifests, string? url, DateTimeOffset now)
    {
        OrgId = env.OrgId;
        EnvironmentId = env.Id;
        EnvironmentName = env.Name;
        PipelineId = pipelineId;
        BuildId = buildId;
        BuildNumber = buildNumber;
        JobId = jobId;
        Name = name;
        Compose = compose;
        Manifests = manifests;
        Url = url;
        CreatedAt = now;
    }

    public void Completed(bool succeeded, DateTimeOffset now)
    {
        Status = succeeded ? DeploymentStatus.Active : DeploymentStatus.Failed;
        UpdatedAt = now;
    }

    /// <summary>A newer deployment of the same app to the same environment replaced this one.</summary>
    public void Superseded(DateTimeOffset now)
    {
        Status = DeploymentStatus.Destroyed;
        Output = "Superseded by a newer deployment";
        UpdatedAt = now;
    }

    public void Destroying(Guid agentId, DateTimeOffset now)
    {
        if (Status == DeploymentStatus.Destroyed) throw new DomainException("Deployment is already destroyed.");
        Status = DeploymentStatus.Destroying;
        TeardownAgentId = agentId;
        UpdatedAt = now;
    }

    public void Destroyed(bool succeeded, string output, DateTimeOffset now)
    {
        Status = succeeded ? DeploymentStatus.Destroyed : DeploymentStatus.Failed;
        Output = output;
        UpdatedAt = now;
    }
}
