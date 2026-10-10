using Builder.Application.Abstractions;
using Builder.Application.Services;
using Builder.Domain.Builds;
using Builder.Infrastructure.Taskfiles;

namespace Builder.Tests;

public class StepTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private const string Runner = """
        version: '3'
        tasks:
          helper:
            cmds:
              - echo helper
          release:
            requires:
              vars: [VERSION, { name: TARGET, enum: [staging, prod] }]
            cmds:
              - echo "build {{.VERSION}}"
              - task: helper
                vars: { DB: orders, MODE: fast }
              - for: [a, b]
                cmd: echo {{.ITEM}}
              - cmd: |
                  ./deploy.sh
                  echo done
                silent: true
              - defer: echo cleanup
          short: [echo one, echo two]
        """;

    [Fact]
    public void Planner_turns_cmds_into_steps()
    {
        var job = new TaskfilePlanner().Plan(Runner, "release").Jobs.Single();
        Assert.Equal([
            (0, StepKind.Command, "echo \"build {{.VERSION}}\""),
            (1, StepKind.TaskCall, "task: helper"),
            (2, StepKind.Command, "for each: echo {{.ITEM}}"),
            (3, StepKind.Command, "./deploy.sh"),
            (4, StepKind.Defer, "defer: echo cleanup"),
        ], job.Steps.Select(s => (s.Index, s.Kind, s.Label)));
        Assert.Equal(new Dictionary<string, string> { ["DB"] = "orders", ["MODE"] = "fast" }, job.Steps[1].Vars);
        Assert.Equal(["echo one", "echo two"], new TaskfilePlanner().Plan(Runner, "short").Jobs.Single().Steps.Select(s => s.Label));
    }

    [Fact]
    public void Planner_reads_required_inputs()
    {
        var plan = new TaskfilePlanner().Plan(Runner, "release");
        var (version, by) = plan.Inputs.Single(i => i.Input.Name == "VERSION");
        Assert.Null(version.Enum);
        Assert.Equal(["release"], by);
        Assert.Equal(["staging", "prod"], plan.Inputs.Single(i => i.Input.Name == "TARGET").Input.Enum);
    }

    [Theory]
    [InlineData("", "", "VERSION (required by release); TARGET (required by release)")]
    [InlineData("1.0", "dev", "TARGET='dev' is not one of: staging, prod")]
    public void Missing_or_invalid_inputs_stop_the_build(string version, string target, string message)
    {
        var plan = new TaskfilePlanner().Plan(Runner, "release");
        var vars = new Dictionary<string, string>();
        if (version != "") vars["VERSION"] = version;
        if (target != "") vars["TARGET"] = target;
        var ex = Assert.Throws<TaskfileException>(() => BuildPlanningService.CheckInputs(plan, vars));
        Assert.Contains(message, ex.Message);
        BuildPlanningService.CheckInputs(plan, new Dictionary<string, string> { ["VERSION"] = "1", ["TARGET"] = "prod" });
    }

    [Fact]
    public void Steps_follow_the_markers_and_the_job_outcome()
    {
        var job = new BuildJob("j", "j", null, 0, [], null, null, null, true, null, null,
            steps: [new(0, StepKind.Command, "a", null), new(1, StepKind.Command, "b", null), new(2, StepKind.Command, "c", null)]);
        var build = Build.Queue(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "main", null, null, "me", Now);
        build.Planned("abc", "j", [job], Now);
        job.AssignTo(Guid.NewGuid(), "agent");
        build.JobStarted(job.Id, Now);

        build.StepStarted(job.Id, 0, Now);
        build.StepStarted(job.Id, 1, Now.AddSeconds(5));
        Assert.Equal([StepStatus.Succeeded, StepStatus.Running, StepStatus.Pending], job.Steps.Select(s => s.Status));
        Assert.Equal(Now.AddSeconds(5), job.Steps[0].FinishedAt);

        build.JobCompleted(job.Id, false, false, 1, "boom", Now.AddSeconds(9));
        Assert.Equal([StepStatus.Succeeded, StepStatus.Failed, StepStatus.Skipped], job.Steps.Select(s => s.Status));
        build.StepStarted(job.Id, 2, Now.AddSeconds(10)); // late markers are ignored once the job is done
        Assert.Equal(StepStatus.Skipped, job.Steps[2].Status);
    }
}
