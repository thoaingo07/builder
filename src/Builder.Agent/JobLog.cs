using System.Text.RegularExpressions;
using System.Threading.Channels;
using Builder.Contracts;

namespace Builder.Agent;

/// <summary>Buffers a job's log lines and ships them to the server in batches (every 250 ms or 200 lines).</summary>
public sealed partial class JobLog : IAsyncDisposable
{
    private readonly Channel<LogChunk> _channel = Channel.CreateUnbounded<LogChunk>(new() { SingleReader = true });
    private readonly Func<List<LogChunk>, Task> _send;
    private readonly Task _pump;
    private List<string> _secrets;

    public JobLog(Func<List<LogChunk>, Task> send, IEnumerable<string?> secrets)
    {
        _send = send;
        // mask secrets (and each of their lines, for multi-line keys) in everything we ship
        _secrets = Expand(secrets).Distinct().OrderByDescending(s => s.Length).ToList();
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Adds values to mask (e.g. secrets fetched after the log was created).</summary>
    public void AddSecrets(IEnumerable<string> values) =>
        _secrets = _secrets.Concat(Expand(values)).Distinct().OrderByDescending(s => s.Length).ToList();

    private static IEnumerable<string> Expand(IEnumerable<string?> values) =>
        values.Where(s => !string.IsNullOrWhiteSpace(s) && s!.Length >= 4)
            .SelectMany(s => s!.Split('\n').Select(l => l.Trim()).Where(l => l.Length >= 4).Append(s!));

    /// <summary>The step subsequent lines belong to (set from the step markers).</summary>
    public int? Step { get; set; }

    public void Out(string text) => Write(LogStream.Out, text);
    public void Err(string text) => Write(LogStream.Err, text);
    public void System(string text) => Write(LogStream.System, text);

    public void Write(LogStream stream, string text)
    {
        text = Ansi().Replace(text, "");
        foreach (var s in _secrets) text = text.Replace(s, "***");
        _channel.Writer.TryWrite(new LogChunk(DateTimeOffset.UtcNow, stream, text, Step));
    }

    private async Task PumpAsync()
    {
        var batch = new List<LogChunk>();
        while (await _channel.Reader.WaitToReadAsync())
        {
            await Task.Delay(250);
            while (batch.Count < 200 && _channel.Reader.TryRead(out var line)) batch.Add(line);
            try { await _send(batch); } catch { /* log lines are best effort */ }
            batch = new();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _pump;
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex Ansi();
}
