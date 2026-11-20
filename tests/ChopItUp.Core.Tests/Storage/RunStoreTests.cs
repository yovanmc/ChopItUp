using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Core.Tests.Storage;

/// <summary>The <c>runs</c> table and its three satellites. "general" and "opus" come from the seed
/// roster/room every fresh database already carries (<see cref="ChopDb"/>'s V1 seed room and V3+
/// roster). This file relies on that rather than inserting its own, which is what makes the
/// foreign-key checks meaningful: a bad room or participant id must fail as an FK violation, never
/// be mislabelled "already active".</summary>
public sealed class RunStoreTests : IDisposable
{
    private const string Room = "general";
    private const string Conductor = "opus";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_runs_" + Guid.NewGuid().ToString("N"));
    private readonly ChopDb _db;
    private readonly RunStore _store;
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-09-07T10:00:00Z");

    public RunStoreTests()
    {
        TemplateDatabase.SeedInto(_dir);
        _db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        _db.EnsureDatabase();
        _store = new RunStore(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Start_round_trips_every_field_and_ById_Active_Latest_agree()
    {
        var run = _store.Start(Room, Conductor, "roadmap", "--phase build", rootMessageId: 42, 4, 40, T0);

        Assert.Equal(1L, run.Id);
        Assert.Equal(Room, run.RoomId);
        Assert.Equal(Conductor, run.ConductorId);
        Assert.Equal("roadmap", run.SkillName);
        Assert.Equal("--phase build", run.Arguments);
        Assert.Equal(RunStatus.Active, run.Status);
        Assert.Null(run.Reason);
        Assert.False(run.CapSpent);
        Assert.Equal("(start)", run.Phase);
        Assert.Equal(42L, run.RootMessageId);
        Assert.Equal(T0, run.StartedAt);
        Assert.Null(run.ParkedAt);
        Assert.Equal(0L, run.ParkedSeconds);
        Assert.Null(run.EndedAt);
        Assert.Equal(0, run.SpawnsUsed);
        Assert.Equal(0, run.Exchanges);
        Assert.Equal((4, 40, (DateTimeOffset?)null, 0L), (run.BudgetHours, run.SpawnBudget, run.WaitingSince, run.WaitedSeconds));

        Assert.Equal(run, _store.ById(run.Id));
        Assert.Equal(run, _store.Active(Room));
        Assert.Equal(run, _store.Latest(Room));
        Assert.Equal([run], _store.ListActive());
    }

    [Fact]
    public void A_second_Start_in_the_same_room_is_refused_as_already_active_not_a_bare_constraint_error()
    {
        _store.Start(Room, Conductor, "roadmap", "", 1, 4, 40, T0);

        var ex = Assert.Throws<InvalidOperationException>(() => _store.Start(Room, Conductor, "roadmap", "", 2, 4, 40, T0));
        Assert.Contains("already", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Still exactly the first run - the failed insert left nothing behind.
        Assert.Single(_store.ListActive());
    }

    [Fact]
    public void A_foreign_key_violation_is_never_reported_as_already_active()
    {
        // Neither "no-such-room" nor "no-such-participant" exists - PRAGMA foreign_keys=ON must reject
        // this as an FK violation, and Start's narrow catch (SqliteExtendedErrorCode == 2067) must let
        // it through rather than relabelling it.
        var ex = Assert.ThrowsAny<SqliteException>(() => _store.Start("no-such-room", Conductor, "roadmap", "", 1, 4, 40, T0));
        Assert.DoesNotContain("already", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Park_and_End_both_clear_Active()
    {
        var run = _store.Start(Room, Conductor, "roadmap", "", 1, 4, 40, T0);
        _store.Park(run.Id, "the conductor did not post", capSpent: false, T0.AddMinutes(5));
        Assert.Null(_store.Active(Room));
        var parked = _store.ById(run.Id)!;
        Assert.Equal(RunStatus.Parked, parked.Status);
        Assert.Equal("the conductor did not post", parked.Reason);
        Assert.False(parked.CapSpent);
        Assert.Equal(T0.AddMinutes(5), parked.ParkedAt);

        _store.End(run.Id, "stopped by the owner", T0.AddMinutes(10));
        var ended = _store.ById(run.Id)!;
        Assert.Equal(RunStatus.Ended, ended.Status);
        Assert.Equal("stopped by the owner", ended.Reason);
        Assert.Equal(T0.AddMinutes(10), ended.EndedAt);
        Assert.Null(_store.Active(Room));
    }

    [Fact]
    public void Resume_restores_active_and_accumulates_parked_seconds_across_two_parks()
    {
        var run = _store.Start(Room, Conductor, "roadmap", "", 1, 4, 40, T0);

        _store.Park(run.Id, "idle", capSpent: false, T0.AddMinutes(10));
        var resumed = _store.Resume(run.Id, T0.AddMinutes(70));   // parked for 1 hour = 3600s
        Assert.Equal(RunStatus.Active, resumed.Status);
        Assert.Null(resumed.Reason);
        Assert.Null(resumed.ParkedAt);
        Assert.Equal(3600L, resumed.ParkedSeconds);
        Assert.Equal(resumed, _store.Active(Room));

        _store.Park(run.Id, "idle again", capSpent: false, T0.AddMinutes(100));
        var resumedAgain = _store.Resume(run.Id, T0.AddMinutes(105));   // + 5 minutes = 300s
        Assert.Equal(3900L, resumedAgain.ParkedSeconds);
    }

    [Fact]
    public void Hold_refunds_the_spawn_parks_softly_and_sets_the_resume_instant()
    {
        var run = _store.Start(Room, Conductor, "roadmap", "", 1, 4, 40, T0);
        _store.CountSpawn(run.Id);
        _store.CountSpawn(run.Id);

        var held = _store.Hold(run.Id, "usage limit", T0.AddHours(3), T0.AddMinutes(5));

        Assert.Equal((RunStatus.Parked, false, 1, (DateTimeOffset?)T0.AddHours(3), (DateTimeOffset?)T0.AddMinutes(5)), (held.Status, held.CapSpent, held.SpawnsUsed, held.ResumeAt, held.ParkedAt));
        Assert.Equal(run.Id, _store.NextResume()!.Id);
        Assert.Throws<InvalidOperationException>(() => _store.Hold(run.Id, "again", null, T0.AddMinutes(6)));   // only an active run is held
    }

    [Fact]
    public void Hold_never_refunds_below_zero_and_with_no_reset_sets_no_resume()
    {
        var run = _store.Start(Room, Conductor, "roadmap", "", 1, 4, 40, T0);

        var held = _store.Hold(run.Id, "usage limit, no reset given", null, T0.AddMinutes(5));

        Assert.Equal((0, (DateTimeOffset?)null), (held.SpawnsUsed, held.ResumeAt));
        Assert.Null(_store.NextResume());
    }

    /// <summary>Every other status write clears a held run's resume instant, so a later park that
    /// waits for the owner never reopens by itself.</summary>
    [Theory]
    [InlineData("park")]
    [InlineData("resume")]
    [InlineData("extend")]
    [InlineData("end")]
    public void Every_other_status_write_clears_the_resume_instant(string write)
    {
        var run = _store.Start(Room, Conductor, "roadmap", "", 1, 4, 40, T0);
        _store.Hold(run.Id, "usage limit", T0.AddHours(3), T0.AddMinutes(5));

        var after = write switch
        {
            "park" => _store.Park(run.Id, "two refused posts", capSpent: false, T0.AddMinutes(6)),
            "resume" => _store.Resume(run.Id, T0.AddMinutes(6)),
            "extend" => _store.Extend(run.Id, 6, 60, T0.AddMinutes(6)),
            _ => _store.End(run.Id, "stopped", T0.AddMinutes(6)),
        };

        Assert.Null(after.ResumeAt);
        Assert.Null(_store.NextResume());
    }

    [Fact]
    public void Resume_on_a_cap_spent_park_throws_and_leaves_the_run_parked()
    {
        var run = _store.Start(Room, Conductor, "roadmap", "", 1, 4, 40, T0);
        _store.Park(run.Id, "the run used its 80 spawns", capSpent: true, T0.AddMinutes(5));

        Assert.Throws<InvalidOperationException>(() => _store.Resume(run.Id, T0.AddMinutes(10)));
        Assert.Equal(RunStatus.Parked, _store.ById(run.Id)!.Status);
    }

    [Fact]
    public void ActiveElapsed_excludes_parked_time()
    {
        var run = _store.Start(Room, Conductor, "roadmap", "", 1, 4, 40, T0);
        _store.Park(run.Id, "idle", capSpent: false, T0.AddHours(1));
        var resumed = _store.Resume(run.Id, T0.AddHours(2));   // parked 1 hour

        // Wall clock since start is 3 hours; 1 of them was parked, so 2 hours of ACTIVE elapsed.
        var elapsed = RunStore.ActiveElapsed(resumed, T0.AddHours(3));
        Assert.Equal(TimeSpan.FromHours(2), elapsed);
    }

    [Fact]
    public void ActiveElapsed_freezes_while_still_parked_rather_than_growing_with_the_wall_clock()
    {
        var run = _store.Start(Room, Conductor, "roadmap", "", 1, 4, 40, T0);
        var parked = _store.Park(run.Id, "idle", capSpent: false, T0.AddHours(1));   // 1 hour active before parking

        // Asked long after, while STILL parked (never resumed), elapsed must stay pinned at what it
        // was the instant the run parked, not keep growing with the wall clock.
        var elapsed = RunStore.ActiveElapsed(parked, T0.AddHours(10));
        Assert.Equal(TimeSpan.FromHours(1), elapsed);
    }

    [Fact]
    public void ActiveElapsed_freezes_while_waiting_and_excludes_every_finished_wait_across_a_reopened_store()
    {
        var run = _store.Start(Room, Conductor, "roadmap", "", 1, 4, 40, T0);
        var waiting = _store.StartWait(run.Id, T0.AddHours(1));
        Assert.Equal(T0.AddHours(1), _store.StartWait(run.Id, T0.AddHours(2)).WaitingSince);   // a second start keeps the first instant
        Assert.Equal(TimeSpan.FromHours(1), RunStore.ActiveElapsed(waiting, T0.AddHours(9)));   // frozen while waiting

        var done = _store.EndWait(run.Id, T0.AddHours(3));   // waited 2 h
        Assert.Equal(((DateTimeOffset?)null, 7200L), (done.WaitingSince, done.WaitedSeconds));
        Assert.Equal(TimeSpan.FromHours(2), RunStore.ActiveElapsed(done, T0.AddHours(4)));
        Assert.Equal(done, _store.EndWait(run.Id, T0.AddHours(5)));   // not waiting: unchanged

        var reopened = new RunStore(new ChopDb(Path.Combine(_dir, "chopitup.db"))).ById(run.Id)!;
        Assert.Equal(TimeSpan.FromHours(2), RunStore.ActiveElapsed(reopened, T0.AddHours(4)));   // persisted
    }

    [Fact]
    public void Park_and_End_fold_an_open_wait_and_a_parked_run_cannot_start_one()
    {
        var run = _store.Start(Room, Conductor, "roadmap", "", 1, 4, 40, T0);
        _store.StartWait(run.Id, T0.AddHours(1));
        var parked = _store.Park(run.Id, "the hub restarted while this run was active", capSpent: false, T0.AddHours(2));
        Assert.Equal(((DateTimeOffset?)null, 3600L), (parked.WaitingSince, parked.WaitedSeconds));
        Assert.Equal(TimeSpan.FromHours(1), RunStore.ActiveElapsed(parked, T0.AddHours(5)));
        Assert.Null(_store.StartWait(run.Id, T0.AddHours(3)).WaitingSince);

        _store.Resume(run.Id, T0.AddHours(3));
        _store.StartWait(run.Id, T0.AddHours(4));
        var ended = _store.End(run.Id, "stopped by the owner", T0.AddHours(4.5));
        Assert.Equal(((DateTimeOffset?)null, 5400L), (ended.WaitingSince, ended.WaitedSeconds));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Extend_adds_budget_resets_phase_counts_clears_cap_spent_and_resumes(bool capSpent)
    {
        var run = _store.Start(Room, Conductor, "roadmap", "", 1, 2, 20, T0);
        _store.EnterPhase(run.Id, "build/1", T0);
        _store.EnterPhase(run.Id, "build/1", T0);
        _store.Park(run.Id, "phase build/1 entered 3 times", capSpent, T0.AddHours(1));

        Assert.Throws<InvalidOperationException>(() => _store.Extend(run.Id, 1, 30, T0.AddHours(2)));   // never shrinks
        var extended = _store.Extend(run.Id, 5, 50, T0.AddHours(2));

        Assert.Equal((RunStatus.Active, false, (string?)null, (DateTimeOffset?)null), (extended.Status, extended.CapSpent, extended.Reason, extended.ParkedAt));
        Assert.Equal((5, 50, 3600L), (extended.BudgetHours, extended.SpawnBudget, extended.ParkedSeconds));
        Assert.Empty(_store.PhaseEntries(run.Id));
        Assert.Equal("build/1", extended.Phase);
        Assert.Throws<InvalidOperationException>(() => _store.Extend(run.Id, 1, 10, T0.AddHours(3)));   // active: not parked
    }

    [Fact]
    public void CountSpawn_and_CountExchange_increment_and_return_the_new_count()
    {
        var run = _store.Start(Room, Conductor, "roadmap", "", 1, 4, 40, T0);

        Assert.Equal(1, _store.CountSpawn(run.Id));
        Assert.Equal(2, _store.CountSpawn(run.Id));
        Assert.Equal(1, _store.CountExchange(run.Id));

        var reloaded = _store.ById(run.Id)!;
        Assert.Equal(2, reloaded.SpawnsUsed);
        Assert.Equal(1, reloaded.Exchanges);
    }

    [Fact]
    public void EnterPhase_counts_per_tag_independently_and_sets_runs_phase_and_PhaseEntries_reads_the_whole_map()
    {
        var run = _store.Start(Room, Conductor, "roadmap", "", 1, 4, 40, T0);

        Assert.Equal(1, _store.EnterPhase(run.Id, "build", T0));
        Assert.Equal(2, _store.EnterPhase(run.Id, "build", T0));
        Assert.Equal(1, _store.EnterPhase(run.Id, "critique/pass-1", T0));
        Assert.Equal(3, _store.EnterPhase(run.Id, "build", T0));

        Assert.Equal("build", _store.ById(run.Id)!.Phase);   // last-entered tag, not the one with more entries

        var entries = _store.PhaseEntries(run.Id);
        Assert.Equal(3, entries["build"]);
        Assert.Equal(1, entries["critique/pass-1"]);
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void RecordArtifact_twice_keeps_the_latest_author()
    {
        var run = _store.Start(Room, Conductor, "roadmap", "", 1, 4, 40, T0);

        _store.RecordArtifact(run.Id, "docs/PLAN.md", "opus", T0);
        _store.RecordArtifact(run.Id, "docs/PLAN.md", "sonnet", T0.AddMinutes(1));

        Assert.Equal("sonnet", _store.ArtifactAuthor(run.Id, "docs/PLAN.md"));
        var artifact = Assert.Single(_store.Artifacts(run.Id));
        Assert.Equal("sonnet", artifact.AuthorId);
    }

    [Theory]
    [InlineData("docs/PLAN.md")]
    [InlineData("./docs/PLAN.md")]
    [InlineData("docs\\PLAN.md")]
    [InlineData("`docs/PLAN.md`")]
    [InlineData("DOCS/PLAN.MD")]
    public void ArtifactAuthor_matches_four_different_spellings_of_one_recorded_path(string spelling)
    {
        var run = _store.Start(Room, Conductor, "roadmap", "", 1, 4, 40, T0);
        _store.RecordArtifact(run.Id, "docs/PLAN.md", "opus", T0);

        Assert.Equal("opus", _store.ArtifactAuthor(run.Id, spelling));
    }

    [Fact]
    public void ArtifactAuthor_returns_null_for_an_unknown_path()
    {
        var run = _store.Start(Room, Conductor, "roadmap", "", 1, 4, 40, T0);
        Assert.Null(_store.ArtifactAuthor(run.Id, "nope.md"));
    }

    [Fact]
    public void RecordGateRun_with_a_null_run_id_succeeds_and_reads_back_with_a_run_scoped_run_id()
    {
        // The hub must be able to record a refusal even when there is no run at all.
        _store.RecordGateRun(runId: null, roomId: Room, gate: "budget", callerId: "opus", exitCode: null, outcome: "refused: no active run", T0);

        var run = _store.Start(Room, Conductor, "roadmap", "", 1, 4, 40, T0);
        _store.RecordGateRun(run.Id, Room, "budget", "opus", 0, "ok", T0.AddMinutes(1));
        _store.RecordGateRun(run.Id, Room, "budget", "opus", 1, "failed", T0.AddMinutes(2));

        var gateRuns = _store.GateRuns(run.Id);
        Assert.Equal(2, gateRuns.Count);
        Assert.Equal(["ok", "failed"], gateRuns.Select(g => g.Outcome));
    }
}
