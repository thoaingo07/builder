using Builder.Domain.Agents;
using Builder.Domain.Builds;
using Builder.Domain.Connections;
using Builder.Domain.Deployments;

namespace Builder.Application.Dtos;

public static class Mapping
{
    public static JobCounts Counts(IReadOnlyCollection<BuildJob> jobs) => new(
        jobs.Count,
        jobs.Count(j => j.Status is JobStatus.Pending or JobStatus.Queued),
        jobs.Count(j => j.Status is JobStatus.Assigned or JobStatus.Running),
        jobs.Count(j => j.Status == JobStatus.WaitingApproval),
        jobs.Count(j => j.Status == JobStatus.Succeeded),
        jobs.Count(j => j.Status == JobStatus.Failed),
        jobs.Count(j => j.Status == JobStatus.Skipped),
        jobs.Count(j => j.Status == JobStatus.Canceled));

    public static BuildSummaryDto ToSummary(this Build b, string pipelineName) => new(
        b.Id, b.PipelineId, pipelineName, b.Number, b.Branch, b.Commit, b.EntryTask, b.Status, b.RequestedBy, b.Error,
        b.QueuedAt, b.StartedAt, b.FinishedAt, Counts(b.Jobs), b.Reason, b.PullRequestId, b.ProjectId);

    public static BuildDetailDto ToDetail(this Build b, string pipelineName, List<ArtifactDto> artifacts,
        List<DeploymentDto> deployments) => new(
        b.Id, b.PipelineId, pipelineName, b.Number, b.Branch, b.Commit, b.EntryTask, b.Status, b.RequestedBy, b.Error,
        b.QueuedAt, b.StartedAt, b.FinishedAt, Counts(b.Jobs), b.Variables,
        b.Jobs.OrderBy(j => j.Order).Select(j => j.ToDto()).ToList(), artifacts, deployments, b.Reason, b.PullRequestId, b.SourceRef, b.ProjectId);

    public static JobDto ToDto(this BuildJob j) => new(
        j.Id, j.BuildId, j.Key, j.TaskName, j.Description, j.Order, j.DependsOn, j.Labels, j.Artifacts, j.Secrets, j.Status,
        j.AgentId, j.AgentName, j.ExitCode, j.Error, j.StartedAt, j.FinishedAt,
        j.Approval is null ? null : new ApprovalDto(j.Approval.Message, j.Approval.Approvers, j.ApprovedBy, j.ApprovedAt, j.ApprovalComment),
        j.Deploy, j.Steps);

    public static ArtifactDto ToDto(this Artifact a) => new(a.Id, a.JobId, a.Name, a.SizeBytes, a.CreatedAt);

    public static LogLineDto ToDto(this LogLine l) => new(l.Id, l.JobId, l.Timestamp, l.Stream, l.Text, l.Step);

    public static AgentDto ToDto(this Agent a, AgentMetricsDto? metrics, List<AgentRunningJobDto> running) => new(
        a.Id, a.OrgId is null, a.Name, a.HostName, a.Os, a.Version, a.Capacity, a.Labels, a.Enabled, a.Online, a.LastSeenAt, metrics, running);

    public static EnvironmentDto ToDto(this DeployEnvironment e) => new(
        e.Id, e.Name, e.Type, e.RequiresApproval, e.Approvers, e.AgentLabels, e.Host, e.Port, e.Username,
        e.PrivateKeyProtected is not null, e.KubeconfigProtected is not null, e.AksTenantId, e.AksClientId,
        e.AksClientSecretProtected is not null, e.AksSubscriptionId, e.AksResourceGroup, e.AksClusterName, e.AksAdmin, e.ProjectId, e.PasswordProtected is not null);

    public static DeploymentDto ToDto(this Deployment d, string pipelineName) => new(
        d.Id, d.EnvironmentId, d.EnvironmentName, d.PipelineId, pipelineName, d.BuildId, d.BuildNumber, d.JobId,
        d.Name, d.Url, d.Status, d.Output, d.CreatedAt, d.UpdatedAt, d.Container is not null, d.ProjectId);

    public static ConnectionDto ToDto(this GitConnection c) =>
        new(c.Id, c.Name, c.Type, c.Url, c.Username, c.TokenProtected is not null, c.AuthKind, c.TenantId, c.ClientId, c.ProjectId);
}
