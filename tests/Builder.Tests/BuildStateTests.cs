using Builder.Domain;
using Builder.Domain.Builds;

namespace Builder.Tests;

public class BuildStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static BuildJob Job(string key, params string[] deps) =>
        new(key, key, null, 0, deps, null, null, null, true, null, null);

    private static BuildJob Gate(string key, params string[] deps) =>
        new(key, key, null, 0, deps, null, null, null, false, new ApprovalSpec("ok?", ["alice"]), null);

    private static Build Planned(params BuildJob[] jobs)
    {
        var build = Build.Queue(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "main", null, null, "bob", Now);
        build.Planned("abc", "ci", jobs, Now);
        return build;
    }

    private static JobStatus StatusOf(Build b, string key) => b.Jobs.Single(j => j.Key == key).Status;

    [Fact]
    public void Jobs_without_deps_are_queued_and_dependents_wait()
    {
        var b = Planned(Job("a"), Job("b"), Job("c", "a", "b"));
        Assert.Equal(JobStatus.Queued, StatusOf(b, "a"));
        Assert.Equal(JobStatus.Queued, StatusOf(b, "b"));
        Assert.Equal(JobStatus.Pending, StatusOf(b, "c"));

        Complete(b, "a", true);
        Assert.Equal(JobStatus.Pending, StatusOf(b, "c"));
        Complete(b, "b", true);
        Assert.Equal(JobStatus.Queued, StatusOf(b, "c"));
        Complete(b, "c", true);
        Assert.Equal(BuildStatus.Succeeded, b.Status);
    }

    [Fact]
    public void A_failure_skips_everything_downstream_and_fails_the_build()
    {
        var b = Planned(Job("a"), Job("b", "a"), Job("c", "b"), Job("d"));
        Complete(b, "a", false);
        Assert.Equal(JobStatus.Skipped, StatusOf(b, "b"));
        Assert.Equal(JobStatus.Skipped, StatusOf(b, "c"));
        Assert.Equal(BuildStatus.Running, b.Status); // d still runs
        Complete(b, "d", true);
        Assert.Equal(BuildStatus.Failed, b.Status);
    }

    [Fact]
    public void Approval_gates_wait_and_only_listed_approvers_may_decide()
    {
        var b = Planned(Job("a"), Gate("gate", "a"), Job("deploy", "gate"));
        Complete(b, "a", true);
        var gate = b.Jobs.Single(j => j.Key == "gate");
        Assert.Equal(JobStatus.WaitingApproval, gate.Status);

        Assert.Throws<DomainException>(() => b.Approve(gate.Id, "mallory", true, null, Now));
        b.Approve(gate.Id, "Alice", true, "go", Now);

        Assert.Equal(JobStatus.Succeeded, gate.Status);
        Assert.Equal("Alice", gate.ApprovedBy);
        Assert.Equal(JobStatus.Queued, StatusOf(b, "deploy"));
    }

    [Fact]
    public void Rejection_fails_the_gate()
    {
        var b = Planned(Gate("gate"), Job("deploy", "gate"));
        b.Approve(b.Jobs[0].Id, "alice", false, "no", Now);
        Assert.Equal(JobStatus.Failed, StatusOf(b, "gate"));
        Assert.Equal(JobStatus.Skipped, StatusOf(b, "deploy"));
        Assert.Equal(BuildStatus.Failed, b.Status);
    }

    [Fact]
    public void Cancel_stops_pending_work_and_returns_running_jobs()
    {
        var b = Planned(Job("a"), Job("b"), Job("c", "a"));
        var a = b.Jobs.Single(j => j.Key == "a");
        a.AssignTo(Guid.NewGuid(), "agent-1");
        b.JobStarted(a.Id, Now);

        var running = b.Cancel(Now);

        Assert.Equal([a], running);
        Assert.Equal(BuildStatus.Canceling, b.Status);
        Assert.Equal(JobStatus.Canceled, StatusOf(b, "b"));
        Assert.Equal(JobStatus.Canceled, StatusOf(b, "c"));

        b.JobCompleted(a.Id, false, true, -1, "Canceled", Now);
        Assert.Equal(BuildStatus.Canceled, b.Status);
    }

    [Fact]
    public void Gates_without_commands_or_approval_complete_on_the_server()
    {
        var b = Planned(Job("a"), new BuildJob("ci", "ci", null, 1, ["a"], null, null, null, false, null, null));
        Complete(b, "a", true);
        Assert.Equal(BuildStatus.Succeeded, b.Status);
    }

    private static void Complete(Build b, string key, bool ok)
    {
        var job = b.Jobs.Single(j => j.Key == key);
        job.AssignTo(Guid.NewGuid(), "agent");
        b.JobStarted(job.Id, Now);
        b.JobCompleted(job.Id, ok, false, ok ? 0 : 1, ok ? null : "boom", Now);
    }
}
