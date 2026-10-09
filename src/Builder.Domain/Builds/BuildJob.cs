namespace Builder.Domain.Builds;

public enum JobStatus { Pending, Queued, WaitingApproval, Assigned, Running, Succeeded, Failed, Canceled, Skipped }

public sealed record ApprovalSpec(string Message, List<string> Approvers);

public sealed record DeploySpec(
    string Environment,
    string? Compose,
    string? Project,
    string? Manifests,
    string? Namespace,
    string? Url);

public sealed class BuildJob
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid BuildId { get; private set; }

    /// <summary>Unique key inside the build graph (task name, plus a suffix for parametrised deps).</summary>
    public string Key { get; private set; } = "";
    public string TaskName { get; private set; } = "";
    public string? Description { get; private set; }
    public int Order { get; private set; }
    public List<string> DependsOn { get; private set; } = new();
    public Dictionary<string, string> TaskVars { get; private set; } = new();
    public List<string> Labels { get; private set; } = new();
    public List<string> Artifacts { get; private set; } = new();
    /// <summary>Secret names the task declared (x-secrets); values are fetched by the agent at run time.</summary>
    public List<string> Secrets { get; private set; } = new();
    public bool HasCommands { get; private set; }
    public ApprovalSpec? Approval { get; private set; }
    public DeploySpec? Deploy { get; private set; }

    public JobStatus Status { get; private set; } = JobStatus.Pending;
    public Guid? AgentId { get; private set; }
    public string? AgentName { get; private set; }
    public int? ExitCode { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? ReadyAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    public string? ApprovedBy { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public string? ApprovalComment { get; private set; }

    private BuildJob() { }

    public BuildJob(string key, string taskName, string? description, int order, IEnumerable<string> dependsOn,
        Dictionary<string, string>? taskVars, IEnumerable<string>? labels, IEnumerable<string>? artifacts,
        bool hasCommands, ApprovalSpec? approval, DeploySpec? deploy, IEnumerable<string>? secrets = null)
    {
        Secrets = secrets?.ToList() ?? new();
        Key = key;
        TaskName = taskName;
        Description = description;
        Order = order;
        DependsOn = dependsOn.ToList();
        TaskVars = taskVars ?? new();
        Labels = labels?.ToList() ?? new();
        Artifacts = artifacts?.ToList() ?? new();
        HasCommands = hasCommands;
        Approval = approval;
        Deploy = deploy;
    }

    public bool IsFinished => Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Canceled or JobStatus.Skipped;

    /// <summary>True when the job needs an agent (gates without commands complete on the server).</summary>
    public bool NeedsAgent => HasCommands || Deploy is not null;

    internal void Ready(DateTimeOffset now)
    {
        ReadyAt = now;
        Status = Approval is not null ? JobStatus.WaitingApproval
            : NeedsAgent ? JobStatus.Queued
            : JobStatus.Succeeded;
        if (Status == JobStatus.Succeeded) { StartedAt = now; FinishedAt = now; }
    }

    internal void Decide(string user, bool approved, string? comment, DateTimeOffset now)
    {
        if (Status != JobStatus.WaitingApproval)
            throw new DomainException($"Job '{TaskName}' is not waiting for approval.");
        if (Approval!.Approvers.Count > 0 && !Approval.Approvers.Contains(user, StringComparer.OrdinalIgnoreCase))
            throw new DomainException($"'{user}' is not an approver for '{TaskName}'.");

        ApprovedBy = user;
        ApprovedAt = now;
        ApprovalComment = comment;
        if (!approved) { Finish(JobStatus.Failed, null, $"Rejected by {user}", now); return; }
        if (NeedsAgent) Status = JobStatus.Queued;
        else { StartedAt = now; Finish(JobStatus.Succeeded, null, null, now); }
    }

    public void AssignTo(Guid agentId, string agentName)
    {
        if (Status != JobStatus.Queued) throw new DomainException($"Job '{TaskName}' is not queued.");
        AgentId = agentId;
        AgentName = agentName;
        Status = JobStatus.Assigned;
    }

    /// <summary>The agent could not take the job (disconnect before ack): put it back in the queue.</summary>
    public void Requeue()
    {
        if (Status != JobStatus.Assigned) return;
        AgentId = null;
        AgentName = null;
        Status = JobStatus.Queued;
    }

    internal void Start(DateTimeOffset now)
    {
        if (Status is not (JobStatus.Assigned or JobStatus.Running)) return;
        Status = JobStatus.Running;
        StartedAt ??= now;
    }

    internal void Finish(JobStatus status, int? exitCode, string? error, DateTimeOffset now)
    {
        Status = status;
        ExitCode = exitCode;
        Error = error;
        FinishedAt = now;
    }
}
