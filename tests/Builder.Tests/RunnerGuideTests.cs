using System.Text.RegularExpressions;
using Builder.Infrastructure.Taskfiles;

namespace Builder.Tests;

/// <summary>docs/runner-guide.md is what people and coding agents follow: its complete examples must plan.</summary>
public sealed class RunnerGuideTests
{
    private static string Guide()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "runner-guide.md"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "docs", "runner-guide.md"));
    }

    [Fact]
    public void Complete_examples_plan()
    {
        var examples = Regex.Matches(Guide(), "```yaml\n(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value).Where(y => y.StartsWith("version: '3'")).ToList();
        Assert.True(examples.Count >= 2);
        var planner = new TaskfilePlanner();
        foreach (var yaml in examples)
        {
            var plan = planner.Plan(yaml, null);
            Assert.NotEmpty(plan.Jobs);
            planner.Triggers(yaml);
        }

        var release = planner.Plan(examples.Last(), null);
        Assert.Equal(3, release.Jobs.Count(j => j.TaskName == "image"));
        Assert.Contains(release.Jobs, j => j.Deploy?.Container?.Strategy == Builder.Domain.Builds.ContainerStrategy.BlueGreen);
        Assert.Contains(release.Jobs, j => j.Approval is not null);
    }
}
