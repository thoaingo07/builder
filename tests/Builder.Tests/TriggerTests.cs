using Builder.Application.Abstractions;
using Builder.Application.Services;
using Builder.Domain.Triggers;
using Builder.Infrastructure.Git;
using Builder.Infrastructure.Taskfiles;

namespace Builder.Tests;

public class TriggerTests
{
    [Theory]
    [InlineData("main", "main", true)]
    [InlineData("release/*", "release/1.2", true)]
    [InlineData("release/*", "release/1.2/hotfix", false)]
    [InlineData("release/**", "release/1.2/hotfix", true)]
    [InlineData("feature/*", "main", false)]
    public void Branch_globs(string pattern, string branch, bool matches) => Assert.Equal(matches, Glob.Matches([pattern], branch));

    [Fact]
    public void Path_filters_include_and_exclude()
    {
        var f = new Filter([], ["src/**", "!src/**/*.md", "Taskfile.yml"]);
        Assert.True(f.MatchesPaths(["src/api/Program.cs"]));
        Assert.True(f.MatchesPaths(["Taskfile.yml", "docs/x.md"]));
        Assert.False(f.MatchesPaths(["src/api/README.md"]));
        Assert.False(f.MatchesPaths(["docs/guide.md"]));
        Assert.True(f.MatchesPaths(null));                                    // unknown changes: run
        Assert.True(new Filter([], ["!docs/**"]).MatchesPaths(["src/a.cs"])); // only excludes: everything else
        Assert.True(Glob.Matches(["**/*.cs"], "Program.cs"));                 // **/ matches zero folders
    }

    [Fact]
    public void Reads_triggers_from_the_runner_file()
    {
        var spec = new TaskfilePlanner().Triggers("""
            version: '3'
            x-builder:
              triggers:
                push:
                  branches: [main, release/*]
                  paths: [src/**, "!docs/**"]
                pull-request: main
                schedule:
                  - cron: "0 2 * * *"
                    branch: main
                    vars: { TARGET: staging }
                  - cron: "*/30 * * * * *"
                    timezone: Europe/Amsterdam
            tasks:
              default: echo hi
            """);
        Assert.Equal(["main", "release/*"], spec.Push!.Branches);
        Assert.Equal(["src/**", "!docs/**"], spec.Push.Paths);
        Assert.Equal(["main"], spec.PullRequest!.Branches);
        Assert.Equal(2, spec.Schedules.Count);
        Assert.Equal(("0 2 * * *", "main", "UTC", "staging"), (spec.Schedules[0].Cron, spec.Schedules[0].Branch, spec.Schedules[0].TimeZone, spec.Schedules[0].Vars["TARGET"]));
        Assert.Equal("Europe/Amsterdam", spec.Schedules[1].TimeZone);
        Assert.True(new TaskfilePlanner().Triggers("version: '3'\ntasks:\n  a: echo\n").IsEmpty);
        Assert.Equal([], new TaskfilePlanner().Triggers("version: '3'\nx-builder:\n  triggers:\n    push: true\ntasks:\n  a: echo\n").Push!.Branches);
    }

    [Theory]
    [InlineData("    push: [main]\n    deploy: true", "unknown trigger 'deploy'")]
    [InlineData("    schedule:\n      - branch: main", "needs a cron")]
    [InlineData("    schedule:\n      - cron: \"99 * * * *\"", "Schedule '99 * * * *'")]
    [InlineData("    schedule:\n      - cron: \"0 2 * * *\"\n        timezone: Mars/Base", "Mars/Base")]
    public void Rejects_bad_triggers(string triggers, string error)
    {
        var ex = Assert.Throws<TaskfileException>(() => new TaskfilePlanner().Triggers($"version: '3'\nx-builder:\n  triggers:\n{triggers}\ntasks:\n  a: echo\n"));
        Assert.Contains(error, ex.Message);
    }

    [Fact]
    public void Cron_next_runs_honour_time_zones()
    {
        var at = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 2, 0, 0, TimeSpan.Zero), TriggerService.Next("0 2 * * *", "UTC", at));
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero), TriggerService.Next("0 2 * * *", "Europe/Amsterdam", at)); // CEST = UTC+2
        Assert.Equal(at.AddSeconds(30), TriggerService.Next("*/30 * * * * *", "UTC", at));
    }

    [Theory]
    [InlineData(null, "https://dev.azure.com/contoso/Shop/_apis/git/repositories/web/commits/abc123/statuses?api-version=7.1")]
    [InlineData(7, "https://dev.azure.com/contoso/Shop/_apis/git/repositories/web/pullRequests/7/statuses?api-version=7.1")]
    public async Task Reports_build_status_to_azure_devops(int? pr, string url)
    {
        var stub = new CredentialTests.Stub(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Created)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        });
        var git = new AzureDevOpsGit(new HttpClient(stub));
        await git.ReportAsync(new GitRemote("https://dev.azure.com/contoso/Shop/_git/web", "Basic x"),
            new BuildStatusReport("abc123", pr, "builder/ci", "succeeded", "#4 succeeded", "https://builder.example/builds/1"), default);
        var request = Assert.Single(stub.Requests);
        Assert.Equal(url, request.Url);
        var body = System.Text.Json.Nodes.JsonNode.Parse(request.Body)!;
        Assert.Equal(("succeeded", "builder/ci", "builder"), (body["state"]!.GetValue<string>(), body["context"]!["name"]!.GetValue<string>(), body["context"]!["genre"]!.GetValue<string>()));
        Assert.Equal("https://builder.example/builds/1", body["targetUrl"]!.GetValue<string>());

        // other hosts: nothing is sent
        await git.ReportAsync(new GitRemote("/tmp/repo.git", null), new BuildStatusReport("abc", null, "x", "pending", "", null), default);
        Assert.Single(stub.Requests);
    }
}
