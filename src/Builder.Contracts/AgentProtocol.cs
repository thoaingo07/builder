namespace Builder.Contracts;

/// <summary>SignalR hub route and method names shared by the API and the agents.</summary>
public static class AgentHubNames
{
    public const string Route = "/hubs/agent";
    public const string TokenHeader = "X-Agent-Token";

    // agent → server
    public const string Register = nameof(Register);
    public const string Heartbeat = nameof(Heartbeat);
    public const string JobStarted = nameof(JobStarted);
    public const string JobLog = nameof(JobLog);
    public const string JobCompleted = nameof(JobCompleted);
    public const string CleanupCompleted = nameof(CleanupCompleted);
    public const string GetJobSecrets = nameof(GetJobSecrets);
    public const string GetJobCredentials = nameof(GetJobCredentials);
    public const string GetTeardownCredentials = nameof(GetTeardownCredentials);
    public const string TeardownCompleted = nameof(TeardownCompleted);

    // server → agent
    public const string AssignJob = nameof(AssignJob);
    public const string CancelJob = nameof(CancelJob);
    public const string Cleanup = nameof(Cleanup);
    public const string Teardown = nameof(Teardown);
    /// <summary>The build finished: delete its checkout unless the agent keeps workspaces.</summary>
    public const string ReleaseBuild = nameof(ReleaseBuild);
}

public sealed record AgentHello(
    string Name,
    string HostName,
    string Os,
    string Version,
    int Capacity,
    string[] Labels,
    bool HasTask,
    bool HasDocker,
    bool HasKubectl,
    bool HasAz,
    bool HasSsh,
    Guid[] RunningJobIds);

public sealed record AgentWelcome(Guid AgentId, Guid[] JobsToAbort);

public sealed record AgentMetrics(
    double CpuPercent,
    int CpuCount,
    long MemoryTotalBytes,
    long MemoryUsedBytes,
    long DiskTotalBytes,
    long DiskUsedBytes,
    double LoadAverage1,
    int RunningJobs,
    Guid[] RunningJobIds);

public enum LogStream { Out, Err, System }

public sealed record LogChunk(DateTimeOffset Timestamp, LogStream Stream, string Text);

/// <summary>What to fetch. Credentials are not part of the assignment: the agent asks for them when the job starts.</summary>
public sealed record GitSource(string Url, string Branch, string Commit);

public sealed record RegistryLogin(string Server, string Username, string Password);

/// <summary>
/// Short-lived credentials for one job, fetched by the agent running it (GetJobCredentials). Entra tokens last
/// about an hour, ACR tokens about three; PAT-based connections hand out the PAT.
/// </summary>
public sealed record JobCredentials(
    string? GitAuthorization,
    RegistryLogin[] Registries,
    string? AzureDevOpsToken,
    DateTimeOffset? ExpiresAt,
    DeploySecrets? Deploy = null);

/// <summary>An environment's secrets, fetched by the agent that deploys to it (or tears it down), never pushed.</summary>
public sealed record DeploySecrets(string? PrivateKey, string? Kubeconfig, string? AksClientSecret);

public sealed record ArtifactRef(Guid ArtifactId, string Name, string DownloadPath);

public enum DeployTargetType { SshDocker, Kubernetes }

public sealed record DeployTarget(
    DeployTargetType Type,
    string EnvironmentName,
    // ssh-docker
    string? Host,
    int Port,
    string? Username,
    // kubernetes
    string? AksTenantId,
    string? AksClientId,
    string? AksSubscriptionId,
    string? AksResourceGroup,
    string? AksClusterName,
    bool AksAdmin,
    // what to deploy
    string? ComposeFile,
    string? Project,
    string? Manifests,
    string? Namespace,
    string? Url);

public sealed record JobAssignment(
    Guid JobId,
    Guid BuildId,
    int BuildNumber,
    string PipelineName,
    string TaskName,
    Dictionary<string, string> TaskVars,
    GitSource Source,
    string TaskfilePath,
    Dictionary<string, string> Env,
    string[] UploadArtifacts,
    ArtifactRef[] DownloadArtifacts,
    DeployTarget? Deploy,
    string[] Secrets);

public sealed record JobResult(Guid JobId, bool Succeeded, int ExitCode, string? Error, bool Canceled);

public sealed record CleanupRequest(Guid RequestId, Guid[] KeepBuildIds, bool RemoveWorkspaces, bool DockerPrune);

public sealed record CleanupResult(Guid RequestId, long FreedBytes, string Output);

public sealed record TeardownRequest(Guid DeploymentId, DeployTarget Target);

public sealed record TeardownResult(Guid DeploymentId, bool Succeeded, string Output);
