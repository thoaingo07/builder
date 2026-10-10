using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Builder.Application;
using Builder.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Builder.Infrastructure.Git;

public sealed class BuilderStorageOptions
{
    /// <summary>Root folder for the git cache, artifacts and Data Protection keys.</summary>
    public string DataDirectory { get; set; } = "data";
}

/// <summary>
/// <see cref="IGitService"/> on top of the git CLI. Credentials are passed per command as an
/// <c>http.extraHeader</c> and are never written to a config file.
/// </summary>
public sealed partial class GitCli(IOptions<BuilderStorageOptions> options) : IGitService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepoLocks = new();
    private readonly string _cacheRoot = Path.GetFullPath(Path.Combine(options.Value.DataDirectory, "git-cache"));

    public async Task<string> ResolveBranchAsync(GitRemote remote, string branch, CancellationToken ct)
    {
        if (ShaRegex().IsMatch(branch)) return branch.ToLowerInvariant();
        var r = await RunAsync(null, remote, ct, "ls-remote", remote.Url, $"refs/heads/{branch}", $"refs/tags/{branch}");
        if (r.ExitCode != 0) throw new ExternalServiceException($"Cannot reach repository: {r.Error}");
        var line = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? throw new ExternalServiceException($"Branch '{branch}' does not exist in {remote.Url}.");
        return line.Split('\t')[0].Trim();
    }

    public Task<string?> ReadFileAsync(GitRemote remote, string branch, string commit, string path, CancellationToken ct) =>
        WithCommitAsync(remote, branch, commit, ct, async dir =>
        {
            var show = await RunAsync(dir, null, ct, "show", $"{commit}:{path.TrimStart('/')}");
            if (show.ExitCode == 0) return show.Output;
            if (show.Error.Contains("does not exist") || show.Error.Contains("exists on disk, but not in")) return (string?)null;
            throw new ExternalServiceException($"git show failed: {show.Error}");
        });

    public Task<IReadOnlyList<string>> ListFilesAsync(GitRemote remote, string branch, string commit, string folder, CancellationToken ct) =>
        WithCommitAsync(remote, branch, commit, ct, async dir =>
        {
            var tree = await RunAsync(dir, null, ct, "ls-tree", "--name-only", commit, folder.Trim('/') + "/");
            if (tree.ExitCode != 0) throw new ExternalServiceException($"git ls-tree failed: {tree.Error}");
            return (IReadOnlyList<string>)tree.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        });

    public Task<IReadOnlyList<string>?> ChangedFilesAsync(GitRemote remote, string branch, string baseCommit, string headCommit, CancellationToken ct) =>
        WithCommitAsync(remote, branch, headCommit, ct, async dir =>
        {
            if ((await RunAsync(dir, null, ct, "cat-file", "-e", $"{baseCommit}^{{commit}}")).ExitCode != 0
                && (await RunAsync(dir, remote, ct, "fetch", "--quiet", "--depth", "50", remote.Url, baseCommit)).ExitCode != 0)
                return (IReadOnlyList<string>?)null; // base not reachable (new branch, force push): no path filtering
            var diff = await RunAsync(dir, null, ct, "diff", "--name-only", baseCommit, headCommit);
            return diff.ExitCode != 0 ? null : diff.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        });

    public async Task<IReadOnlyList<string>> ListBranchesAsync(GitRemote remote, CancellationToken ct)
    {
        var r = await RunAsync(null, remote, ct, "ls-remote", "--heads", remote.Url);
        if (r.ExitCode != 0) throw new ExternalServiceException($"Cannot reach repository: {r.Error.Trim()}");
        return r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t')[1].Replace("refs/heads/", "")).ToList();
    }

    /// <summary>Makes sure the commit is in the bare cache (shallow fetch by SHA, else the branch) and runs <paramref name="action"/>.</summary>
    private async Task<T> WithCommitAsync<T>(GitRemote remote, string branch, string commit, CancellationToken ct, Func<string, Task<T>> action)
    {
        var dir = Path.Combine(_cacheRoot, Hash(remote.Url));
        var gate = RepoLocks.GetOrAdd(dir, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(Path.Combine(dir, "objects")))
            {
                Directory.CreateDirectory(dir);
                await RunCheckedAsync(dir, null, ct, "init", "--bare", "--quiet");
            }
            if ((await RunAsync(dir, null, ct, "cat-file", "-e", $"{commit}^{{commit}}")).ExitCode != 0)
            {
                var byCommit = await RunAsync(dir, remote, ct, "fetch", "--quiet", "--depth", "1", remote.Url, commit);
                if (byCommit.ExitCode != 0)
                    await RunCheckedAsync(dir, remote, ct, "fetch", "--quiet", remote.Url, RefSpec(branch));
            }
            return await action(dir);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<string> RunCheckedAsync(string? cwd, GitRemote? auth, CancellationToken ct, params string[] args)
    {
        var r = await RunAsync(cwd, auth, ct, args);
        if (r.ExitCode != 0) throw new ExternalServiceException($"git {args.FirstOrDefault(a => !a.StartsWith('-'))} failed: {r.Error.Trim()}");
        return r.Output;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string? cwd, GitRemote? auth, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = cwd ?? Path.GetTempPath(),
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        if (auth?.AuthorizationHeader is { } header)
        {
            // through the environment, never on the command line
            psi.Environment["GIT_CONFIG_COUNT"] = "1";
            psi.Environment["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            psi.Environment["GIT_CONFIG_VALUE_0"] = "Authorization: " + header;
        }
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* already gone */ }
            throw;
        }
        return (p.ExitCode, await stdout, await stderr);
    }

    /// <summary>A branch name, or a full ref such as refs/pull/7/merge (pull request merge commits).</summary>
    private static string RefSpec(string branchOrRef) => branchOrRef.StartsWith("refs/", StringComparison.Ordinal)
        ? $"+{branchOrRef}:{branchOrRef}"
        : $"+refs/heads/{branchOrRef}:refs/heads/{branchOrRef}";

    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..24];

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    private static partial Regex ShaRegex();
}
