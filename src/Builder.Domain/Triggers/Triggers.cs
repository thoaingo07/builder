using System.Text.RegularExpressions;
using Builder.Domain.Organizations;

namespace Builder.Domain.Triggers;

/// <summary>Branch and path filters; a leading '!' excludes. Empty include list = everything.</summary>
public sealed record Filter(List<string> Branches, List<string> Paths)
{
    public bool MatchesBranch(string branch) => Glob.Matches(Branches, branch);

    /// <summary>True when no path filter is set, or at least one changed file matches it.</summary>
    public bool MatchesPaths(IReadOnlyCollection<string>? changedFiles) =>
        Paths.Count == 0 || changedFiles is null || changedFiles.Any(f => Glob.Matches(Paths, f));
}

public sealed record PushTrigger(List<string> Branches, List<string> Paths, Dictionary<string, string> Vars);

/// <summary>Pull requests whose <em>target</em> branch matches.</summary>
public sealed record PullRequestTrigger(List<string> Branches, List<string> Paths, Dictionary<string, string> Vars);

public sealed record ScheduleTrigger(string Cron, string Branch, string TimeZone, Dictionary<string, string> Vars);

/// <summary><c>x-builder.triggers</c> of a runner file.</summary>
public sealed record TriggerSpec(PushTrigger? Push, PullRequestTrigger? PullRequest, List<ScheduleTrigger> Schedules)
{
    public static readonly TriggerSpec None = new(null, null, []);
    public bool IsEmpty => Push is null && PullRequest is null && Schedules.Count == 0;
}

public static class Glob
{
    /// <summary>
    /// Matches like Azure Pipelines / GitHub filters: <c>*</c> within a segment, <c>**</c> across segments,
    /// <c>?</c> one character; patterns starting with <c>!</c> exclude. With only excludes, everything else matches.
    /// </summary>
    public static bool Matches(IReadOnlyCollection<string> patterns, string value)
    {
        if (patterns.Count == 0) return true;
        var includes = patterns.Where(p => !p.StartsWith('!')).ToList();
        var excludes = patterns.Where(p => p.StartsWith('!')).Select(p => p[1..]).ToList();
        var included = includes.Count == 0 || includes.Any(p => Regex(p).IsMatch(value));
        return included && !excludes.Any(p => Regex(p).IsMatch(value));
    }

    private static Regex Regex(string pattern)
    {
        var p = pattern.Trim().TrimStart('/');
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            if (c == '*' && i + 1 < p.Length && p[i + 1] == '*')
            {
                // "**/" also matches zero segments
                if (i + 2 < p.Length && p[i + 2] == '/') { sb.Append("(.*/)?"); i += 2; }
                else { sb.Append(".*"); i++; }
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));
        }
        // a pattern for a folder ("src/") covers everything below it
        if (p.EndsWith('/')) sb.Append(".*");
        return new Regex(sb.Append('$').ToString(), RegexOptions.CultureInvariant);
    }
}

/// <summary>A schedule of a runner, kept from its runner file on the default branch; the scheduler runs due ones.</summary>
public sealed class RunnerSchedule : IOrgScoped
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid OrgId { get; private set; }
    public Guid PipelineId { get; private set; }
    public string Cron { get; private set; } = "";
    public string TimeZone { get; private set; } = "UTC";
    public string Branch { get; private set; } = "";
    public Dictionary<string, string> Vars { get; private set; } = new();
    public DateTimeOffset? NextRunAt { get; private set; }
    public DateTimeOffset? LastRunAt { get; private set; }

    private RunnerSchedule() { }

    public RunnerSchedule(Guid orgId, Guid pipelineId, ScheduleTrigger trigger, DateTimeOffset? nextRunAt)
    {
        OrgId = orgId;
        PipelineId = pipelineId;
        Cron = trigger.Cron;
        TimeZone = trigger.TimeZone;
        Branch = trigger.Branch;
        Vars = trigger.Vars;
        NextRunAt = nextRunAt;
    }

    public void Ran(DateTimeOffset at, DateTimeOffset? next)
    {
        LastRunAt = at;
        NextRunAt = next;
    }
}
