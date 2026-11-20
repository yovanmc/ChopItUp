using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Rooms;
using ChopItUp.Hub.Spawning;
using ChopItUp.Hub.Tests.Spawning;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static ChopItUp.Hub.Tests.RunHostFixture;

namespace ChopItUp.Hub.Tests.Mcp;

/// <summary>Background jobs end to end: the job tools called through a real MCP client from inside a
/// fake spawn (the <c>RunToolsTests</c> pattern, which is what makes the caller in flight), and real
/// pwsh jobs in real job objects. Serialized with the other process tests because a job's peer
/// lookups read the machine's TCP table.</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class JobToolsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private static readonly SpawnLimits Instant = new(Budget: 4, Debounce: TimeSpan.Zero, MinSpacing: TimeSpan.Zero, Timeout: TimeSpan.FromSeconds(30), TranscriptMessages: 60, TranscriptChars: 24_000);
    // A one-second spawn timeout: a job that sleeps longer outlives anything a spawn could do itself.
    private static readonly RunLimits Limits = new(SpawnsPerHour: 100, Hour: TimeSpan.FromHours(1), DefaultHours: 8, SpawnTimeout: TimeSpan.FromSeconds(1), PhaseEntries: 100, GateTimeout: TimeSpan.FromMilliseconds(500));

    private static Task Post(HubTestHost host, string room, string body) =>
        host.Client.PostAsJsonAsync($"api/rooms/{room}/messages", new { body });

    private static async Task<CallToolResult> Call(HubTestHost host, string participant, string tool, Dictionary<string, object?> args)
    {
        await using var client = await host.ClientFor(participant);
        return await client.CallToolAsync(tool, args);
    }

    private static string ErrorText(CallToolResult r)
    {
        Assert.True(r.IsError, "expected a tool error");
        return string.Join("", r.Content.OfType<TextContentBlock>().Select(t => t.Text));
    }

    private static Dictionary<string, object?> StartArgs(string room, string command, bool gpu = false, int? timeout = null, string? cancel = null) => new()
    {
        ["room_id"] = room, ["command"] = command, ["gpu"] = gpu, ["timeout_minutes"] = timeout, ["cancel_command"] = cancel,
    };

    private static JobStore Jobs(HubTestHost host) => host.Services.GetRequiredService<JobStore>();

    private static async Task<T> Until<T>(Func<T?> read, string what) where T : class
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            if (read() is { } value) return value;
            await Task.Delay(50);
        }
        throw new TimeoutException($"never saw {what}");
    }

    private static Task<Job> JobEnded(HubTestHost host, long id) =>
        Until(() => Jobs(host).Get(id) is { Status: not JobStatus.Running } j ? j : null, $"job {id} end");

    private static async Task<int> PidOf(Job job) =>
        int.Parse(await Until(() => File.Exists(Path.Combine(job.Folder, "pid.txt")) && File.ReadAllText(Path.Combine(job.Folder, "pid.txt")).Trim() is { Length: > 0 } t ? t : null, "the job's pid file"));

    /// <summary>Reads a file the job may still be writing.</summary>
    private static string ReadShared(string path)
    {
        if (!File.Exists(path)) return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd();
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>The conductor's first turn starts one job and ends; every later turn presents.</summary>
    private static TaskCompletionSource<JsonElement> ConductorStartsAJob(HubTestHost host, FakeProcessRunner runner, string room, Dictionary<string, object?> args, Func<JsonElement, Task>? afterStart = null)
    {
        var started = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launches = 0;
        runner.Handler = async (spec, _, _) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) != "sonnet") return FakeProcessRunner.Ok("""{"result":"ok"}""");
            if (Interlocked.Increment(ref launches) == 1)
            {
                try
                {
                    var reply = HubTestHost.Json(await Call(host, "sonnet", "start_job", args));
                    if (afterStart is not null) await afterStart(reply);
                    started.TrySetResult(reply);
                }
                catch (Exception e) { started.TrySetException(e); }
                return FakeProcessRunner.Ok("""{"result":"waiting on the job"}""");
            }
            await PostAsInHost(host, "sonnet", room, "phase: ping the job is done");
            return FakeProcessRunner.Ok("""{"result":"presented"}""");
        };
        return started;
    }

    private const string SleepingJob = "$PID | Set-Content pid.txt\nStart-Sleep -Seconds 120\n";
    private const string CancelMarker = "Add-Content cancel-ran.txt 'x'\n";

    /// <summary>Done-when 1, 6 and 7: the job outlives its spawn, carries no hub credential, logs in
    /// its own folder, and its end posts the exit code with the log tail, credits its files to the
    /// starter and opens the conductor again.</summary>
    [Fact]
    public async Task A_job_that_outlives_its_spawn_reports_its_exit_and_tail_credits_its_files_and_wakes_the_conductor()
    {
        var probe = "CHOPITUP_PROBE_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(probe, "secret");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY_PROBE_85", "secret");
        try
        {
            var (host, runner, room) = await StartRunHostAsync(Instant, Limits);
            await using var _ = host;
            var started = ConductorStartsAJob(host, runner, room, StartArgs(room, """
                'rendered' | Set-Content out.txt
                'env: ' + ((Get-ChildItem env: | Where-Object { $_.Name -like 'CHOPITUP*' -or $_.Name -like 'OPENAI_API_KEY*' } | ForEach-Object Name | Sort-Object) -join ',')
                Start-Sleep -Milliseconds 2500
                'done'
                exit 3
                """));

            await Post(host, room, "/build-thing @sonnet begin");
            var reply = await started.Task.WaitAsync(Wait);
            var id = Job.IdFrom(reply.GetProperty("id").GetString()!)!.Value;
            await runner.NextSpecAsync(Wait);   // the first turn, now over
            var spawnEnded = DateTimeOffset.UtcNow;

            var conductorAgain = await runner.NextSpecAsync(Wait);
            Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(conductorAgain));
            var job = await JobEnded(host, id);
            Assert.Equal((JobStatus.Exited, (int?)3), (job.Status, job.ExitCode));
            Assert.True(job.EndedAt > spawnEnded && job.EndedAt - job.ProcessStartedAt > Limits.SpawnTimeout);

            var note = await WaitForNoteContaining(host, room, $"Job j{id} by @sonnet exited with code 3.");
            Assert.Contains("env: CHOPITUP_ARTIFACTS,CHOPITUP_JOB_DIR\n", note.Body);
            Assert.Contains("done", note.Body);
            Assert.Contains(note.Body, conductorAgain.StandardInput);   // the note rides the conductor's turn
            await WaitForNoteContaining(host, room, $"Job j{id} started by @sonnet (gpu: no, limit 120 min). Command:");

            var roomDir = host.Services.GetRequiredService<MessageStore>().GetRoom(room)!.Directory!;
            Assert.StartsWith(Path.Combine(RoomArtifacts.FolderFor(roomDir), "jobs", $"j{id}-"), job.Folder);
            Assert.True(File.Exists(Path.Combine(job.Folder, JobRunner.LogFile)));
            var folder = Path.GetFileName(job.Folder);
            var runs = host.Services.GetRequiredService<RunStore>();
            Assert.Equal("sonnet", runs.ArtifactAuthor(job.RunId, $"artifact:jobs/{folder}/out.txt"));
            Assert.NotNull((await Until(() => Jobs(host).Get(id) is { NotedAt: not null } j ? j : null, "the note mark")).NotedAt);
        }
        finally
        {
            Environment.SetEnvironmentVariable(probe, null);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY_PROBE_85", null);
        }
    }

    /// <summary>Done-when 4: every caller and argument the tools refuse, each with a note, and no job.</summary>
    [Fact]
    public async Task The_job_tools_refuse_hosts_spawns_not_in_flight_bad_limits_and_other_rooms_jobs()
    {
        var (host, runner, room) = await StartRunHostAsync(Instant, Limits);
        await using var _ = host;
        host.Services.GetRequiredService<MessageStore>().CreateRoom("other", "OTHER", null);
        var checks = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Handler = async (spec, _, _) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) != "sonnet") return FakeProcessRunner.Ok("""{"result":"ok"}""");
            try
            {
                checks.TrySetResult(
                [
                    ErrorText(await Call(host, "claude", "start_job", StartArgs(room, "'x'"))),
                    ErrorText(await Call(host, "opus", "start_job", StartArgs(room, "'x'"))),
                    ErrorText(await Call(host, "sonnet", "start_job", StartArgs(room, "'x'", timeout: 0))),
                    ErrorText(await Call(host, "sonnet", "start_job", StartArgs(room, "'x'", timeout: 481))),
                    ErrorText(await Call(host, "sonnet", "start_job", StartArgs(room, "  "))),
                    ErrorText(await Call(host, "sonnet", "job_status", new() { ["room_id"] = room, ["id"] = "j999" })),
                    ErrorText(await Call(host, "sonnet", "cancel_job", new() { ["room_id"] = room, ["id"] = "nope" })),
                ]);
            }
            catch (Exception e) { checks.TrySetException(e); }
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await Post(host, room, "/build-thing @sonnet begin");
        var errors = await checks.Task.WaitAsync(Wait);

        Assert.Contains("not by an app-backed host", errors[0]);
        Assert.Contains("in flight in room 'lab'", errors[1]);
        Assert.Contains("1 to 480", errors[2]);
        Assert.Contains("1 to 480", errors[3]);
        Assert.Contains("command is empty", errors[4]);
        Assert.Contains("No job 'j999'", errors[5]);
        Assert.Contains("No job 'nope'", errors[6]);
        foreach (var slug in new[] { "start_job by @claude: refused (not-a-spawn)", "start_job by @opus: refused (not-in-flight)", "refused (bad-timeout)", "refused (no-command)", "job_status by @sonnet: refused (unknown-job)" })
            await WaitForNoteContaining(host, room, slug);
        Assert.Null(Jobs(host).Get(1));
        Assert.Contains("has no directory", ErrorText(await Call(host, "sonnet", "start_job", StartArgs("other", "'x'"))));
    }

    /// <summary>Done-when 3, cancel leg: cancel_job runs the cancel command, kills the process and
    /// posts how the job ended.</summary>
    [Fact]
    public async Task Cancel_runs_the_cancel_command_kills_the_job_and_posts_a_note()
    {
        var (host, runner, room) = await StartRunHostAsync(Instant, Limits);
        await using var _ = host;
        var pid = 0;
        JsonElement cancelled = default;
        var started = ConductorStartsAJob(host, runner, room, StartArgs(room, SleepingJob, cancel: CancelMarker), async reply =>
        {
            pid = await PidOf(Jobs(host).Get(Job.IdFrom(reply.GetProperty("id").GetString()!)!.Value)!);
            cancelled = HubTestHost.Json(await Call(host, "sonnet", "cancel_job", new() { ["room_id"] = room, ["id"] = reply.GetProperty("id").GetString() }));
        });
        await Post(host, room, "/build-thing @sonnet begin");
        var id = Job.IdFrom((await started.Task.WaitAsync(Wait)).GetProperty("id").GetString()!)!.Value;
        var job = Jobs(host).Get(id)!;

        Assert.Equal(("cancelled", true), (cancelled.GetProperty("status").GetString(), cancelled.GetProperty("was_running").GetBoolean()));
        Assert.Equal(JobStatus.Cancelled, (await JobEnded(host, id)).Status);
        Assert.False(IsAlive(pid));
        Assert.Single(File.ReadAllLines(Path.Combine(job.Folder, "cancel-ran.txt")));
        await WaitForNoteContaining(host, room, $"Job j{id} by @sonnet was cancelled.");
    }

    /// <summary>Done-when 3, timeout leg, on a fake clock.</summary>
    [Fact]
    public async Task A_job_past_its_limit_runs_the_cancel_command_is_killed_and_noted()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (host, runner, room) = await StartRunHostAsync(Instant, Limits, clock);
        await using var _ = host;
        var started = ConductorStartsAJob(host, runner, room, StartArgs(room, SleepingJob, timeout: 1, cancel: CancelMarker));
        await Post(host, room, "/build-thing @sonnet begin");
        var id = Job.IdFrom((await started.Task.WaitAsync(Wait)).GetProperty("id").GetString()!)!.Value;
        var job = Jobs(host).Get(id)!;
        var pid = await PidOf(job);

        clock.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(JobStatus.TimedOut, (await JobEnded(host, id)).Status);
        Assert.False(IsAlive(pid));
        Assert.Single(File.ReadAllLines(Path.Combine(job.Folder, "cancel-ran.txt")));
        await WaitForNoteContaining(host, room, $"Job j{id} by @sonnet passed its 1 min limit and was stopped.");
    }

    /// <summary>Done-when 3, /stop leg: the run's end stops its jobs.</summary>
    [Fact]
    public async Task Stopping_the_run_stops_its_job_with_the_cancel_command_and_a_note()
    {
        var (host, runner, room) = await StartRunHostAsync(Instant, Limits);
        await using var _ = host;
        var started = ConductorStartsAJob(host, runner, room, StartArgs(room, SleepingJob, cancel: CancelMarker));
        await Post(host, room, "/build-thing @sonnet begin");
        var id = Job.IdFrom((await started.Task.WaitAsync(Wait)).GetProperty("id").GetString()!)!.Value;
        var job = Jobs(host).Get(id)!;
        var pid = await PidOf(job);

        await Post(host, room, "/stop");

        Assert.Equal(JobStatus.Stopped, (await JobEnded(host, id)).Status);
        Assert.False(IsAlive(pid));
        Assert.Single(File.ReadAllLines(Path.Combine(job.Folder, "cancel-ran.txt")));
        await WaitForNoteContaining(host, room, $"Job j{id} by @sonnet was stopped because its run ended or was stopped.");
    }

    /// <summary>Done-when 3, restart leg: the hub stops with a live job (the job is killed after its
    /// cancel command, and no note wakes anything during shutdown), and the next hub on the same
    /// data reports it.</summary>
    [Fact]
    public async Task A_hub_stopped_with_a_live_job_kills_it_and_the_next_start_reports_it_lost()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_jobrestart_" + Guid.NewGuid().ToString("N"));
        try
        {
            var (host, runner, room) = await StartRunHostAsync(Instant, Limits, dir: dir, deleteOnDispose: false);
            Job job;
            int pid;
            await using (host)
            {
                var started = ConductorStartsAJob(host, runner, room, StartArgs(room, SleepingJob, cancel: CancelMarker));
                await Post(host, room, "/build-thing @sonnet begin");
                var id = Job.IdFrom((await started.Task.WaitAsync(Wait)).GetProperty("id").GetString()!)!.Value;
                job = Jobs(host).Get(id)!;
                pid = await PidOf(job);
            }

            Assert.False(IsAlive(pid));
            Assert.Single(File.ReadAllLines(Path.Combine(job.Folder, "cancel-ran.txt")));
            SqliteConnection.ClearAllPools();

            var (again, _, _) = await StartRunHostAsync(Instant, Limits, dir: dir, deleteOnDispose: false);
            await using (again)
            {
                await WaitForNoteContaining(again, room, $"Job j{job.Id} by @sonnet was lost: the hub stopped while it ran.");
                var row = await Until(() => Jobs(again).Get(job.Id) is { NotedAt: not null } j ? j : null, "the lost job's note mark");
                Assert.Equal(JobStatus.Lost, row.Status);
            }
            Assert.Single(File.ReadAllLines(Path.Combine(job.Folder, "cancel-ran.txt")));   // not run again for a job that already stopped
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestDirs.DeleteTree(dir);
            TestDirs.DeleteTree(dir + "_rooms");
        }
    }

    /// <summary>Done-when 3, crash leg: a row a dead hub left running, whose process had started, is
    /// ended as lost at the next start, its cancel command runs once, and the note reaches the run
    /// it belonged to (parked by the restart).</summary>
    [Fact]
    public async Task A_running_row_left_by_a_crashed_hub_runs_its_cancel_command_and_is_reported_to_its_parked_run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_jobcrash_" + Guid.NewGuid().ToString("N"));
        try
        {
            var (host, _, room) = await StartRunHostAsync(Instant, Limits, dir: dir, deleteOnDispose: false);
            Job job;
            await using (host)
            {
                var run = host.Services.GetRequiredService<RunStore>().Start(room, "sonnet", "build-thing", "", 1, 4, 40, DateTimeOffset.UtcNow);
                var roomDir = host.Services.GetRequiredService<MessageStore>().GetRoom(room)!.Directory!;
                var folder = Path.Combine(RoomArtifacts.FolderFor(roomDir), "jobs", "j1-crashed");
                Directory.CreateDirectory(folder);
                job = Jobs(host).Start(room, run.Id, "sonnet", "render", CancelMarker, gpu: false, 120, _ => folder, DateTimeOffset.UtcNow);
                Jobs(host).MarkProcessStarted(job.Id, 999_999, DateTimeOffset.UtcNow);
                File.WriteAllText(Path.Combine(folder, "frame.png"), "png");
            }
            SqliteConnection.ClearAllPools();

            var (again, _, _) = await StartRunHostAsync(Instant, Limits, dir: dir, deleteOnDispose: false);
            await using (again)
            {
                await WaitForNoteContaining(again, room, $"Job j{job.Id} by @sonnet was lost: the hub stopped while it ran.");
                var row = await Until(() => Jobs(again).Get(job.Id) is { NotedAt: not null } j ? j : null, "the note mark");
                Assert.Equal(JobStatus.Lost, row.Status);
                Assert.Equal("sonnet", again.Services.GetRequiredService<RunStore>().ArtifactAuthor(job.RunId, "artifact:jobs/j1-crashed/frame.png"));
                Assert.Single(File.ReadAllLines(Path.Combine(job.Folder, "cancel-ran.txt")));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestDirs.DeleteTree(dir);
            TestDirs.DeleteTree(dir + "_rooms");
        }
    }

    /// <summary>Done-when 5 and 10, against the real owner-peer check: from inside a job, the owner's
    /// bearer and the starter's own bearer are both refused, and the job's process is inside a
    /// background-job entry for its room.</summary>
    [Fact]
    public async Task Bearers_presented_from_inside_a_job_are_refused_owner_and_model_alike()
    {
        var (host, runner, room) = await StartRunHostAsync(Instant, Limits);
        await using var _ = host;
        var owner = host.TokenFor(ChopDb.OwnerParticipantId);
        var model = host.TokenFor("sonnet");
        var url = new Uri(host.BaseAddress, $"api/rooms/{room}/messages");
        var mcp = new Uri(host.BaseAddress, "mcp");
        var command = $$"""
            $PID | Set-Content pid.txt
            foreach ($t in @(@{ Name = 'owner'; Url = '{{url}}'; Token = '{{owner}}' }, @{ Name = 'model'; Url = '{{mcp}}'; Token = '{{model}}' })) { # public-ok: placeholders the test fills at run time with scratch-hub bearers
                try {
                    Invoke-WebRequest -Method Post -Uri $t.Url -Headers @{ Authorization = "Bearer $($t.Token)"; Accept = 'application/json, text/event-stream' } -ContentType 'application/json' -Body '{"body":"forged","jsonrpc":"2.0","id":1,"method":"ping"}' -UseBasicParsing | Out-Null
                    "$($t.Name): allowed"
                } catch { "$($t.Name): $([int]$_.Exception.Response.StatusCode) $($_.ErrorDetails.Message)" }
            }
            Start-Sleep -Seconds 120
            """;
        var started = ConductorStartsAJob(host, runner, room, StartArgs(room, command));
        await Post(host, room, "/build-thing @sonnet begin");
        var id = Job.IdFrom((await started.Task.WaitAsync(Wait)).GetProperty("id").GetString()!)!.Value;
        var job = Jobs(host).Get(id)!;
        var pid = await PidOf(job);

        Assert.Equal(JobMembership.Inside, host.Services.GetRequiredService<SpawnJobs>().Membership(pid, out var entry));
        Assert.Equal((SpawnJobKind.BackgroundJob, room, "sonnet"), (entry!.Kind, entry.RoomId, entry.ParticipantId));

        var log = Path.Combine(job.Folder, JobRunner.LogFile);
        var text = await Until(() => ReadShared(log) is var t && t.Contains("model:") ? t : null, "both attempts in the log");
        Assert.Contains("owner: 403", text);
        Assert.Contains("model: 403", text);
        Assert.Contains("presented from inside a background job", text);
        Assert.DoesNotContain(await AllMessagesFrom(host, room), m => m.Body == "forged");
        await host.Services.GetRequiredService<JobRunner>().CancelAsync(id);
    }

    /// <summary>Done-when 2 and 11: two hubs on one GPU lock run their GPU jobs one after the other
    /// against a fake GPU service that records overlap, and a holder that died mid-render has its
    /// render interrupted before the next one starts.</summary>
    [Fact]
    public async Task Two_hubs_gpu_jobs_never_overlap_and_a_dead_holders_render_is_interrupted_first()
    {
        using var service = new FakeGpuService();
        var lockPath = Path.Combine(Path.GetTempPath(), "chopitup_gpulock_" + Guid.NewGuid().ToString("N"), "gpu.lock");
        var settings = new JobSettings(lockPath, TimeSpan.FromMilliseconds(50));
        try
        {
            // A holder process that died mid-render, as a crashed hub would leave it.
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            var record = JsonSerializer.Serialize(new GpuHolder(4242, "j9", @"C:\gone", $"Invoke-RestMethod {service.Url}interrupt | Out-Null"));
            using (var holder = Process.Start(new ProcessStartInfo("pwsh", ["-NoProfile", "-NonInteractive", "-Command",
                $"$f = [IO.File]::Open('{lockPath}', 'OpenOrCreate', 'ReadWrite', 'None'); $b = [Text.Encoding]::UTF8.GetBytes('{record.Replace("'", "''")}'); $f.Write($b, 0, $b.Length); $f.Flush(); 'held'; Start-Sleep -Seconds 60"])
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!)
            {
                Assert.Equal("held", await holder.StandardOutput.ReadLineAsync().WaitAsync(Wait));
                await service.Render("dead-hub");   // its render, still running on the service
                holder.Kill(entireProcessTree: true);
                await holder.WaitForExitAsync();
            }

            var (a, runnerA, room) = await StartRunHostAsync(Instant, Limits, jobSettings: settings);
            await using var _a = a;
            var (b, runnerB, _) = await StartRunHostAsync(Instant, Limits, jobSettings: settings);
            await using var _b = b;
            var render = $"Invoke-RestMethod {service.Url}render -TimeoutSec 60 | Out-Null";
            var cancel = $"Invoke-RestMethod {service.Url}interrupt | Out-Null";
            var startedA = ConductorStartsAJob(a, runnerA, room, StartArgs(room, render, gpu: true, cancel: cancel));
            var startedB = ConductorStartsAJob(b, runnerB, room, StartArgs(room, render, gpu: true, cancel: cancel));
            await Task.WhenAll(Post(a, room, "/build-thing @sonnet begin"), Post(b, room, "/build-thing @sonnet begin"));
            var idA = Job.IdFrom((await startedA.Task.WaitAsync(Wait)).GetProperty("id").GetString()!)!.Value;
            var idB = Job.IdFrom((await startedB.Task.WaitAsync(Wait)).GetProperty("id").GetString()!)!.Value;

            Assert.Equal((JobStatus.Exited, (int?)0), ((await JobEnded(a, idA)).Status, (await JobEnded(a, idA)).ExitCode));
            Assert.Equal((JobStatus.Exited, (int?)0), ((await JobEnded(b, idB)).Status, (await JobEnded(b, idB)).ExitCode));
            Assert.Equal(1, service.MaxConcurrent);
            Assert.Equal(["render dead-hub", "interrupt", "render", "render"], service.Events.Where(e => e != "end").ToArray()[..4]);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(lockPath)!, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A GPU service stand-in on a loopback socket: <c>/render</c> holds the GPU for a while
    /// and counts how many renders run at once, <c>/interrupt</c> ends the render in progress.</summary>
    private sealed class FakeGpuService : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private CancellationTokenSource _current = new();
        private int _running;

        public FakeGpuService()
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _ = AcceptLoop();
        }

        public string Url { get; }
        public int MaxConcurrent { get; private set; }
        public List<string> Events { get; } = [];

        /// <summary>Starts a render from the test itself, as a crashed hub's job left it running.</summary>
        public Task Render(string who)
        {
            lock (_gate) { Events.Add("render " + who); _running++; MaxConcurrent = Math.Max(MaxConcurrent, _running); }
            var mine = _current;
            _ = Task.Delay(TimeSpan.FromSeconds(60), mine.Token).ContinueWith(_ => { lock (_gate) { _running--; Events.Add("end"); } });
            return Task.CompletedTask;
        }

        private async Task AcceptLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception) { return; }
                _ = Serve(client);
            }
        }

        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var request = new StringBuilder();
                while (!request.ToString().Contains("\r\n\r\n"))
                {
                    var read = await stream.ReadAsync(buffer);
                    if (read == 0) return;
                    request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
                var path = request.ToString().Split(' ')[1];
                if (path.StartsWith("/interrupt"))
                {
                    lock (_gate) { Events.Add("interrupt"); _current.Cancel(); _current = new CancellationTokenSource(); }
                    await Task.Delay(300);   // the interrupted render ends
                }
                else
                {
                    CancellationToken token;
                    lock (_gate) { Events.Add("render"); _running++; MaxConcurrent = Math.Max(MaxConcurrent, _running); token = _current.Token; }
                    try { await Task.Delay(800, token); } catch (OperationCanceledException) { }
                    lock (_gate) { _running--; Events.Add("end"); }
                }
                var body = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"u8.ToArray();
                await stream.WriteAsync(body);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
