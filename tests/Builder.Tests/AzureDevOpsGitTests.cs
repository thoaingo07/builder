using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Builder.Application;
using Builder.Application.Abstractions;
using Builder.Infrastructure.Git;
using Builder.Infrastructure.Services;

namespace Builder.Tests;

/// <summary>Azure DevOps REST git: URL parsing and the exact requests/responses (shapes as Azure DevOps 7.1 returns them).</summary>
public class AzureDevOpsGitTests
{
    private const string Repo = "https://dev.azure.com/contoso/Shop/_apis/git/repositories/web";
    private static readonly GitRemote Remote = new("https://contoso@dev.azure.com/contoso/Shop/_git/web", "Basic abc");

    [Theory]
    [InlineData("https://dev.azure.com/contoso/Shop/_git/web", "https://dev.azure.com/contoso", "Shop", "web")]
    [InlineData("https://contoso@dev.azure.com/contoso/My%20Project/_git/My%20Repo", "https://dev.azure.com/contoso", "My Project", "My Repo")]
    [InlineData("https://dev.azure.com/contoso/_git/Shop", "https://dev.azure.com/contoso", "Shop", "Shop")]
    [InlineData("https://contoso.visualstudio.com/DefaultCollection/Shop/_git/web.git", "https://contoso.visualstudio.com", "Shop", "web")]
    [InlineData("https://contoso.visualstudio.com/Shop/_git/web", "https://contoso.visualstudio.com", "Shop", "web")]
    public void Parses_azure_devops_git_urls(string url, string org, string project, string repo) =>
        Assert.Equal(new AzureDevOpsGit.Coordinates(org, project, repo), AzureDevOpsGit.Parse(url));

    [Theory]
    [InlineData("https://github.com/thoaingo07/builder.git")]
    [InlineData("/tmp/repo.git")]
    [InlineData("git@ssh.dev.azure.com:v3/contoso/Shop/web")]
    public void Other_urls_are_not_azure_devops(string url) => Assert.Null(AzureDevOpsGit.Parse(url));

    [Fact]
    public void Clone_urls_lose_the_user_part() =>
        Assert.Equal("https://dev.azure.com/contoso/Shop/_git/web", AzureDevOpsClient.CleanCloneUrl("https://contoso@dev.azure.com/contoso/Shop/_git/web"));

    [Fact]
    public async Task Resolves_and_lists_branches_from_refs()
    {
        var stub = new Stub()
            .On($"{Repo}/refs?filter=heads/main&api-version=7.1", Refs(("refs/heads/main", "aaaa"), ("refs/heads/main-old", "bbbb")))
            .On($"{Repo}/refs?filter=heads/&api-version=7.1", Refs(("refs/heads/main", "aaaa"), ("refs/heads/feature/x", "cccc")));
        var git = new AzureDevOpsGit(new HttpClient(stub));

        Assert.Equal("aaaa", await git.ResolveBranchAsync(Remote, "main", default)); // exact match, not the prefix match
        Assert.Equal(["main", "feature/x"], await git.ListBranchesAsync(Remote, default));
        Assert.All(stub.Requests, r => Assert.Equal("Basic abc", r.Headers.Authorization!.ToString()));
        await Assert.ThrowsAsync<ExternalServiceException>(() => git.ResolveBranchAsync(Remote, "nope", default));
    }

    [Fact]
    public async Task Lists_runner_files_and_reads_them_at_a_commit()
    {
        var stub = new Stub()
            .On($"{Repo}/items?scopePath=%2F.builder%2Frunners&recursionLevel=OneLevel&versionDescriptor.version=c0ffee&versionDescriptor.versionType=commit&api-version=7.1",
                """{"count":4,"value":[{"path":"/.builder/runners","isFolder":true,"gitObjectType":"tree"},{"path":"/.builder/runners/ci.yml","gitObjectType":"blob"},{"path":"/.builder/runners/deploy.yaml","gitObjectType":"blob"},{"path":"/.builder/runners/old","isFolder":true,"gitObjectType":"tree"}]}""")
            .On($"{Repo}/items?path=%2F.builder%2Frunners%2Fci.yml&includeContent=true&versionDescriptor.version=c0ffee&versionDescriptor.versionType=commit&$format=json&api-version=7.1",
                """{"path":"/.builder/runners/ci.yml","content":"version: '3'\n"}""");
        var git = new AzureDevOpsGit(new HttpClient(stub));

        Assert.Equal([".builder/runners/ci.yml", ".builder/runners/deploy.yaml"],
            await git.ListFilesAsync(Remote, "main", "c0ffee", ".builder/runners", default));
        Assert.Equal("version: '3'\n", await git.ReadFileAsync(Remote, "main", "c0ffee", ".builder/runners/ci.yml", default));
        Assert.Null(await git.ReadFileAsync(Remote, "main", "c0ffee", "missing.yml", default)); // 404 → null
        Assert.Empty(await git.ListFilesAsync(Remote, "main", "c0ffee", ".nothing", default));
    }

    [Theory]
    [InlineData(true, "edit")]
    [InlineData(false, "add")]
    public async Task Commits_a_taskfile_with_a_push(bool exists, string changeType)
    {
        var stub = new Stub()
            .On($"{Repo}/refs?filter=heads/main&api-version=7.1", Refs(("refs/heads/main", "head1")))
            .On($"{Repo}/pushes?api-version=7.1", """{"commits":[{"commitId":"newsha"}]}""");
        if (exists)
            stub.On($"{Repo}/items?path=%2F.builder%2Frunners%2Fci.yml&includeContent=true&versionDescriptor.version=head1&versionDescriptor.versionType=commit&$format=json&api-version=7.1",
                """{"path":"/.builder/runners/ci.yml","content":"old"}""");
        var git = new AzureDevOpsGit(new HttpClient(stub));

        var sha = await git.CommitFileAsync(Remote, "main", ".builder/runners/ci.yml", "new\r\n", "Update ci", "Dev", "dev@example.com", default);

        Assert.Equal("newsha", sha);
        var push = JsonNode.Parse(stub.Bodies.Single())!;
        Assert.Equal("refs/heads/main", push["refUpdates"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("head1", push["refUpdates"]![0]!["oldObjectId"]!.GetValue<string>());   // optimistic: fails if the branch moved
        var change = push["commits"]![0]!["changes"]![0]!;
        Assert.Equal(changeType, change["changeType"]!.GetValue<string>());
        Assert.Equal("/.builder/runners/ci.yml", change["item"]!["path"]!.GetValue<string>());
        Assert.Equal("new\n", change["newContent"]!["content"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(HttpStatusCode.NonAuthoritativeInformation, "text/html")]
    [InlineData(HttpStatusCode.Redirect, "text/html")]
    [InlineData(HttpStatusCode.Unauthorized, "application/json")]
    public async Task A_rejected_pat_is_reported_clearly(HttpStatusCode status, string contentType)
    {
        var stub = new Stub { Fallback = () => new HttpResponseMessage(status) { Content = new StringContent("<html>sign in</html>", Encoding.UTF8, contentType) } };
        var git = new AzureDevOpsGit(new HttpClient(stub));
        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() => git.ListBranchesAsync(Remote, default));
        Assert.Contains("rejected the credentials", ex.Message);
    }

    [Fact]
    public async Task Router_sends_azure_devops_to_rest_and_the_rest_to_git()
    {
        var stub = new Stub().On($"{Repo}/refs?filter=heads/&api-version=7.1", Refs(("refs/heads/main", "aaaa")));
        var router = new GitRouter(new AzureDevOpsGit(new HttpClient(stub)),
            new GitCli(Microsoft.Extensions.Options.Options.Create(new BuilderStorageOptions { DataDirectory = Path.GetTempPath() })));
        Assert.Equal(["main"], await router.ListBranchesAsync(Remote, default));
        await Assert.ThrowsAsync<ExternalServiceException>(() => router.ListBranchesAsync(new GitRemote("/does/not/exist.git", null), default));
    }

    private static string Refs(params (string Name, string Sha)[] refs) =>
        "{\"count\":" + refs.Length + ",\"value\":[" + string.Join(",", refs.Select(r => $"{{\"name\":\"{r.Name}\",\"objectId\":\"{r.Sha}\"}}")) + "]}";

    private sealed class Stub : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = new();
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();
        public Func<HttpResponseMessage>? Fallback { get; init; }

        public Stub On(string url, string json)
        {
            _responses[url] = json;
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(ct));
            if (Fallback is not null) return Fallback();
            var url = request.RequestUri!.ToString();
            return _responses.TryGetValue(url, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"TF401174"}""", Encoding.UTF8, "application/json") };
        }
    }
}
