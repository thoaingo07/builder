namespace Builder.Agent;

public sealed class AgentOptions
{
    /// <summary>Base URL of Builder.Api, e.g. http://builder-api:19100.</summary>
    public string ServerUrl { get; set; } = "http://localhost:19100";
    public string Token { get; set; } = "";
    /// <summary>Unique agent name; defaults to the machine name.</summary>
    public string? Name { get; set; }
    public string[] Labels { get; set; } = [];
    /// <summary>How many jobs may run at the same time.</summary>
    public int Capacity { get; set; } = 2;
    public string WorkDirectory { get; set; } = "work";
    /// <summary>go-task executable.</summary>
    public string TaskBinary { get; set; } = "task";

    public string EffectiveName => string.IsNullOrWhiteSpace(Name) ? Environment.MachineName.ToLowerInvariant() : Name.Trim();
    public string WorkRoot => Path.GetFullPath(WorkDirectory);
}
