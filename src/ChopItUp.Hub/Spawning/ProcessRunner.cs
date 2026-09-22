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
    string Label,
    string? RoomId = null,
    string? ParticipantId = null,
    IReadOnlyList<string>? RemoveEnvironmentPrefixes = null,
    int? OutputLimitBytes = null);

/// <summary><see cref="ExitCode"/> is null when the process was killed. Exactly one of
/// <see cref="TimedOut"/>/<see cref="Cancelled"/> is true for a killed run; both false otherwise.</summary>
public sealed record ProcessResult(int? ExitCode, bool TimedOut, bool Cancelled, string StandardOutput, string StandardError, TimeSpan Elapsed, int ProcessId = 0, bool OutputLimitExceeded = false);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellation);
}

/// <summary>Runs a child with stdin/stdout/stderr redirected, kills the whole tree on timeout or
/// cancellation (the Codex shim is <c>cmd.exe</c> with the real exe underneath — killing only the
/// parent would leave the model running and posting), and drains both output pipes before
/// returning (LESSONS, M4: a process can exit with its last line still in the pipe).</summary>
public sealed class ProcessRunner(SpawnJobs jobs) : IProcessRunner
{
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);

    public ProcessRunner() : this(new SpawnJobs()) { }

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellation)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            // Pinned: unset, .NET picks the console code page, and a hub started without a console (the
            // Desktop shell) falls back to ANSI, where U+2013 leaves as the single byte 0x96 and Codex
            // refuses the prompt as invalid UTF-8 (2026-09-22). Both CLIs read stdin as UTF-8.
            StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = spec.WorkingDirectory,
        };
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);
        if (spec.OutputLimitBytes is <= 0) throw new ArgumentOutOfRangeException(nameof(spec));
        if (spec.RemoveEnvironmentPrefixes is { } prefixes)
        {
            bool Removed(string name) => prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (spec.Environment.Keys.Any(Removed)) throw new ArgumentException("A removed environment variable cannot be restored by the spawn overlay.", nameof(spec));
            foreach (var key in psi.Environment.Keys.Where(Removed).ToArray()) psi.Environment.Remove(key);
        }
        foreach (var (k, v) in spec.Environment) psi.Environment[k] = v;

        var clock = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        process.Start();
        IDisposable tracked;
        try { tracked = jobs.Track(process, spec); }   // row 29: the next statement after Start, on purpose
        catch
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw;
        }

        // The timeout is armed BEFORE the stdin write: a child that stalls before reading its prompt
        // (auth prompt, MCP startup hang) leaves the writer blocked on a full pipe, and an un-armed
        // timeout would never fire (critique pass 1, B1 — measured: a 24,000-char write to a
        // non-reading child did not complete in 6 s).
        bool timedOut = false, cancelled = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        linked.CancelAfter(timeout);
        int outputLimit = 0;
        void Overflow()
        {
            Interlocked.Exchange(ref outputLimit, 1);
            try { linked.Cancel(); } catch (ObjectDisposedException) { /* a late pipe close after drain grace */ }
        }

        // Readers first, then stdin: a child that fills its stdout pipe before reading stdin would
        // otherwise deadlock against a writer waiting on a full pipe.
        var stdout = spec.OutputLimitBytes is { } outCap ? ReadCappedAsync(process.StandardOutput.BaseStream, outCap, Overflow) : process.StandardOutput.ReadToEndAsync();
        var stderr = spec.OutputLimitBytes is { } errCap ? ReadCappedAsync(process.StandardError.BaseStream, errCap, Overflow) : process.StandardError.ReadToEndAsync();
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
            timedOut = !cancelled && Volatile.Read(ref outputLimit) == 0;
            try { process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { /* already gone */ }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(DrainGrace); }
            catch (TimeoutException) { /* reported through ExitCode == null below */ }
        }
        finally
        {
            tracked.Dispose();   // close the job BEFORE the drain: a grandchild that survived the tree
        }                        // kill dies here, instead of holding the pipes open for DrainGrace

        // Drain. After a tree kill the pipes close promptly; the grace only matters for a grandchild
        // that survived (not expected) and would otherwise hold the read open forever.
        string outText = "", errText = "";
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(DrainGrace);
            outText = stdout.Result;
            errText = stderr.Result;
        }
        catch (TimeoutException)
        {
            errText = "(output pipes did not close within the drain grace)";
            if (stdout.IsCompletedSuccessfully) outText = stdout.Result;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Row 46, R11: a process that started always yields a result, so its turn commit can credit it.
            errText = "(output pipes failed: " + e.GetBaseException().Message + ")";
            if (stdout.IsCompletedSuccessfully) outText = stdout.Result;
        }

        int? exitCode = null;
        try { if (process.HasExited) exitCode = process.ExitCode; } catch (InvalidOperationException) { }
        if (timedOut || cancelled || outputLimit != 0) exitCode = null;

        return new ProcessResult(exitCode, timedOut, cancelled, outText, errText, clock.Elapsed, process.Id, outputLimit != 0);
    }

    private static async Task<string> ReadCappedAsync(Stream stream, int cap, Action overflow)
    {
        using var retained = new MemoryStream(Math.Min(cap, 8192));
        var buffer = new byte[8192];
        bool exceeded = false;
        int count;
        while ((count = await stream.ReadAsync(buffer)) != 0)
        {
            var keep = Math.Min(count, cap - (int)retained.Length);
            if (keep > 0) retained.Write(buffer, 0, keep);
            if (keep < count && !exceeded) { exceeded = true; overflow(); }
        }
        // Discard partial output on overflow; it is never a successful model answer.
        return exceeded ? "" : System.Text.Encoding.UTF8.GetString(retained.GetBuffer(), 0, (int)retained.Length);
    }
}
