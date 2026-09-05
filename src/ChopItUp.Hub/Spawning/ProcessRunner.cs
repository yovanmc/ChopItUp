using System.Diagnostics;

namespace ChopItUp.Hub.Spawning;

/// <summary>Everything a spawn needs to start: no shell, no inherited console, stdin is the prompt.
/// <see cref="Label"/> is for logs only (participant/spawn id); it never reaches the child.</summary>
public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string WorkingDirectory,
    string StandardInput,
    string Label);

/// <summary><see cref="ExitCode"/> is null when the process was killed. Exactly one of
/// <see cref="TimedOut"/>/<see cref="Cancelled"/> is true for a killed run; both false otherwise.</summary>
public sealed record ProcessResult(int? ExitCode, bool TimedOut, bool Cancelled, string StandardOutput, string StandardError, TimeSpan Elapsed, int ProcessId = 0);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellation);
}

/// <summary>Runs a child with stdin/stdout/stderr redirected, kills the whole tree on timeout or
/// cancellation (the Codex shim is <c>cmd.exe</c> with the real exe underneath — killing only the
/// parent would leave the model running and posting), and drains both output pipes before
/// returning (LESSONS, M4: a process can exit with its last line still in the pipe).</summary>
public sealed class ProcessRunner : IProcessRunner
{
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellation)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = spec.WorkingDirectory,
        };
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);
        foreach (var (k, v) in spec.Environment) psi.Environment[k] = v;

        var clock = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        process.Start();

        // The timeout is armed BEFORE the stdin write: a child that stalls before reading its prompt
        // (auth prompt, MCP startup hang) leaves the writer blocked on a full pipe, and an un-armed
        // timeout would never fire (critique pass 1, B1 — measured: a 24,000-char write to a
        // non-reading child did not complete in 6 s).
        bool timedOut = false, cancelled = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        linked.CancelAfter(timeout);

        // Readers first, then stdin: a child that fills its stdout pipe before reading stdin would
        // otherwise deadlock against a writer waiting on a full pipe.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            try
            {
                await process.StandardInput.WriteAsync(spec.StandardInput.AsMemory(), linked.Token);
                process.StandardInput.Close();
            }
            catch (IOException) { /* the child closed its stdin early; it may still be running, so the wait below still applies */ }
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = cancellation.IsCancellationRequested;
            timedOut = !cancelled;
            try { process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { /* already gone */ }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(DrainGrace); }
            catch (TimeoutException) { /* reported through ExitCode == null below */ }
        }

        // Drain. After a tree kill the pipes close promptly; the grace only matters for a grandchild
        // that survived (not expected) and would otherwise hold the read open forever.
        string outText = "", errText = "";
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(DrainGrace);
            outText = stdout.Result;
            errText = stderr.Result;
        }
        catch (TimeoutException) { errText = "(output pipes did not close within the drain grace)"; }

        int? exitCode = null;
        try { if (process.HasExited) exitCode = process.ExitCode; } catch (InvalidOperationException) { }
        if (timedOut || cancelled) exitCode = null;

        return new ProcessResult(exitCode, timedOut, cancelled, outText, errText, clock.Elapsed, process.Id);
    }
}
