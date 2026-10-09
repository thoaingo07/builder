using System.Diagnostics;
using Builder.Contracts;

namespace Builder.Agent;

public static class ProcessRunner
{
    /// <summary>
    /// Runs a process, streaming each output line to <paramref name="onLine"/>. Cancelling kills the whole
    /// process tree. Returns the exit code.
    /// </summary>
    public static async Task<int> RunAsync(string fileName, IEnumerable<string> args, string workingDirectory,
        IDictionary<string, string>? env, Action<LogStream, string> onLine, CancellationToken ct, string? stdin = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(LogStream.Out, e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine(LogStream.Err, e.Data); };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Cannot start '{fileName}': {ex.Message}. Is it installed on this agent?", ex);
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }

        await using (ct.Register(() =>
                     {
                         try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* exited */ }
                     }))
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        process.WaitForExit(); // flush async output handlers
        ct.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    /// <summary>Runs a command and returns its stdout; throws with stderr on failure.</summary>
    public static async Task<string> CaptureAsync(string fileName, IEnumerable<string> args, string workingDirectory,
        IDictionary<string, string>? env, CancellationToken ct)
    {
        var output = new List<string>();
        var errors = new List<string>();
        var code = await RunAsync(fileName, args, workingDirectory, env,
            (s, l) => (s == LogStream.Err ? errors : output).Add(l), ct);
        if (code != 0) throw new InvalidOperationException($"{fileName} exited with {code}: {string.Join("\n", errors.TakeLast(10))}");
        return string.Join("\n", output);
    }

    public static bool Exists(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(dir => File.Exists(Path.Combine(dir, fileName)) || File.Exists(Path.Combine(dir, fileName + ".exe")));
}
