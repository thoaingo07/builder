using Builder.Application.Dtos;
using Builder.Contracts;
using Builder.Domain.Agents;
using Builder.Domain.Builds;
using Builder.Domain.Connections;
using Builder.Domain.Deployments;
using Builder.Domain.Organizations;
using Builder.Domain.Pipelines;
using Builder.Domain.Repositories;
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
    DbSet<Organization> Organizations { get; }
    DbSet<Membership> Memberships { get; }
    DbSet<Repository> Repositories { get; }
    DbSet<Domain.Secrets.Secret> Secrets { get; }
    DbSet<Domain.Triggers.RunnerSchedule> RunnerSchedules { get; }
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

    /// <summary>Files directly inside <paramref name="folder"/> at a commit (paths relative to the repository root).</summary>
    Task<IReadOnlyList<string>> ListFilesAsync(GitRemote remote, string branch, string commit, string folder, CancellationToken ct);

    /// <summary>Paths changed between two commits (null when the host can't tell: then path filters don't apply).</summary>
    Task<IReadOnlyList<string>?> ChangedFilesAsync(GitRemote remote, string branch, string baseCommit, string headCommit, CancellationToken ct);

    /// <summary>Branch names on the remote.</summary>
    Task<IReadOnlyList<string>> ListBranchesAsync(GitRemote remote, CancellationToken ct);

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
    DeploySpec? Deploy,
    IReadOnlyList<string> Secrets,
    IReadOnlyList<RegistrySpec> Registries,
    bool AzureArtifacts,
    IReadOnlyList<JobStep> Steps,
    IReadOnlyList<InputSpec> Inputs);

public sealed record TaskfilePlan(string EntryTask, IReadOnlyList<PlannedJob> Jobs)
{
    /// <summary>Variables the run needs (requires.vars of every task in the graph), merged by name.</summary>
    public IReadOnlyList<(InputSpec Input, IReadOnlyList<string> RequiredBy)> Inputs =>
        Jobs.SelectMany(j => j.Inputs.Select(i => (i, j.TaskName)))
            .GroupBy(x => x.i.Name)
            .Select(g => (new InputSpec(g.Key, g.Select(x => x.i.Enum).FirstOrDefault(e => e is { Count: > 0 })),
                (IReadOnlyList<string>)g.Select(x => x.TaskName).Distinct().ToList()))
            .ToList();
}

public interface ITaskfilePlanner
{
    /// <summary>Builds the job graph reachable from the entry task. Throws <see cref="TaskfileException"/>.</summary>
    TaskfilePlan Plan(string taskfileYaml, string? entryTask);

    /// <summary>The runner file's <c>x-builder.triggers</c> (push, pull-request, schedule). Throws <see cref="TaskfileException"/>.</summary>
    Domain.Triggers.TriggerSpec Triggers(string taskfileYaml);
}

public interface IAgentGateway
{
    bool IsConnected(Guid agentId);
    Task<bool> AssignJobAsync(Guid agentId, JobAssignment assignment, CancellationToken ct);
    Task CancelJobAsync(Guid agentId, Guid jobId, CancellationToken ct);
    Task<bool> CleanupAsync(Guid agentId, CleanupRequest request, CancellationToken ct);
    Task<bool> TeardownAsync(Guid agentId, TeardownRequest request, CancellationToken ct);
    Task ReleaseBuildAsync(Guid agentId, Guid buildId, CancellationToken ct);
}

/// <summary>Live updates for the UI; organization-wide events only reach that organization's members.</summary>
public interface IUiNotifier
{
    Task BuildUpdated(Guid orgId, BuildSummaryDto build);
    Task JobUpdated(JobDto job);
    Task Log(Guid buildId, IReadOnlyList<LogLineDto> lines);
    Task AgentsUpdated(Guid orgId, IReadOnlyList<AgentDto> agents);
    Task DeploymentUpdated(Guid orgId, DeploymentDto deployment);
}

/// <summary>
/// The organization the current request acts in (set by the API from the X-Org header after checking
/// membership). Null outside a request (scheduler, agents) - then queries are not organization-filtered.
/// </summary>
public interface ICurrentOrg
{
    Guid? OrgId { get; }
    OrgRole? Role { get; }
    Guid? UserId { get; }
    void Set(Guid orgId, OrgRole role, Guid userId);

    Guid RequireOrgId() => OrgId ?? throw new ForbiddenException("Select an organization first.");

    void RequireRole(OrgRole minimum)
    {
        if (Role is null || Role < minimum) throw new ForbiddenException($"This needs the {minimum} role in the organization.");
    }
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

/// <summary>Azure DevOps REST (api-version 7.1). <paramref name="authorization"/> is a full Authorization header value.</summary>
public interface IAzureDevOpsClient
{
    /// <summary>Checks the organization URL and credentials; returns the number of visible projects.</summary>
    Task<int> TestAsync(string organizationUrl, string authorization, CancellationToken ct);
    Task<IReadOnlyList<string>> ListProjectsAsync(string organizationUrl, string authorization, CancellationToken ct);
    /// <summary>Enabled repositories, of one project or of the whole organization.</summary>
    Task<IReadOnlyList<RemoteRepositoryDto>> ListRepositoriesAsync(string organizationUrl, string? project, string authorization, CancellationToken ct);

    /// <summary>
    /// Creates service-hook subscriptions (git.push, git.pullrequest.created/updated) for one repository that post to
    /// <paramref name="hookUrl"/> with the given header, replacing earlier ones to the same URL. Returns how many exist now.
    /// </summary>
    Task<int> InstallWebhooksAsync(string organizationUrl, string authorization, string project, string repository,
        string hookUrl, string headerName, string headerValue, CancellationToken ct);
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

public sealed class CurrentOrg : ICurrentOrg
{
    public Guid? OrgId { get; private set; }
    public OrgRole? Role { get; private set; }
    public Guid? UserId { get; private set; }

    public void Set(Guid orgId, OrgRole role, Guid userId)
    {
        OrgId = orgId;
        Role = role;
        UserId = userId;
    }
}

public sealed record AccessToken(string Token, DateTimeOffset ExpiresOn);

/// <summary>Entra ID client-credentials tokens (cached until shortly before they expire).</summary>
public interface IEntraTokens
{
    public const string AzureDevOpsScope = "499b84ac-1321-427f-aa17-267ca6975798/.default";
    public const string ArmScope = "https://management.azure.com/.default";

    Task<AccessToken> GetAsync(string tenantId, string clientId, string clientSecret, string scope, CancellationToken ct);
}

/// <summary>Azure Container Registry: exchanges an Entra (ARM) token for a registry refresh token (~3 h) usable with docker login.</summary>
public interface IAcrTokens
{
    public const string DockerUser = "00000000-0000-0000-0000-000000000000";

    Task<AccessToken> ExchangeAsync(string registry, string tenantId, string armToken, CancellationToken ct);
}

/// <summary>What a build reports back to the git host (Azure DevOps: commit status, or pull request status).</summary>
public sealed record BuildStatusReport(string Commit, int? PullRequestId, string Name, string State, string Description, string? TargetUrl);

/// <summary>Reports build status to the git host; hosts without status support ignore it.</summary>
public interface IGitHostStatus
{
    Task ReportAsync(GitRemote remote, BuildStatusReport report, CancellationToken ct);
}

/// <summary>Where Builder's UI is reachable (links in statuses and webhook URLs). Builder:PublicUrl.</summary>
public sealed class BuilderLinks
{
    public string? PublicUrl { get; set; }
    public string? BuildUrl(Guid buildId) => PublicUrl is { Length: > 0 } u ? $"{u.TrimEnd('/')}/builds/{buildId}" : null;
}
