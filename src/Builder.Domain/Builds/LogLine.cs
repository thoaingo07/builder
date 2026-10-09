namespace Builder.Domain.Builds;

public enum LogStreamKind { Out, Err, System }

public sealed class LogLine
{
    public long Id { get; private set; }
    public Guid BuildId { get; private set; }
    public Guid JobId { get; private set; }
    public DateTimeOffset Timestamp { get; private set; }
    public LogStreamKind Stream { get; private set; }
    public string Text { get; private set; } = "";

    private LogLine() { }

    public LogLine(Guid buildId, Guid jobId, DateTimeOffset timestamp, LogStreamKind stream, string text)
    {
        BuildId = buildId;
        JobId = jobId;
        Timestamp = timestamp;
        Stream = stream;
        Text = text;
    }
}

public sealed class Artifact
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid BuildId { get; private set; }
    public Guid JobId { get; private set; }
    public string Name { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public string StoragePath { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }

    private Artifact() { }

    public Artifact(Guid buildId, Guid jobId, string name, long sizeBytes, string storagePath, DateTimeOffset now)
    {
        BuildId = buildId;
        JobId = jobId;
        Name = name;
        SizeBytes = sizeBytes;
        StoragePath = storagePath;
        CreatedAt = now;
    }
}
