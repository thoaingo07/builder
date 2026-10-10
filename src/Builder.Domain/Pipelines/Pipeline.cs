using Builder.Domain.Organizations;
using Builder.Domain.Repositories;

namespace Builder.Domain.Pipelines;

/// <summary>
/// A runner: one Taskfile under <c>.builder/runners/</c> of a repository, mapped into Builder. Running it runs that
/// file (from the repository root) on the branch the build asks for.
/// </summary>
public sealed class Pipeline : IOrgScoped
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid OrgId { get; private set; }
    public Guid RepositoryId { get; private set; }
    public string Name { get; private set; } = "";
    /// <summary>Path of the runner Taskfile inside the repository, e.g. <c>.builder/runners/ci.yml</c>.</summary>
    public string TaskfilePath { get; private set; } = "";
    public string? EntryTask { get; private set; }
    public int LastBuildNumber { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    /// <summary>Triggers as found in the runner file on the default branch (refreshed on pushes there).</summary>
    public Triggers.TriggerSpec? Triggers { get; private set; }
    public string? TriggersCommit { get; private set; }
    public string? TriggersError { get; private set; }

    private Pipeline() { }

    public void TriggersRead(Triggers.TriggerSpec? triggers, string commit, string? error)
    {
        Triggers = triggers;
        TriggersCommit = commit;
        TriggersError = error;
    }

    public Pipeline(Repository repository, string name, string taskfilePath, string? entryTask, DateTimeOffset now)
    {
        OrgId = repository.OrgId;
        RepositoryId = repository.Id;
        TaskfilePath = taskfilePath.Trim().TrimStart('/');
        CreatedAt = now;
        Update(name, entryTask);
    }

    public void Update(string name, string? entryTask)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Runner name is required.");
        Name = name.Trim();
        EntryTask = string.IsNullOrWhiteSpace(entryTask) ? null : entryTask.Trim();
    }

    public int NextBuildNumber() => ++LastBuildNumber;

    /// <summary>Runner name from its file: <c>.builder/runners/deploy-prod.yml</c> → <c>deploy-prod</c>.</summary>
    public static string NameFromFile(string path) => Path.GetFileNameWithoutExtension(path);
}
