using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Builder.Application;
using Builder.Application.Abstractions;

namespace Builder.Infrastructure.Git;

/// <summary>
/// <see cref="IGitService"/> for Azure DevOps Git over its REST API (7.1): the server never clones. Branches come from
/// <c>refs</c>, files and folders from <c>items</c>. Read-only: Builder never pushes to your repositories.
/// Build agents still fetch with git.
/// </summary>
public sealed class AzureDevOpsGit(HttpClient http) : IGitService, IGitHostStatus
{
    private const string Api = "api-version=7.1";

    /// <summary>Organization URL, project and repository of an Azure DevOps Git URL; null for anything else.</summary>
    public sealed record Coordinates(string OrganizationUrl, string Project, string Repository)
    {
        public string RepoApi => $"{OrganizationUrl}/{Uri.EscapeDataString(Project)}/_apis/git/repositories/{Uri.EscapeDataString(Repository)}";
    }

    public static Coordinates? Parse(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return null;
        var parts = u.AbsolutePath.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        var git = Array.IndexOf(parts, "_git");
        if (git < 1 || git + 1 >= parts.Length) return null;
        var repo = parts[git + 1];
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];

        if (u.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            // https://dev.azure.com/{org}/{project}/_git/{repo}; a repo named like its project may omit the project
            var org = parts[0];
            var project = git >= 2 ? parts[git - 1] : repo;
            return new Coordinates($"https://dev.azure.com/{org}", project, repo);
        }
        if (u.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            // https://{org}.visualstudio.com[/DefaultCollection]/{project}/_git/{repo}
            var project = git >= 1 && !parts[git - 1].Equals("DefaultCollection", StringComparison.OrdinalIgnoreCase) ? parts[git - 1] : repo;
            return new Coordinates($"https://{u.Host}", project, repo);
        }
        return null;
    }

    public async Task<string> ResolveBranchAsync(GitRemote remote, string branch, CancellationToken ct)
    {
        if (branch.Length == 40 && branch.All(Uri.IsHexDigit)) return branch.ToLowerInvariant();
        var c = Coords(remote);
        var refs = await GetAsync<ListOf<GitRef>>(remote, $"{c.RepoApi}/refs?filter=heads/{Uri.EscapeDataString(branch)}&{Api}", ct);
        return refs.Value.FirstOrDefault(r => r.Name == $"refs/heads/{branch}")?.ObjectId
            ?? throw new ExternalServiceException($"Branch '{branch}' does not exist in {c.Project}/{c.Repository}.");
    }

    public async Task<IReadOnlyList<string>> ListBranchesAsync(GitRemote remote, CancellationToken ct)
    {
        var c = Coords(remote);
        var refs = await GetAsync<ListOf<GitRef>>(remote, $"{c.RepoApi}/refs?filter=heads/&{Api}", ct);
        return refs.Value.Select(r => r.Name["refs/heads/".Length..]).ToList();
    }

    public async Task<string?> ReadFileAsync(GitRemote remote, string branch, string commit, string path, CancellationToken ct)
    {
        var c = Coords(remote);
        var url = $"{c.RepoApi}/items?path={Uri.EscapeDataString("/" + path.TrimStart('/'))}&includeContent=true" +
                  $"&versionDescriptor.version={commit}&versionDescriptor.versionType=commit&$format=json&{Api}";
        var item = await GetOrNullAsync<GitItem>(remote, url, ct);
        return item?.Content;
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(GitRemote remote, string branch, string commit, string folder, CancellationToken ct)
    {
        var c = Coords(remote);
        var scope = "/" + folder.Trim('/');
        var url = $"{c.RepoApi}/items?scopePath={Uri.EscapeDataString(scope)}&recursionLevel=OneLevel" +
                  $"&versionDescriptor.version={commit}&versionDescriptor.versionType=commit&{Api}";
        var items = await GetOrNullAsync<ListOf<GitItem>>(remote, url, ct);
        return (items?.Value ?? [])
            .Where(i => i.IsFolder != true && i.Path != scope)
            .Select(i => i.Path.TrimStart('/'))
            .ToList();
    }

    public async Task<IReadOnlyList<string>?> ChangedFilesAsync(GitRemote remote, string branch, string baseCommit, string headCommit, CancellationToken ct)
    {
        if (baseCommit.Trim('0').Length == 0) return null; // new branch: everything is "changed"
        var c = Coords(remote);
        var diff = await GetOrNullAsync<CommitDiff>(remote,
            $"{c.RepoApi}/diffs/commits?baseVersion={baseCommit}&baseVersionType=commit&targetVersion={headCommit}&targetVersionType=commit&$top=2000&{Api}", ct);
        return diff?.Changes?.Where(ch => ch.Item?.IsFolder != true && ch.Item?.Path is not null)
            .Select(ch => ch.Item!.Path.TrimStart('/')).ToList();
    }

    /// <summary>Commit status for push/manual builds, pull request status for PR builds (genre "builder", name = runner).</summary>
    public async Task ReportAsync(GitRemote remote, BuildStatusReport report, CancellationToken ct)
    {
        if (Parse(remote.Url) is not { } c) return;
        var url = report.PullRequestId is { } pr
            ? $"{c.RepoApi}/pullRequests/{pr}/statuses?{Api}"
            : $"{c.RepoApi}/commits/{report.Commit}/statuses?{Api}";
        var body = new
        {
            state = report.State,
            description = report.Description,
            targetUrl = report.TargetUrl,
            context = new { name = report.Name, genre = "builder" },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        Authorize(req, remote);
        using var res = await http.SendAsync(req, ct);
        await EnsureOkAsync(res, ct);
    }

    private static Coordinates Coords(GitRemote remote) =>
        Parse(remote.Url) ?? throw new ExternalServiceException($"'{remote.Url}' is not an Azure DevOps Git URL.");

    private async Task<T> GetAsync<T>(GitRemote remote, string url, CancellationToken ct) where T : class =>
        await GetOrNullAsync<T>(remote, url, ct) ?? throw new ExternalServiceException($"Azure DevOps returned 404 for {Redact(url)}.");

    private async Task<T?> GetOrNullAsync<T>(GitRemote remote, string url, CancellationToken ct) where T : class
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        Authorize(req, remote);
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureOkAsync(res, ct);
        return await res.Content.ReadFromJsonAsync<T>(ct);
    }

    private static void Authorize(HttpRequestMessage req, GitRemote remote)
    {
        if (remote.AuthorizationHeader is { } h) req.Headers.TryAddWithoutValidation("Authorization", h);
    }

    private static async Task EnsureOkAsync(HttpResponseMessage res, CancellationToken ct)
    {
        // a sign-in redirect / 203 means the PAT was not accepted
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NonAuthoritativeInformation or HttpStatusCode.Redirect
            || res.Content.Headers.ContentType?.MediaType == "text/html")
            throw new ExternalServiceException("Azure DevOps rejected the credentials. Check the connection's PAT (scope Code: Read).");
        if (res.StatusCode == HttpStatusCode.Forbidden)
            throw new ExternalServiceException("The PAT has no access to this repository (needs Code: Read; Code: Status to report build results).");
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new ExternalServiceException($"Azure DevOps returned {(int)res.StatusCode}: {body[..Math.Min(300, body.Length)]}");
        }
    }

    private static string Redact(string url) => url.Split('?')[0];

    private sealed record ListOf<T>(List<T> Value);
    private sealed record GitRef(string Name, string ObjectId);
    private sealed record GitItem(string Path, bool? IsFolder, string? Content);
    private sealed record CommitDiff(List<Change>? Changes);
    private sealed record Change(GitItem? Item);
}

/// <summary>Routes Azure DevOps URLs to the REST implementation and everything else to the git CLI.</summary>
public sealed class GitRouter(AzureDevOpsGit azureDevOps, GitCli cli) : IGitService
{
    private IGitService For(GitRemote remote) => AzureDevOpsGit.Parse(remote.Url) is not null ? azureDevOps : cli;

    public Task<string> ResolveBranchAsync(GitRemote remote, string branch, CancellationToken ct) => For(remote).ResolveBranchAsync(remote, branch, ct);
    public Task<string?> ReadFileAsync(GitRemote remote, string branch, string commit, string path, CancellationToken ct) =>
        For(remote).ReadFileAsync(remote, branch, commit, path, ct);
    public Task<IReadOnlyList<string>> ListFilesAsync(GitRemote remote, string branch, string commit, string folder, CancellationToken ct) =>
        For(remote).ListFilesAsync(remote, branch, commit, folder, ct);
    public Task<IReadOnlyList<string>> ListBranchesAsync(GitRemote remote, CancellationToken ct) => For(remote).ListBranchesAsync(remote, ct);
    public Task<IReadOnlyList<string>?> ChangedFilesAsync(GitRemote remote, string branch, string baseCommit, string headCommit, CancellationToken ct) =>
        For(remote).ChangedFilesAsync(remote, branch, baseCommit, headCommit, ct);
}
