using ChopItUp.Core.Model;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Spawning;

/// <summary>One test per row of task 3's transition table (row19-runs.md), plus the termination
/// property test the ticket requires. <see cref="Limits"/> uses small numbers purely for test
/// ergonomics - D9's real numbers live in RunLimitsTests.</summary>
public sealed class RunPolicyTests
{
    private static readonly RunLimits Limits = new(
        Spawns: 5, WallClock: TimeSpan.FromHours(1), SpawnTimeout: TimeSpan.FromMinutes(5), PhaseEntries: 3,
        GateTimeout: TimeSpan.FromMinutes(4));

    private readonly RunPolicy _policy = new(Limits);

    private static RunState Base() => new(
        RunId: 1, RoomId: "room-1", ConductorId: "opus", Status: RunStatus.Active, CapSpent: false,
        Phase: "(start)", PhaseEntries: new Dictionary<string, int>(),
        SpawnsUsed: 0, Exchanges: 0, Elapsed: TimeSpan.Zero,
        RefusalsThisPhase: 0, SilencesThisPhase: 0,
        ExchangeOpen: false, AnythingInFlight: false, RootMessageId: 100);

    // Row 1: any event but StopRequested, spawn cap spent -> hard Park, regardless of what else is true.
    [Fact]
    public void Row1_spawn_cap_parks_hard_ahead_of_everything_else()
    {
        var s = Base() with { SpawnsUsed = Limits.Spawns };
        var decision = _policy.Decide(s, new RunEvent.Tick(), []);

        var park = Assert.IsType<RunDecision.Park>(decision);
        Assert.True(park.CapSpent);
        Assert.Contains("5", park.Reason);
    }

    // Row 2: wall-clock cap spent -> hard Park.
    [Fact]
    public void Row2_wall_clock_cap_parks_hard()
    {
        var s = Base() with { Elapsed = Limits.WallClock };
        var decision = _policy.Decide(s, new RunEvent.Tick(), []);

        var park = Assert.IsType<RunDecision.Park>(decision);
        Assert.True(park.CapSpent);
    }

    // Row 3: first refused conductor post asks again.
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

    // Row 4: a second refused post in the same phase parks - and does not re-ask.
    [Fact]
    public void Row4_second_refusal_parks_without_asking_again()
    {
        var s = Base() with { RefusalsThisPhase = 1 };
        var evt = new RunEvent.ConductorPosted(MessageId: 43, Tag: null, Mentioned: [], Refusal: "no phase tag");
        var decision = _policy.Decide(s, evt, []);

        var park = Assert.IsType<RunDecision.Park>(decision);
        Assert.False(park.CapSpent);
    }

    // Row 5: a valid ping ends the run.
    [Fact]
    public void Row5_valid_ping_ends_the_run()
    {
        var s = Base();
        var evt = new RunEvent.ConductorPosted(MessageId: 44, Tag: new PhaseTag("ping", null), Mentioned: [], Refusal: null);
        var decision = _policy.Decide(s, evt, []);

        Assert.IsType<RunDecision.End>(decision);
    }

    // Row 6: a valid post into a phase already entered the cap number of times parks hard.
    [Fact]
    public void Row6_phase_re_entry_cap_parks_hard()
    {
        var s = Base() with { PhaseEntries = new Dictionary<string, int> { ["build"] = Limits.PhaseEntries } };
        var evt = new RunEvent.ConductorPosted(MessageId: 45, Tag: new PhaseTag("build", null), Mentioned: ["sonnet"], Refusal: null);
        var decision = _policy.Decide(s, evt, []);

        var park = Assert.IsType<RunDecision.Park>(decision);
        Assert.True(park.CapSpent);
    }

    // Row 7: a valid post under the phase cap opens the workers it mentions.
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

    // Row 8: the room's current exchange concluded, nothing open, nothing in flight -> re-spawn the
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

    // Row 9: something else is still driving (open exchange, or in flight) -> do nothing.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Row9_exchange_concluded_while_something_else_drives_does_nothing(bool exchangeOpen, bool inFlight)
    {
        var s = Base() with { ExchangeOpen = exchangeOpen, AnythingInFlight = inFlight };
        var decision = _policy.Decide(s, new RunEvent.ExchangeConcluded(LastMessageId: 51), []);

        Assert.IsType<RunDecision.Nothing>(decision);
    }

    // Row 10: the conductor's first silence in this phase asks again with the same triggers.
    [Fact]
    public void Row10_first_silence_asks_again()
    {
        var s = Base() with { SilencesThisPhase = 0 };
        var decision = _policy.Decide(s, new RunEvent.SpawnSilent(s.ConductorId, [9, 10]), []);

        var open = Assert.IsType<RunDecision.OpenConductor>(decision);
        Assert.Equal(s.RootMessageId, open.RootMessageId);
        Assert.Equal([9, 10], open.TriggerIds);
    }

    // Row 11: a second silence, with the current phase already at its entry cap, parks (soft).
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

    // Row 12: a second silence under the phase cap asks again (the service counts a phase entry).
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

    // Row 13: a non-conductor spawn going silent is not this policy's business.
    [Fact]
    public void Row13_non_conductor_silence_does_nothing()
    {
        var s = Base();
        var decision = _policy.Decide(s, new RunEvent.SpawnSilent("some-other-row", [13]), []);

        Assert.IsType<RunDecision.Nothing>(decision);
    }

    // Row 14: a human posting into an active run does nothing here - the service records the steer.
    [Fact]
    public void Row14_human_post_into_active_run_does_nothing()
    {
        var s = Base() with { Status = RunStatus.Active };
        var decision = _policy.Decide(s, new RunEvent.HumanPosted(14), []);

        Assert.IsType<RunDecision.Nothing>(decision);
    }

    // Row 15: a human posting into a parked-but-not-cap-spent run wakes the conductor.
    [Fact]
    public void Row15_human_post_into_softly_parked_run_wakes_conductor()
    {
        var s = Base() with { Status = RunStatus.Parked, CapSpent = false };
        var decision = _policy.Decide(s, new RunEvent.HumanPosted(15), []);

        var open = Assert.IsType<RunDecision.OpenConductor>(decision);
        Assert.Equal(s.RootMessageId, open.RootMessageId);
        Assert.Equal([15], open.TriggerIds);
    }

    // Row 16: a human posting into a run parked because a hard cap is spent gets refused, not resumed.
    // SpawnsUsed and Elapsed are kept under their caps here so rows 1/2 cannot be the ones firing -
    // this state is "parked because the phase-entry cap tripped", which sets CapSpent without also
    // tripping rows 1/2's own checks.
    [Fact]
    public void Row16_human_post_into_hard_capped_park_is_refused()
    {
        var s = Base() with { Status = RunStatus.Parked, CapSpent = true };
        var decision = _policy.Decide(s, new RunEvent.HumanPosted(16), []);

        var refuse = Assert.IsType<RunDecision.Refuse>(decision);
        Assert.Equal("this run is parked because a cap is spent; /stop and start a new one", refuse.Note);
    }

    // Orchestrator diff-review finding against task 9f: rows 1/2 must not re-fire for a run that is
    // not active. A hard-capped park's SpawnsUsed/Elapsed stay at or over the cap forever; without the
    // Status == Active guard, this HumanPosted resume attempt would hit row 1 before row 16 ever ran,
    // re-Parking instead of refusing.
    [Fact]
    public void Row16_refuses_even_though_the_spawn_and_wall_clock_caps_are_still_over_their_limits()
    {
        var s = Base() with
        {
            Status = RunStatus.Parked, CapSpent = true,
            SpawnsUsed = Limits.Spawns + 1, Elapsed = Limits.WallClock + TimeSpan.FromHours(1),
        };
        var decision = _policy.Decide(s, new RunEvent.HumanPosted(16), []);

        var refuse = Assert.IsType<RunDecision.Refuse>(decision);
        Assert.Equal(RunPolicy.CapSpentRefusal, refuse.Note);
    }

    // Row 17: /stop always ends the run, even one whose spawn cap is already spent - proving rows 1/2
    // are skipped for StopRequested (AC11: the owner must get End, never Park, on the state that most
    // needs it).
    [Fact]
    public void Row17_stop_ends_the_run_even_when_a_hard_cap_is_already_spent()
    {
        var s = Base() with { SpawnsUsed = Limits.Spawns, Elapsed = Limits.WallClock };
        var decision = _policy.Decide(s, new RunEvent.StopRequested(), []);

        var end = Assert.IsType<RunDecision.End>(decision);
        Assert.Equal("stopped by the owner", end.Reason);
    }

    // Row 18: a tick on an idle active run with steers pending wakes the conductor.
    [Fact]
    public void Row18_tick_with_pending_steers_wakes_conductor()
    {
        var s = Base();
        var decision = _policy.Decide(s, new RunEvent.Tick(), [21, 22]);

        var open = Assert.IsType<RunDecision.OpenConductor>(decision);
        Assert.Equal(s.RootMessageId, open.RootMessageId);
        Assert.Equal([21, 22], open.TriggerIds);
    }

    // Row 19: a tick on an idle active run with nothing pending parks as stalled - the termination
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

    // Row 20: a tick while something is still open, in flight, or the run isn't active does nothing.
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

    /// <summary>The termination property test the ticket requires: for every RunState with
    /// Status == active, !ExchangeOpen and !AnythingInFlight, Decide(s, Tick, steers) is always
    /// OpenConductor, Park or End - never Nothing. This is the test that makes a silent stall
    /// impossible rather than unlikely; it is necessary but not sufficient (pass 2's F-2), since the
    /// counters it reads are hand-built here rather than pinned end-to-end (that pin is tasks 8/10's
    /// job).</summary>
    [Fact]
    public void An_in_progress_run_always_has_something_driving_it_on_a_tick()
    {
        var spawnsUsed = new[] { 0, Limits.Spawns - 1, Limits.Spawns, Limits.Spawns + 1 };
        var elapsed = new[]
        {
            TimeSpan.Zero,
            Limits.WallClock - TimeSpan.FromMinutes(1),
            Limits.WallClock,
            Limits.WallClock + TimeSpan.FromMinutes(1),
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
                ExchangeOpen: false, AnythingInFlight: false, RootMessageId: 100);

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
