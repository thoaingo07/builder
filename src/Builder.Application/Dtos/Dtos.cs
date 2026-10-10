using Builder.Domain.Builds;
using Builder.Domain.Connections;
using Builder.Domain.Deployments;
using Builder.Domain.Organizations;

namespace Builder.Application.Dtos;

public sealed record UserDto(string UserName, string DisplayName, bool IsAdmin, string? Email = null);

public sealed record JobCounts(int Total, int Pending, int Running, int WaitingApproval, int Succeeded, int Failed, int Skipped, int Canceled);

public sealed record BuildSummaryDto(
    Guid Id, Guid PipelineId, string PipelineName, int Number, string Branch, string? Commit, string EntryTask,
    BuildStatus Status, string RequestedBy, string? Error,
    DateTimeOffset QueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, JobCounts JobCounts,
    BuildReason Reason = BuildReason.Manual, int? PullRequestId = null);

public sealed record ApprovalDto(string Message, List<string> Approvers, string? DecidedBy, DateTimeOffset? DecidedAt, string? Comment);

public sealed record JobDto(
    Guid Id, Guid BuildId, string Key, string TaskName, string? Description, int Order, List<string> DependsOn,
    List<string> Labels, List<string> Artifacts, List<string> Secrets, JobStatus Status, Guid? AgentId, string? AgentName,
    int? ExitCode, string? Error, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    ApprovalDto? Approval, DeploySpec? Deploy, List<JobStep> Steps);

public sealed record ArtifactDto(Guid Id, Guid JobId, string Name, long SizeBytes, DateTimeOffset CreatedAt);

public sealed record BuildDetailDto(
    Guid Id, Guid PipelineId, string PipelineName, int Number, string Branch, string? Commit, string EntryTask,
    BuildStatus Status, string RequestedBy, string? Error,
    DateTimeOffset QueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, JobCounts JobCounts,
    Dictionary<string, string> Variables, List<JobDto> Jobs, List<ArtifactDto> Artifacts, List<DeploymentDto> Deployments,
    BuildReason Reason, int? PullRequestId, string? SourceRef);

public sealed record LogLineDto(long Id, Guid JobId, DateTimeOffset Timestamp, LogStreamKind Stream, string Text, int? Step);

/// <summary>A variable a runner needs at run time (requires.vars), for the Run dialog.</summary>
public sealed record RunInputDto(string Name, List<string>? Enum, List<string> RequiredBy);
public sealed record RunInputsDto(string Branch, string EntryTask, List<RunInputDto> Inputs);

/// <summary>A runner: one mapped file from a repository's .builder/runners folder.</summary>
public sealed record PipelineDto(
    Guid Id, string Name, Guid RepositoryId, string RepositoryName, string RepositoryUrl, string DefaultBranch,
    string TaskfilePath, string? EntryTask, BuildSummaryDto? LastBuild);

/// <summary>Rename a runner or change its default entry task (the file it runs never changes).</summary>
public sealed record PipelineInput(string Name, string? EntryTask);

public sealed record TaskfileDto(string Path, string Branch, string Commit, string Content);
public sealed record PlanRequest(string Content, string? EntryTask);
public sealed record PlanJobDto(string Key, string TaskName, List<string> DependsOn, bool Approval, string? Deploy);
public sealed record PlanPreviewDto(string EntryTask, List<PlanJobDto> Jobs, string? Error);

public sealed record QueueBuildInput(string? Branch, string? EntryTask, Dictionary<string, string>? Variables);
public sealed record ApprovalInput(bool Approved, string? Comment);

public sealed record AgentMetricsDto(
    DateTimeOffset At, double CpuPercent, int CpuCount, long MemoryTotalBytes, long MemoryUsedBytes,
    long DiskTotalBytes, long DiskUsedBytes, double LoadAverage1, int RunningJobs);

public sealed record AgentRunningJobDto(Guid BuildId, int BuildNumber, string PipelineName, Guid JobId, string TaskName);

public sealed record AgentDto(
    Guid Id, bool Shared, string Name, string HostName, string Os, string Version, int Capacity, List<string> Labels,
    bool Enabled, bool Online, DateTimeOffset? LastSeenAt, AgentMetricsDto? Metrics, List<AgentRunningJobDto> RunningJobs);

public sealed record AgentUpdateInput(bool Enabled);
public sealed record AgentCleanupInput(bool RemoveWorkspaces, bool DockerPrune);

public sealed record EnvironmentDto(
    Guid Id, string Name, EnvironmentType Type, bool RequiresApproval, List<string> Approvers, List<string> AgentLabels,
    string? Host, int Port, string? Username, bool HasPrivateKey,
    bool HasKubeconfig, string? AksTenantId, string? AksClientId, bool HasAksClientSecret,
    string? AksSubscriptionId, string? AksResourceGroup, string? AksClusterName, bool AksAdmin);

public sealed record EnvironmentInput(
    string Name, EnvironmentType Type, bool RequiresApproval, List<string>? Approvers, List<string>? AgentLabels,
    string? Host, int? Port, string? Username, string? PrivateKey,
    string? Kubeconfig, string? AksTenantId, string? AksClientId, string? AksClientSecret,
    string? AksSubscriptionId, string? AksResourceGroup, string? AksClusterName, bool AksAdmin);

public sealed record DeploymentDto(
    Guid Id, Guid EnvironmentId, string EnvironmentName, Guid PipelineId, string PipelineName, Guid BuildId,
    int BuildNumber, Guid JobId, string Name, string? Url, DeploymentStatus Status, string? Output,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, bool IsContainer);

public sealed record ConnectionDto(Guid Id, string Name, ConnectionType Type, string Url, string? Username, bool HasToken,
    ConnectionAuthKind AuthKind, string? TenantId, string? ClientId);
/// <summary>Token = the PAT, or the client secret when AuthKind is ServicePrincipal (write-only either way).</summary>
public sealed record ConnectionInput(string Name, ConnectionType Type, string Url, string? Username, string? Token,
    ConnectionAuthKind? AuthKind = null, string? TenantId = null, string? ClientId = null);
public sealed record ConnectionTestDto(bool Ok, string Message);
/// <summary>A repository as the git host lists it (Azure DevOps picker).</summary>
public sealed record RemoteRepositoryDto(string Project, string Name, string Url, string? DefaultBranch);

public sealed record RepositoryDto(Guid Id, string Name, string Url, Guid? ConnectionId, string? ConnectionName,
    string DefaultBranch, int RunnerCount, DateTimeOffset CreatedAt);
public sealed record RepositoryInput(Guid? ConnectionId, string? Name, string Url, string? DefaultBranch);

/// <summary>A file in .builder/runners at a branch, with what Builder would run, and whether it is mapped.</summary>
public sealed record RunnerFileDto(string Path, string SuggestedName, string? EntryTask, List<string> Tasks,
    string? Error, Guid? MappedRunnerId, string? MappedRunnerName);
public sealed record RunnerFilesDto(string Branch, string Commit, List<RunnerFileDto> Files);
public sealed record MapRunnerInput(string Path, string? Name, string? EntryTask);
public sealed record MapRunnersInput(List<MapRunnerInput> Runners);

public sealed record OrgDto(Guid Id, string Name, string Slug, OrgRole Role, int MemberCount, DateTimeOffset CreatedAt);
public sealed record OrgInput(string Name);
public sealed record OrgCreatedDto(OrgDto Org, string AgentToken);
public sealed record AgentTokenDto(string AgentToken);
public sealed record MemberDto(Guid UserId, string UserName, string DisplayName, string? Email, OrgRole Role,
    bool CanSignInWithGoogle, DateTimeOffset JoinedAt);
public sealed record AddMemberInput(string Email, OrgRole Role);
public sealed record ChangeRoleInput(OrgRole Role);
public sealed record MeDto(UserDto User, List<OrgDto> Orgs);

/// <summary>Where and how Azure DevOps posts webhooks. Secret is shown once (null after automatic installation).</summary>
public sealed record HookSetupDto(string Url, string Header, string? Secret, int? Installed);
public sealed record ScheduleDto(string Cron, string TimeZone, string Branch, DateTimeOffset? NextRunAt, DateTimeOffset? LastRunAt);
public sealed record PipelineTriggersDto(Domain.Triggers.TriggerSpec Triggers, string? Commit, string? Error, List<ScheduleDto> Schedules);

public sealed record SecretDto(Guid Id, string Name, string? Description, DateTimeOffset UpdatedAt, string UpdatedBy);
public sealed record SecretInput(string Name, string? Value, string? Description);

public sealed record CleanupInput(int OlderThanDays, int KeepLastPerPipeline, bool RemoveWorkspaces, bool DockerPrune);
public sealed record CleanupResultDto(int BuildsDeleted, int ArtifactsDeleted, long BytesFreed, int AgentsNotified);

public sealed record Last24hDto(int Succeeded, int Failed, int Canceled, int Running);
public sealed record DashboardDto(
    List<AgentDto> Agents, List<BuildSummaryDto> ActiveBuilds, List<BuildSummaryDto> RecentBuilds,
    List<DeploymentDto> ActiveDeployments, Last24hDto Last24h);

public sealed record LoginInput(string UserName, string Password);
public sealed record ChangePasswordInput(string CurrentPassword, string NewPassword);
public sealed record ExternalLoginInput(string Provider, string Email, bool EmailVerified, string? DisplayName);

/// <summary>A user allowed to sign in with Google, from configuration (Auth:AllowedUsers).</summary>
public sealed class AllowedUser
{
    public string Email { get; set; } = "";
    public string? DisplayName { get; set; }
    public bool IsAdmin { get; set; }
}
