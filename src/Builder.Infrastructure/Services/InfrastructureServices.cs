using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
    public async Task<IReadOnlyList<RepositoryDto>> ListRepositoriesAsync(string organizationUrl, string? username, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{organizationUrl.TrimEnd('/')}/_apis/git/repositories?api-version=7.1");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username ?? ""}:{token}")));
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Azure DevOps returned {(int)res.StatusCode} {res.ReasonPhrase}. Check the organization URL and the PAT (needs Code: Read).");
        var body = await res.Content.ReadFromJsonAsync<RepoList>(ct);
        return (body?.Value ?? [])
            .Select(r => new RepositoryDto(r.Project?.Name ?? "", r.Name, r.RemoteUrl,
                r.DefaultBranch?.Replace("refs/heads/", "")))
            .OrderBy(r => r.Project).ThenBy(r => r.Name).ToList();
    }

    private sealed record RepoList(List<Repo> Value);
    private sealed record Repo(string Name, string RemoteUrl, string? DefaultBranch, Project? Project);
    private sealed record Project(string Name);
}
