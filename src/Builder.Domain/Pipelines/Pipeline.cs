namespace Builder.Domain.Pipelines;

public sealed class Pipeline
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public string Name { get; private set; } = "";
    public Guid? ConnectionId { get; private set; }
    public string RepositoryUrl { get; private set; } = "";
    public string DefaultBranch { get; private set; } = "main";
    public string TaskfilePath { get; private set; } = "Taskfile.yml";
    public string? EntryTask { get; private set; }
    public int LastBuildNumber { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private Pipeline() { }

    public Pipeline(string name, Guid? connectionId, string repositoryUrl, string defaultBranch,
        string taskfilePath, string? entryTask, DateTimeOffset now)
    {
        CreatedAt = now;
        Update(name, connectionId, repositoryUrl, defaultBranch, taskfilePath, entryTask);
    }

    public void Update(string name, Guid? connectionId, string repositoryUrl, string defaultBranch,
        string taskfilePath, string? entryTask)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Pipeline name is required.");
        if (string.IsNullOrWhiteSpace(repositoryUrl)) throw new DomainException("Repository URL is required.");
        Name = name.Trim();
        ConnectionId = connectionId;
        RepositoryUrl = repositoryUrl.Trim();
        DefaultBranch = string.IsNullOrWhiteSpace(defaultBranch) ? "main" : defaultBranch.Trim();
        TaskfilePath = string.IsNullOrWhiteSpace(taskfilePath) ? "Taskfile.yml" : taskfilePath.Trim().TrimStart('/');
        EntryTask = string.IsNullOrWhiteSpace(entryTask) ? null : entryTask.Trim();
    }

    public int NextBuildNumber() => ++LastBuildNumber;
}
