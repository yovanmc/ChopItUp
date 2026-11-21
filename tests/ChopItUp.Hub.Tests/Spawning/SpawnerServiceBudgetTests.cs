using System.Net.Http.Json;
using System.Text.Json;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Spawning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static ChopItUp.Hub.Tests.RunHostFixture;

namespace ChopItUp.Hub.Tests.Spawning;

/// <summary>Run budgets and job waits end to end: the `hours:` token at run start, `/continue` on a
/// park, a ping past the budget, and a run waiting on a background job.</summary>
public sealed class SpawnerServiceBudgetTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private static readonly SpawnLimits Instant = new(Budget: 4, Debounce: TimeSpan.Zero, MinSpacing: TimeSpan.Zero, Timeout: TimeSpan.FromSeconds(30), TranscriptMessages: 60, TranscriptChars: 24_000);

    // One budget hour buys one spawn here, so the conductor's own launch spends a one-hour budget.
    private static readonly RunLimits OneSpawnPerHour = new(SpawnsPerHour: 1, Hour: TimeSpan.FromHours(1), DefaultHours: 1, SpawnTimeout: TimeSpan.FromMinutes(30), PhaseEntries: 100);

    private static Task Post(HubTestHost host, string room, string body) =>
        host.Client.PostAsJsonAsync($"api/rooms/{room}/messages", new { body });

    [Theory]
    [InlineData("/build-thing @sonnet hours: 2 begin", 2, 20)]
    [InlineData("/build-thing @sonnet begin", 4, 40)]
    public async Task A_run_start_stores_its_hours_and_spawn_allowance_and_a_conductor_post_cannot_change_them(string start, int hours, int spawns)
    {
        var (host, runner, room) = await StartRunHostAsync(Instant, RunLimits.Default);
        await using var _ = host;
        runner.Handler = async (spec, _, _) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet")
                await PostAsInHost(host, "sonnet", room, "phase: build @opus hours: 1 build it");
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };

        await Post(host, room, start);
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(await runner.NextSpecAsync(Wait)));
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(await runner.NextSpecAsync(Wait)));   // the post with hours: 1 was accepted as a phase

        var run = host.Services.GetRequiredService<RunStore>().Latest(room)!;
        Assert.Equal((hours, spawns), (run.BudgetHours, run.SpawnBudget));
        var started = await WaitForNoteContaining(host, room, "started");
        Assert.Contains($"budget {hours} h of active time and {spawns} spawns", started.Body);
    }

    [Theory]
    [InlineData("hours: 9")]
    [InlineData("hours: 0")]
    [InlineData("hours: 2.5")]
    public async Task A_run_start_with_hours_outside_1_to_8_is_refused_with_the_range_and_starts_nothing(string token)
    {
        var (host, runner, room) = await StartRunHostAsync(Instant, RunLimits.Default);
        await using var _ = host;
        runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"result":"working"}"""));

        await Post(host, room, $"/build-thing @sonnet {token} begin");

        var note = await WaitForNoteContaining(host, room, "Run not started");
        Assert.Equal("Run not started: hours: must be a whole number from 1 to 8 (default 4).", note.Body);
        Assert.Null(host.Services.GetRequiredService<RunStore>().Latest(room));
        Assert.True(await runner.NoSpecWithin(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task Continue_revives_a_spawn_cap_park_with_added_budget_and_reset_phase_counts_after_refusing_a_bad_value()
    {
        var (host, runner, room) = await StartRunHostAsync(Instant, OneSpawnPerHour);
        await using var _ = host;
        runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"result":"working"}"""));
        var runs = host.Services.GetRequiredService<RunStore>();

        await Post(host, room, "/build-thing @sonnet begin");
        await runner.NextSpecAsync(Wait);   // the one budgeted spawn; it exits silently and the cap parks the run
        var parked = await WaitForRun(host, room, r => r.Status == RunStatus.Parked);
        Assert.True(parked.CapSpent);
        runs.EnterPhase(parked.Id, "build/1", DateTimeOffset.UtcNow);   // a count the extension must clear

        await PostAsInHost(host, "opus", room, "/continue hours: 3");   // only a human post sets hours

        // The loop handles posts in order, so this note proves the model's post was already decided.
        await Post(host, room, "/continue hours: 0");
        Assert.Equal("hours: must be a whole number from 1 to 8 (default 4).", (await WaitForNoteContaining(host, room, "whole number")).Body);
        Assert.Equal((RunStatus.Parked, 1, 1), (runs.Latest(room)!.Status, runs.Latest(room)!.BudgetHours, runs.Latest(room)!.SpawnBudget));

        // Held in flight, so no later silence counts a phase entry before the asserts below.
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Handler = async (_, _, ct) =>
        {
            try { await hold.Task.WaitAsync(ct); } catch (OperationCanceledException) { }
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };
        await Post(host, room, "/continue hours: 2");
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(await runner.NextSpecAsync(Wait)));   // the conductor is open again
        var note = await WaitForNoteContaining(host, room, "extended");
        Assert.Equal($"Run #{parked.Id} extended by 2 h and 2 spawns: budget now 3 h and 3 spawns, phase entry counts reset.", note.Body);
        var extended = runs.ById(parked.Id)!;
        Assert.Equal((RunStatus.Active, false, 3, 3), (extended.Status, extended.CapSpent, extended.BudgetHours, extended.SpawnBudget));
        Assert.Empty(runs.PhaseEntries(parked.Id));
        hold.TrySetResult();
    }

    [Fact]
    public async Task The_conductors_ping_presents_when_its_own_launch_spent_the_budget()
    {
        var (host, runner, room) = await StartRunHostAsync(Instant, OneSpawnPerHour);
        await using var _ = host;
        runner.Handler = async (spec, _, _) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet")
                await PostAsInHost(host, "sonnet", room, "phase: ping the result is in the room");
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };

        await Post(host, room, "/build-thing @sonnet begin");
        await runner.NextSpecAsync(Wait);

        var ended = await WaitForNoteContaining(host, room, "ended");
        Assert.Contains("the conductor pinged", ended.Body);
        Assert.Equal(RunStatus.Ended, host.Services.GetRequiredService<RunStore>().Latest(room)!.Status);
        Assert.DoesNotContain(await AllMessagesFrom(host, room), m => m.Body.Contains("parked"));
    }

    /// <summary>The conductor starts a job and ends its turn. The run then waits: three times the
    /// spawn timeout and three times the wall clock pass with no stall park, no wall-clock park and
    /// no new spawn. The job's end opens the conductor, and the run's active time leaves the wait
    /// out.</summary>
    [Fact]
    public async Task A_run_waiting_on_a_job_neither_stalls_nor_spends_its_wall_clock_and_the_jobs_end_wakes_the_conductor()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limits = new RunLimits(SpawnsPerHour: 100, Hour: TimeSpan.FromMinutes(10), DefaultHours: 1, SpawnTimeout: TimeSpan.FromMinutes(6), PhaseEntries: 100);
        var (host, runner, room) = await StartRunHostAsync(Instant, limits, clock);
        await using var _ = host;
        var spawner = host.Services.GetRequiredService<SpawnerService>();
        var launches = 0;
        runner.Handler = async (spec, _, _) =>
        {
            // The second run's first turn starts a job with the same id: a finished id belongs to its run.
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet" && Interlocked.Increment(ref launches) is 1 or 3)
                spawner.TrackJob(room, "render-1", TimeSpan.FromHours(2), _ => { });
            else
                await PostAsInHost(host, "sonnet", room, "phase: ping rendered");
            return FakeProcessRunner.Ok("""{"result":"waiting on render-1"}""");
        };

        await Post(host, room, "/build-thing @sonnet begin");
        await runner.NextSpecAsync(Wait);
        var waiting = await WaitForRun(host, room, r => r.WaitingSince is not null);

        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.True(await runner.NoSpecWithin(TimeSpan.FromMilliseconds(700)));
        var stillWaiting = host.Services.GetRequiredService<RunStore>().Latest(room)!;
        Assert.Equal((RunStatus.Active, waiting.WaitingSince), (stillWaiting.Status, stillWaiting.WaitingSince));
        Assert.DoesNotContain(await AllMessagesFrom(host, room), m => m.Body.Contains("parked") || m.Body.Contains("@owner"));

        spawner.FinishJob(room, "render-1", "Job render-1 exited with code 0.");
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(await runner.NextSpecAsync(Wait)));
        var ended = await WaitForRun(host, room, r => r.Status == RunStatus.Ended);
        Assert.True(ended.WaitedSeconds >= 30 * 60);
        Assert.True(RunStore.ActiveElapsed(ended, clock.GetUtcNow()) < limits.Hour);
        await WaitForNoteContaining(host, room, "Job render-1 exited with code 0.");

        await Post(host, room, "/build-thing @sonnet again");
        await runner.NextSpecAsync(Wait);
        await WaitForRun(host, room, r => r.Id != ended.Id && r.WaitingSince is not null);
    }

    private static async Task WaitForCount(Func<int> read, int expected)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline && read() != expected) await Task.Delay(50);
        Assert.Equal(expected, read());
    }

    [Theory]
    [InlineData("text")]
    [InlineData("api")]
    public async Task A_park_keeps_the_runs_jobs_and_a_stop_kills_them(string how)
    {
        var (host, runner, room) = await StartRunHostAsync(Instant, OneSpawnPerHour);
        await using var _ = host;
        var spawner = host.Services.GetRequiredService<SpawnerService>();
        var kills = 0;
        runner.Handler = (_, _, _) =>
        {
            spawner.TrackJob(room, "train-1", TimeSpan.FromHours(2), _ => Interlocked.Increment(ref kills));
            return Task.FromResult(FakeProcessRunner.Ok("""{"result":"started train-1"}"""));
        };

        await Post(host, room, "/build-thing @sonnet begin");
        await runner.NextSpecAsync(Wait);
        await WaitForRun(host, room, r => r.Status == RunStatus.Parked);   // the spawn budget is spent
        // A later loop round trip: the refusal proves the park was fully carried out before the kill count is read.
        await Post(host, room, "keep going");
        await WaitForNoteContaining(host, room, "a cap is spent");
        Assert.Equal(0, Volatile.Read(ref kills));

        if (how == "text") await Post(host, room, "/stop");
        else Assert.True((await host.Client.PostAsync($"api/rooms/{room}/exchange/stop", null)).IsSuccessStatusCode);
        await WaitForRun(host, room, r => r.Status == RunStatus.Ended);
        await WaitForCount(() => Volatile.Read(ref kills), 1);
    }

    [Fact]
    public async Task A_ping_end_kills_the_runs_jobs()
    {
        var (host, runner, room) = await StartRunHostAsync(Instant, RunLimits.Default);
        await using var _ = host;
        var spawner = host.Services.GetRequiredService<SpawnerService>();
        var kills = 0;
        runner.Handler = async (_, _, _) =>
        {
            spawner.TrackJob(room, "train-2", TimeSpan.FromHours(2), _ => Interlocked.Increment(ref kills));
            await PostAsInHost(host, "sonnet", room, "phase: ping done, the job is no longer needed");
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };

        await Post(host, room, "/build-thing @sonnet begin");
        await WaitForRun(host, room, r => r.Status == RunStatus.Ended);
        await WaitForCount(() => Volatile.Read(ref kills), 1);
    }

    /// <summary>A job that never reports is killed and finished by the hub at its timeout plus the
    /// grace, so the wait ends and the conductor opens.</summary>
    [Fact]
    public async Task A_job_that_never_reports_is_stopped_at_its_deadline_and_wakes_the_conductor()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limits = new RunLimits(SpawnsPerHour: 100, Hour: TimeSpan.FromHours(1), DefaultHours: 8, SpawnTimeout: TimeSpan.FromMinutes(6), PhaseEntries: 100);
        var (host, runner, room) = await StartRunHostAsync(Instant, limits, clock);
        await using var _ = host;
        var spawner = host.Services.GetRequiredService<SpawnerService>();
        var kills = 0;
        var reason = -1;
        var launches = 0;
        runner.Handler = async (_, _, _) =>
        {
            if (Interlocked.Increment(ref launches) == 1)
                spawner.TrackJob(room, "hung-1", TimeSpan.FromMinutes(10), r => { Volatile.Write(ref reason, (int)r); Interlocked.Increment(ref kills); });
            else await PostAsInHost(host, "sonnet", room, "phase: ping the job hung");
            return FakeProcessRunner.Ok("""{"result":"waiting"}""");
        };

        await Post(host, room, "/build-thing @sonnet begin");
        await runner.NextSpecAsync(Wait);
        await WaitForRun(host, room, r => r.WaitingSince is not null);
        clock.Advance(TimeSpan.FromMinutes(13));   // past 10 min plus the 2 min grace

        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(await runner.NextSpecAsync(Wait)));
        await WaitForNoteContaining(host, room, "Job hung-1 passed its time limit with no finish report; the hub stopped it.");
        await WaitForCount(() => Volatile.Read(ref kills), 1);
        Assert.Equal((int)JobStopReason.TimedOut, Volatile.Read(ref reason));

        // The job's own late report was already noted by the hub's stop: it posts nothing more.
        var acknowledged = new TaskCompletionSource();
        spawner.FinishJob(room, "hung-1", "Job hung-1 exited late.", () => acknowledged.TrySetResult());
        await acknowledged.Task.WaitAsync(Wait);
        Assert.DoesNotContain("Job hung-1 exited late.", await host.Client.GetStringAsync($"api/rooms/{room}/messages?afterId=0&limit=200"));
    }

    [Fact]
    public async Task A_job_in_a_room_with_no_run_is_stopped_with_a_note()
    {
        var (host, _, room) = await StartRunHostAsync(Instant, RunLimits.Default);
        await using var _h = host;
        var kills = 0;
        host.Services.GetRequiredService<SpawnerService>().TrackJob(room, "orphan-1", TimeSpan.FromHours(1), _ => Interlocked.Increment(ref kills));

        await WaitForNoteContaining(host, room, "Job orphan-1 was stopped: this room has no active or parked run to wait for it.");
        await WaitForCount(() => Volatile.Read(ref kills), 1);
    }

    /// <summary>The wall clock passes while the conductor is still running: the run waits for that
    /// turn, and its ping presents.</summary>
    [Fact]
    public async Task A_conductor_running_past_the_wall_clock_still_presents_with_a_ping()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limits = new RunLimits(SpawnsPerHour: 100, Hour: TimeSpan.FromMinutes(10), DefaultHours: 1, SpawnTimeout: TimeSpan.FromHours(1), PhaseEntries: 100);
        var (host, runner, room) = await StartRunHostAsync(Instant, limits, clock);
        await using var _ = host;
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Handler = async (_, _, ct) =>
        {
            await finish.Task.WaitAsync(ct);
            await PostAsInHost(host, "sonnet", room, "phase: ping here is what got done");
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };

        await Post(host, room, "/build-thing @sonnet begin");
        await runner.NextSpecAsync(Wait);
        clock.Advance(TimeSpan.FromMinutes(15));   // past the 10-minute budget, conductor still running
        // A steer past the budget is checked against the caps too, and its note is a loop round trip.
        await Post(host, room, "also cover the edge cases");
        await WaitForNoteContaining(host, room, "Steer noted");
        Assert.Equal(RunStatus.Active, host.Services.GetRequiredService<RunStore>().Latest(room)!.Status);

        finish.SetResult();
        Assert.Contains("the conductor pinged", (await WaitForNoteContaining(host, room, "ended")).Body);
        Assert.DoesNotContain(await AllMessagesFrom(host, room), m => m.Body.Contains("parked"));
    }

    /// <summary>A workers exchange with queued turns launches nothing past the spawn budget.</summary>
    [Fact]
    public async Task Queued_worker_turns_stop_launching_once_the_spawn_budget_is_used()
    {
        var limits = new RunLimits(SpawnsPerHour: 3, Hour: TimeSpan.FromHours(1), DefaultHours: 1, SpawnTimeout: TimeSpan.FromMinutes(30), PhaseEntries: 100);
        var (host, runner, room) = await StartRunHostAsync(Instant, limits);
        await using var _ = host;
        runner.Handler = async (spec, _, _) =>
        {
            switch (FakeProcessRunner.ParticipantOf(spec))
            {
                case "sonnet": await PostAsInHost(host, "sonnet", room, "phase: build @opus @gpt-6-astra build it"); break;
                case "opus": await PostAsInHost(host, "opus", room, "@gpt-6-astra your turn"); break;
                case "gpt-6-astra": await PostAsInHost(host, "gpt-6-astra", room, "@opus your turn"); break;
            }
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };

        await Post(host, room, "/build-thing @sonnet begin");
        var parked = await WaitForRun(host, room, r => r.Status == RunStatus.Parked);
        Assert.True(parked.CapSpent);
        Assert.Equal(3, parked.SpawnsUsed);   // a launch past the budget would have counted a fourth spawn
    }

    /// <summary>A hub that dies while a run waits on a job: the restart park folds the open wait, so
    /// the wait (and the downtime inside it) stays out of the run's active time.</summary>
    [Fact]
    public async Task A_restart_parks_a_waiting_run_and_keeps_its_wait_out_of_the_active_time()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_restart_" + Guid.NewGuid().ToString("N"));
        try
        {
            TemplateDatabase.SeedInto(dir);
            var db = new ChopDb(Path.Combine(dir, "chopitup.db"));
            db.EnsureDatabase();
            var now = DateTimeOffset.UtcNow;
            var seeded = new RunStore(db).Start("general", "opus", "build-thing", "", 1, 4, 40, now.AddHours(-3));
            new RunStore(db).StartWait(seeded.Id, now.AddHours(-2));

            await using var host = await HubTestHost.StartAsync(dir, processRunner: new FakeProcessRunner());

            var run = host.Services.GetRequiredService<RunStore>().ById(seeded.Id)!;
            Assert.Equal((RunStatus.Parked, (DateTimeOffset?)null), (run.Status, run.WaitingSince));
            Assert.InRange(run.WaitedSeconds, 7190, 7300);
            Assert.InRange(RunStore.ActiveElapsed(run, DateTimeOffset.UtcNow), TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(62));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task A_continue_with_hours_in_a_room_with_no_parked_run_is_refused_and_continues_nothing()
    {
        var (host, runner, room) = await StartRunHostAsync(Instant, RunLimits.Default);
        await using var _ = host;
        runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"result":"working"}"""));
        await Post(host, room, "@sonnet say hi");
        await runner.NextSpecAsync(Wait);
        await WaitForNoteContaining(host, room, "@sonnet");   // the plain exchange concluded

        await Post(host, room, "/continue hours: 3");

        await WaitForNoteContaining(host, room, "No run is parked in this room; hours: applies to a parked run, so nothing was continued.");
        Assert.True(await runner.NoSpecWithin(TimeSpan.FromMilliseconds(500)));
    }

    private static async Task<long> NoteId(HubTestHost host, string room, string body)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            using var doc = JsonDocument.Parse(await host.Client.GetStringAsync($"api/rooms/{room}/messages?afterId=0&limit=200"));
            foreach (var m in doc.RootElement.GetProperty("messages").EnumerateArray())
                if (m.GetProperty("body").GetString() == body) return m.GetProperty("id").GetInt64();
            await Task.Delay(50);
        }
        throw new TimeoutException($"no message '{body}' in '{room}'");
    }

    /// <summary>A job ends while a worker spends the last spawn: the run parks, and the job's note
    /// still reaches the conductor that the extension opens.</summary>
    [Fact]
    public async Task A_job_note_that_parks_a_spent_run_rides_the_extension()
    {
        var limits = new RunLimits(SpawnsPerHour: 2, Hour: TimeSpan.FromHours(1), DefaultHours: 1, SpawnTimeout: TimeSpan.FromMinutes(30), PhaseEntries: 100);
        var (host, runner, room) = await StartRunHostAsync(Instant, limits);
        await using var _ = host;
        var spawner = host.Services.GetRequiredService<SpawnerService>();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sonnetLaunches = 0;
        runner.Handler = async (spec, _, ct) =>
        {
            switch (FakeProcessRunner.ParticipantOf(spec))
            {
                case "sonnet" when Interlocked.Increment(ref sonnetLaunches) == 1:
                    spawner.TrackJob(room, "train-1", TimeSpan.FromHours(2), _ => { });
                    await PostAsInHost(host, "sonnet", room, "phase: build @opus write the thing");
                    break;
                case "opus":
                    try { await hold.Task.WaitAsync(ct); } catch (OperationCanceledException) { }
                    break;
            }
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };

        await Post(host, room, "/build-thing @sonnet begin");
        await runner.NextSpecAsync(Wait);   // sonnet
        await runner.NextSpecAsync(Wait);   // opus, the second and last budgeted spawn, held running
        Assert.Equal(RunStatus.Active, host.Services.GetRequiredService<RunStore>().Latest(room)!.Status);
        spawner.FinishJob(room, "train-1", "Job train-1 exited with code 0.");
        var noteId = await NoteId(host, room, "Job train-1 exited with code 0.");
        await WaitForRun(host, room, r => r.Status == RunStatus.Parked);

        await Post(host, room, "/continue hours: 1");

        var conductor = await runner.NextSpecAsync(Wait);
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(conductor));
        Assert.Contains($"#{noteId}", WhyHere(conductor.StandardInput));
        hold.TrySetResult();
    }

    /// <summary>A job ends while the conductor runs, and the conductor then exits without a post:
    /// the re-ask carries the job's note.</summary>
    [Fact]
    public async Task A_job_note_that_lands_while_the_conductor_runs_rides_its_silent_re_ask()
    {
        var (host, runner, room) = await StartRunHostAsync(Instant, RunLimits.Default);
        await using var _ = host;
        var spawner = host.Services.GetRequiredService<SpawnerService>();
        var launches = 0;
        runner.Handler = async (spec, _, _) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet" && Interlocked.Increment(ref launches) == 1)
            {
                spawner.TrackJob(room, "render-2", TimeSpan.FromHours(2), _ => { });
                spawner.FinishJob(room, "render-2", "Job render-2 exited with code 0.");
                await WaitForNoteContaining(host, room, "Job render-2 exited with code 0.");
            }
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };

        await Post(host, room, "/build-thing @sonnet begin");
        await runner.NextSpecAsync(Wait);
        var noteId = await NoteId(host, room, "Job render-2 exited with code 0.");

        var reAsk = await runner.NextSpecAsync(Wait);
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(reAsk));
        Assert.Contains($"#{noteId}", WhyHere(reAsk.StandardInput));
    }

    /// <summary>The conductor hands work to a worker and is still finishing when the wall clock
    /// passes: the worker never launches, and the run parks on its active time.</summary>
    [Fact]
    public async Task A_worker_due_after_the_wall_clock_passed_is_held_and_the_run_parks()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limits = new RunLimits(SpawnsPerHour: 100, Hour: TimeSpan.FromMinutes(10), DefaultHours: 1, SpawnTimeout: TimeSpan.FromHours(1), PhaseEntries: 100);
        var (host, runner, room) = await StartRunHostAsync(Instant, limits, clock);
        await using var _ = host;
        var runs = host.Services.GetRequiredService<RunStore>();
        runner.Handler = async (spec, _, _) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet")
            {
                await PostAsInHost(host, "sonnet", room, "phase: build @opus write the thing");
                await WaitForRun(host, room, r => r.Exchanges == 2);   // the workers exchange is open
                clock.Advance(TimeSpan.FromMinutes(15));               // past the 10-minute budget
            }
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };

        await Post(host, room, "/build-thing @sonnet begin");
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(await runner.NextSpecAsync(Wait)));

        var parked = await WaitForRun(host, room, r => r.Status == RunStatus.Parked);
        Assert.True(parked.CapSpent);
        Assert.Equal(1, parked.SpawnsUsed);   // opus never launched
    }

    /// <summary>A finish report for a job the ended run killed arrives after the next run started:
    /// the next run still tracks a job with the same id.</summary>
    [Fact]
    public async Task A_late_finish_from_an_ended_runs_job_does_not_stop_the_next_run_tracking_that_id()
    {
        var (host, runner, room) = await StartRunHostAsync(Instant, RunLimits.Default);
        await using var _ = host;
        var spawner = host.Services.GetRequiredService<SpawnerService>();
        var launches = 0;
        runner.Handler = async (spec, _, _) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) != "sonnet") return FakeProcessRunner.Ok("""{"result":"working"}""");
            switch (Interlocked.Increment(ref launches))
            {
                case 1:
                    spawner.TrackJob(room, "render-3", TimeSpan.FromHours(2), _ => { });
                    break;
                case 2:
                    spawner.FinishJob(room, "render-3", "Job render-3 exited with code 0.");
                    await WaitForNoteContaining(host, room, "Job render-3 exited with code 0.");
                    spawner.TrackJob(room, "render-3", TimeSpan.FromHours(2), _ => { });
                    break;
            }
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };

        await Post(host, room, "/build-thing @sonnet begin");
        await runner.NextSpecAsync(Wait);
        var first = await WaitForRun(host, room, r => r.WaitingSince is not null);
        await Post(host, room, "/stop");
        await WaitForRun(host, room, r => r.Status == RunStatus.Ended);

        await Post(host, room, "/build-thing @sonnet again");
        await runner.NextSpecAsync(Wait);
        await WaitForRun(host, room, r => r.Id != first.Id && r.WaitingSince is not null);
    }
}
