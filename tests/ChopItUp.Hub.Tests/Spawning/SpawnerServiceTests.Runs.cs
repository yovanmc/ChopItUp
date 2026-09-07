using System.Net.Http.Json;
using System.Text.Json;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Git;
using ChopItUp.Hub.Memory;
using ChopItUp.Hub.Skills;
using ChopItUp.Hub.Spawning;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests.Spawning;

/// <summary>Row 19, task 4: starting a run, and every refusal at the start (ticket 04). End-to-end
/// through the real HTTP + MCP surface, same fixture shape as the Skill_04 tests above.</summary>
public sealed partial class SpawnerServiceTests
{
    private const string RunSkillMd = "---\nname: build-thing\nrun: true\n---\n\n# Build Thing\n\nBuild the thing.\n";

    private RunStore Runs => _host.Services.GetRequiredService<RunStore>();

    [Fact]
    public async Task Run04_A1_a_valid_invocation_in_a_directory_room_starts_a_run_and_asks_the_conductor()
    {
        WriteSkill("build-thing", RunSkillMd);
        await MakeRoom("lab-run-start");
        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"result":"working"}"""));

        await PostAsOwnerIn("lab-run-start", "/build-thing @sonnet begin");
        var spec = await _runner.NextSpecAsync(Wait);
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(spec));

        var note = await WaitForMessageIn("lab-run-start", m => m.Author == ChopDb.HubParticipantId && m.Body.StartsWith("Run #"));
        Assert.StartsWith("Run #", note.Body);
        Assert.Contains(" started", note.Body);
        Assert.Contains("@sonnet conducts", note.Body);

        var run = Runs.Active("lab-run-start");
        Assert.NotNull(run);
        Assert.Equal("sonnet", run!.ConductorId);
        Assert.Equal("build-thing", run.SkillName);
        Assert.Equal(RunStatus.Active, run.Status);
        Assert.Equal(1, run.Exchanges);
    }

    [Fact]
    public async Task Run04_A2_a_room_with_no_directory_refuses_and_starts_nothing()
    {
        WriteSkill("build-thing", RunSkillMd);
        await PostAsOwner("/build-thing @sonnet begin");   // "general" has no directory
        var note = await WaitForMessage(m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("needs a room bound to a directory"));
        Assert.Equal("/build-thing starts a run, which needs a room bound to a directory; this room has none.", note.Body);
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(500)));
        Assert.Null(Runs.Active("general"));
    }

    [Fact]
    public async Task Run04_A2_zero_or_many_conductors_refuses_naming_the_count()
    {
        WriteSkill("build-thing", RunSkillMd);
        await MakeRoom("lab-run-count");

        await PostAsOwnerIn("lab-run-count", "/build-thing begin, no one mentioned");
        var zero = await WaitForMessageIn("lab-run-count", m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("none was"));
        Assert.Equal("/build-thing starts a run and needs exactly one conductor mentioned; none was.", zero.Body);

        await PostAsOwnerIn("lab-run-count", "/build-thing @opus @sonnet both of you");
        var many = await WaitForMessageIn("lab-run-count", m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("were:"));
        Assert.Equal("/build-thing starts a run and needs exactly one conductor mentioned; 2 were: @opus, @sonnet.", many.Body);

        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(500)));
        Assert.Null(Runs.Active("lab-run-count"));
    }

    [Fact]
    public async Task Run04_A2_a_second_run_start_invocation_while_one_is_active_refuses_without_disturbing_it()
    {
        WriteSkill("build-thing", RunSkillMd);
        await MakeRoom("lab-run-twice");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, ct) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet") await release.Task.WaitAsync(ct);
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostAsOwnerIn("lab-run-twice", "/build-thing @sonnet begin");
        await _runner.NextSpecAsync(Wait);
        var firstRun = Runs.Active("lab-run-twice");
        Assert.NotNull(firstRun);

        await PostAsOwnerIn("lab-run-twice", "/build-thing @opus begin again");
        var note = await WaitForMessageIn("lab-run-twice", m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("already active"));
        Assert.Equal($"A run is already active in this room (#{firstRun!.Id}); /stop it first.", note.Body);

        var stillActive = Runs.Active("lab-run-twice");
        Assert.Equal(firstRun.Id, stillActive!.Id);
        Assert.Equal("sonnet", stillActive.ConductorId);
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(500)));   // opus never spawned
        release.SetResult();
    }

    [Fact]
    public async Task Run04_AC5_a_human_message_mentioning_a_participant_during_a_run_creates_no_work()
    {
        WriteSkill("build-thing", RunSkillMd);
        await MakeRoom("lab-run-steer");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, ct) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet") await release.Task.WaitAsync(ct);
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostAsOwnerIn("lab-run-steer", "/build-thing @sonnet begin");
        await _runner.NextSpecAsync(Wait);

        await PostAsOwnerIn("lab-run-steer", "@opus actually you take it");
        await Task.Delay(300);
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(300)));   // opus never spawned
        var run = Runs.Active("lab-run-steer");
        Assert.NotNull(run);
        Assert.Equal("sonnet", run!.ConductorId);                                 // untouched
        release.SetResult();
    }

    [Fact]
    public async Task Run04_owner_remote_starts_a_run_exactly_as_the_owner_does()
    {
        WriteSkill("build-thing", RunSkillMd);
        await MakeRoom("lab-run-remote");
        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"result":"working"}"""));

        await PostAsIn(ChopDb.OwnerRemoteParticipantId, "lab-run-remote", "/build-thing @sonnet begin");
        var spec = await _runner.NextSpecAsync(Wait);
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(spec));
        Assert.NotNull(Runs.Active("lab-run-remote"));
    }

    // --- Task 5 (row 19): the loop, skill continuity, and artifact authorship (ticket 05) ----------

    [Fact]
    public async Task Run05_the_conductor_is_re_spawned_when_its_exchange_concludes_carrying_the_skill_again()
    {
        WriteSkill("build-thing", RunSkillMd);
        await MakeRoom("lab-run-respawn");
        var holdSecondTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _runner.Handler = async (spec, _, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 2) await holdSecondTurn.Task.WaitAsync(ct);
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };

        await PostAsOwnerIn("lab-run-respawn", "/build-thing @sonnet begin");
        var first = await _runner.NextSpecAsync(Wait);
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(first));
        Assert.Contains("Build the thing.", first.StandardInput);

        // No owner post involved (AC3): the conductor's own exchange concluding is what re-spawns it.
        var second = await _runner.NextSpecAsync(Wait);
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(second));
        Assert.Contains("Build the thing.", second.StandardInput);   // the skill fence, again (ticket 05)

        var run = Runs.Active("lab-run-respawn");
        Assert.NotNull(run);
        Assert.Equal(2, run!.Exchanges);
        holdSecondTurn.SetResult();
    }

    [Fact]
    public async Task Run05_a_skill_tampered_after_the_first_turn_parks_the_run_rather_than_continuing_blind()
    {
        WriteSkill("build-thing", RunSkillMd);
        await MakeRoom("lab-run-tamper");
        var releaseTurn1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, ct) =>
        {
            await releaseTurn1.Task.WaitAsync(ct);
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };

        await PostAsOwnerIn("lab-run-tamper", "/build-thing @sonnet begin");
        await _runner.NextSpecAsync(Wait);
        File.WriteAllText(Path.Combine(_dir, "skills", "build-thing", "SKILL.md"), "mutated\n");   // hash now stale
        releaseTurn1.SetResult();   // turn 1 concludes -> re-spawn attempt reads the tampered skill

        var note = await WaitForMessageIn("lab-run-tamper", m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("parked"));
        Assert.Contains("does not match what was imported", note.Body);
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(500)));   // no second spawn

        var run = Runs.Latest("lab-run-tamper");
        Assert.Equal(RunStatus.Parked, run!.Status);
        Assert.False(run.CapSpent);
    }

    [Fact]
    public async Task Run05_artifact_authorship_is_recorded_from_the_conductors_whole_diff_including_self_committed_work()
    {
        WriteSkill("build-thing", RunSkillMd);
        var dir = await MakeRoom("lab-run-artifacts");
        // A seed commit so headBefore is non-null - the range-diff path (P4's main case). The
        // no-prior-commit fallback (ChangedFilesInAsync) is unit-tested directly on GitTrail.
        File.WriteAllText(Path.Combine(dir, "seed.txt"), "seed\n");
        var holdFurtherTurns = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _runner.Handler = async (spec, _, ct) =>
        {
            if (Interlocked.Increment(ref calls) > 1) { await holdFurtherTurns.Task.WaitAsync(ct); return FakeProcessRunner.Ok("""{"result":"done"}"""); }
            // The model commits some of its own work mid-spawn (the common case for Codex, and what
            // a rogue Bash call does for Claude) - P4's scenario: authorship must still land on it.
            File.WriteAllText(Path.Combine(spec.WorkingDirectory, "rogue.txt"), "committed by the model itself\n");
            await new GitTrail(spec.WorkingDirectory).CommitAllAsync("rogue commit", new GitIdentity("Rogue", "rogue@example.test"), allowEmpty: false);
            File.WriteAllText(Path.Combine(spec.WorkingDirectory, "hub-written.txt"), "left for the hub's own after-commit\n");
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };

        await PostAsOwnerIn("lab-run-artifacts", "/build-thing @sonnet begin");
        await _runner.NextSpecAsync(Wait);
        await WaitForMessageIn("lab-run-artifacts", m => m.Author == ChopDb.HubParticipantId && m.Body.StartsWith(HubNotes.TrailPrefix));

        var run = Runs.Active("lab-run-artifacts");
        Assert.NotNull(run);
        var artifacts = Runs.Artifacts(run!.Id).Select(a => (a.Path, a.AuthorId)).OrderBy(x => x.Path, StringComparer.Ordinal).ToList();
        Assert.Equal([("hub-written.txt", "sonnet"), ("rogue.txt", "sonnet")], artifacts);
        holdFurtherTurns.SetResult();
    }

    // --- Task 6 (row 19): steer (ticket 06) ---------------------------------------------------------

    private async Task<long> MessageIdIn(string room, string body)
    {
        using var doc = JsonDocument.Parse(await _host.Client.GetStringAsync($"api/rooms/{room}/messages?afterId=0&limit=200"));
        return doc.RootElement.GetProperty("messages").EnumerateArray()
            .First(m => m.GetProperty("body").GetString() == body).GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task Run06_an_owner_post_during_a_run_is_noted_as_a_steer_leaves_the_queue_intact_and_drains_into_the_next_trigger_set()
    {
        WriteSkill("build-thing", RunSkillMd);
        await MakeRoom("lab-run-steer2");
        var holdTurn1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _runner.Handler = async (spec, _, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1) await holdTurn1.Task.WaitAsync(ct);
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };

        await PostAsOwnerIn("lab-run-steer2", "/build-thing @sonnet begin");
        await _runner.NextSpecAsync(Wait);   // turn 1 launched, held in flight

        await PostAsOwnerIn("lab-run-steer2", "@opus actually reconsider this");
        var noted = await WaitForMessageIn("lab-run-steer2", m => m.Author == ChopDb.HubParticipantId && m.Body.StartsWith("Steer noted"));
        Assert.Equal("Steer noted; @sonnet is given it when the current exchange concludes.", noted.Body);
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(500)));   // opus never spawned; nothing woken while turn 1 runs
        var stillActive = Runs.Active("lab-run-steer2");
        Assert.Equal("sonnet", stillActive!.ConductorId);                          // the run's queue (its conductor) is untouched

        var steerId = await MessageIdIn("lab-run-steer2", "@opus actually reconsider this");
        holdTurn1.SetResult();   // turn 1 concludes -> the conductor is re-spawned, draining the steer

        var second = await _runner.NextSpecAsync(Wait);
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(second));
        Assert.Contains($"#{steerId}", second.StandardInput);   // the steer is among its triggers

        Assert.Equal(2, Runs.Active("lab-run-steer2")!.Exchanges);
    }

    // --- Task 8 (row 19): the phase tag, the D8 class rules, and the refusal counter ----------------

    [Fact]
    public async Task Run08_a_valid_conductor_post_opens_exactly_the_work_it_asks_for()
    {
        WriteSkill("build-thing", RunSkillMd);
        await MakeRoom("lab-run-build");
        _runner.Handler = async (spec, _, _) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet") await PostAsIn("sonnet", "lab-run-build", "phase: build @opus write the thing");
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };

        await PostAsOwnerIn("lab-run-build", "/build-thing @sonnet begin");
        await _runner.NextSpecAsync(Wait);                          // sonnet (conductor)
        var worker = await _runner.NextSpecAsync(Wait);              // opus, from the rooted worker exchange
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(worker));

        var run = Runs.Active("lab-run-build");
        Assert.NotNull(run);
        Assert.Equal("build", run!.Phase);
        Assert.Equal(1, Runs.PhaseEntries(run.Id)["build"]);
        Assert.Equal(2, run.Exchanges);   // 1: the conductor's own; 2: the rooted worker exchange
    }

    [Fact]
    public async Task Run08_a_refused_post_then_a_valid_post_in_one_turn_opens_exactly_one_worker_exchange()
    {
        WriteSkill("build-thing", RunSkillMd);
        await MakeRoom("lab-run-selfcorrect");
        _runner.Handler = async (spec, _, _) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet")
            {
                await PostAsIn("sonnet", "lab-run-selfcorrect", "oops, forgot the phase tag");
                await PostAsIn("sonnet", "lab-run-selfcorrect", "phase: build @opus now for real");
            }
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };

        await PostAsOwnerIn("lab-run-selfcorrect", "/build-thing @sonnet begin");
        await _runner.NextSpecAsync(Wait);                          // sonnet
        var worker = await _runner.NextSpecAsync(Wait);              // opus - the ONE piece of work
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(worker));

        // Once opus's (silent, un-handled) turn ends, the run's own loop naturally re-spawns the
        // conductor (task 5/AC3) - that is not a SECOND piece of work from this post, so it is not
        // asserted against here. What pins "exactly one piece of work" is the phase-entry and
        // exchange counts: the refused first post never counted or rooted anything.
        var run = Runs.Active("lab-run-selfcorrect");
        Assert.NotNull(run);   // not parked - the first bad post asked again, it was not a SECOND failure
        Assert.Equal(RunStatus.Active, run!.Status);
        Assert.Equal(1, Runs.PhaseEntries(run.Id)["build"]);   // exactly one phase entry - the bad post never counted
        Assert.Equal(2, run.Exchanges);                        // 1: the conductor's own; 2: the ONE rooted worker exchange
    }

    [Fact]
    public async Task Run08_only_the_conductors_post_is_ever_checked_against_d8_a_workers_post_is_ordinary_chat()
    {
        WriteSkill("build-thing", RunSkillMd);
        await MakeRoom("lab-run-worker-chat");
        _runner.Handler = async (spec, _, _) =>
        {
            switch (FakeProcessRunner.ParticipantOf(spec))
            {
                case "sonnet": await PostAsIn("sonnet", "lab-run-worker-chat", "phase: build @opus write the thing"); break;
                case "opus": await PostAsIn("opus", "lab-run-worker-chat", "phase: build @gpt-6-astra also help"); break;   // opus is NOT the conductor
            }
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };

        await PostAsOwnerIn("lab-run-worker-chat", "/build-thing @sonnet begin");
        await _runner.NextSpecAsync(Wait);   // sonnet
        await _runner.NextSpecAsync(Wait);   // opus

        var run = Runs.Active("lab-run-worker-chat");
        Assert.NotNull(run);
        Assert.Equal(1, Runs.PhaseEntries(run!.Id)["build"]);   // opus's own "phase: build" line never counted a second entry
        Assert.Equal(2, run.Exchanges);                         // opus's post extended its OWN exchange; it opened nothing new
    }

    [Fact]
    public async Task Run08_F1_the_conductors_own_exchange_concluding_after_it_rooted_a_worker_exchange_leaves_the_worker_exchange_alone()
    {
        WriteSkill("build-thing", RunSkillMd);
        await MakeRoom("lab-run-f1");
        var releaseOpus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, ct) =>
        {
            switch (FakeProcessRunner.ParticipantOf(spec))
            {
                case "sonnet": await PostAsIn("sonnet", "lab-run-f1", "phase: build @opus write the thing"); break;
                case "opus": await releaseOpus.Task.WaitAsync(ct); break;
            }
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };

        await PostAsOwnerIn("lab-run-f1", "/build-thing @sonnet begin");
        await _runner.NextSpecAsync(Wait);                            // sonnet: posts and its process exits
        var opusSpec = await _runner.NextSpecAsync(Wait);              // opus: the rooted worker exchange, held in flight
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(opusSpec));

        // sonnet's OWN exchange concludes once its process exits - the guard (pass 2's F-1) must not
        // let that conclusion touch the newer, still-open worker exchange that superseded it in _rooms.
        // sonnet's own exchange is the run's FIRST one (from the owner's run-start post, through the
        // ordinary owner-exchange path with Budget = SpawnLimits.Budget) - only a RE-spawn uses
        // OpenForConductor's Budget-of-1 shape (task 5a) - so its conclusion note reads "1 of 4", not
        // "1 of 1".
        await WaitForMessageIn("lab-run-f1", m => m.Author == ChopDb.HubParticipantId && m.Body == "Exchange concluded: 1 of 4 turns used.");
        await Task.Delay(300);
        var snap = Spawner.Snapshot("lab-run-f1");
        Assert.Equal("open", snap.Status);              // the WORKER exchange, still open - not concluded, not replaced
        Assert.Contains("opus", snap.InFlight);
        Assert.Equal(1, snap.TurnsUsed);                // opus - not a second respawn of sonnet

        // Both conclusions read identically ("1 of 4"), so a second WaitForMessageIn on the same text
        // would just re-match sonnet's already-posted one - wait for the COUNT to reach two instead.
        releaseOpus.SetResult();
        var deadline = DateTime.UtcNow + Wait;
        int concludedCount;
        do
        {
            concludedCount = (await MessagesIn("lab-run-f1")).Count(m => m.Author == ChopDb.HubParticipantId && m.Body.StartsWith("Exchange concluded"));
            if (concludedCount >= 2) break;
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);
        Assert.Equal(2, concludedCount);   // sonnet's own exchange, then the worker exchange - both concluded, neither dropped
    }

    [Fact]
    public async Task Run08_F2_a_second_refused_post_in_the_same_phase_parks_the_run_and_pings_the_owner()
    {
        WriteSkill("build-thing", RunSkillMd);
        await MakeRoom("lab-run-refusal-park");
        _runner.Handler = async (spec, _, _) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet")
            {
                await PostAsIn("sonnet", "lab-run-refusal-park", "no phase tag at all, first bad post");
                await PostAsIn("sonnet", "lab-run-refusal-park", "still no phase tag, second bad post");
            }
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };

        await PostAsOwnerIn("lab-run-refusal-park", "/build-thing @sonnet begin");
        await _runner.NextSpecAsync(Wait);

        await WaitForMessageIn("lab-run-refusal-park", m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("phase tag"));
        var parked = await WaitForMessageIn("lab-run-refusal-park", m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("parked"));
        Assert.Contains("@owner", parked.Body);

        var run = Runs.Latest("lab-run-refusal-park");
        Assert.Equal(RunStatus.Parked, run!.Status);
        Assert.False(run.CapSpent);   // a refusal park is soft (D8's own second-refusal rule, not a hard cap)
    }

    /// <summary>Deferred from tasks 5/6 (pass 2's F-1), reachable end to end only once task 8 gives the
    /// conductor a way to root a SECOND exchange while its own is still in flight (AC5): a steer
    /// posted while a run is genuinely idle - nothing open, nothing in flight IN MEMORY, even though
    /// the run row is still 'active' in the database - wakes the conductor through table row 18's Tick
    /// arm. Before task 9's AC12 exists, the one legitimate way to reach that state is a restart: a
    /// fresh SpawnerService against the SAME data starts with empty _rooms/_inFlight while the run
    /// persists as active.</summary>
    [Fact]
    public async Task Run08_AC5_a_steer_into_a_run_idle_in_memory_but_active_in_the_db_wakes_the_conductor_via_tick()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_spawner_idle_" + Guid.NewGuid().ToString("N"));
        var roomsRoot = dir + "_rooms";
        var runnerA = new FakeProcessRunner();
        long rootMessageId;
        await using (var hostA = await HubTestHost.StartAsync(dir, deleteOnDispose: false, processRunner: runnerA, limits: Fast, roomsRoot: roomsRoot))
        {
            var roomDir = Path.Combine(roomsRoot, "lab");
            Assert.True(await new GitTrail(roomDir).InitAsync());
            hostA.Services.GetRequiredService<MessageStore>().CreateRoom("lab", "LAB", roomDir);

            var skillDir = Path.Combine(dir, "skills", "build-thing");
            Directory.CreateDirectory(skillDir);
            var bytes = new System.Text.UTF8Encoding(false).GetBytes(RunSkillMd);
            File.WriteAllBytes(Path.Combine(skillDir, "SKILL.md"), bytes);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            new SkillHashes(hostA.Services.GetRequiredService<ChopDb>()).Record("build-thing", hash, "test-fixture");

            runnerA.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"result":"working"}"""));
            var start = await hostA.Client.PostAsJsonAsync("api/rooms/lab/messages", new { body = "/build-thing @sonnet begin" });
            Assert.Equal(System.Net.HttpStatusCode.Created, start.StatusCode);
            await runnerA.NextSpecAsync(Wait);   // sonnet launched

            var runOnA = hostA.Services.GetRequiredService<RunStore>().Active("lab");
            Assert.NotNull(runOnA);
            rootMessageId = runOnA!.RootMessageId;

            await Task.Delay(300);   // let sonnet's silent turn conclude and the loop re-open it at least once
        }   // hostA disposed WITHOUT deleting the directory: its in-memory _rooms/_inFlight are gone,
            // but the 'runs' row it wrote is still on disk.

        var runnerB = new FakeProcessRunner();
        await using var hostB = await HubTestHost.StartAsync(dir, deleteOnDispose: true, processRunner: runnerB, limits: Fast, roomsRoot: roomsRoot);
        var runOnB = hostB.Services.GetRequiredService<RunStore>().Active("lab");
        Assert.NotNull(runOnB);   // still active - AC12's restart-park is task 9's job, not yet wired
        Assert.Equal(rootMessageId, runOnB!.RootMessageId);

        runnerB.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"result":"working"}"""));
        var steer = await hostB.Client.PostAsJsonAsync("api/rooms/lab/messages", new { body = "@opus reconsider this" });
        Assert.Equal(System.Net.HttpStatusCode.Created, steer.StatusCode);

        var spawned = await runnerB.NextSpecAsync(Wait);   // row 18: Tick -> OpenConductor, woken by the steer alone
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(spawned));
    }

    [Fact]
    public async Task Run06_outside_a_run_an_owner_post_still_supersedes()
    {
        // The regression F-23 asks to keep: task 4's step 3 already returns for a run; this room
        // never has one, so the pre-row-19 supersede path is exactly what runs.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, ct) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "opus") await release.Task.WaitAsync(ct);
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };
        await PostAsOwner("@opus think slowly");
        await _runner.NextSpecAsync(Wait);
        await PostAsOwner("never mind");
        await Task.Delay(300);
        Assert.Equal("superseded", Spawner.Snapshot("general").Status);
        release.SetResult();
    }
}
