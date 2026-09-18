using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Skills;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Spawning;

public sealed class ExchangePolicyTests
{
    private static readonly SpawnLimits Limits = new(Budget: 4, Debounce: TimeSpan.FromSeconds(2), MinSpacing: TimeSpan.FromSeconds(10), Timeout: TimeSpan.FromMinutes(5), TranscriptMessages: 60, TranscriptChars: 24_000);
    private static readonly DateTimeOffset T0 = new(2026, 9, 5, 20, 0, 0, TimeSpan.Zero);
    private static Message Msg(long id, string author, string body) => new(id, "general", author, body, T0);
    private static ExchangePolicy Policy() => new(ChopDb.SeedRoster, Limits);
    private static readonly IReadOnlyDictionary<string, DateTimeOffset> NoStarts = new Dictionary<string, DateTimeOffset>();
    private static readonly HashSet<string> Nobody = new();

    [Fact]
    public void An_owner_mention_opens_an_exchange_and_ignores_owner_app_backed_hub_and_self()
    {
        var (x, notes) = Policy().OnMessage(null, Msg(10, "owner", "@opus @claude @codex @owner @gpt-6-astra go"), T0);
        Assert.NotNull(x);
        Assert.Equal(ExchangeStatus.Open, x!.Status);
        Assert.Equal(10, x.RootMessageId);
        Assert.Equal(4, x.Budget);
        Assert.Equal(["opus", "gpt-6-astra"], x.Pending.Keys);
        Assert.Equal(2, x.TurnsCommitted);
        Assert.Equal(0, x.TurnsStarted);
        Assert.Empty(notes);

        var (y, _) = Policy().OnMessage(null, Msg(11, "owner", "@claude @codex only windows"), T0);
        Assert.Null(y);
    }

    [Fact]
    public void A_model_message_never_opens_an_exchange()
    {
        var p = Policy();
        var (x, notes) = p.OnMessage(null, Msg(10, "codex", "@opus what do you think?"), T0);
        Assert.Null(x);
        Assert.Empty(notes);
        var (c, _) = p.OnMessage(null, Msg(11, "opus", "@sonnet"), T0);
        Assert.Null(c);
    }

    [Fact]
    public void A_model_mention_inside_an_open_exchange_is_a_turn_until_the_budget_is_used_up()
    {
        var p = Policy();
        var (x, _) = p.OnMessage(null, Msg(1, "owner", "@opus"), T0);
        var (x2, n2) = p.OnMessage(x, Msg(2, "opus", "@sonnet @opus your view?"), T0);   // self-mention ignored
        Assert.Same(x, x2);
        Assert.Equal(["opus", "sonnet"], x!.Pending.Keys);
        Assert.Equal(2, x.TurnsCommitted);
        Assert.Empty(n2);
        var (_, n3) = p.OnMessage(x, Msg(3, "sonnet", "@gpt-6-astra @gpt-5.5 @fable"), T0);
        Assert.Equal(4, x.TurnsCommitted);
        Assert.Equal(["opus", "sonnet", "gpt-6-astra", "gpt-5.5"], x.Pending.Keys);
        var note = Assert.Single(n3);
        Assert.Contains("not spawning @fable", note);
        Assert.Contains("#1", note);
    }

    [Fact]
    public void A_repeat_mention_of_a_pending_participant_adds_a_trigger_but_no_turn()
    {
        var p = Policy();
        var (x, _) = p.OnMessage(null, Msg(1, "owner", "@opus @sonnet"), T0);
        ExchangePolicy.Started(x!, p.Due(x!, T0.AddSeconds(2), NoStarts, Nobody).First(d => d.ParticipantId == "opus"));
        p.OnMessage(x, Msg(2, "opus", "@sonnet also consider this"), T0.AddSeconds(3));
        Assert.Equal(2, x!.TurnsCommitted);
        Assert.Equal([1L, 2L], x.Pending["sonnet"].TriggerIds);
        Assert.Equal(T0.AddSeconds(3), x.Pending["sonnet"].LastTriggerAt);
        Assert.Equal(1, x.RootMessageId);
    }

    [Fact]
    public void A_second_owner_message_re_roots_rather_than_appending_a_trigger()
    {
        var p = Policy();
        var (x, _) = p.OnMessage(null, Msg(1, "owner", "@opus"), T0);
        var (y, _) = p.OnMessage(x, Msg(2, "owner", "@opus also this"), T0.AddMilliseconds(500));
        Assert.Equal(ExchangeStatus.Superseded, x!.Status);
        Assert.Equal(2, y!.RootMessageId);
        Assert.Equal([2L], y.Pending["opus"].TriggerIds);
    }

    [Fact]
    public void Due_honours_debounce_min_spacing_and_one_in_flight_per_room()
    {
        var p = Policy();
        var (x, _) = p.OnMessage(null, Msg(1, "owner", "@opus @sonnet"), T0);

        Assert.Empty(p.Due(x!, T0.AddSeconds(1), NoStarts, Nobody));                                  // debounce
        var due = p.Due(x!, T0.AddSeconds(2), NoStarts, Nobody);
        Assert.Equal(["opus", "sonnet"], due.Select(d => d.ParticipantId));
        Assert.Equal([1, 2], due.Select(d => d.TurnNumber));
        Assert.All(due, d => Assert.Equal(2, d.RemainingAfter));
        Assert.All(due, d => Assert.Equal([1L], d.TriggerIds));

        var recent = new Dictionary<string, DateTimeOffset> { ["opus"] = T0.AddSeconds(-5) };          // started 5 s ago elsewhere
        Assert.Equal(["sonnet"], p.Due(x!, T0.AddSeconds(2), recent, Nobody).Select(d => d.ParticipantId));
        Assert.Equal(["opus", "sonnet"], p.Due(x!, T0.AddSeconds(6), recent, Nobody).Select(d => d.ParticipantId));

        Assert.Equal(["sonnet"], p.Due(x!, T0.AddSeconds(2), NoStarts, new HashSet<string> { "opus" }).Select(d => d.ParticipantId));
    }

    [Fact]
    public void NextWake_is_the_earliest_moment_anything_pending_could_launch()
    {
        var p = Policy();
        var (x, _) = p.OnMessage(null, Msg(1, "owner", "@opus @sonnet"), T0);
        Assert.Equal(T0.AddSeconds(2), p.NextWake(x!, T0, NoStarts, Nobody));
        var recent = new Dictionary<string, DateTimeOffset> { ["opus"] = T0.AddSeconds(-1), ["sonnet"] = T0.AddSeconds(-3) };
        Assert.Equal(T0.AddSeconds(7), p.NextWake(x!, T0, recent, Nobody));                            // sonnet: 10 s after its last start
        Assert.Null(p.NextWake(x!, T0, NoStarts, new HashSet<string> { "opus", "sonnet" }));            // both in flight: woken by completion
        ExchangePolicy.Stop(x!, ExchangeStopCause.Owner);
        Assert.Null(p.NextWake(x!, T0, NoStarts, Nobody));
    }

    [Fact]
    public void Started_then_Finished_walks_pending_to_in_flight_to_concluded()
    {
        var p = Policy();
        var (x, _) = p.OnMessage(null, Msg(1, "owner", "@opus"), T0);
        var req = Assert.Single(p.Due(x!, T0.AddSeconds(2), NoStarts, Nobody));
        ExchangePolicy.Started(x!, req);
        Assert.Empty(x!.Pending);
        Assert.Equal(["opus"], x.InFlight);
        Assert.Equal(1, x.TurnsStarted);

        p.OnMessage(x, Msg(2, "opus", "done, no one else needed"), T0.AddSeconds(30));
        Assert.Equal(ExchangeStatus.Open, x.Status);                                                   // process still running
        var note = ExchangePolicy.Finished(x, "opus");
        Assert.Equal(ExchangeStatus.Concluded, x.Status);
        Assert.Equal("Exchange concluded: 1 of 4 turns used.", note);
        Assert.Null(ExchangePolicy.Finished(x, "opus"));                                                // idempotent, no second note
    }

    [Fact]
    public void A_mention_of_a_participant_already_in_flight_is_recorded_pending_but_excluded_from_Due_until_it_finishes()
    {
        var p = Policy();
        var (x, _) = p.OnMessage(null, Msg(1, "owner", "@opus @sonnet"), T0);
        var opusReq = p.Due(x!, T0.AddSeconds(2), NoStarts, Nobody).First(d => d.ParticipantId == "opus");
        ExchangePolicy.Started(x!, opusReq);
        Assert.DoesNotContain("opus", x!.Pending.Keys);
        Assert.Equal(["opus"], x.InFlight);

        p.OnMessage(x, Msg(2, "sonnet", "@opus back to you"), T0.AddSeconds(3));
        Assert.Contains("opus", x.Pending.Keys);                                                       // recorded pending while still in flight

        var opusInFlight = new HashSet<string> { "opus" };
        Assert.DoesNotContain(p.Due(x, T0.AddSeconds(5), NoStarts, opusInFlight), d => d.ParticipantId == "opus");   // left out of Due while in flight

        ExchangePolicy.Finished(x, "opus");
        Assert.Contains(p.Due(x, T0.AddSeconds(5), NoStarts, Nobody), d => d.ParticipantId == "opus");   // included after Finished
    }

    [Fact]
    public void A_disjoint_owner_prompt_opens_a_second_exchange_and_leaves_the_first_running()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus task A"), T0);
        ExchangePolicy.Started(a!, p.Due(a!, T0.AddSeconds(2), NoStarts, Nobody).Single());

        var (b, notes) = p.OnRoomMessage([a!], null, Msg(2, "owner", "@gpt-6-astra task B"), T0.AddSeconds(3));

        Assert.Equal(ExchangeStatus.Open, a!.Status);
        Assert.Equal(["opus"], a.InFlight);
        Assert.NotNull(b);
        Assert.Equal(2, b!.RootMessageId);
        Assert.Equal(["gpt-6-astra"], b.Pending.Keys);
        Assert.Equal(4, b.Budget);
        Assert.Empty(notes);
    }

    [Fact]
    public void An_overlapping_owner_prompt_supersedes_only_the_exchange_it_overlaps()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus @sonnet task A"), T0);
        var (b, _) = p.OnRoomMessage([a!], null, Msg(2, "owner", "@gpt-6-astra task B"), T0);
        ExchangePolicy.Started(a!, p.Due(a!, T0.AddSeconds(2), NoStarts, Nobody).First(d => d.ParticipantId == "opus"));

        var (c, _) = p.OnRoomMessage([a!, b!], null, Msg(3, "owner", "@opus redo A"), T0.AddSeconds(3));

        Assert.Equal(ExchangeStatus.Superseded, a!.Status);
        Assert.Empty(a.Pending);                     // sonnet's queued turn dropped
        Assert.Equal(["opus"], a.InFlight);          // still finishing
        Assert.Equal(ExchangeStatus.Open, b!.Status);
        Assert.Equal(["gpt-6-astra"], b.Pending.Keys);
        Assert.Equal(3, c!.RootMessageId);
        Assert.Equal(1, c.TurnsCommitted);           // fresh budget
    }

    [Fact]
    public void An_owner_post_with_no_spawnable_mention_supersedes_nothing()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus"), T0);
        var (none, notes) = p.OnRoomMessage([a!], null, Msg(2, "owner", "@claude thanks, carry on"), T0);
        Assert.Null(none);
        Assert.Empty(notes);
        Assert.Equal(ExchangeStatus.Open, a!.Status);
        Assert.Equal(["opus"], a.Pending.Keys);
    }

    [Fact]
    public void A_model_post_is_accepted_into_its_target_only_and_ignored_once_the_target_closed()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus"), T0);
        var (b, _) = p.OnRoomMessage([a!], null, Msg(2, "owner", "@gpt-6-astra"), T0);

        var (opened, _) = p.OnRoomMessage([a!, b!], a, Msg(3, "opus", "@sonnet your view?"), T0);
        Assert.Null(opened);
        Assert.Equal(["opus", "sonnet"], a!.Pending.Keys);
        Assert.Equal(["gpt-6-astra"], b!.Pending.Keys);

        ExchangePolicy.Stop(a, ExchangeStopCause.Owner);
        p.OnRoomMessage([a, b], a, Msg(4, "opus", "@fable too"), T0);
        Assert.Empty(a.Pending);
        Assert.Equal(["gpt-6-astra"], b.Pending.Keys);
    }

    [Fact]
    public void A_run_start_supersedes_every_open_exchange_in_the_room()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus"), T0);
        var (b, _) = p.OnRoomMessage([a!], null, Msg(2, "owner", "@gpt-6-astra"), T0);
        var (conductor, _) = p.OnRoomMessage([a!, b!], null, Msg(3, "owner", "/build-thing @sonnet"), T0,
            skill: new SkillResolution.Found(RunSkill, ""), startsRun: true, hasDirectory: true);
        Assert.NotNull(conductor);
        Assert.Equal(ExchangeStatus.Superseded, a!.Status);
        Assert.Equal(ExchangeStatus.Superseded, b!.Status);
    }

    [Fact]
    public void A_refused_run_start_supersedes_only_what_it_overlaps()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus"), T0);
        var (none, notes) = p.OnRoomMessage([a!], null, Msg(2, "owner", "/build-thing @sonnet"), T0,
            skill: new SkillResolution.Found(RunSkill, ""), startsRun: true, hasDirectory: false);
        Assert.Null(none);
        Assert.Single(notes);
        Assert.Equal(ExchangeStatus.Open, a!.Status);
    }

    [Fact]
    public void Participants_records_every_accepted_mention_including_a_launched_one()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus"), T0);
        ExchangePolicy.Started(a!, p.Due(a!, T0.AddSeconds(2), NoStarts, Nobody).Single());
        p.OnRoomMessage([a!], a, Msg(2, "opus", "@sonnet"), T0);
        Assert.Equal(new HashSet<string> { "opus", "sonnet" }, a!.Participants);
        Assert.Equal(new HashSet<string> { "conductor-x" }, ExchangePolicy.OpenForConductor("general", "conductor-x", 1, [1], T0, RunSkill).Participants);
    }

    [Fact]
    public void An_overlapping_owner_message_mid_exchange_supersedes_it_and_a_disjoint_or_empty_one_does_not()
    {
        var p = Policy();
        var (x, _) = p.OnMessage(null, Msg(1, "owner", "@opus @sonnet"), T0);
        var opus = p.Due(x!, T0.AddSeconds(2), NoStarts, Nobody).First(d => d.ParticipantId == "opus");
        ExchangePolicy.Started(x!, opus);

        var (y, _) = p.OnMessage(x, Msg(5, "owner", "@opus @gpt-6-astra instead"), T0.AddSeconds(3));
        Assert.Equal(ExchangeStatus.Superseded, x!.Status);
        Assert.Empty(x.Pending);                                                                       // sonnet dropped
        Assert.Equal(["opus"], x.InFlight);                                                            // still finishing
        Assert.NotSame(x, y);
        Assert.Equal(5, y!.RootMessageId);
        Assert.Equal(["opus", "gpt-6-astra"], y.Pending.Keys);

        p.OnMessage(x, Msg(6, "opus", "@sonnet late mention"), T0.AddSeconds(4));                       // its exchange is closed: ignored
        Assert.Empty(x.Pending);
        p.OnMessage(y, Msg(6, "opus", "@sonnet late mention"), T0.AddSeconds(4), acceptMentions: false);  // what the service passes for a stale spawn
        Assert.Equal(["opus", "gpt-6-astra"], y.Pending.Keys);
        Assert.Equal(2, y.TurnsCommitted);
        Assert.Null(ExchangePolicy.Finished(x, "opus"));                                                // no conclusion note for a superseded exchange
        Assert.Equal(ExchangeStatus.Superseded, x.Status);

        var (z, _) = p.OnMessage(y, Msg(7, "owner", "thanks, that is all"), T0.AddSeconds(5));
        Assert.Equal(ExchangeStatus.Open, y.Status);                                                   // no mention: nothing superseded
        Assert.Same(y, z);

        var (w, _) = p.OnMessage(y, Msg(8, "owner", "@fable something else"), T0.AddSeconds(6));
        Assert.Equal(ExchangeStatus.Open, y.Status);                                                   // disjoint: runs beside it
        Assert.NotSame(y, w);
    }

    [Fact]
    public void Stop_clears_pending_and_says_how_many_turns_ran()
    {
        var p = Policy();
        var (x, _) = p.OnMessage(null, Msg(1, "owner", "@opus @sonnet"), T0);
        ExchangePolicy.Started(x!, p.Due(x!, T0.AddSeconds(2), NoStarts, Nobody)[0]);
        var note = ExchangePolicy.Stop(x!, ExchangeStopCause.Owner);
        Assert.Equal(ExchangeStatus.Stopped, x!.Status);
        Assert.Empty(x.Pending);
        Assert.Equal(ExchangeStopCause.Owner, x.StopCause);
        Assert.Equal("Exchange stopped by the owner: 1 of 4 turns used.", note);
        Assert.Null(ExchangePolicy.Finished(x, "opus"));
    }

    // Row 27: the run-caused stop must not read as an owner stop, and StopCause must record why.
    [Fact]
    public void Stop_with_run_cause_does_not_attribute_the_stop_to_the_owner_and_sets_StopCause()
    {
        var p = Policy();
        var (x, _) = p.OnMessage(null, Msg(1, "owner", "@opus"), T0);
        ExchangePolicy.Started(x!, p.Due(x!, T0.AddSeconds(2), NoStarts, Nobody)[0]);
        var note = ExchangePolicy.Stop(x!, ExchangeStopCause.Run);
        Assert.Equal(ExchangeStatus.Stopped, x!.Status);
        Assert.Equal(ExchangeStopCause.Run, x.StopCause);
        Assert.Equal("Exchange stopped by the run: 1 of 4 turns used.", note);
        Assert.DoesNotContain("owner", note);
        Assert.DoesNotContain("you", note);
    }

    [Fact]
    public void M9_A10_an_exclusive_room_launches_one_pending_spawn_per_pass_and_nothing_while_one_is_in_flight()
    {
        var p = Policy();
        var (x, _) = p.OnMessage(null, Msg(10, "owner", "@opus @gpt-6-astra go"), T0);
        var later = T0 + Limits.Debounce;
        Assert.Equal(2, p.Due(x!, later, NoStarts, Nobody).Count);                          // a NULL-directory room: both
        var one = p.Due(x!, later, NoStarts, Nobody, exclusive: true);
        Assert.Equal(["opus"], one.Select(r => r.ParticipantId).ToArray());
        Assert.Equal(1, one[0].TurnNumber);
        Assert.Empty(p.Due(x!, later, NoStarts, new HashSet<string> { "opus" }, exclusive: true));
        Assert.Null(p.NextWake(x!, later, NoStarts, new HashSet<string> { "opus" }, exclusive: true));
        Assert.NotNull(p.NextWake(x!, T0, NoStarts, Nobody, exclusive: true));
        ExchangePolicy.Started(x!, one[0]);
        ExchangePolicy.Finished(x!, "opus");
        var next = p.Due(x!, later, NoStarts, Nobody, exclusive: true);
        Assert.Equal(["gpt-6-astra"], next.Select(r => r.ParticipantId).ToArray());
        Assert.Equal(2, next[0].TurnNumber);
    }

    // Row 35: exclusiveOver narrows what an exclusive exchange waits on to its own in-flight set
    // (a worktree exchange) rather than the whole room's (row 32's plain directory-room behaviour,
    // still the default when exclusiveOver is omitted).
    [Fact]
    public void Due_and_NextWake_take_exclusiveOver_to_narrow_exclusivity_to_one_exchange()
    {
        var p = Policy();
        var (x, _) = p.OnMessage(null, Msg(20, "owner", "@opus @gpt-6-astra go"), T0);
        var later = T0 + Limits.Debounce;
        var roomInFlight = new HashSet<string> { "sonnet" };   // a sibling exchange's own spawn, still running in the room

        Assert.Empty(p.Due(x!, later, NoStarts, roomInFlight, exclusive: true));                  // room-wide: blocked by sonnet
        Assert.Null(p.NextWake(x!, later, NoStarts, roomInFlight, exclusive: true));

        var overOwn = p.Due(x!, later, NoStarts, roomInFlight, exclusive: true, exclusiveOver: Nobody);
        Assert.Equal(["opus"], overOwn.Select(r => r.ParticipantId).ToArray());                   // narrowed to this exchange's own (empty) in-flight set
        Assert.NotNull(p.NextWake(x!, later, NoStarts, roomInFlight, exclusive: true, exclusiveOver: Nobody));

        var over = new HashSet<string> { "opus" };   // this exchange's own opus is in flight: still waits
        Assert.Empty(p.Due(x!, later, NoStarts, roomInFlight, exclusive: true, exclusiveOver: over));
        Assert.Null(p.NextWake(x!, later, NoStarts, roomInFlight, exclusive: true, exclusiveOver: over));
    }

    [Fact]
    public void Five_mentions_in_one_owner_message_commit_four_and_note_the_fifth()
    {
        var (x, notes) = Policy().OnMessage(null, Msg(1, "owner", "@opus @sonnet @fable @gpt-6-astra @gpt-5.5"), T0);
        Assert.Equal(4, x!.TurnsCommitted);
        Assert.Equal(["opus", "sonnet", "fable", "gpt-6-astra"], x.Pending.Keys);
        Assert.Contains("not spawning @gpt-5.5", Assert.Single(notes));
        Assert.All(Policy().Due(x, T0.AddSeconds(2), NoStarts, Nobody), d => Assert.Equal(0, d.RemainingAfter));
    }

    // --- Task 4: the exchange carries the skill in force ------------------------------------------

    private static readonly ResolvedSkill DemoSkill = new("demo", "Demo Skill", "Do the demo thing.", false);
    private static readonly ResolvedSkill TruncatedSkill = new("demo", "Demo Skill", "Do the demo thing.", true);

    [Fact]
    public void An_owner_post_with_a_found_skill_opens_an_exchange_carrying_it_and_notes_it_in_force()
    {
        var (x, notes) = Policy().OnMessage(null, Msg(1, "owner", "/demo @sonnet please begin"), T0,
            skill: new SkillResolution.Found(DemoSkill, "please begin"));
        Assert.NotNull(x);
        Assert.Same(DemoSkill, x!.Skill);
        Assert.Equal(["sonnet"], x.Pending.Keys);
        Assert.Contains(notes, n => n == "Skill /demo is in force for this exchange; every turn of it is rendered the same instruction.");
    }

    [Fact]
    public void A_truncated_skill_says_so_in_the_in_force_note()
    {
        var (_, notes) = Policy().OnMessage(null, Msg(1, "owner", "/demo @sonnet"), T0,
            skill: new SkillResolution.Found(TruncatedSkill, ""));
        Assert.Contains(notes, n => n.Contains($"Its text was cut to {SkillStore.MaxSkillChars} characters."));
    }

    [Fact]
    public void Unknown_tampered_and_unavailable_each_refuse_without_opening_an_exchange_but_still_supersede_one_that_was_open()
    {
        foreach (SkillResolution refusal in new SkillResolution[]
                 {
                     new SkillResolution.Unknown("nope", ["demo"]),
                     new SkillResolution.Tampered("demo"),
                     new SkillResolution.Unavailable("demo", "disk went away"),
                 })
        {
            var p = Policy();
            var (open, _) = p.OnMessage(null, Msg(1, "owner", "@opus"), T0);
            Assert.Equal(ExchangeStatus.Open, open!.Status);

            var (next, notes) = p.OnMessage(open, Msg(2, "owner", "/x @opus"), T0.AddSeconds(1), skill: refusal);
            Assert.Same(open, next);                                    // no NEW exchange opened
            Assert.Equal(ExchangeStatus.Superseded, open.Status);       // but the owner still spoke (D5)
            Assert.Empty(open.Pending);                                 // the overlap supersede cleared opus's queued turn
            var note = Assert.Single(notes);
            switch (refusal)
            {
                case SkillResolution.Unknown u:
                    Assert.Equal("No skill named '/nope'. Installed: /demo.", note);
                    break;
                case SkillResolution.Tampered t:
                    Assert.Equal("Skill /demo does not match what was imported; nothing was spawned. Re-import it with --import-skill before using it.", note);
                    break;
                case SkillResolution.Unavailable a:
                    Assert.Equal("Could not read skill /demo: disk went away. Nothing was spawned.", note);
                    break;
            }

            var (other, _) = p.OnMessage(null, Msg(3, "owner", "@opus"), T0);
            p.OnMessage(other, Msg(4, "owner", "/x @sonnet"), T0.AddSeconds(1), skill: refusal);
            Assert.Equal(ExchangeStatus.Open, other!.Status);                   // a disjoint refusal supersedes nothing
        }
    }

    [Fact]
    public void An_unknown_skill_with_no_skills_installed_says_so_rather_than_naming_none()
    {
        var (_, notes) = Policy().OnMessage(null, Msg(1, "owner", "/nope @sonnet"), T0,
            skill: new SkillResolution.Unknown("nope", []));
        Assert.Equal("No skill named '/nope'; this hub has no skills installed. Import one with --import-skill.", Assert.Single(notes));
    }

    [Fact]
    public void Found_with_no_mention_notes_and_opens_nothing()
    {
        var (x, notes) = Policy().OnMessage(null, Msg(1, "owner", "/demo"), T0, skill: new SkillResolution.Found(DemoSkill, ""));
        Assert.Null(x);
        Assert.Equal("/demo needs a mention to run: nobody was addressed, so no exchange started.", Assert.Single(notes));
    }

    [Fact]
    public void A_model_post_carrying_a_found_skill_never_opens_an_exchange()
    {
        // The service never actually produces this (only a human's post is resolved), but the policy's
        // own human-only gate is what guarantees it, and that is worth pinning directly.
        var (x, notes) = Policy().OnMessage(null, Msg(1, "codex", "/demo @sonnet"), T0, skill: new SkillResolution.Found(DemoSkill, ""));
        Assert.Null(x);
        Assert.Empty(notes);
    }

    [Fact]
    public void Budget_refusal_and_the_skill_in_force_note_can_both_appear()
    {
        var limited = new ExchangePolicy(ChopDb.SeedRoster, Limits with { Budget = 1 });
        var (x, notes) = limited.OnMessage(null, Msg(1, "owner", "/demo @opus @sonnet"), T0, skill: new SkillResolution.Found(DemoSkill, ""));
        Assert.Equal(["opus"], x!.Pending.Keys);
        Assert.Contains(notes, n => n.StartsWith("Skill /demo is in force"));
        Assert.Contains(notes, n => n.Contains("not spawning @sonnet"));
    }

    // --- Task 4 (row 19): starting a run, and every refusal at the start ---------------------------

    private static readonly ResolvedSkill RunSkill = new("build-thing", "Build Thing", "Build the thing.", false, IsRun: true);

    [Fact]
    public void A_human_post_inside_an_active_run_leaves_the_open_exchange_untouched()
    {
        var p = Policy();
        var (open, _) = p.OnMessage(null, Msg(1, "owner", "@opus go"), T0);
        Assert.Equal(ExchangeStatus.Open, open!.Status);

        var run = new RunContext(RunId: 7, ConductorId: "opus", CurrentPhase: "(start)");
        var (next, notes) = p.OnMessage(open, Msg(2, "owner", "@sonnet actually you"), T0.AddSeconds(1), run: run);

        Assert.Same(open, next);
        Assert.Equal(ExchangeStatus.Open, open.Status);          // NOT superseded - step 3 returns first
        Assert.DoesNotContain("sonnet", open.Pending.Keys);      // sonnet never accepted
        Assert.Empty(notes);
    }

    [Fact]
    public void A_run_start_invocation_needs_a_directory()
    {
        var (x, notes) = Policy().OnMessage(null, Msg(1, "owner", "/build-thing @opus begin"), T0,
            skill: new SkillResolution.Found(RunSkill, "begin"), startsRun: true, hasDirectory: false);
        Assert.Null(x);
        Assert.Equal("/build-thing starts a run, which needs a room bound to a directory; this room has none.", Assert.Single(notes));
    }

    [Fact]
    public void A_run_start_invocation_with_zero_or_many_conductors_refuses_naming_the_count()
    {
        var zero = Policy().OnMessage(null, Msg(1, "owner", "/build-thing begin"), T0,
            skill: new SkillResolution.Found(RunSkill, "begin"), startsRun: true, hasDirectory: true);
        Assert.Null(zero.Next);
        Assert.Equal("/build-thing starts a run and needs exactly one conductor mentioned; none was.", Assert.Single(zero.Notes));

        var many = Policy().OnMessage(null, Msg(1, "owner", "/build-thing @opus @sonnet begin"), T0,
            skill: new SkillResolution.Found(RunSkill, "begin"), startsRun: true, hasDirectory: true);
        Assert.Null(many.Next);
        Assert.Equal("/build-thing starts a run and needs exactly one conductor mentioned; 2 were: @opus, @sonnet.", Assert.Single(many.Notes));
    }

    [Fact]
    public void A_valid_run_start_invocation_opens_a_conductor_only_exchange_carrying_the_skill()
    {
        var (x, notes) = Policy().OnMessage(null, Msg(1, "owner", "/build-thing @opus begin"), T0,
            skill: new SkillResolution.Found(RunSkill, "begin"), startsRun: true, hasDirectory: true);
        Assert.NotNull(x);
        Assert.Equal(["opus"], x!.Pending.Keys);
        Assert.Same(RunSkill, x.Skill);
        Assert.Contains(notes, n => n.StartsWith("Skill /build-thing is in force"));
    }

    [Fact]
    public void The_supersede_gate_is_a_second_line_of_defence_behind_the_run_is_not_null_return()
    {
        // Load-bearing distinction (pass 2's F-23): step 3's early return is what actually protects
        // an active run's exchange; the `run is null` guard on the supersede below it can never by
        // itself be exercised through OnMessage, because step 3 always returns first when run is not
        // null. This test pins step 3 as the one doing the work (see the test above); the classic
        // outside-a-run supersede path (An_owner_message_mid_exchange_supersedes...) is the regression
        // that matters and stays covered by the pre-existing suite.
        var p = Policy();
        var (open, _) = p.OnMessage(null, Msg(1, "owner", "@opus go"), T0);
        var run = new RunContext(RunId: 7, ConductorId: "opus", CurrentPhase: "(start)");
        p.OnMessage(open, Msg(2, "owner", "never mind"), T0.AddSeconds(1), run: run);
        Assert.Equal(ExchangeStatus.Open, open!.Status);
    }

    // --- Task 5a (row 19): the hub re-spawning its run's conductor ----------------------------------

    [Fact]
    public void OpenForConductor_builds_a_one_turn_exchange_carrying_the_skill_and_every_trigger()
    {
        var x = ExchangePolicy.OpenForConductor("general", "opus", rootMessageId: 5, triggerIds: [3, 4], T0, RunSkill);
        Assert.Equal("general", x.RoomId);
        Assert.Equal(5, x.RootMessageId);
        Assert.Equal(1, x.Budget);
        Assert.Equal(1, x.TurnsCommitted);
        Assert.Equal(["opus"], x.Pending.Keys);
        Assert.Equal([3L, 4L], x.Pending["opus"].TriggerIds);
        Assert.Equal(T0, x.Pending["opus"].LastTriggerAt);
        Assert.Same(RunSkill, x.Skill);
        Assert.Equal(ExchangeStatus.Open, x.Status);
    }

    // --- Task 8 (row 19): the phase tag, the D8 class rules, and the refusal counter ---------------

    private static string? Refuse(ExchangePolicy p, string body, string conductor = "sonnet",
        Func<string, string?>? artifactAuthor = null, Func<string, bool>? artifactExists = null)
    {
        var msg = Msg(1, conductor, body);
        var run = new RunContext(RunId: 9, ConductorId: conductor, CurrentPhase: "(start)");
        return p.RefuseConductorPost(msg, run, p.MentionedSpawnable(msg), artifactAuthor ?? (_ => null), artifactExists ?? (_ => false));
    }

    [Fact]
    public void A_conductor_post_with_no_valid_phase_tag_is_refused()
    {
        var refusal = Refuse(Policy(), "just talking, no tag");
        Assert.NotNull(refusal);
        Assert.Contains("phase tag", refusal);
    }

    [Fact]
    public void A_conductor_post_mentioning_itself_is_refused()
    {
        var refusal = Refuse(Policy(), "phase: build @sonnet go");   // sonnet is both author and conductor here
        Assert.NotNull(refusal);
        Assert.Contains("itself", refusal);
    }

    [Fact]
    public void Ping_needs_no_mention_and_skips_every_other_rule()
    {
        Assert.Null(Refuse(Policy(), "phase: ping all done"));
    }

    [Fact]
    public void A_non_ping_phase_with_no_mention_is_refused()
    {
        var refusal = Refuse(Policy(), "phase: plan thinking out loud");
        Assert.NotNull(refusal);
        Assert.Contains("mention", refusal);
    }

    [Fact]
    public void Build_without_a_plumbing_or_visible_row_mentioned_is_refused()
    {
        // fable is judge-only; gpt-6-astra carries no classes at all.
        var refusal = Refuse(Policy(), "phase: build @gpt-6-astra go", conductor: "fable");
        Assert.NotNull(refusal);
        Assert.Contains("plumbing- or visible-class row", refusal);
        Assert.Contains("@opus, @sonnet", refusal);   // the qualifying rows, named
    }

    [Fact]
    public void Build_mentioning_a_plumbing_row_is_accepted()
    {
        Assert.Null(Refuse(Policy(), "phase: build @sonnet go", conductor: "fable"));   // sonnet: plumbing
    }

    [Fact]
    public void Build_mentioning_a_visible_row_is_accepted()
    {
        Assert.Null(Refuse(Policy(), "phase: build @opus go", conductor: "fable"));     // opus: visible,judge
    }

    // --- Row 20 task 3 (AC4b): a refusal names the rows that would have satisfied the rule ----------

    [Fact]
    public void A_refused_build_post_names_the_qualifying_rows()
    {
        var refusal = Refuse(Policy(), "phase: build @gpt-6-astra go", conductor: "fable");
        Assert.Equal("phase build needs a mention of a plumbing- or visible-class row: @opus, @sonnet", refusal);

        var noneClassed = ChopDb.SeedRoster.Select(p => p with { Classes = null }).ToList();
        var noneRefusal = Refuse(new ExchangePolicy(noneClassed, Limits), "phase: build @gpt-6-astra go", conductor: "fable");
        Assert.Equal("phase build needs a mention of a plumbing- or visible-class row: none is classed; set one with --set-classes", noneRefusal);
    }

    [Fact]
    public void Critique_without_an_artifact_line_is_refused()
    {
        var refusal = Refuse(Policy(), "phase: critique @opus have a look", conductor: "fable");
        Assert.NotNull(refusal);
        Assert.Contains("artifact:", refusal);
    }

    [Fact]
    public void Critique_naming_an_artifact_neither_recorded_nor_in_the_room_tree_is_refused()
    {
        var refusal = Refuse(Policy(), "phase: critique @opus have a look\nartifact: src/Foo.cs", conductor: "fable");
        Assert.NotNull(refusal);
        Assert.Contains("neither recorded", refusal);
    }

    [Fact]
    public void Critique_with_no_judge_mentioned_is_refused()
    {
        var refusal = Refuse(Policy(), "phase: critique @gpt-6-astra have a look\nartifact: src/Foo.cs", conductor: "fable",
            artifactAuthor: _ => "sonnet");
        Assert.NotNull(refusal);
        Assert.Contains("a judge mentioned other than the artifact's recorded author", refusal);
        Assert.Contains("@opus, @fable", refusal);   // the qualifying judge rows other than "sonnet"
    }

    [Fact]
    public void Critique_mentioning_only_the_artifacts_own_recorded_author_as_judge_is_refused()
    {
        var refusal = Refuse(Policy(), "phase: critique @opus have a look\nartifact: src/Foo.cs", conductor: "fable",
            artifactAuthor: _ => "opus");   // opus (judge) IS the recorded author
        Assert.NotNull(refusal);
        Assert.Contains("other than", refusal);
    }

    [Fact]
    public void A_valid_critique_naming_a_recorded_artifact_and_a_different_judge_is_accepted()
    {
        Assert.Null(Refuse(Policy(), "phase: critique @opus review this\nartifact: src/Foo.cs", conductor: "fable",
            artifactAuthor: _ => "sonnet"));
    }

    [Fact]
    public void An_artifact_present_in_the_room_tree_but_never_recorded_still_passes()
    {
        Assert.Null(Refuse(Policy(), "phase: critique @opus review this\nartifact: src/Foo.cs", conductor: "fable",
            artifactAuthor: _ => null, artifactExists: _ => true));
    }

    [Fact]
    public void Three_spellings_of_one_recorded_artifact_path_resolve_to_the_same_author()
    {
        var recorded = new Dictionary<string, string> { [RunStore.Normalize("src/Foo.cs")] = "sonnet" };
        string? Lookup(string p) => recorded.GetValueOrDefault(RunStore.Normalize(p));

        foreach (var spelling in new[] { "src/Foo.cs", "`src/Foo.cs`", "./src/Foo.cs", @"SRC\Foo.cs" })
        {
            var refusal = Refuse(Policy(), $"phase: critique @opus review this\nartifact: {spelling}", conductor: "fable", artifactAuthor: Lookup);
            Assert.Null(refusal);   // opus (judge) != sonnet (recorded author), whatever spelling named it
        }
    }

    [Fact]
    public void OpenForWorkers_seeds_the_mentioned_rows_as_pending_rooted_at_the_conductors_post_and_carries_no_skill()
    {
        var (x, notes) = Policy().OpenForWorkers("general", rootMessageId: 12, mentioned: ["sonnet", "opus"], T0);
        Assert.Equal("general", x.RoomId);
        Assert.Equal(12, x.RootMessageId);
        Assert.Equal(Limits.Budget, x.Budget);
        Assert.Equal(["sonnet", "opus"], x.Pending.Keys);
        Assert.Null(x.Skill);
        Assert.Empty(notes);
    }

    /// <summary>Pins the complete set of refusal arms (M-7): if a future arm is added to
    /// <see cref="SkillResolution"/> without this list being updated too, this test is the thing that
    /// catches it, since a plain switch statement does not fail to compile on a missing case.</summary>
    [Fact]
    public void Every_SkillResolution_arm_besides_None_and_Found_is_a_pinned_refusal()
    {
        var arms = typeof(SkillResolution).GetNestedTypes()
            .Where(t => t != typeof(SkillResolution.None) && t != typeof(SkillResolution.Found))
            .OrderBy(t => t.Name).ToList();
        var expected = new[] { typeof(SkillResolution.Tampered), typeof(SkillResolution.Unavailable), typeof(SkillResolution.Unknown) }
            .OrderBy(t => t.Name).ToList();
        Assert.Equal(expected, arms);
    }

    // --- Row 36: the policy joins a reply to its exchange -------------------------------------------

    private static Message Reply(long id, string author, string body, long replyTo) => new(id, "general", author, body, T0, replyTo);

    [Fact]
    public void R36_a_reply_to_an_open_exchange_joins_it_against_its_budget_and_supersedes_nothing()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus task A"), T0);
        ExchangePolicy.Started(a!, p.Due(a!, T0.AddSeconds(2), NoStarts, Nobody).Single());

        var (opened, notes) = p.OnRoomMessage([a!], null, Reply(2, "owner", "@opus @sonnet and also this", 1), T0.AddSeconds(3), joins: a);

        Assert.Null(opened);
        Assert.Empty(notes);
        Assert.Equal(ExchangeStatus.Open, a!.Status);                 // row 32 would have superseded it (opus overlaps)
        Assert.Equal(["opus", "sonnet"], a.Pending.Keys);
        Assert.Equal(3, a.TurnsCommitted);
        Assert.Contains(2L, a.MessageIds);
    }

    [Fact]
    public void R36_a_reply_with_a_mention_reopens_a_concluded_exchange_with_its_turns_and_skill()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "/build-thing @opus go"), T0, skill: new SkillResolution.Found(RunSkill, "go"));
        var launch = p.Due(a!, T0.AddSeconds(2), NoStarts, Nobody).Single();
        ExchangePolicy.Started(a!, launch);
        Assert.NotNull(ExchangePolicy.Finished(a!, "opus"));
        Assert.Equal(ExchangeStatus.Concluded, a!.Status);

        var (opened, notes) = p.OnRoomMessage([a!], null, Reply(5, "owner", "@opus keep going", 1), T0.AddSeconds(9), joins: a);

        Assert.Null(opened);
        Assert.Empty(notes);
        Assert.Equal(ExchangeStatus.Open, a.Status);
        Assert.Same(RunSkill, a.Skill);
        var next = p.Due(a, T0.AddSeconds(12), NoStarts, Nobody).Single();
        Assert.Equal((1L, 2, 2), (next.RootMessageId, next.TurnNumber, next.RemainingAfter));
    }

    [Fact]
    public void R36_a_reply_reopens_a_stopped_or_superseded_exchange_and_clears_the_stop_cause()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus task A"), T0);
        ExchangePolicy.Stop(a!, ExchangeStopCause.Owner);

        p.OnRoomMessage([a!], null, Reply(2, "owner", "@opus resume", 1), T0.AddSeconds(1), joins: a);

        Assert.Equal(ExchangeStatus.Open, a!.Status);
        Assert.Null(a.StopCause);
        Assert.Equal(["opus"], a.Pending.Keys);
    }

    [Fact]
    public void R36_a_reopen_spends_only_the_turns_that_ran_not_the_ones_a_stop_dropped()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus @sonnet @fable task"), T0);
        ExchangePolicy.Started(a!, p.Due(a!, T0.AddSeconds(2), NoStarts, Nobody).First(d => d.ParticipantId == "opus"));
        ExchangePolicy.Stop(a!, ExchangeStopCause.Owner);
        ExchangePolicy.Finished(a!, "opus");
        Assert.Equal((3, 1), (a!.TurnsCommitted, a.TurnsStarted));

        p.OnRoomMessage([a], null, Reply(2, "owner", "@sonnet go on", 1), T0.AddSeconds(5), joins: a);

        var next = p.Due(a, T0.AddSeconds(8), NoStarts, Nobody).Single();
        Assert.Equal((2, 2), (next.TurnNumber, next.RemainingAfter));
    }

    [Fact]
    public void R36_a_reply_to_a_closed_exchange_still_finishing_a_spawn_notes_it_and_accepts_nothing()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus task A"), T0);
        ExchangePolicy.Started(a!, p.Due(a!, T0.AddSeconds(2), NoStarts, Nobody).Single());
        p.OnRoomMessage([a!], null, Msg(2, "owner", "@opus redo"), T0.AddSeconds(3));   // supersedes a, opus still running
        Assert.Equal(ExchangeStatus.Superseded, a!.Status);

        var (opened, notes) = p.OnRoomMessage([a], null, Reply(3, "owner", "@sonnet help", 1), T0.AddSeconds(4), joins: a);

        Assert.Null(opened);
        Assert.Equal(["Exchange #1 is still finishing @opus; reply again once it has."], notes);
        Assert.Equal(ExchangeStatus.Superseded, a.Status);
        Assert.Empty(a.Pending);
    }

    [Fact]
    public void R36_a_reply_with_no_mention_changes_no_state()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus task A"), T0);
        ExchangePolicy.Started(a!, p.Due(a!, T0.AddSeconds(2), NoStarts, Nobody).Single());
        ExchangePolicy.Finished(a!, "opus");

        var (opened, notes) = p.OnRoomMessage([a!], null, Reply(2, "owner", "thanks", 1), T0.AddSeconds(3), joins: a);

        Assert.Null(opened);
        Assert.Empty(notes);
        Assert.Equal((ExchangeStatus.Concluded, 1, 0), (a!.Status, a.TurnsCommitted, a.Pending.Count));
        Assert.Contains(2L, a.MessageIds);
    }

    [Fact]
    public void R36_a_reply_whose_mentions_are_all_over_budget_does_not_reopen()
    {
        var p = new ExchangePolicy(ChopDb.SeedRoster, Limits with { Budget = 1 });
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus task A"), T0);
        ExchangePolicy.Started(a!, p.Due(a!, T0.AddSeconds(2), NoStarts, Nobody).Single());
        ExchangePolicy.Finished(a!, "opus");

        var (_, notes) = p.OnRoomMessage([a!], null, Reply(2, "owner", "@sonnet more", 1), T0.AddSeconds(3), joins: a);

        Assert.Equal(ExchangeStatus.Concluded, a!.Status);
        Assert.Single(notes, n => n.StartsWith("Budget of 1 turns is used up for the exchange started at #1"));
    }

    [Fact]
    public void R36_an_unresolved_reply_with_a_mention_is_a_new_prompt_with_a_note()
    {
        var p = Policy();
        var (opened, notes) = p.OnRoomMessage([], null, Reply(4, "owner", "@opus go", 2), T0, joins: null);

        Assert.Equal(4, opened!.RootMessageId);
        Assert.Equal(["Reply to #2: that message is in no exchange this hub still holds, so this post was handled as a new prompt."], notes);
    }

    [Fact]
    public void R36_an_unresolved_reply_with_no_mention_posts_nothing()
    {
        var (opened, notes) = Policy().OnRoomMessage([], null, Reply(4, "owner", "just a thought", 2), T0, joins: null);
        Assert.Null(opened);
        Assert.Empty(notes);
    }

    [Fact]
    public void R36_a_reply_that_invokes_a_skill_is_a_new_prompt_with_a_note_even_when_its_exchange_is_held()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus task A"), T0);

        var (opened, notes) = p.OnRoomMessage([a!], null, Reply(2, "owner", "/build-thing @sonnet go", 1), T0.AddSeconds(1),
            skill: new SkillResolution.Found(RunSkill, "go"), joins: a);

        Assert.Equal(2, opened!.RootMessageId);
        Assert.Equal("A reply that invokes /" + RunSkill.Name + " does not join an exchange; it was handled as a new prompt.", notes[0]);
        Assert.Equal(["opus"], a!.Pending.Keys);                        // disjoint: row 32's rule, untouched
    }

    [Fact]
    public void R36_a_reply_with_a_refused_skill_is_handled_as_the_same_post_without_reply_to()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus task A"), T0);

        var (opened, notes) = p.OnRoomMessage([a!], null, Reply(2, "owner", "/typo @opus go", 1), T0.AddSeconds(1),
            skill: new SkillResolution.Unknown("typo", []), joins: a);

        Assert.Null(opened);
        Assert.Equal(ExchangeStatus.Superseded, a!.Status);           // row 32's overlap rule, as without reply-to
        Assert.Single(notes);
        Assert.StartsWith("No skill named '/typo'", notes[0]);
    }

    [Fact]
    public void R36_a_reply_to_a_run_exchange_is_an_unresolved_reply()
    {
        var p = Policy();
        var conductor = ExchangePolicy.OpenForConductor("general", "opus", 1, [1], T0, RunSkill);
        ExchangePolicy.Started(conductor, p.Due(conductor, T0.AddSeconds(2), NoStarts, Nobody).Single());
        ExchangePolicy.Finished(conductor, "opus");
        conductor.MessageIds.Add(2);                                     // the conductor's own post, as the service records it

        var (opened, notes) = p.OnRoomMessage([conductor], null, Reply(3, "owner", "@sonnet pick this up", 2), T0.AddSeconds(5), joins: conductor);

        Assert.False(conductor.Joinable);
        Assert.Equal(ExchangeStatus.Concluded, conductor.Status);
        Assert.Equal(3, opened!.RootMessageId);
        Assert.True(opened.Joinable);
        Assert.Equal(["Reply to #2: that message is in no exchange this hub still holds, so this post was handled as a new prompt."], notes);
    }

    [Fact]
    public void R36_a_run_start_exchange_is_not_joinable()
    {
        var (opened, _) = Policy().OnRoomMessage([], null, Msg(1, "owner", "/build-thing @opus go"), T0,
            skill: new SkillResolution.Found(RunSkill, "go"), startsRun: true, hasDirectory: true);
        Assert.False(opened!.Joinable);
    }

    [Fact]
    public void R36_inside_a_run_a_reply_is_left_to_the_run()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus task A"), T0);

        var (opened, notes) = p.OnRoomMessage([a!], null, Reply(2, "owner", "@sonnet go", 1), T0.AddSeconds(1),
            run: new RunContext(7, "opus", "plan"), joins: a);

        Assert.Null(opened);
        Assert.Empty(notes);
        Assert.Equal(["opus"], a!.Pending.Keys);
    }

    [Fact]
    public void R36_an_exchange_counts_its_root_and_the_model_posts_routed_to_it_as_members()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus task A"), T0);
        p.OnRoomMessage([a!], a, Msg(2, "claude", "noted"), T0.AddSeconds(1));

        Assert.Equal([1L, 2L], a!.MessageIds.Order());
    }

    // --- Row 43 (D-b, D-c): only a leading mention addresses anyone; an inline id is a reference -----

    private static readonly ResolvedSkill GrillSkill = new("grill", "Grill", "Grill it.", false);

    [Fact]
    public void Row43_AC1_only_leading_mentions_open_an_exchange()
    {
        var (x, notes) = Policy().OnMessage(null, Msg(10, "owner", "@opus Please ask @gpt-5.6-sol one question"), T0);
        Assert.NotNull(x);
        Assert.Equal(["opus"], x!.Pending.Keys);
        Assert.Equal(1, x.TurnsCommitted);
        Assert.Empty(notes);
    }

    [Fact]
    public void Row43_AC2_an_inline_mention_alone_opens_nothing_and_is_noted_as_a_reference()
    {
        var (x, notes) = Policy().OnMessage(null, Msg(1, "owner", "please ask @opus something"), T0);
        Assert.Null(x);
        Assert.Equal("Nobody was addressed: @opus appears inside the text, so it was read as a reference. Start the message with @opus to send it.",
            Assert.Single(notes));
    }

    [Fact]
    public void Row43_AC2_a_bracket_prefix_is_prose()
    {
        var (x, notes) = Policy().OnMessage(null, Msg(1, "owner", "[usability trial] @sonnet describe"), T0);
        Assert.Null(x);
        Assert.Equal("Nobody was addressed: @sonnet appears inside the text, so it was read as a reference. Start the message with @sonnet to send it.",
            Assert.Single(notes));
    }

    [Fact]
    public void Row43_AC2_a_non_spawnable_recipient_is_addressed_so_no_reference_note()
    {
        var (x, notes) = Policy().OnMessage(null, Msg(1, "owner", "@claude what did @opus say?"), T0);
        Assert.Null(x);
        Assert.Empty(notes);
    }

    [Fact]
    public void Row43_AC2_a_skill_with_inline_only_carries_the_reference_on_its_idle_line()
    {
        var (x, notes) = Policy().OnMessage(null, Msg(1, "owner", "/grill see what @opus thinks"), T0,
            skill: new SkillResolution.Found(GrillSkill, "see what @opus thinks"));
        Assert.Null(x);
        Assert.Equal("/grill needs a mention to run: nobody was addressed, so no exchange started. @opus appears inside the text, so it was read as a reference.",
            Assert.Single(notes));
    }

    [Fact]
    public void Row43_AC2_an_unknown_skill_with_inline_only_gets_the_skill_note_alone()
    {
        var (x, notes) = Policy().OnMessage(null, Msg(1, "owner", "/nosuchskill see what @opus thinks"), T0,
            skill: new SkillResolution.Unknown("nosuchskill", ["demo"]));
        Assert.Null(x);
        Assert.Equal("No skill named '/nosuchskill'. Installed: /demo.", Assert.Single(notes));
    }

    [Fact]
    public void Row43_AC2_a_reply_that_joins_gets_no_reference_note()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus task A"), T0);

        var (opened, notes) = p.OnRoomMessage([a!], null, Reply(2, "owner", "thanks, @opus was right", 1), T0.AddSeconds(1), joins: a);

        Assert.Null(opened);
        Assert.Empty(notes);
        Assert.Contains(2L, a!.MessageIds);
    }

    [Fact]
    public void Row43_AC3_an_unknown_leading_word_is_noted_and_the_rest_still_act()
    {
        var (x, notes) = Policy().OnMessage(null, Msg(1, "owner", "@nobody @opus hi"), T0);
        Assert.Equal(["opus"], x!.Pending.Keys);
        var addressable = string.Join(", ", ChopDb.SeedRoster.Where(ExchangePolicy.IsSpawnable).Select(p => "@" + p.Id));
        Assert.Equal($"No participant named @nobody. Address one of: {addressable}.", Assert.Single(notes));
    }

    [Fact]
    public void Row43_AC3_hub_gets_its_own_sentence()
    {
        var (x, notes) = Policy().OnMessage(null, Msg(1, "owner", "@hub @opus hi"), T0);
        Assert.Equal(["opus"], x!.Pending.Keys);
        Assert.Equal("The hub cannot be addressed; it only posts notes.", Assert.Single(notes));
    }

    [Fact]
    public void Row43_AC3_a_model_author_is_not_noted()
    {
        var p = Policy();
        var (x, _) = p.OnMessage(null, Msg(1, "owner", "@opus"), T0);
        var (_, notes) = p.OnMessage(x, Msg(2, "opus", "@nobody @sonnet your view?"), T0);
        Assert.Contains("sonnet", x!.Pending.Keys);
        Assert.Empty(notes);
    }

    [Fact]
    public void Row43_AC4_a_model_reply_hands_on_only_when_leading()
    {
        var p1 = Policy();
        var (x1, _) = p1.OnMessage(null, Msg(1, "owner", "@opus"), T0);
        p1.OnMessage(x1, Msg(2, "opus", "@sonnet your view?"), T0);
        Assert.Equal(["opus", "sonnet"], x1!.Pending.Keys);

        var p2 = Policy();
        var (x2, _) = p2.OnMessage(null, Msg(1, "owner", "@opus"), T0);
        var (_, notes2) = p2.OnMessage(x2, Msg(2, "opus", "thanks, maybe @sonnet knows"), T0);
        Assert.Equal(["opus"], x2!.Pending.Keys);
        Assert.Equal(1, x2.TurnsCommitted);
        Assert.Empty(notes2);
    }

    [Fact]
    public void Row43_AC4_a_conductor_post_needs_the_mention_after_the_tag()
    {
        Assert.Null(Refuse(Policy(), "phase: build/x @sonnet do it", conductor: "fable"));

        var refusal = Refuse(Policy(), "phase: build/x\nSee @sonnet's note", conductor: "fable");
        Assert.NotNull(refusal);
        Assert.Contains("needs a mention of who does the work", refusal);
    }

    [Fact]
    public void Row43_D_d_referenced_spawnable_reads_the_whole_body()
    {
        Assert.Equal(["opus"], Policy().ReferencedSpawnable(Msg(1, "claude", "I agree with @opus")));
    }
}
