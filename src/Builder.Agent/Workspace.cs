using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using Builder.Contracts;
using Microsoft.Extensions.FileSystemGlobbing;
using YamlDotNet.RepresentationModel;

namespace Builder.Agent;

/// <summary>A build's checkout on this agent: <c>work/builds/{buildId}/src</c>, shared by its jobs.</summary>
public sealed class Workspace
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    public Workspace(string workRoot, Guid buildId)
    {
        BuildId = buildId;
        Root = Path.Combine(workRoot, "builds", buildId.ToString("N"));
        Source = Path.Combine(Root, "src");
        Temp = Path.Combine(Root, "tmp");
    }

    public Guid BuildId { get; }
    public string Root { get; }
    public string Source { get; }
    public string Temp { get; }

    public async Task CheckoutAsync(GitSource source, string? authorization, JobLog log, CancellationToken ct)
    {
        var gate = Locks.GetOrAdd(BuildId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Temp);
            if (Directory.Exists(Path.Combine(Source, ".git")))
            {
                var head = (await ProcessRunner.CaptureAsync("git", ["rev-parse", "HEAD"], Source, null, ct)).Trim();
                if (head.Equals(source.Commit, StringComparison.OrdinalIgnoreCase))
                {
                    log.System($"Workspace already at {Short(source.Commit)}");
                    return;
                }
            }
            log.System($"Checking out {source.Url} @ {source.Branch} ({Short(source.Commit)})");
            await Git(log, ct, null, "init", "--quiet");
            var auth = GitAuthEnvironment(authorization);
            var fetched = await Git(log, ct, auth, "fetch", "--quiet", "--depth", "1", source.Url, source.Commit) == 0;
            if (!fetched)
            {
                log.System("Shallow fetch by commit is not supported by this server; fetching the branch");
                if (await Git(log, ct, auth, "fetch", "--quiet", source.Url, $"+refs/heads/{source.Branch}:refs/remotes/origin/{source.Branch}") != 0)
                    throw new InvalidOperationException("git fetch failed");
            }
            if (await Git(log, ct, null, "checkout", "--quiet", "--force", source.Commit) != 0)
                throw new InvalidOperationException($"git checkout {source.Commit} failed");
            await Git(log, ct, null, "clean", "-ffdq");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Writes a copy of the Taskfile next to the original with the job's own <c>deps</c> removed (Builder already
    /// ran them, possibly on other agents) and returns its path.
    /// </summary>
    /// <param name="stepMarker">When set, a silent <c>echo {stepMarker}{i}</c> is inserted before every step i of the
    /// task (not before defers), so the agent can tell which step is running.</param>
    public string PrepareTaskfile(string taskfilePath, string taskName, Guid jobId, string? stepMarker = null)
    {
        var original = Path.GetFullPath(Path.Combine(Source, taskfilePath));
        if (!original.StartsWith(Source, StringComparison.Ordinal) || !File.Exists(original))
            throw new InvalidOperationException($"Taskfile '{taskfilePath}' was not found in the checkout.");

        var yaml = new YamlStream();
        using (var reader = new StreamReader(original)) yaml.Load(reader);
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var tasks = (YamlMappingNode)root.Children[new YamlScalarNode("tasks")];
        var key = new YamlScalarNode(taskName);
        if (tasks.Children.TryGetValue(key, out var task))
        {
            // shorthand tasks ("t: echo hi" / "t: [a, b]") become mappings so they can be edited
            var m = task as YamlMappingNode ?? new YamlMappingNode(new YamlScalarNode("cmds"),
                task is YamlSequenceNode seq ? seq : new YamlSequenceNode(task));
            tasks.Children[key] = m;
            m.Children.Remove(new YamlScalarNode("deps"));
            if (stepMarker is not null && m.Children.TryGetValue(new YamlScalarNode("cmds"), out var cmds) && cmds is YamlSequenceNode list)
            {
                var marked = new YamlSequenceNode();
                for (var i = 0; i < list.Children.Count; i++)
                {
                    var step = list.Children[i];
                    if (step is not YamlMappingNode sm || !sm.Children.ContainsKey(new YamlScalarNode("defer")))
                        marked.Add(new YamlMappingNode(
                            new YamlScalarNode("cmd"), new YamlScalarNode($"echo '{stepMarker}{i}'"),
                            new YamlScalarNode("silent"), new YamlScalarNode("true")));
                    marked.Add(step);
                }
                m.Children[new YamlScalarNode("cmds")] = marked;
            }
        }

        var derived = Path.Combine(Path.GetDirectoryName(original)!, $".builder-{jobId:N}.Taskfile.yml");
        using (var writer = new StreamWriter(derived)) yaml.Save(writer, assignAnchors: false);
        return derived;
    }

    /// <summary>Packs files matching the globs (relative to <paramref name="baseDir"/>) into one tar.gz.</summary>
    public async Task<(string Path, int Files)> PackAsync(IEnumerable<string> globs, string name, string baseDir, CancellationToken ct)
    {
        var matcher = new Matcher();
        foreach (var g in globs) matcher.AddInclude(g.TrimStart('/'));
        var files = matcher.GetResultsInFullPath(baseDir).ToList();
        var archive = Path.Combine(Temp, $"{name}-{Guid.NewGuid():N}.tar.gz");
        await using (var file = File.Create(archive))
        await using (var gz = new GZipStream(file, CompressionLevel.Fastest))
        await using (var tar = new TarWriter(gz, TarEntryFormat.Pax))
        {
            foreach (var f in files)
                await tar.WriteEntryAsync(f, Path.GetRelativePath(baseDir, f).Replace('\\', '/'), ct);
        }
        return (archive, files.Count);
    }

    public async Task UnpackAsync(Stream tarGz, string baseDir, CancellationToken ct)
    {
        Directory.CreateDirectory(baseDir);
        await using var gz = new GZipStream(tarGz, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gz, baseDir, overwriteFiles: true, ct);
    }

    private async Task<int> Git(JobLog log, CancellationToken ct, Dictionary<string, string>? env, params string[] args) =>
        await ProcessRunner.RunAsync("git", args, Source, env,
            (stream, line) => log.Write(stream == LogStream.Err ? LogStream.System : stream, line), ct);

    /// <summary>
    /// The Authorization header as git config passed through the environment (GIT_CONFIG_COUNT/KEY/VALUE, git ≥ 2.31):
    /// not in .git/config, not on the command line (`ps`), only readable by this user and root while git runs.
    /// </summary>
    public static Dictionary<string, string>? GitAuthEnvironment(string? authorizationHeader) => authorizationHeader is null ? null : new()
    {
        ["GIT_CONFIG_COUNT"] = "1",
        ["GIT_CONFIG_KEY_0"] = "http.extraHeader",
        ["GIT_CONFIG_VALUE_0"] = "Authorization: " + authorizationHeader,
    };

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    public static void Release(Guid buildId) => Locks.TryRemove(buildId, out _);
}
