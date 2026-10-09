using Builder.Domain.Organizations;

namespace Builder.Domain.Builds;

public enum BuildStatus { Planning, Running, Canceling, Succeeded, Failed, Canceled }

public sealed class Build : IOrgScoped
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid OrgId { get; private set; }
    public Guid PipelineId { get; private set; }
    public int Number { get; private set; }
    public string Branch { get; private set; } = "";
    public string? Commit { get; private set; }
    public string EntryTask { get; private set; } = "";
    public Dictionary<string, string> Variables { get; private set; } = new();
    public BuildStatus Status { get; private set; } = BuildStatus.Planning;
    public string RequestedBy { get; private set; } = "";
    public string? Error { get; private set; }
    public DateTimeOffset QueuedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    public List<BuildJob> Jobs { get; private set; } = new();

    private Build() { }

    public static Build Queue(Guid orgId, Guid pipelineId, int number, string branch, string? entryTask,
        Dictionary<string, string>? variables, string requestedBy, DateTimeOffset now, string? commit = null) => new()
    {
        OrgId = orgId,
        PipelineId = pipelineId,
        Commit = commit,
        Number = number,
        Branch = branch,
        EntryTask = entryTask ?? "",
        Variables = variables ?? new(),
        RequestedBy = requestedBy,
        QueuedAt = now,
    };

    public bool IsFinished => Status is BuildStatus.Succeeded or BuildStatus.Failed or BuildStatus.Canceled;

    /// <summary>Planning finished: the graph is known. Jobs whose deps are satisfied become runnable.</summary>
    public void Planned(string commit, string entryTask, IEnumerable<BuildJob> jobs, DateTimeOffset now)
    {
        if (Status != BuildStatus.Planning) return;
        Commit = commit;
        EntryTask = entryTask;
        Jobs.AddRange(jobs);
        Status = BuildStatus.Running;
        StartedAt = now;
        Advance(now);
    }

    public void PlanningFailed(string error, DateTimeOffset now)
    {
        Error = error;
        Status = BuildStatus.Failed;
        StartedAt ??= now;
        FinishedAt = now;
    }

    public BuildJob Job(Guid jobId) =>
        Jobs.FirstOrDefault(j => j.Id == jobId) ?? throw new DomainException($"Job {jobId} is not part of build #{Number}.");

    /// <summary>
    /// Promotes pending jobs whose dependencies are done, skips jobs whose dependencies failed,
    /// and finishes the build once every job is terminal. Returns the jobs whose state changed.
    /// </summary>
    public List<BuildJob> Advance(DateTimeOffset now)
    {
        var changed = new List<BuildJob>();
        bool progress;
        do
        {
            progress = false;
            foreach (var job in Jobs.Where(j => j.Status == JobStatus.Pending))
            {
                var deps = Jobs.Where(d => job.DependsOn.Contains(d.Key)).ToList();
                if (Status == BuildStatus.Canceling || deps.Any(d => d.Status == JobStatus.Canceled))
                {
                    job.Finish(JobStatus.Canceled, null, "Canceled", now);
                }
                else if (deps.Any(d => d.Status is JobStatus.Failed or JobStatus.Skipped))
                {
                    job.Finish(JobStatus.Skipped, null, "A dependency did not succeed", now);
                }
                else if (deps.All(d => d.Status == JobStatus.Succeeded))
                {
                    job.Ready(now);
                }
                else continue;
                changed.Add(job);
                progress = true;
            }
        } while (progress);

        if (Jobs.All(j => j.IsFinished))
        {
            Status = Status == BuildStatus.Canceling || Jobs.Any(j => j.Status == JobStatus.Canceled)
                ? BuildStatus.Canceled
                : Jobs.All(j => j.Status == JobStatus.Succeeded) ? BuildStatus.Succeeded : BuildStatus.Failed;
            FinishedAt = now;
        }
        return changed;
    }

    /// <summary>Cancels everything not yet running; returns the jobs that must be stopped on agents.</summary>
    public List<BuildJob> Cancel(DateTimeOffset now)
    {
        if (IsFinished) return [];
        if (Status == BuildStatus.Planning)
        {
            Status = BuildStatus.Canceled;
            FinishedAt = now;
            return [];
        }
        Status = BuildStatus.Canceling;
        var running = new List<BuildJob>();
        foreach (var job in Jobs.Where(j => !j.IsFinished))
        {
            if (job.Status is JobStatus.Assigned or JobStatus.Running) running.Add(job);
            else job.Finish(JobStatus.Canceled, null, "Canceled", now);
        }
        Advance(now);
        return running;
    }

    public void Approve(Guid jobId, string user, bool approved, string? comment, DateTimeOffset now)
    {
        var job = Job(jobId);
        job.Decide(user, approved, comment, now);
        Advance(now);
    }

    public void JobStarted(Guid jobId, DateTimeOffset now) => Job(jobId).Start(now);

    public void JobCompleted(Guid jobId, bool succeeded, bool canceled, int? exitCode, string? error, DateTimeOffset now)
    {
        var job = Job(jobId);
        if (job.IsFinished) return;
        var status = canceled ? JobStatus.Canceled : succeeded ? JobStatus.Succeeded : JobStatus.Failed;
        job.Finish(status, exitCode, error, now);
        Advance(now);
    }
}
