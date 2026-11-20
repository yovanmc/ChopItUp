using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ChopItUp.Core.Messaging;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Rooms;
using Microsoft.Extensions.Hosting;

namespace ChopItUp.Hub.Spawning;

/// <summary>Where the GPU lock lives and which shell runs a job. Tests point the lock at a temp
/// file, so no test ever waits on the live hub's GPU slot.</summary>
public sealed record JobSettings(string GpuLockPath, TimeSpan? LockPoll = null, Func<ResolvedCli>? Shell = null)
{
    public static JobSettings Default => new(GpuLock.DefaultPath);

    /// <summary>How long a <c>cancel_command</c> may run before it is killed.</summary>
    public static readonly TimeSpan CancelLimit = TimeSpan.FromSeconds(60);

    public ResolvedCli ResolveShell() => Shell?.Invoke() ?? CliResolver.Resolve("pwsh");
}

/// <summary>Runs the background jobs spawns start with <c>start_job</c>. Each job is a pwsh script
/// written into its own folder under the room's artifacts folder (<c>jobs\j&lt;id&gt;-&lt;hex&gt;\</c>),
/// run in that folder with its output in <c>job.log</c>, inside a kill-on-close job object of
/// <see cref="SpawnJobs"/>, so it dies with the hub and the owner-peer check treats it as inside. Its
/// environment drops every <c>CHOPITUP</c> variable and both vendor API key variables, so it carries
/// neither the spawn bearer nor the shell's owner bearer.
///
/// A job moves Pending, then Running once its process starts, then Ended. A stop (cancel, timeout,
/// the run's end, the hub's stop) is single-flight and the first reason wins. A stop before the
/// process starts ends the job with no process and no cancel command. A stop while it runs runs the
/// cancel command first (a GPU job holds the GPU lock while it runs), then kills the tree.
///
/// Every job ends with a note through <see cref="SpawnerService.FinishJob"/>, which wakes the run,
/// and the row records when that note reached the room. At hub stop, jobs are stopped as lost and no
/// note is posted, so nothing wakes a run during shutdown. The next start reports every job still
/// marked running, or ended with no note.</summary>
public sealed class JobRunner(JobStore store, SpawnJobs tracker, SpawnerService spawner, RunStore runs,
    MessageStore messages, MessageSignal signal, JobSettings settings, TimeProvider clock) : BackgroundService
{
    public const int DefaultTimeoutMinutes = 120;
    public const int MaxTimeoutMinutes = 480;
    public const string LogFile = "job.log";
    public const string ScriptFile = "job.ps1";
    private const string CancelFile = "cancel.ps1";
    private const int TailLines = 40;
    private const int TailLineChars = 300;
    private const int TailChars = 4_000;
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);
    private static readonly string[] RemovedPrefixes = ["CHOPITUP", "OPENAI_API_KEY", "ANTHROPIC_API_KEY"];

    private readonly GpuLock _gpu = new(settings.GpuLockPath, settings.LockPoll);
    private readonly ConcurrentDictionary<long, Live> _live = new();
    private volatile bool _stopping;

    private enum Phase { Pending, Running, Ended }

    private sealed class Live(Job job, List<FileStream> scripts)
    {
        public Job Job { get; } = job;
        public List<FileStream> Scripts { get; } = scripts;
        public object Gate { get; } = new();
        public Phase Phase { get; set; } = Phase.Pending;
        public string? StopStatus { get; set; }
        public CancellationTokenSource Stop { get; } = new();
        public Task Done { get; set; } = Task.CompletedTask;
    }

    /// <summary>The jobs this hub process is running now.</summary>
    public int LiveCount => _live.Count;

    /// <summary>Writes the row, the folder and the script, tells the run loop, and starts the job's
    /// life in the background. Throws <see cref="IOException"/> when the folder cannot be made; the
    /// row then ends as failed.</summary>
    public Job Start(string roomId, long runId, string authorId, string command, string? cancelCommand, bool gpu,
        int timeoutMinutes, string artifactsFolder)
    {
        var job = store.Start(roomId, runId, authorId, command, cancelCommand, gpu, timeoutMinutes,
            id => Path.Combine(artifactsFolder, RoomArtifacts.JobsFolder, $"{Job.NameFor(id)}-{Guid.NewGuid().ToString("N")[..8]}"),
            clock.GetUtcNow());
        var scripts = new List<FileStream>();
        try
        {
            if (Directory.Exists(job.Folder)) throw new IOException($"'{job.Folder}' already exists");
            Directory.CreateDirectory(job.Folder);
            File.WriteAllText(Path.Combine(job.Folder, ScriptFile), command);
            if (cancelCommand is not null) File.WriteAllText(Path.Combine(job.Folder, CancelFile), cancelCommand);
            // Held read-only until the job ends: a spawn that can write the artifacts folder cannot
            // swap the code between the start note and the run, which may wait hours for the GPU.
            scripts.Add(HoldReadOnly(Path.Combine(job.Folder, ScriptFile)));
            if (cancelCommand is not null) scripts.Add(HoldReadOnly(Path.Combine(job.Folder, CancelFile)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            foreach (var held in scripts) held.Dispose();
            store.End(job.Id, JobStatus.Failed, null, clock.GetUtcNow());
            store.MarkNoted(job.Id, clock.GetUtcNow());   // the tool's refusal is the report
            throw new IOException(e.Message, e);
        }
        var live = new Live(job, scripts);
        _live[job.Id] = live;
        spawner.TrackJob(roomId, job.Name, TimeSpan.FromMinutes(timeoutMinutes),
            reason => StopAsync(job.Id, reason == JobStopReason.TimedOut ? JobStatus.TimedOut : JobStatus.Stopped).GetAwaiter().GetResult());
        live.Done = Task.Run(() => LifeAsync(live));
        return job;
    }

    /// <summary>Stops a live job as cancelled and waits for it to end. False when this hub is not
    /// running that job.</summary>
    public async Task<bool> CancelAsync(long id) => await StopAsync(id, JobStatus.Cancelled);

    private async Task<bool> StopAsync(long id, string status)
    {
        if (!_live.TryGetValue(id, out var live)) return false;
        RequestStop(live, status);
        await live.Done;
        return true;
    }

    private static void RequestStop(Live live, string status)
    {
        lock (live.Gate)
        {
            if (live.Phase == Phase.Ended || live.StopStatus is not null) return;
            live.StopStatus = status;
        }
        try { live.Stop.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task LifeAsync(Live live)
    {
        var job = live.Job;
        string status = JobStatus.Exited;
        int? exitCode = null;
        string? failure = null;
        using var deadline = clock.CreateTimer(_ => RequestStop(live, JobStatus.TimedOut), null, TimeSpan.FromMinutes(job.TimeoutMinutes), Timeout.InfiniteTimeSpan);
        GpuLock.Lease? lease = null;
        try
        {
            if (job.Gpu)
            {
                try { lease = await _gpu.AcquireAsync(new GpuHolder(Environment.ProcessId, job.Name, job.Folder, job.CancelCommand), live.Stop.Token); }
                catch (OperationCanceledException) { }
                if (lease?.Stale is { CancelCommand: { } staleCancel } stale)
                    await RunTempCancelAsync(job, staleCancel, Directory.Exists(stale.Folder) ? stale.Folder : job.Folder);
            }
            (status, exitCode, failure) = await RunProcessAsync(live, lease);
        }
        catch (Exception e)
        {
            (status, failure) = (JobStatus.Failed, e.Message);
            Console.Error.WriteLine($"job {job.Name}: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            lease?.Dispose();
            lock (live.Gate) live.Phase = Phase.Ended;
            _live.TryRemove(job.Id, out _);
            live.Stop.Dispose();
            foreach (var held in live.Scripts) held.Dispose();
        }
        Finish(job, status, exitCode, failure, report: !_stopping);
    }

    /// <summary>Starts the process unless a stop came first, waits for it to exit or be stopped, then
    /// closes its job object and drains its output.</summary>
    private async Task<(string Status, int? ExitCode, string? Failure)> RunProcessAsync(Live live, GpuLock.Lease? lease)
    {
        var job = live.Job;
        if (job.Gpu && lease is null) return (live.StopStatus ?? JobStatus.Stopped, null, null);

        var shell = settings.ResolveShell();
        var psi = ShellStart(shell, job.Folder, ScriptFile);
        psi.Environment["CHOPITUP_JOB_DIR"] = job.Folder;
        psi.Environment["CHOPITUP_ARTIFACTS"] = Path.GetDirectoryName(Path.GetDirectoryName(job.Folder))!;

        using var process = new Process { StartInfo = psi };
        // Opened before the start, so nothing between the start and the try below can throw.
        using var log = new FileStream(Path.Combine(job.Folder, LogFile), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        IDisposable jobObject;
        lock (live.Gate)
        {
            if (live.StopStatus is { } early) return (early, null, null);
            jobObject = StartConfined(process, p => tracker.Track(p, new ProcessSpec(psi.FileName, [], new Dictionary<string, string>(), job.Folder, "", "job/" + job.Name, job.RoomId, job.AuthorId), SpawnJobKind.BackgroundJob));
            live.Phase = Phase.Running;
        }
        var gate = new SemaphoreSlim(1, 1);
        var pumps = Task.WhenAll(Pump(process.StandardOutput.BaseStream, log, gate), Pump(process.StandardError.BaseStream, log, gate));
        string? stopped = null;
        try
        {
            store.MarkProcessStarted(job.Id, process.Id, clock.GetUtcNow());
            try { await process.WaitForExitAsync(live.Stop.Token); }
            catch (OperationCanceledException)
            {
                stopped = live.StopStatus ?? JobStatus.Stopped;
                if (job.CancelCommand is not null) await RunCancelAsync(job, Path.Combine(job.Folder, CancelFile), job.Folder);
                KillTree(process);
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(DrainGrace); }
                catch (TimeoutException) { }
            }
        }
        catch
        {
            KillTree(process);   // a job the hub cannot track any more must not outlive this call
            throw;
        }
        finally
        {
            jobObject.Dispose();   // kill-on-close: a helper the job left running dies here
        }
        try { await pumps.WaitAsync(DrainGrace); }
        catch (TimeoutException) { }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }

        if (stopped is not null) return (stopped, null, null);
        int? code = null;
        try { code = process.ExitCode; } catch (InvalidOperationException) { }
        return (JobStatus.Exited, code, null);
    }

    private static async Task Pump(Stream from, FileStream to, SemaphoreSlim gate)
    {
        var buffer = new byte[8192];
        int read;
        while ((read = await from.ReadAsync(buffer)) > 0)
        {
            await gate.WaitAsync();
            try { await to.WriteAsync(buffer.AsMemory(0, read)); await to.FlushAsync(); }
            finally { gate.Release(); }
        }
    }

    private static FileStream HoldReadOnly(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>Runs <paramref name="command"/> from a script in a fresh temp folder (never in a folder
    /// a spawn can write), then deletes the folder. For a dead holder's or a lost job's cancel command,
    /// whose text comes from the hub's own records.</summary>
    private async Task RunTempCancelAsync(Job job, string command, string workingDirectory)
    {
        DirectoryInfo? temp = null;
        try
        {
            temp = Directory.CreateTempSubdirectory("chopitup_job_");
            var script = Path.Combine(temp.FullName, CancelFile);
            await File.WriteAllTextAsync(script, command);
            await RunCancelAsync(job, script, Directory.Exists(workingDirectory) ? workingDirectory : temp.FullName);
        }
        catch (Exception e) { Console.Error.WriteLine($"job {job.Name}: cancel command failed: {e.GetType().Name}: {e.Message}"); }
        finally
        {
            try { temp?.Delete(recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Runs the cancel script at <paramref name="script"/> in
    /// <paramref name="workingDirectory"/>, inside a job object, for at most
    /// <see cref="JobSettings.CancelLimit"/>. Never throws.</summary>
    private async Task RunCancelAsync(Job job, string script, string workingDirectory)
    {
        try
        {
            var psi = ShellStart(settings.ResolveShell(), workingDirectory, script);
            psi.Environment["CHOPITUP_JOB_DIR"] = job.Folder;
            using var process = new Process { StartInfo = psi };
            using var jobObject = StartConfined(process, p => tracker.Track(p, new ProcessSpec(psi.FileName, [], new Dictionary<string, string>(), workingDirectory, "", "job-cancel/" + job.Name, job.RoomId, job.AuthorId), SpawnJobKind.BackgroundJob));
            var drain = Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
            using var limit = new CancellationTokenSource(JobSettings.CancelLimit);
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine($"job {job.Name}: cancel command passed {JobSettings.CancelLimit.TotalSeconds:0} s and was killed");
                KillTree(process);
            }
            try { await drain.WaitAsync(DrainGrace); } catch (TimeoutException) { }
        }
        catch (Exception e) { Console.Error.WriteLine($"job {job.Name}: cancel command failed: {e.GetType().Name}: {e.Message}"); }
    }

    private static ProcessStartInfo ShellStart(ResolvedCli shell, string workingDirectory, string script)
    {
        var psi = new ProcessStartInfo(shell.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var a in shell.LeadingArguments) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-File", script }) psi.ArgumentList.Add(a);
        foreach (var key in psi.Environment.Keys.Where(k => RemovedPrefixes.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToArray())
            psi.Environment.Remove(key);
        return psi;
    }

    /// <summary>Starts <paramref name="process"/> and confines it with <paramref name="track"/>. A
    /// live child the hub cannot confine is killed, never left running outside a job object, and the
    /// failure is rethrown.</summary>
    internal static IDisposable StartConfined(Process process, Func<Process, IDisposable> track)
    {
        process.Start();
        try { return track(process); }
        catch
        {
            KillTree(process);
            throw;
        }
    }

    private static void KillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Credits the job's files to its starter, ends the row and, unless the hub is stopping,
    /// reports through the run loop.</summary>
    private void Finish(Job job, string status, int? exitCode, string? failure, bool report)
    {
        CreditFiles(job);
        store.End(job.Id, status, exitCode, clock.GetUtcNow());
        if (!report) return;
        spawner.FinishJob(job.RoomId, job.Name, EndNote(job, status, exitCode, failure), () => store.MarkNoted(job.Id, clock.GetUtcNow()));
    }

    private void CreditFiles(Job job)
    {
        try
        {
            if (!Directory.Exists(job.Folder)) return;
            var scan = RoomArtifacts.Scan(job.Folder, skipJobs: false);
            var prefix = ArtifactRef.Prefix + RoomArtifacts.JobsFolder + "/" + Path.GetFileName(job.Folder) + "/";
            if (scan.Files.Count > 0) runs.RecordArtifacts(job.RunId, scan.Files.Keys.Select(p => prefix + p), job.AuthorId, clock.GetUtcNow());
        }
        catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"job {job.Name}: crediting its files failed: {e.Message}"); }
    }

    public static string EndNote(Job job, string status, int? exitCode, string? failure)
    {
        var what = status switch
        {
            JobStatus.Exited => $"exited with code {exitCode?.ToString() ?? "unknown"}",
            JobStatus.Cancelled => "was cancelled",
            JobStatus.TimedOut => $"passed its {job.TimeoutMinutes} min limit and was stopped",
            JobStatus.Stopped => "was stopped because its run ended or was stopped",
            JobStatus.Lost => "was lost: the hub stopped while it ran",
            _ => $"could not run ({failure ?? "unknown error"})",
        };
        var head = $"Job {job.Name} by @{job.AuthorId} {what}. Folder: artifact:{RoomArtifacts.JobsFolder}/{Path.GetFileName(job.Folder)}/";
        var tail = Tail(Path.Combine(job.Folder, LogFile));
        if (tail.Length == 0) return head + " The log is empty.";
        var fence = new string('`', Math.Max(3, LongestBacktickRun(tail) + 1));
        return $"{head} Last lines of {LogFile}:\n{fence}\n{tail}\n{fence}";
    }

    /// <summary>The last <see cref="TailLines"/> lines of a log, each cut to
    /// <see cref="TailLineChars"/>, the whole cut to <see cref="TailChars"/> from the end.</summary>
    public static string Tail(string logPath)
    {
        try
        {
            if (!File.Exists(logPath)) return "";
            using var file = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, file.Length - 64 * 1024);
            file.Position = start;
            using var reader = new StreamReader(file, Encoding.UTF8);
            var lines = reader.ReadToEnd().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            if (start > 0 && lines.Length > 1) lines = lines[1..];   // the first line may be cut
            var kept = lines.TakeLast(TailLines).Select(l => l.Length <= TailLineChars ? l : l[..TailLineChars] + "…");
            var text = string.Join('\n', kept);
            return text.Length <= TailChars ? text : "…" + text[^TailChars..];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static int LongestBacktickRun(string text)
    {
        int longest = 0, run = 0;
        foreach (var c in text) { run = c == '`' ? run + 1 : 0; longest = Math.Max(longest, run); }
        return longest;
    }

    private IReadOnlyList<Job> _unreported = [];

    /// <summary>Reads the jobs a previous hub process left while the host starts, before the server
    /// takes a request: ExecuteAsync runs on a pool thread, and a read there could see a job this hub
    /// has just started and end it as lost.</summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        try { _unreported = store.Unreported(); }
        catch (Exception e) { Console.Error.WriteLine($"jobs: reading lost jobs failed: {e.Message}"); }
        return base.StartAsync(cancellationToken);
    }

    /// <summary>Hub start: reports every job a previous hub process lost or never noted, one row at
    /// a time, so one bad row never stops the others.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var unreported = _unreported;
        if (unreported.Count == 0) return;

        // A previous hub that died holding the GPU left its record: stop that render now, unless
        // another hub holds the slot (it handled the record when it took the slot).
        if (unreported.Any(j => j.Status == JobStatus.Running && j.Gpu))
        {
            try
            {
                var recovering = unreported.First(j => j.Status == JobStatus.Running && j.Gpu);
                using var lease = _gpu.TryAcquire(new GpuHolder(Environment.ProcessId, "recovery", recovering.Folder, null));
                if (lease?.Stale is { CancelCommand: { } staleCancel } stale)
                    await RunTempCancelAsync(recovering, staleCancel, stale.Folder);
            }
            catch (Exception e) { Console.Error.WriteLine($"jobs: GPU record check at start failed: {e.Message}"); }
        }

        foreach (var job in unreported)
        {
            if (stoppingToken.IsCancellationRequested) return;
            try { await RecoverAsync(job); }
            catch (Exception e) { Console.Error.WriteLine($"jobs: recovering {job.Name} failed: {e.GetType().Name}: {e.Message}"); }
        }
    }

    private async Task RecoverAsync(Job job)
    {
        var status = job.Status;
        if (status == JobStatus.Running)
        {
            if (!job.Gpu && job.Pid is not null && job.CancelCommand is { } cancel) await RunTempCancelAsync(job, cancel, job.Folder);
            CreditFiles(job);
            store.End(job.Id, JobStatus.Lost, null, clock.GetUtcNow());
            status = JobStatus.Lost;
        }
        var note = EndNote(job, status, job.ExitCode, null);
        var owner = runs.Active(job.RoomId) ?? (runs.Latest(job.RoomId) is { Status: RunStatus.Parked } parked ? parked : null);
        if (owner?.Id == job.RunId)
        {
            spawner.TrackJob(job.RoomId, job.Name, TimeSpan.FromMinutes(1), _ => { });
            spawner.FinishJob(job.RoomId, job.Name, note, () => store.MarkNoted(job.Id, clock.GetUtcNow()));
            return;
        }
        try
        {
            signal.Publish(job.RoomId, messages.Post(job.RoomId, ChopDb.HubParticipantId, note));
            store.MarkNoted(job.Id, clock.GetUtcNow());
        }
        catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"jobs: note for {job.Name} not posted: {e.Message}"); }
    }

    /// <summary>Hub stop: every live job is stopped as lost and left for the next start to report.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        var live = _live.Values.ToList();
        foreach (var l in live) RequestStop(l, JobStatus.Lost);
        try { await Task.WhenAll(live.Select(l => l.Done)).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException) { Console.Error.WriteLine("jobs: some jobs did not stop within 10 s of shutdown"); }
        await base.StopAsync(cancellationToken);
    }
}
