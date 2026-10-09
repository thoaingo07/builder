using Builder.Application.Dtos;
using Builder.Contracts;
using Builder.Domain.Agents;
using Builder.Domain.Builds;
using Builder.Domain.Connections;
using Builder.Domain.Deployments;
using Builder.Domain.Pipelines;
using Builder.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Builder.Application.Abstractions;

public interface IAppDbContext
{
    DbSet<Pipeline> Pipelines { get; }
    DbSet<Build> Builds { get; }
    DbSet<BuildJob> BuildJobs { get; }
    DbSet<LogLine> LogLines { get; }
    DbSet<Artifact> Artifacts { get; }
    DbSet<Agent> Agents { get; }
    DbSet<DeployEnvironment> Environments { get; }
    DbSet<Deployment> Deployments { get; }
    DbSet<GitConnection> Connections { get; }
    DbSet<User> Users { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>A git remote plus the HTTP Authorization header to use with it (never persisted to disk).</summary>
public sealed record GitRemote(string Url, string? AuthorizationHeader);

public interface IGitService
{
    /// <summary>Resolves a branch name to a commit SHA.</summary>
    Task<string> ResolveBranchAsync(GitRemote remote, string branch, CancellationToken ct);

    /// <summary>Reads a file at a commit; null when the file does not exist.</summary>
    Task<string?> ReadFileAsync(GitRemote remote, string branch, string commit, string path, CancellationToken ct);

    /// <summary>Writes one file on a branch, commits and pushes. Returns the new commit SHA.</summary>
    Task<string> CommitFileAsync(GitRemote remote, string branch, string path, string content, string message,
        string authorName, string authorEmail, CancellationToken ct);
}

public sealed class TaskfileException(string message) : Exception(message);

public sealed record PlannedJob(
    string Key,
    string TaskName,
    string? Description,
    int Order,
    IReadOnlyList<string> DependsOn,
    Dictionary<string, string> TaskVars,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Artifacts,
    bool HasCommands,
    ApprovalSpec? Approval,
    DeploySpec? Deploy);

public sealed record TaskfilePlan(string EntryTask, IReadOnlyList<PlannedJob> Jobs);

public interface ITaskfilePlanner
{
    /// <summary>Builds the job graph reachable from the entry task. Throws <see cref="TaskfileException"/>.</summary>
    TaskfilePlan Plan(string taskfileYaml, string? entryTask);
}

public interface IAgentGateway
{
    bool IsConnected(Guid agentId);
    Task<bool> AssignJobAsync(Guid agentId, JobAssignment assignment, CancellationToken ct);
    Task CancelJobAsync(Guid agentId, Guid jobId, CancellationToken ct);
    Task<bool> CleanupAsync(Guid agentId, CleanupRequest request, CancellationToken ct);
    Task<bool> TeardownAsync(Guid agentId, TeardownRequest request, CancellationToken ct);
}

public interface IUiNotifier
{
    Task BuildUpdated(BuildSummaryDto build);
    Task JobUpdated(JobDto job);
    Task Log(Guid buildId, IReadOnlyList<LogLineDto> lines);
    Task AgentsUpdated(IReadOnlyList<AgentDto> agents);
    Task DeploymentUpdated(DeploymentDto deployment);
}

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string hash, string password);
}

public interface IAzureDevOpsClient
{
    Task<IReadOnlyList<RepositoryDto>> ListRepositoriesAsync(string organizationUrl, string? username, string token, CancellationToken ct);
}

public interface IArtifactStore
{
    Task<(string StoragePath, long Size)> SaveAsync(Guid buildId, Guid jobId, string name, Stream content, CancellationToken ct);
    Stream OpenRead(string storagePath);
    void Delete(string storagePath);
    void DeleteBuild(Guid buildId);
}

/// <summary>Latest metrics per agent, kept in memory (not persisted).</summary>
public interface IAgentMetricsStore
{
    void Record(Guid agentId, AgentMetricsDto metrics);
    AgentMetricsDto? Latest(Guid agentId);
    IReadOnlyList<AgentMetricsDto> History(Guid agentId);
    void Forget(Guid agentId);
}

/// <summary>
/// Serialises mutations of build state (scheduler, agent reports, approvals, cancel).
/// The API runs as a single instance, so an in-process lock is enough.
/// </summary>
public interface IBuildLock
{
    Task<IDisposable> AcquireAsync(CancellationToken ct);
}

/// <summary>Wakes the scheduler / planner immediately instead of waiting for the next poll.</summary>
public interface ISchedulerSignal
{
    void Wake();
    Task WaitAsync(TimeSpan timeout, CancellationToken ct);
}
