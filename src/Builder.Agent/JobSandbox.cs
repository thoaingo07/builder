namespace Builder.Agent;

/// <summary>
/// A per-job home: HOME, DOCKER_CONFIG, AZURE_CONFIG_DIR, XDG dirs and TMPDIR all point inside
/// <c>work/builds/{build}/jobs/{job}</c>, so whatever tools write there (docker login, dotnet nuget add source,
/// az login, npm login, kubeconfigs) is deleted with the job. Package caches can stay shared: they hold packages,
/// not credentials.
/// </summary>
public sealed class JobSandbox : IDisposable
{
    private JobSandbox(string root) => Root = root;

    public string Root { get; }
    public string Home => Path.Combine(Root, "home");
    public string Temp => Path.Combine(Root, "tmp");
    public Dictionary<string, string> Environment { get; } = new();

    public static JobSandbox Create(Workspace workspace, Guid jobId, string workRoot, bool sharedPackageCaches)
    {
        var sandbox = new JobSandbox(Path.Combine(workspace.Root, "jobs", jobId.ToString("N")));
        var dirs = new Dictionary<string, string>
        {
            ["HOME"] = sandbox.Home,
            ["USERPROFILE"] = sandbox.Home,
            ["DOTNET_CLI_HOME"] = sandbox.Home,
            ["DOCKER_CONFIG"] = Path.Combine(sandbox.Root, "docker"),
            ["AZURE_CONFIG_DIR"] = Path.Combine(sandbox.Root, "azure"),
            ["XDG_CONFIG_HOME"] = Path.Combine(sandbox.Home, ".config"),
            ["XDG_DATA_HOME"] = Path.Combine(sandbox.Home, ".local", "share"),
            ["XDG_CACHE_HOME"] = Path.Combine(sandbox.Home, ".cache"),
            ["TMPDIR"] = sandbox.Temp,
            ["TMP"] = sandbox.Temp,
            ["TEMP"] = sandbox.Temp,
        };
        foreach (var (key, dir) in dirs)
        {
            Directory.CreateDirectory(dir);
            sandbox.Environment[key] = dir;
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(sandbox.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // the user's global git config is not read (HOME moved); keep git quiet and non-interactive
        sandbox.Environment["GIT_TERMINAL_PROMPT"] = "0";
        sandbox.Environment["DOTNET_NOLOGO"] = "1";
        sandbox.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        if (sharedPackageCaches)
        {
            sandbox.Environment["NUGET_PACKAGES"] = Directory.CreateDirectory(Path.Combine(workRoot, "cache", "nuget")).FullName;
            sandbox.Environment["npm_config_cache"] = Directory.CreateDirectory(Path.Combine(workRoot, "cache", "npm")).FullName;
        }
        return sandbox;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        catch { /* files still open by a killed child: removed by the next start-up sweep */ }
    }

    /// <summary>Removes job sandboxes left behind by a crash or power loss.</summary>
    public static int SweepLeftovers(string workRoot)
    {
        var builds = Path.Combine(workRoot, "builds");
        if (!Directory.Exists(builds)) return 0;
        var removed = 0;
        foreach (var jobs in Directory.GetDirectories(builds).Select(b => Path.Combine(b, "jobs")).Where(Directory.Exists))
        {
            try { Directory.Delete(jobs, true); removed++; } catch { /* best effort */ }
        }
        return removed;
    }
}
