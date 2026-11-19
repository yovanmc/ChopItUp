using ChopItUp.Core.Model;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Spawning;

/// <summary>One test per transition of <see cref="RunPolicy"/>, plus the termination property
/// test. <see cref="Limits"/> uses small numbers purely for test ergonomics; the real numbers live
/// in RunLimitsTests.</summary>
public sealed class RunPolicyTests
{
    private static readonly RunLimits Limits = new(
        SpawnsPerHour: 5, Hour: TimeSpan.FromHours(1), SpawnTimeout: TimeSpan.FromMinutes(5), PhaseEntries: 3,
        GateTimeout: TimeSpan.FromMinutes(4));

    // The run's own caps, as a one-hour budget at these limits.
    private const int SpawnBudget = 5;
    private static readonly TimeSpan WallClock = TimeSpan.FromHours(1);

    private readonly RunPolicy _policy = new(Limits);

    private static RunState Base() => new(
        RunId: 1, RoomId: "room-1", ConductorId: "opus", Status: RunStatus.Active, CapSpent: false,
        Phase: "(start)", PhaseEntries: new Dictionary<string, int>(),
        SpawnsUsed: 0, Exchanges: 0, Elapsed: TimeSpan.Zero,
        RefusalsThisPhase: 0, SilencesThisPhase: 0,
        ExchangeOpen: false, AnythingInFlight: false, RootMessageId: 100,
        SpawnBudget: SpawnBudget, WallClockBudget: WallClock, JobsInFlight: 0);

    // Any event but StopRequested, spawn cap spent -> hard Park, regardless of what else is true.
    [Fact]
    public void Row1_spawn_cap_parks_hard_ahead_of_everything_else()
    {
        var s = Base() with { SpawnsUsed = SpawnBudget };
        var decision = _policy.Decide(s, new RunEvent.Tick(), []);

        var park = Assert.IsType<RunDecision.Park>(decision);
        Assert.True(park.CapSpent);
        Assert.Contains("5", park.Reason);
    }

    // Wall-clock cap spent -> hard Park.
    [Fact]
    public void Row2_wall_clock_cap_parks_hard()
    {
        var s = Base() with { Elapsed = WallClock };
        var decision = _policy.Decide(s, new RunEvent.Tick(), []);

        var park = Assert.IsType<RunDecision.Park>(decision);
        Assert.True(park.CapSpent);
    }

    // First refused conductor post asks again.
    [Fact]
    public void Row3_first_refusal_asks_again()
    {
        var s = Base() with { RefusalsThisPhase = 0 };
        var evt = new RunEvent.ConductorPosted(MessageId: 42, Tag: null, Mentioned: [], Refusal: "no phase tag");
        var decision = _policy.Decide(s, evt, []);

        var ask = Assert.IsType<RunDecision.RefuseAndAsk>(decision);
        Assert.Equal("no phase tag", ask.Note);
        Assert.Equal(42, ask.TriggerMessageId);
    }

    // A second refused post in the same phase parks, and does not re-ask.
    [Fact]
    public void Row4_second_refusal_parks_without_asking_again()
    {
        var s = Base() with { RefusalsThisPhase = 1 };
        var evt = new RunEvent.ConductorPosted(MessageId: 43, Tag: null, Mentioned: [], Refusal: "no phase tag");
        var decision = _policy.Decide(s, evt, []);

        var park = Assert.IsType<RunDecision.Park>(decision);
        Assert.False(park.CapSpent);
    }

    // A valid ping ends the run. A ping-caused End must carry the Run cause, never Owner: the run ended itself.
    [Fact]
    public void Row5_valid_ping_ends_the_run_with_the_run_cause_not_the_owner()
    {
        var s = Base();
        var evt = new RunEvent.ConductorPosted(MessageId: 44, Tag: new PhaseTag("ping", null), Mentioned: [], Refusal: null);
        var decision = _policy.Decide(s, evt, []);

        var end = Assert.IsType<RunDecision.End>(decision);
        Assert.Equal(ExchangeStopCause.Run, end.Cause);
    }

    // A valid post into a phase already entered the cap number of times parks hard.
    [Fact]
    public void Row6_phase_re_entry_cap_parks_hard()
    {
        var s = Base() with { PhaseEntries = new Dictionary<string, int> { ["build"] = Limits.PhaseEntries } };
        var evt = new RunEvent.ConductorPosted(MessageId: 45, Tag: new PhaseTag("build", null), Mentioned: ["sonnet"], Refusal: null);
        var decision = _policy.Decide(s, evt, []);

        var park = Assert.IsType<RunDecision.Park>(decision);
        Assert.True(park.CapSpent);
    }

    // A valid post under the phase cap opens the workers it mentions.
    [Fact]
    public void Row7_valid_post_opens_workers()
    {
        var s = Base() with { PhaseEntries = new Dictionary<string, int> { ["build"] = Limits.PhaseEntries - 1 } };
        var tag = new PhaseTag("build", null);
        var evt = new RunEvent.ConductorPosted(MessageId: 46, Tag: tag, Mentioned: ["sonnet"], Refusal: null);
        var decision = _policy.Decide(s, evt, []);

        var open = Assert.IsType<RunDecision.OpenWorkers>(decision);
        Assert.Equal(46, open.RootMessageId);
        Assert.Equal(["sonnet"], open.Mentioned);
        Assert.Equal(tag, open.Tag);
    }

    // The room's current exchange concluded, nothing open, nothing in flight -> re-spawn the
    // conductor with the concluding message plus every pending steer.
    [Fact]
    public void Row8_exchange_concluded_with_nothing_driving_respawns_conductor_with_steers()
    {
        var s = Base();
        var decision = _policy.Decide(s, new RunEvent.ExchangeConcluded(LastMessageId: 50), [7, 8]);

        var open = Assert.IsType<RunDecision.OpenConductor>(decision);
        Assert.Equal(s.RootMessageId, open.RootMessageId);
        Assert.Equal([50, 7, 8], open.TriggerIds);
    }

    // Something else is still driving (open exchange, or in flight) -> do nothing.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Row9_exchange_concluded_while_something_else_drives_does_nothing(bool exchangeOpen, bool inFlight)
    {
        var s = Base() with { ExchangeOpen = exchangeOpen, AnythingInFlight = inFlight };
        var decision = _policy.Decide(s, new RunEvent.ExchangeConcluded(LastMessageId: 51), []);

        Assert.IsType<RunDecision.Nothing>(decision);
    }

    // The conductor's first silence in this phase asks again with the same triggers.
    [Fact]
    public void Row10_first_silence_asks_again()
    {
        var s = Base() with { SilencesThisPhase = 0 };
        var decision = _policy.Decide(s, new RunEvent.SpawnSilent(s.ConductorId, [9, 10]), []);

        var open = Assert.IsType<RunDecision.OpenConductor>(decision);
        Assert.Equal(s.RootMessageId, open.RootMessageId);
        Assert.Equal([9, 10], open.TriggerIds);
    }

    // A second silence, with the current phase already at its entry cap, parks (soft).
    [Fact]
    public void Row11_second_silence_at_phase_cap_parks_soft()
    {
        var s = Base() with
        {
            SilencesThisPhase = 1,
            Phase = "build",
            PhaseEntries = new Dictionary<string, int> { ["build"] = Limits.PhaseEntries },
        };
        var decision = _policy.Decide(s, new RunEvent.SpawnSilent(s.ConductorId, [11]), []);

        var park = Assert.IsType<RunDecision.Park>(decision);
        Assert.False(park.CapSpent);
    }

    // A second silence under the phase cap asks again (the service counts a phase entry).
    [Fact]
    public void Row12_second_silence_under_phase_cap_asks_again()
    {
        var s = Base() with
        {
            SilencesThisPhase = 1,
            Phase = "build",
            PhaseEntries = new Dictionary<string, int> { ["build"] = Limits.PhaseEntries - 1 },
        };
        var decision = _policy.Decide(s, new RunEvent.SpawnSilent(s.ConductorId, [12]), []);

        Assert.IsType<RunDecision.OpenConductor>(decision);
    }

    // A non-conductor spawn going silent is not this policy's business.
    [Fact]
    public void Row13_non_conductor_silence_does_nothing()
    {
        var s = Base();
        var decision = _policy.Decide(s, new RunEvent.SpawnSilent("some-other-row", [13]), []);

        Assert.IsType<RunDecision.Nothing>(decision);
    }

    // A human posting into an active run does nothing here: the service records the steer.
    [Fact]
    public void Row14_human_post_into_active_run_does_nothing()
    {
        var s = Base() with { Status = RunStatus.Active };
        var decision = _policy.Decide(s, new RunEvent.HumanPosted(14), []);

        Assert.IsType<RunDecision.Nothing>(decision);
    }

    // A human posting into a parked-but-not-cap-spent run wakes the conductor.
    [Fact]
    public void Row15_human_post_into_softly_parked_run_wakes_conductor()
    {
        var s = Base() with { Status = RunStatus.Parked, CapSpent = false };
        var decision = _policy.Decide(s, new RunEvent.HumanPosted(15), []);

        var open = Assert.IsType<RunDecision.OpenConductor>(decision);
        Assert.Equal(s.RootMessageId, open.RootMessageId);
        Assert.Equal([15], open.TriggerIds);
    }

    // A human posting into a run parked because a hard cap is spent gets refused, not resumed.
    // SpawnsUsed and Elapsed are kept under their caps here so the spawn/wall-clock checks cannot be
    // the ones firing: this state is "parked because the phase-entry cap tripped", which sets
    // CapSpent without tripping those checks.
    // The second case: the spawn/wall-clock cap checks must not re-fire for a run that is not active.
    // A hard-capped park's SpawnsUsed/Elapsed stay at or over the cap forever; without the
    // Status == Active guard, this HumanPosted resume attempt would hit the spawn cap check first and
    // re-Park instead of refusing.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Row16_human_post_into_hard_capped_park_is_refused(bool spawnAndWallClockCapsStillOverTheirLimits)
    {
        var s = Base() with { Status = RunStatus.Parked, CapSpent = true };
        if (spawnAndWallClockCapsStillOverTheirLimits)
            s = s with { SpawnsUsed = SpawnBudget + 1, Elapsed = WallClock + TimeSpan.FromHours(1) };
        var decision = _policy.Decide(s, new RunEvent.HumanPosted(16), []);

        var refuse = Assert.IsType<RunDecision.Refuse>(decision);
        Assert.Equal("this run is parked because a cap is spent; /continue hours: N adds budget, or /stop it", refuse.Note);
        Assert.Equal(RunPolicy.CapSpentRefusal, refuse.Note);
    }

    // /stop always ends the run, even one whose spawn cap is already spent, proving the cap checks
    // are skipped for StopRequested (the hub owner must get End, never Park).
    [Fact]
    public void Row17_stop_ends_the_run_even_when_a_hard_cap_is_already_spent()
    {
        var s = Base() with { SpawnsUsed = SpawnBudget, Elapsed = WallClock };
        var decision = _policy.Decide(s, new RunEvent.StopRequested(), []);

        var end = Assert.IsType<RunDecision.End>(decision);
        Assert.Equal("stopped by the owner", end.Reason);
        Assert.Equal(ExchangeStopCause.Owner, end.Cause);   // the owner's own /stop, never inferred
    }

    // A tick on an idle active run with steers pending wakes the conductor.
    [Fact]
    public void Row18_tick_with_pending_steers_wakes_conductor()
    {
        var s = Base();
        var decision = _policy.Decide(s, new RunEvent.Tick(), [21, 22]);

        var open = Assert.IsType<RunDecision.OpenConductor>(decision);
        Assert.Equal(s.RootMessageId, open.RootMessageId);
        Assert.Equal([21, 22], open.TriggerIds);
    }

    // A tick on an idle active run with nothing pending parks as stalled: the termination
    // obligation's own arm.
    [Fact]
    public void Row19_tick_with_nothing_pending_parks_as_stalled()
    {
        var s = Base();
        var decision = _policy.Decide(s, new RunEvent.Tick(), []);

        var park = Assert.IsType<RunDecision.Park>(decision);
        Assert.False(park.CapSpent);
        Assert.Equal("the run stalled", park.Reason);
    }

    // A tick while something is still open, in flight, or the run isn't active does nothing.
    [Theory]
    [InlineData(RunStatus.Active, true, false)]
    [InlineData(RunStatus.Active, false, true)]
    [InlineData(RunStatus.Parked, false, false)]
    [InlineData(RunStatus.Ended, false, false)]
    public void Row20_tick_otherwise_does_nothing(string status, bool exchangeOpen, bool inFlight)
    {
        var s = Base() with { Status = status, ExchangeOpen = exchangeOpen, AnythingInFlight = inFlight };
        var decision = _policy.Decide(s, new RunEvent.Tick(), [99]);

        Assert.IsType<RunDecision.Nothing>(decision);
    }

    // An accepted ping presents at and over every cap: spawns, wall clock, and the ping key's own
    // phase entries. A refused ping is still checked against the caps.
    [Theory]
    [InlineData(5, 0, 0)]
    [InlineData(9, 0, 0)]
    [InlineData(0, 60, 0)]
    [InlineData(0, 300, 0)]
    [InlineData(0, 0, 3)]
    [InlineData(9, 300, 7)]
    public void An_accepted_ping_ends_the_run_at_and_over_every_cap(int spawnsUsed, int elapsedMinutes, int pingEntries)
    {
        var s = Base() with
        {
            SpawnsUsed = spawnsUsed, Elapsed = TimeSpan.FromMinutes(elapsedMinutes),
            PhaseEntries = new Dictionary<string, int> { ["ping"] = pingEntries },
        };
        var ping = new RunEvent.ConductorPosted(MessageId: 60, Tag: new PhaseTag("ping", null), Mentioned: [], Refusal: null);

        var end = Assert.IsType<RunDecision.End>(_policy.Decide(s, ping, []));
        Assert.Equal(("the conductor pinged", ExchangeStopCause.Run), (end.Reason, end.Cause));

        var refused = ping with { Refusal = "no phase tag" };
        if (spawnsUsed >= SpawnBudget || elapsedMinutes >= 60)
            Assert.True(Assert.IsType<RunDecision.Park>(_policy.Decide(s, refused, [])).CapSpent);
    }

    // `/continue` on a park: a cap-spent park of each kind gets the named hours or the default 4, a
    // soft park gets hours only when named, a bad value is refused with the range, and an active run
    // is refused. The caps never fire against Continue.
    [Theory]
    [InlineData("spawns", 3, 3, 4, 20)]
    [InlineData("wall clock", 0, 4, 5, 25)]
    [InlineData("phase", 8, 8, 9, 45)]
    public void Continue_extends_a_cap_spent_park_by_the_named_hours_or_the_default(string cap, int named, int expectedHours, int budgetHours, int spawnBudget)
    {
        var s = Base() with { Status = RunStatus.Parked, CapSpent = true };
        s = cap switch
        {
            "spawns" => s with { SpawnsUsed = SpawnBudget },
            "wall clock" => s with { Elapsed = WallClock },
            _ => s with { Phase = "build", PhaseEntries = new Dictionary<string, int> { ["build"] = Limits.PhaseEntries } },
        };
        var hours = named == 0 ? new ChopItUp.Core.Skills.HoursToken(ChopItUp.Core.Skills.HoursKind.None, 0)
            : new ChopItUp.Core.Skills.HoursToken(ChopItUp.Core.Skills.HoursKind.Valid, named);

        var extend = Assert.IsType<RunDecision.Extend>(_policy.Decide(s, new RunEvent.Continue(61, hours), []));

        Assert.Equal((expectedHours, budgetHours, spawnBudget, s.RootMessageId), (extend.Hours, extend.BudgetHours, extend.SpawnBudget, extend.RootMessageId));
        Assert.Equal([61L], extend.TriggerIds);
    }

    // A restart park can hold a soft-parked run whose active time already passed its budget (the
    // hub was down while it was busy). A plain resume is refused, and /continue counts the added
    // hours from what the run used, so the extended run does not re-park at once.
    [Fact]
    public void A_soft_park_already_over_budget_is_not_resumed_and_continue_counts_from_what_it_used()
    {
        var s = Base() with { Status = RunStatus.Parked, CapSpent = false, Elapsed = TimeSpan.FromHours(12.5), SpawnsUsed = 2 };

        Assert.Equal(RunPolicy.CapSpentRefusal, Assert.IsType<RunDecision.Refuse>(_policy.Decide(s, new RunEvent.HumanPosted(90), [])).Note);
        var extend = Assert.IsType<RunDecision.Extend>(_policy.Decide(s, new RunEvent.Continue(91, default), []));
        Assert.Equal((4, 16, 25), (extend.Hours, extend.BudgetHours, extend.SpawnBudget));   // floor(12.5 h) + 4, max(5, 2) + 4 * 5
    }

    // A real wall-clock park lands a moment past the deadline: /continue hours: 2 adds two hours,
    // not three.
    [Fact]
    public void Continue_after_a_wall_clock_park_just_past_the_deadline_adds_exactly_the_named_hours()
    {
        var s = Base() with { Status = RunStatus.Parked, CapSpent = true, Elapsed = WallClock + TimeSpan.FromSeconds(1) };
        var two = new ChopItUp.Core.Skills.HoursToken(ChopItUp.Core.Skills.HoursKind.Valid, 2);

        var extend = Assert.IsType<RunDecision.Extend>(_policy.Decide(s, new RunEvent.Continue(96, two), []));
        Assert.Equal((2, 3), (extend.Hours, extend.BudgetHours));
    }

    // A steer or job note left pending rides the resume.
    [Fact]
    public void A_resume_or_an_extension_carries_the_pending_triggers()
    {
        var soft = Base() with { Status = RunStatus.Parked };
        Assert.Equal([92L, 7L], Assert.IsType<RunDecision.OpenConductor>(_policy.Decide(soft, new RunEvent.HumanPosted(92), [7])).TriggerIds);
        Assert.Equal([93L, 7L], Assert.IsType<RunDecision.OpenConductor>(_policy.Decide(soft, new RunEvent.Continue(93, default), [7])).TriggerIds);
        Assert.Equal([94L, 7L], Assert.IsType<RunDecision.Extend>(_policy.Decide(soft with { CapSpent = true }, new RunEvent.Continue(94, default), [7])).TriggerIds);
    }

    // The wall-clock wake, a steer or a job finish while the conductor is still running lets its turn
    // finish, so it can still present. Its end or a non-ping post is checked against the caps.
    [Fact]
    public void A_tick_past_the_budget_lets_a_running_conductor_finish_and_anything_else_parks()
    {
        var s = Base() with { Elapsed = WallClock + TimeSpan.FromMinutes(5), AnythingInFlight = true, ConductorInFlight = true };

        Assert.IsType<RunDecision.Nothing>(_policy.Decide(s, new RunEvent.Tick(), []));
        Assert.IsType<RunDecision.Nothing>(_policy.Decide(s, new RunEvent.HumanPosted(96), []));
        Assert.IsType<RunDecision.Nothing>(_policy.Decide(s with { JobsInFlight = 1 }, new RunEvent.JobFinished(97), []));
        var build = new RunEvent.ConductorPosted(95, new PhaseTag("build", null), ["sonnet"], null);
        Assert.True(Assert.IsType<RunDecision.Park>(_policy.Decide(s, build, [])).CapSpent);
        Assert.True(Assert.IsType<RunDecision.Park>(_policy.Decide(s with { ConductorInFlight = false }, new RunEvent.Tick(), [])).CapSpent);
    }

    [Fact]
    public void Continue_on_a_soft_park_resumes_without_budget_unless_hours_are_named()
    {
        var s = Base() with { Status = RunStatus.Parked, CapSpent = false };
        var none = new ChopItUp.Core.Skills.HoursToken(ChopItUp.Core.Skills.HoursKind.None, 0);
        var two = new ChopItUp.Core.Skills.HoursToken(ChopItUp.Core.Skills.HoursKind.Valid, 2);

        var resume = Assert.IsType<RunDecision.OpenConductor>(_policy.Decide(s, new RunEvent.Continue(62, none), []));
        Assert.Equal([62L], resume.TriggerIds);
        Assert.Equal(2, Assert.IsType<RunDecision.Extend>(_policy.Decide(s, new RunEvent.Continue(63, two), [])).Hours);
    }

    [Theory]
    [InlineData(RunStatus.Parked, true)]
    [InlineData(RunStatus.Parked, false)]
    public void Continue_with_a_bad_hours_value_is_refused_with_the_range(string status, bool capSpent)
    {
        var s = Base() with { Status = status, CapSpent = capSpent };
        var bad = new ChopItUp.Core.Skills.HoursToken(ChopItUp.Core.Skills.HoursKind.Invalid, 0);

        var refuse = Assert.IsType<RunDecision.Refuse>(_policy.Decide(s, new RunEvent.Continue(64, bad), []));
        Assert.Equal("hours: must be a whole number from 1 to 8 (default 4).", refuse.Note);
    }

    [Fact]
    public void Continue_on_an_active_run_is_refused_even_over_its_caps()
    {
        var s = Base() with { SpawnsUsed = SpawnBudget + 1 };
        var refuse = Assert.IsType<RunDecision.Refuse>(_policy.Decide(s, new RunEvent.Continue(65, default), []));
        Assert.Equal("A run is active in this room (#1); /continue hours: N extends a parked run.", refuse.Note);
    }

    // Jobs in flight: waiting is neither a stall nor a silence, and a steer still wakes the conductor.
    [Fact]
    public void With_a_job_in_flight_a_tick_and_a_conductor_silence_do_nothing_but_a_steer_still_wakes_it()
    {
        var s = Base() with { JobsInFlight = 1, SilencesThisPhase = 1, Phase = "build", PhaseEntries = new Dictionary<string, int> { ["build"] = Limits.PhaseEntries } };

        Assert.IsType<RunDecision.Nothing>(_policy.Decide(s, new RunEvent.Tick(), []));
        Assert.IsType<RunDecision.Nothing>(_policy.Decide(s, new RunEvent.SpawnSilent(s.ConductorId, [70]), []));
        Assert.Equal([71L], Assert.IsType<RunDecision.OpenConductor>(_policy.Decide(s, new RunEvent.Tick(), [71])).TriggerIds);
        Assert.Equal([72L], Assert.IsType<RunDecision.OpenConductor>(_policy.Decide(s, new RunEvent.SpawnSilent(s.ConductorId, [70]), [72])).TriggerIds);
        Assert.True(Assert.IsType<RunDecision.Park>(_policy.Decide(s with { SpawnsUsed = SpawnBudget }, new RunEvent.Tick(), [])).CapSpent);   // a budget still binds
    }

    [Fact]
    public void JobFinished_opens_an_idle_conductor_with_the_note_first_and_waits_while_busy_or_parked()
    {
        var open = Assert.IsType<RunDecision.OpenConductor>(_policy.Decide(Base(), new RunEvent.JobFinished(80), [81]));
        Assert.Equal(100L, open.RootMessageId);
        Assert.Equal([80L, 81L], open.TriggerIds);

        Assert.IsType<RunDecision.Nothing>(_policy.Decide(Base() with { ExchangeOpen = true }, new RunEvent.JobFinished(82), []));
        Assert.IsType<RunDecision.Nothing>(_policy.Decide(Base() with { AnythingInFlight = true }, new RunEvent.JobFinished(83), []));
        Assert.IsType<RunDecision.Nothing>(_policy.Decide(Base() with { Status = RunStatus.Parked }, new RunEvent.JobFinished(84), []));
    }

    /// <summary>The termination property: for every RunState with Status == active, !ExchangeOpen and
    /// !AnythingInFlight and no job in flight, Decide(s, Tick, steers) is always OpenConductor, Park
    /// or End, never Nothing. With a job in flight the job's own end (JobFinished) drives the run.
    /// This makes a silent stall impossible rather than unlikely. It is necessary but not
    /// sufficient: the counters it reads are hand-built here, not pinned end-to-end.</summary>
    [Fact]
    public void An_in_progress_run_always_has_something_driving_it_on_a_tick()
    {
        var spawnsUsed = new[] { 0, SpawnBudget - 1, SpawnBudget, SpawnBudget + 1 };
        var elapsed = new[]
        {
            TimeSpan.Zero,
            WallClock - TimeSpan.FromMinutes(1),
            WallClock,
            WallClock + TimeSpan.FromMinutes(1),
        };
        var phases = new[] { "(start)", "build", "critique/pass-1" };
        var entryCounts = new[] { 0, Limits.PhaseEntries - 1, Limits.PhaseEntries, Limits.PhaseEntries + 1 };
        var refusals = new[] { 0, 1, 2 };
        var silences = new[] { 0, 1, 2 };
        var steerSets = new[] { Array.Empty<long>(), new long[] { 5 } };

        var examined = 0;
        foreach (var used in spawnsUsed)
        foreach (var el in elapsed)
        foreach (var phase in phases)
        foreach (var count in entryCounts)
        foreach (var refusalCount in refusals)
        foreach (var silenceCount in silences)
        foreach (var steers in steerSets)
        {
            var s = new RunState(
                RunId: 1, RoomId: "room-1", ConductorId: "opus", Status: RunStatus.Active, CapSpent: false,
                Phase: phase, PhaseEntries: new Dictionary<string, int> { [phase] = count },
                SpawnsUsed: used, Exchanges: 0, Elapsed: el,
                RefusalsThisPhase: refusalCount, SilencesThisPhase: silenceCount,
                ExchangeOpen: false, AnythingInFlight: false, RootMessageId: 100,
        SpawnBudget: SpawnBudget, WallClockBudget: WallClock, JobsInFlight: 0);

            var decision = _policy.Decide(s, new RunEvent.Tick(), steers);
            examined++;

            Assert.False(decision is RunDecision.Nothing,
                $"Tick on an in-progress run did nothing for SpawnsUsed={used}, Elapsed={el}, " +
                $"Phase={phase}, Entries={count}, Refusals={refusalCount}, Silences={silenceCount}, " +
                $"Steers=[{string.Join(",", steers)}]");
        }

        Assert.True(examined > 0);
    }
}
