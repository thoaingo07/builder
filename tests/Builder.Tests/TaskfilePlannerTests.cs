using Builder.Application.Abstractions;
using Builder.Infrastructure.Taskfiles;

namespace Builder.Tests;

public class TaskfilePlannerTests
{
    private readonly TaskfilePlanner _planner = new();

    private const string Pipeline = """
        version: '3'
        x-builder:
          entry: ci
        tasks:
          ci:
            deps: [deploy]
          restore:
            cmds: [echo restore]
          build-api:
            deps: [restore]
            x-agent: { labels: [Linux, dotnet] }
            x-artifacts: [out/api/**]
            cmds: [dotnet publish]
          build-web:
            deps: [restore]
            cmds:
              - npm ci
              - npm run build
          approve:
            deps: [build-api, build-web]
            x-approval:
              message: Ship it?
              approvers: [alice]
          deploy:
            deps: [approve]
            x-deploy:
              environment: prod
              compose: deploy/compose.yml
              project: shop
              url: https://shop.example.com
            cmds: [echo deploying]
          unrelated:
            cmds: [echo never]
        """;

    [Fact]
    public void Plans_the_graph_reachable_from_the_entry_task()
    {
        var plan = _planner.Plan(Pipeline, null);

        Assert.Equal("ci", plan.EntryTask);
        Assert.Equal(["restore", "build-api", "build-web", "approve", "deploy", "ci"], plan.Jobs.Select(j => j.Key));
        Assert.DoesNotContain(plan.Jobs, j => j.Key == "unrelated");

        var approve = plan.Jobs.Single(j => j.Key == "approve");
        Assert.Equal(["build-api", "build-web"], approve.DependsOn);
        Assert.False(approve.HasCommands);
        Assert.Equal("Ship it?", approve.Approval!.Message);
        Assert.Equal(["alice"], approve.Approval.Approvers);
    }

    [Fact]
    public void Reads_builder_extensions()
    {
        var plan = _planner.Plan(Pipeline, null);

        var api = plan.Jobs.Single(j => j.Key == "build-api");
        Assert.Equal(["linux", "dotnet"], api.Labels);
        Assert.Equal(["out/api/**"], api.Artifacts);
        Assert.True(api.HasCommands);

        var deploy = plan.Jobs.Single(j => j.Key == "deploy").Deploy!;
        Assert.Equal(("prod", "deploy/compose.yml", "shop", "https://shop.example.com"),
            (deploy.Environment, deploy.Compose, deploy.Project, deploy.Url));
    }

    [Fact]
    public void Explicit_entry_task_overrides_x_builder_and_aliases_resolve()
    {
        var yaml = """
            version: '3'
            tasks:
              lint:
                aliases: [l]
                cmds: [echo lint]
              check:
                deps: [l]
            """;
        var plan = _planner.Plan(yaml, "check");
        Assert.Equal(["lint", "check"], plan.Jobs.Select(j => j.Key));
        Assert.Equal(["lint"], plan.Jobs[1].DependsOn);
    }

    [Fact]
    public void Defaults_to_the_default_task_and_supports_shorthand_tasks()
    {
        var yaml = """
            version: '3'
            tasks:
              default:
                deps: [a]
              a: echo a
              b: [echo b1, echo b2]
            """;
        var plan = _planner.Plan(yaml, null);
        Assert.Equal("default", plan.EntryTask);
        Assert.True(plan.Jobs.Single(j => j.Key == "a").HasCommands);
    }

    [Fact]
    public void Parametrised_deps_become_separate_jobs()
    {
        var yaml = """
            version: '3'
            tasks:
              all:
                deps:
                  - task: build
                    vars: { OS: linux }
                  - task: build
                    vars: { OS: windows }
              build:
                cmds:
                  - echo {{.OS}}
            """;
        var plan = _planner.Plan(yaml, "all");
        Assert.Equal(["build[OS=linux]", "build[OS=windows]", "all"], plan.Jobs.Select(j => j.Key));
        Assert.Equal("windows", plan.Jobs[1].TaskVars["OS"]);
    }

    [Theory]
    [InlineData("version: '3'\ntasks:\n  a:\n    deps: [b]\n  b:\n    deps: [a]\n", "cycle")]
    [InlineData("version: '3'\ntasks:\n  a:\n    deps: [missing]\n", "does not exist")]
    [InlineData("version: '3'\nincludes:\n  x: ./x.yml\ntasks:\n  a: echo\n", "includes")]
    [InlineData("version: '2'\ntasks:\n  a: echo\n", "not supported")]
    [InlineData("version: '3'\ntasks:\n  a:\n    x-deploy: { project: p }\n", "environment is required")]
    [InlineData("version: '3'\ntasks: [\n", "Invalid YAML")]
    public void Rejects_taskfiles_it_cannot_plan(string yaml, string error)
    {
        var ex = Assert.Throws<TaskfileException>(() => _planner.Plan(yaml, "a"));
        Assert.Contains(error, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plans_the_sample_and_the_repository_taskfiles()
    {
        var root = FindRepoRoot();
        Assert.Equal("ci", _planner.Plan(File.ReadAllText(Path.Combine(root, "samples/Taskfile.yml")), null).EntryTask);
        var own = _planner.Plan(File.ReadAllText(Path.Combine(root, "Taskfile.yml")), null);
        Assert.Contains(own.Jobs, j => j.Key == "ui:build");
    }

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Builder.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
