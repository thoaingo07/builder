namespace Builder.Domain.Builds;

public enum JobStatus { Pending, Queued, WaitingApproval, Assigned, Running, Succeeded, Failed, Canceled, Skipped }

public sealed record ApprovalSpec(string Message, List<string> Approvers);

public enum StepKind { Command, TaskCall, Defer }
public enum StepStatus { Pending, Running, Succeeded, Failed, Skipped }

/// <summary>
/// One entry of the task's cmds: a shell command, a call of another task (with its own vars), or a deferred
/// cleanup. Planned from the runner file; status and times come from the agent's step markers.
/// </summary>
public sealed record JobStep(int Index, StepKind Kind, string Label, Dictionary<string, string>? Vars,
    StepStatus Status = StepStatus.Pending, DateTimeOffset? StartedAt = null, DateTimeOffset? FinishedAt = null);

/// <summary>A variable a task requires at run time (go-task requires.vars), optionally limited to some values.</summary>
public sealed record InputSpec(string Name, List<string>? Enum);

/// <summary>A container registry the job logs in to (x-registries), through an Azure connection (null = the organization's only one).</summary>
public sealed record RegistrySpec(string Registry, string? Connection);

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
    /// <summary>Registries the agent logs in to before the task runs (short-lived tokens).</summary>
    public List<RegistrySpec> Registries { get; private set; } = new();
    /// <summary>Hand the task a short-lived token for the Azure Artifacts feeds of the repository's Azure DevOps organization.</summary>
    public bool AzureArtifacts { get; private set; }
    /// <summary>The task's cmds as steps, with live status.</summary>
    public List<JobStep> Steps { get; private set; } = new();
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
        bool hasCommands, ApprovalSpec? approval, DeploySpec? deploy, IEnumerable<string>? secrets = null,
        IEnumerable<RegistrySpec>? registries = null, bool azureArtifacts = false, IEnumerable<JobStep>? steps = null)
    {
        Steps = steps?.ToList() ?? new();
        Secrets = secrets?.ToList() ?? new();
        Registries = registries?.ToList() ?? new();
        AzureArtifacts = azureArtifacts;
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

    /// <summary>The agent reached step <paramref name="index"/>: the running step (if any) finished successfully.</summary>
    public void StepStarted(int index, DateTimeOffset now)
    {
        if (IsFinished || index < 0 || index >= Steps.Count) return;
        Steps = Steps.Select(s =>
            s.Status == StepStatus.Running ? s with { Status = StepStatus.Succeeded, FinishedAt = now }
            : s.Index == index ? s with { Status = StepStatus.Running, StartedAt = now }
            : s).ToList();
    }

    internal void Finish(JobStatus status, int? exitCode, string? error, DateTimeOffset now)
    {
        // close the steps: the running one gets the job's outcome; on success the rest (defers) ran too
        if (Steps.Count > 0 && (StartedAt is not null || status == JobStatus.Succeeded))
            Steps = Steps.Select(s => s.Status switch
            {
                StepStatus.Running => s with
                {
                    Status = status == JobStatus.Succeeded ? StepStatus.Succeeded : StepStatus.Failed,
                    FinishedAt = now,
                },
                StepStatus.Pending => s with { Status = status == JobStatus.Succeeded ? StepStatus.Succeeded : StepStatus.Skipped },
                _ => s,
            }).ToList();
        else if (Steps.Count > 0)
            Steps = Steps.Select(s => s.Status == StepStatus.Pending ? s with { Status = StepStatus.Skipped } : s).ToList();

        Status = status;
        ExitCode = exitCode;
        Error = error;
        FinishedAt = now;
    }
}
