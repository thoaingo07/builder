using Builder.Application.Abstractions;
using Builder.Domain.Builds;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Builder.Infrastructure.Taskfiles;

/// <summary>
/// Reads a go-task Taskfile (v3) and produces the job graph reachable from an entry task:
/// every task becomes a job, <c>deps</c> become edges (run in parallel), <c>cmds</c> stay inside the job.
/// Builder settings come from <c>x-</c> keys, which go-task ignores.
/// </summary>
public sealed class TaskfilePlanner : ITaskfilePlanner
{
    public TaskfilePlan Plan(string taskfileYaml, string? entryTask)
    {
        var root = Load(taskfileYaml);
        if (root.Children.ContainsKey("includes"))
            throw new TaskfileException("Taskfile 'includes:' are not supported by Builder yet; keep pipeline tasks in the root Taskfile.");
        if (Get(root, "tasks") is not YamlMappingNode tasksNode || tasksNode.Children.Count == 0)
            throw new TaskfileException("The Taskfile has no 'tasks:'.");

        var tasks = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in tasksNode.Children)
        {
            var name = Scalar(k)!;
            tasks[name] = v;
            if (v is YamlMappingNode m && Get(m, "aliases") is YamlSequenceNode al)
                foreach (var a in al.Children.Select(Scalar).OfType<string>()) aliases[a] = name;
        }

        var entry = entryTask?.Trim();
        if (string.IsNullOrEmpty(entry))
            entry = Get(root, "x-builder") is YamlMappingNode xb ? Scalar(Get(xb, "entry")) : null;
        if (string.IsNullOrEmpty(entry)) entry = "default";
        entry = Resolve(entry, tasks, aliases, "entry task");

        var jobs = new Dictionary<string, PlannedJob>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var order = 0;

        string Visit(string taskName, Dictionary<string, string> vars, string path)
        {
            var key = vars.Count == 0 ? taskName : $"{taskName}[{string.Join(",", vars.OrderBy(v => v.Key).Select(v => $"{v.Key}={v.Value}"))}]";
            if (jobs.ContainsKey(key)) return key;
            if (!visiting.Add(key)) throw new TaskfileException($"Dependency cycle: {path} → {taskName}.");

            var node = tasks[taskName];
            var deps = new List<string>();
            foreach (var dep in DepsOf(node))
            {
                var (depName, depVars) = dep;
                var resolved = Resolve(depName, tasks, aliases, $"dependency of '{taskName}'");
                deps.Add(Visit(resolved, depVars, $"{path} → {taskName}"));
            }
            visiting.Remove(key);

            jobs[key] = ToJob(key, taskName, node, deps.Distinct().ToList(), vars, order++);
            return key;
        }

        Visit(entry, new(), "");
        return new TaskfilePlan(entry, jobs.Values.OrderBy(j => j.Order).ToList());
    }

    private static PlannedJob ToJob(string key, string name, YamlNode node, List<string> deps, Dictionary<string, string> vars, int order)
    {
        var m = node as YamlMappingNode;
        var hasCommands = node switch
        {
            YamlScalarNode s => !string.IsNullOrWhiteSpace(s.Value),
            YamlSequenceNode seq => seq.Children.Count > 0,
            YamlMappingNode map => Get(map, "cmds") is YamlSequenceNode { Children.Count: > 0 } || Get(map, "cmd") is not null,
            _ => false,
        };

        var labels = new List<string>();
        if (m is not null && Get(m, "x-agent") is { } agent)
        {
            var labelNode = agent is YamlMappingNode am ? Get(am, "labels") : agent;
            labels.AddRange(Strings(labelNode).Select(l => l.ToLowerInvariant()));
        }

        var artifacts = m is null ? [] : Strings(Get(m, "x-artifacts")).ToList();

        var secrets = m is null ? [] : Strings(Get(m, "x-secrets")).ToList();
        foreach (var bad in secrets.Where(s => !Domain.Secrets.Secret.IsValidName(s)))
            throw new TaskfileException($"Task '{name}': '{bad}' is not a valid secret name (UPPER_SNAKE_CASE)."); 

        ApprovalSpec? approval = null;
        if (m is not null && Get(m, "x-approval") is { } ap)
        {
            if (ap is YamlMappingNode apm)
                approval = new ApprovalSpec(Scalar(Get(apm, "message")) ?? $"Approve '{name}'?", Strings(Get(apm, "approvers")).ToList());
            else if (IsTrue(ap))
                approval = new ApprovalSpec($"Approve '{name}'?", []);
        }

        DeploySpec? deploy = null;
        if (m is not null && Get(m, "x-deploy") is YamlMappingNode dm)
        {
            var env = Scalar(Get(dm, "environment"));
            if (string.IsNullOrWhiteSpace(env))
                throw new TaskfileException($"Task '{name}': x-deploy.environment is required.");
            var compose = Scalar(Get(dm, "compose"));
            var manifests = Scalar(Get(dm, "manifests"));
            if (compose is not null && manifests is not null)
                throw new TaskfileException($"Task '{name}': x-deploy takes either 'compose' (ssh-docker) or 'manifests' (kubernetes), not both.");
            deploy = new DeploySpec(env, compose, Scalar(Get(dm, "project")), manifests, Scalar(Get(dm, "namespace")), Scalar(Get(dm, "url")));
        }

        var desc = m is null ? null : Scalar(Get(m, "desc")) ?? Scalar(Get(m, "summary"));
        var registries = new List<RegistrySpec>();
        if (m is not null && Get(m, "x-registries") is YamlSequenceNode regs)
            foreach (var r in regs.Children)
            {
                var spec = r switch
                {
                    YamlScalarNode s when !string.IsNullOrWhiteSpace(s.Value) => new RegistrySpec(s.Value!.Trim().ToLowerInvariant(), null),
                    YamlMappingNode rm when Scalar(Get(rm, "registry")) is { } host => new RegistrySpec(host.Trim().ToLowerInvariant(), Scalar(Get(rm, "connection"))),
                    _ => throw new TaskfileException($"Task '{name}': each x-registries entry is a registry host or {{ registry, connection }}."),
                };
                if (spec.Registry.Contains('/') || spec.Registry.Contains(':'))
                    throw new TaskfileException($"Task '{name}': '{spec.Registry}' should be a registry host such as myregistry.azurecr.io.");
                registries.Add(spec);
            }
        else if (m is not null && Get(m, "x-registries") is not null)
            throw new TaskfileException($"Task '{name}': x-registries must be a list.");
        var azureArtifacts = m is not null && Get(m, "x-azure-artifacts") is { } aa && IsTrue(aa);

        return new PlannedJob(key, name, desc, order, deps, vars, labels, artifacts, hasCommands, approval, deploy, secrets, registries, azureArtifacts,
            StepsOf(node), InputsOf(m));
    }

    /// <summary>The task's cmds as steps (shorthand tasks included).</summary>
    public static List<JobStep> StepsOf(YamlNode node)
    {
        var cmds = node switch
        {
            YamlScalarNode s when !string.IsNullOrWhiteSpace(s.Value) => [s],
            YamlSequenceNode seq => seq.Children.ToList(),
            YamlMappingNode m when Get(m, "cmds") is YamlSequenceNode seq => seq.Children.ToList(),
            YamlMappingNode m when Get(m, "cmd") is { } single => [single],
            _ => new List<YamlNode>(),
        };
        var steps = new List<JobStep>();
        foreach (var c in cmds)
        {
            var index = steps.Count;
            steps.Add(c switch
            {
                YamlScalarNode s => new JobStep(index, StepKind.Command, Label(s.Value), null),
                YamlMappingNode m when Scalar(Get(m, "task")) is { } task =>
                    new JobStep(index, StepKind.TaskCall, $"task: {task}", Get(m, "vars") is YamlMappingNode vm
                        ? vm.Children.Where(kv => Scalar(kv.Value) is not null).ToDictionary(kv => Scalar(kv.Key)!, kv => Scalar(kv.Value)!)
                        : null),
                YamlMappingNode m when Get(m, "defer") is { } d =>
                    new JobStep(index, StepKind.Defer, "defer: " + (d is YamlMappingNode dm ? Scalar(Get(dm, "task")) is { } t ? $"task: {t}" : Label(Scalar(Get(dm, "cmd"))) : Label(Scalar(d))), null),
                YamlMappingNode m when Get(m, "for") is not null => new JobStep(index, StepKind.Command, "for each: " + Label(Scalar(Get(m, "cmd"))), null),
                YamlMappingNode m => new JobStep(index, StepKind.Command, Label(Scalar(Get(m, "cmd"))), null),
                _ => new JobStep(index, StepKind.Command, "(step)", null),
            });
        }
        return steps;

        static string Label(string? cmd)
        {
            var first = (cmd ?? "").Trim().Split('\n')[0].Trim();
            return first.Length > 120 ? first[..117] + "..." : first.Length == 0 ? "(empty)" : first;
        }
    }

    /// <summary>go-task <c>requires: { vars: [A, { name: B, enum: [x, y] }] }</c>.</summary>
    private static List<InputSpec> InputsOf(YamlMappingNode? m)
    {
        if (m is null || Get(m, "requires") is not YamlMappingNode r || Get(r, "vars") is not YamlSequenceNode vars) return [];
        return vars.Children.Select(v => v switch
        {
            YamlScalarNode s when !string.IsNullOrWhiteSpace(s.Value) => new InputSpec(s.Value!.Trim(), null),
            YamlMappingNode vm when Scalar(Get(vm, "name")) is { } n => new InputSpec(n.Trim(), Strings(Get(vm, "enum")).ToList() is { Count: > 0 } e ? e : null),
            _ => null,
        }).OfType<InputSpec>().ToList();
    }

    private static IEnumerable<(string Name, Dictionary<string, string> Vars)> DepsOf(YamlNode node)
    {
        if (node is not YamlMappingNode m || Get(m, "deps") is not { } deps) yield break;
        if (deps is not YamlSequenceNode seq) throw new TaskfileException("'deps' must be a list.");
        foreach (var d in seq.Children)
        {
            switch (d)
            {
                case YamlScalarNode s:
                    yield return (s.Value ?? "", new());
                    break;
                case YamlMappingNode dm when Scalar(Get(dm, "task")) is { } task:
                    var vars = new Dictionary<string, string>();
                    if (Get(dm, "vars") is YamlMappingNode vm)
                        foreach (var (k, v) in vm.Children)
                            vars[Scalar(k)!] = Scalar(v) ?? throw new TaskfileException($"Dependency '{task}': only plain string vars are supported.");
                    yield return (task, vars);
                    break;
                default:
                    throw new TaskfileException("Each dep must be a task name or { task: name, vars: {...} }.");
            }
        }
    }

    private static string Resolve(string name, Dictionary<string, YamlNode> tasks, Dictionary<string, string> aliases, string what)
    {
        var n = name.Trim().TrimStart(':');
        if (n.Contains("{{")) throw new TaskfileException($"The {what} '{name}' is templated; Builder needs literal task names in deps.");
        if (tasks.ContainsKey(n)) return n;
        if (aliases.TryGetValue(n, out var real)) return real;
        throw new TaskfileException($"The {what} '{name}' does not exist in the Taskfile.");
    }

    private static YamlMappingNode Load(string yaml)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
                throw new TaskfileException("The Taskfile is empty or not a YAML mapping.");
            var version = Scalar(Get(root, "version"));
            if (version is not null && !version.StartsWith('3'))
                throw new TaskfileException($"Taskfile version '{version}' is not supported (use version: '3').");
            return root;
        }
        catch (YamlException ex)
        {
            throw new TaskfileException($"Invalid YAML at line {ex.Start.Line}: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private static YamlNode? Get(YamlMappingNode m, string key) =>
        m.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;

    private static string? Scalar(YamlNode? n) => n is YamlScalarNode s ? s.Value : null;

    private static bool IsTrue(YamlNode n) => Scalar(n) is "true" or "yes" or "on";

    private static IEnumerable<string> Strings(YamlNode? n) => n switch
    {
        YamlScalarNode s when !string.IsNullOrWhiteSpace(s.Value) => [s.Value!.Trim()],
        YamlSequenceNode seq => seq.Children.Select(Scalar).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()),
        _ => [],
    };
}
