using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Builder.Application;
using Builder.Application.Abstractions;
using Builder.Application.Dtos;
using Builder.Infrastructure.Git;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Builder.Infrastructure.Services;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Builder.Secrets.v1");
    public string Protect(string plaintext) => _protector.Protect(plaintext);
    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}

/// <summary>PBKDF2-SHA256, 210k iterations. Format: <c>pbkdf2$iterations$salt$hash</c>.</summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const int Iterations = 210_000;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string stored, string password)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[2]), int.Parse(parts[1]),
            HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}

public sealed class FileArtifactStore(IOptions<BuilderStorageOptions> options) : IArtifactStore
{
    private readonly string _root = Path.GetFullPath(Path.Combine(options.Value.DataDirectory, "artifacts"));

    public async Task<(string StoragePath, long Size)> SaveAsync(Guid buildId, Guid jobId, string name, Stream content, CancellationToken ct)
    {
        var safe = string.Concat(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
        var relative = Path.Combine(buildId.ToString("N"), $"{jobId:N}-{safe}-{Guid.NewGuid():N}.tar.gz");
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using (var file = File.Create(full))
            await content.CopyToAsync(file, ct);
        return (relative, new FileInfo(full).Length);
    }

    public Stream OpenRead(string storagePath) => File.OpenRead(Path.Combine(_root, storagePath));

    public void Delete(string storagePath)
    {
        var full = Path.Combine(_root, storagePath);
        if (File.Exists(full)) File.Delete(full);
    }

    public void DeleteBuild(Guid buildId)
    {
        var dir = Path.Combine(_root, buildId.ToString("N"));
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}

public sealed class InMemoryAgentMetricsStore : IAgentMetricsStore
{
    private const int MaxSamples = 180; // 15 min at 5 s
    private readonly ConcurrentDictionary<Guid, LinkedList<AgentMetricsDto>> _samples = new();

    public void Record(Guid agentId, AgentMetricsDto metrics)
    {
        var list = _samples.GetOrAdd(agentId, _ => new());
        lock (list)
        {
            // keep one sample per ~5 s for history; always replace the newest for "latest"
            if (list.Last is { } last && (metrics.At - last.Value.At) < TimeSpan.FromSeconds(4.5))
                list.RemoveLast();
            list.AddLast(metrics);
            while (list.Count > MaxSamples) list.RemoveFirst();
        }
    }

    public AgentMetricsDto? Latest(Guid agentId)
    {
        if (!_samples.TryGetValue(agentId, out var list)) return null;
        lock (list) return list.Last?.Value;
    }

    public IReadOnlyList<AgentMetricsDto> History(Guid agentId)
    {
        if (!_samples.TryGetValue(agentId, out var list)) return [];
        lock (list) return list.ToList();
    }

    public void Forget(Guid agentId) => _samples.TryRemove(agentId, out _);
}

public sealed class BuildLock : IBuildLock
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        return new Releaser(_gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}

/// <summary>Broadcast wake-up: every waiter (planner and scheduler loops) is released.</summary>
public sealed class SchedulerSignal : ISchedulerSignal
{
    private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Wake() => Interlocked.Exchange(ref _tcs, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { await Volatile.Read(ref _tcs).Task.WaitAsync(timeout, ct); }
        catch (TimeoutException) { /* poll */ }
    }
}

public sealed class AzureDevOpsClient(HttpClient http) : IAzureDevOpsClient
{
    public async Task<int> TestAsync(string organizationUrl, string authorization, CancellationToken ct) =>
        (await ListProjectsAsync(organizationUrl, authorization, ct)).Count;

    public async Task<IReadOnlyList<string>> ListProjectsAsync(string organizationUrl, string authorization, CancellationToken ct)
    {
        var body = await GetAsync<ListOf<Project>>(organizationUrl, "_apis/projects?$top=500&api-version=7.1", authorization, ct);
        return body.Value.Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<RemoteRepositoryDto>> ListRepositoriesAsync(string organizationUrl, string? project, string authorization,
        CancellationToken ct)
    {
        var path = string.IsNullOrWhiteSpace(project)
            ? "_apis/git/repositories?api-version=7.1"
            : $"{Uri.EscapeDataString(project)}/_apis/git/repositories?api-version=7.1";
        var body = await GetAsync<ListOf<Repo>>(organizationUrl, path, authorization, ct);
        return body.Value
            .Where(r => r.IsDisabled != true)
            .Select(r => new RemoteRepositoryDto(r.Project?.Name ?? "", r.Name, CleanCloneUrl(r.RemoteUrl),
                r.DefaultBranch?.Replace("refs/heads/", "")))
            .OrderBy(r => r.Project).ThenBy(r => r.Name).ToList();
    }

    public async Task<int> InstallWebhooksAsync(string organizationUrl, string authorization, string project, string repository,
        string hookUrl, string headerName, string headerValue, CancellationToken ct)
    {
        var repo = await GetAsync<RepoRef>(organizationUrl,
            $"{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repository)}?api-version=7.1", authorization, ct);

        // drop Builder's earlier subscriptions to this URL (re-install = rotate the secret)
        var existing = await GetAsync<ListOf<Subscription>>(organizationUrl, "_apis/hooks/subscriptions?api-version=7.1", authorization, ct);
        foreach (var old in existing.Value.Where(s => s.ConsumerInputs?.GetValueOrDefault("url") == hookUrl))
        {
            using var del = new HttpRequestMessage(HttpMethod.Delete, $"{organizationUrl.TrimEnd('/')}/_apis/hooks/subscriptions/{old.Id}?api-version=7.1");
            del.Headers.TryAddWithoutValidation("Authorization", authorization);
            using var _ = await http.SendAsync(del, ct);
        }

        var created = 0;
        foreach (var eventType in new[] { "git.push", "git.pullrequest.created", "git.pullrequest.updated" })
        {
            var body = new
            {
                publisherId = "tfs",
                eventType,
                resourceVersion = "1.0",
                consumerId = "webHooks",
                consumerActionId = "httpRequest",
                publisherInputs = new Dictionary<string, string> { ["projectId"] = repo.Project.Id, ["repository"] = repo.Id },
                consumerInputs = new Dictionary<string, string> { ["url"] = hookUrl, ["httpHeaders"] = $"{headerName}:{headerValue}" },
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{organizationUrl.TrimEnd('/')}/_apis/hooks/subscriptions?api-version=7.1")
            {
                Content = JsonContent.Create(body),
            };
            req.Headers.TryAddWithoutValidation("Authorization", authorization);
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                var text = await res.Content.ReadAsStringAsync(ct);
                throw new ExternalServiceException($"Azure DevOps refused to create the {eventType} service hook ({(int)res.StatusCode}). " +
                    "The PAT / service principal needs permission to manage service hooks in the project. " + text[..Math.Min(200, text.Length)]);
            }
            created++;
        }
        return created;
    }

    private sealed record RepoRef(string Id, string Name, ProjectRef Project);
    private sealed record ProjectRef(string Id, string Name);
    private sealed record Subscription(string Id, Dictionary<string, string>? ConsumerInputs);

    /// <summary>Azure DevOps puts the org name in the URL's user part (https://org@dev.azure.com/...); auth comes from the header instead.</summary>
    public static string CleanCloneUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.UserInfo.Length > 0
            ? new UriBuilder(u) { UserName = "", Password = "" }.Uri.ToString()
            : url;

    private async Task<T> GetAsync<T>(string organizationUrl, string path, string authorization, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{organizationUrl.TrimEnd('/')}/{path}");
        req.Headers.TryAddWithoutValidation("Authorization", authorization);
        using var res = await http.SendAsync(req, ct);
        // Azure DevOps answers bad credentials with a 203 sign-in page or a 401
        if (res.StatusCode == System.Net.HttpStatusCode.NonAuthoritativeInformation || res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new ExternalServiceException("Azure DevOps rejected the credentials. Check the PAT (scope: Code → Read) and that it belongs to this organization.");
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new ExternalServiceException($"Azure DevOps returned 404 for {organizationUrl}. Check the organization URL (https://dev.azure.com/<org>).");
        if (!res.IsSuccessStatusCode)
            throw new ExternalServiceException($"Azure DevOps returned {(int)res.StatusCode} {res.ReasonPhrase}.");
        return await res.Content.ReadFromJsonAsync<T>(ct) ?? throw new ExternalServiceException("Empty response from Azure DevOps.");
    }

    private sealed record ListOf<T>(List<T> Value);
    private sealed record Repo(string Name, string RemoteUrl, string? DefaultBranch, Project? Project, bool? IsDisabled);
    private sealed record Project(string Name);
}
