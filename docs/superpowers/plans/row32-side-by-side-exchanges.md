# Row 32: side-by-side exchanges in one room

**Goal:** Two owner prompts in one room that address different models run as two exchanges at once, each with its own budget, routing and stop.

**Architecture:** The spawner's per-room state goes from one `Exchange` to an ordered list of them. `ExchangePolicy` (pure) decides supersede by mention overlap instead of "any owner post", and a model post is routed to one target exchange the service picks (a spawn's own exchange, else the open exchange involving a model it mentions, else the newest open one). The newest exchange keeps every "current exchange" meaning the run machinery already relies on, so run behaviour is unchanged. The snapshot gains a per-exchange list and the API gains a per-exchange stop; the UI keeps rendering the top-level fields (now the newest open exchange) until row 34. A run room holds exactly one exchange, as before.

**Author model:** Opus 5. Session-model mismatch: HIGH planning routes to Fable. Critique pass 2 is mandatory.

**Blast radius:** HIGH. The spawner loop is the one launch path for every room, the change alters a binding ledger rule (D5: an owner post always closes the open exchange), and the run conductor shares the same per-room slot.

*Written for builder-subagent execution; if something doesn't match, STOP and report rather than guess.*

## Owner rulings this plan implements (2026-09-13)
- A new owner prompt runs alongside an open exchange when its spawnable mentions are disjoint from that exchange's participants. It supersedes an exchange it shares a mentioned participant with, and a fresh exchange (fresh budget) takes its place.
- A post that mentions nobody supersedes nothing. Stop buttons end exchanges.
- One exchange bar per exchange, each with its own stop (API here, UI in row 34).
- Directory rooms: worktree per exchange with auto-merge (row 35). Until then a directory room keeps one spawn at a time across all its exchanges.
- Reply-to picks the exchange (row 36).

Interpretations named (critique pass 1): an exchange's participants are every model it ever accepted a turn for, so a finished model still ties its exchange to a later prompt that mentions it; an app-backed post that mentions models from two open exchanges puts all its mentions into the newest overlapping one, which makes those models that exchange's participants too. Run rooms keep launching only from their newest exchange, exactly as the one-exchange loop did.

Orchestrator defaults (reversible): a run-start supersedes every open exchange in the room; an app-backed model post (no spawn handle) joins the open exchange that involves a model it mentions, else the newest open one; the per-exchange stop answers 409 while a run is active or parked in the room (the run's own stop stays the one control, row 22).

## Acceptance
1. WHEN an owner posts a prompt whose spawnable mentions are disjoint from every open exchange's participants THE SYSTEM SHALL open a new exchange and leave each open exchange open with its pending and in-flight spawns untouched.
2. WHEN an owner prompt mentions a participant of an open exchange THE SYSTEM SHALL supersede that exchange (its running spawn finishes, its queue is dropped, that spawn's later mentions buy nothing) and open a fresh exchange with a full budget, while open exchanges it does not overlap stay open.
3. WHEN an owner posts with no spawnable mention outside a run THE SYSTEM SHALL leave every open exchange open.
4. WHEN a spawn posts THE SYSTEM SHALL apply its mentions only to the exchange that launched it, and only while that exchange is open. WHEN an app-backed model posts THE SYSTEM SHALL apply its mentions to the open exchange involving a model it mentions, else the newest open exchange.
5. WHEN two exchanges are open in a room with no directory THE SYSTEM SHALL run their spawns concurrently. WHEN the room has a directory THE SYSTEM SHALL run at most one spawn at a time across all its exchanges.
6. WHEN `GET /api/rooms/{id}/exchange` is called or `ExchangeChanged` is broadcast THE SYSTEM SHALL include an `exchanges` list, one entry per retained exchange with its own root id, status, budget, turns, in-flight, pending and stop cause, while the existing top-level fields describe the newest open exchange, else the newest.
7. WHEN the owner calls `POST /api/rooms/{id}/exchanges/{rootMessageId}/stop` THE SYSTEM SHALL stop only that exchange and kill only its own in-flight spawns, answer 404 for an unknown room or root id, 409 when that exchange has nothing to stop or a run is active or parked in the room, and keep the room-level stop stopping everything.
8. WHEN a run starts THE SYSTEM SHALL supersede every open exchange in the room, and every existing run test SHALL pass unchanged.

## Lessons consulted
- **M24** (a timing-dependent guard test binds nothing): every rule gets a pure `ExchangePolicy` test, and the service tests keep the real path. The mutation pass in Verification reverts each mechanism once.
- **M25** (a recovery control must be reachable in every state it exists for): the per-exchange stop works for a superseded exchange whose spawn is still running, not only for an open one (Task 3 test).
- **M27** (a note naming an actor takes the actor): every stop goes through `ExchangePolicy.Stop(x, cause)` with an explicit cause. No new actor-naming note builder is added.
- **M28** (name the call sites of a changed signature): Task 1 keeps `ExchangePolicy.OnMessage(Exchange? current, …)` as a one-exchange wrapper so its 42 test call sites compile. Task 2 lists every `_rooms` use it rewrites.
- **M9/M16** (cross-room UI, stop button): the top-level snapshot fields and the room-level stop are unchanged, so the shipped UI keeps working until row 34.

## Could not verify in this environment
- Live behaviour with real CLIs (two real spawns side by side). Covered by the fake runner here; the Phase B deploy self-check runs the M4 self-check only. A live two-model check is not run (spend) and is named in the ping.
- Until row 34 ships, the web UI's one Stop button calls the room-level stop, which now ends every open exchange in the room, and the bar shows only the newest open exchange. Rows 34 (per-exchange bars), 35 (worktrees) and 36 (reply-to) go on the board in the same commit as this plan, 34 next. README says the room-level stop ends every exchange.
- Two "Exchange concluded: n of 4 turns used." notes carry no root id either. Their text is unchanged because the M5 live check and a run test match it exactly.
- The owner's reading of the unchanged stop note text when two exchanges stop at once ("Exchange stopped by the owner: n of 4 turns used." appears once per exchange with no root id). Accepted for this row; row 34's bars carry the distinction.

## Claim ledger
| # | Claim | Verified at (commit) | Recheck (pwsh, exit 0 = holds) |
|---|-------|----------------------|--------------------------------|
| 1 | Baseline: 931 tests green (224 Core + 707 Hub), measured this session at b42d7aa; `src/` and `tests/` unchanged since | df26ad0 | `git diff --quiet b42d7aa HEAD -- src tests` |
| 2 | Per-room state is one exchange: `private readonly Dictionary<string, Exchange> _rooms` | df26ad0 | `if (Select-String -LiteralPath src/ChopItUp.Hub/Spawning/SpawnerService.cs -SimpleMatch 'private readonly Dictionary<string, Exchange> _rooms' -Quiet) { exit 0 } else { exit 1 }` |
| 3 | The human branch supersedes any open exchange: `current.Status = ExchangeStatus.Superseded;` in `ExchangePolicy.OnMessage` | df26ad0 | `if (Select-String -LiteralPath src/ChopItUp.Hub/Spawning/ExchangePolicy.cs -SimpleMatch 'current.Status = ExchangeStatus.Superseded;' -Quiet) { exit 0 } else { exit 1 }` |
| 4 | A spawn's mentions count only while its exchange is current: `acceptMentions = ReferenceEquals(handle.Exchange, current);` | df26ad0 | `if (Select-String -LiteralPath src/ChopItUp.Hub/Spawning/SpawnerService.cs -SimpleMatch 'acceptMentions = ReferenceEquals(handle.Exchange, current);' -Quiet) { exit 0 } else { exit 1 }` |
| 5 | Directory exclusivity reads the room's whole in-flight set: `if (exclusive && inFlightInRoom.Count > 0) return [];` and `LaunchDue` recomputes `InFlightIn(x.RoomId)` per exchange | df26ad0 | `if ((Select-String -LiteralPath src/ChopItUp.Hub/Spawning/ExchangePolicy.cs -SimpleMatch 'if (exclusive && inFlightInRoom.Count > 0) return [];' -Quiet) -and (Select-String -LiteralPath src/ChopItUp.Hub/Spawning/SpawnerService.cs -SimpleMatch 'var inRoom = InFlightIn(x.RoomId);' -Quiet)) { exit 0 } else { exit 1 }` |
| 6 | Every non-GET `/api` request needs a bearer (blanket by method, not a route list) | df26ad0 | `if (Select-String -LiteralPath src/ChopItUp.Hub/Security/BearerTokenMiddleware.cs -SimpleMatch 'return !HttpMethods.IsGet(request.Method)' -Quiet) { exit 0 } else { exit 1 }` |
| 7 | `ExchangeSnapshot` ends `long Seq = 0, string? StoppedBy = null);` | df26ad0 | `if (Select-String -LiteralPath src/ChopItUp.Hub/Spawning/SpawnerService.cs -SimpleMatch 'long Seq = 0, string? StoppedBy = null);' -Quiet) { exit 0 } else { exit 1 }` |
| 8 | Five tests pin the old rule by name | df26ad0 | `$n = @(Select-String -Path tests/ChopItUp.Hub.Tests/Spawning/*.cs -Pattern 'An_owner_message_mid_exchange_supersedes_it_and_roots_a_new_one\|Unknown_tampered_and_unavailable_each_refuse_without_opening_an_exchange_but_still_supersede_one_that_was_open\|A6_an_owner_message_mid_exchange_lets_the_running_spawn_finish_ignores_its_mentions_and_re_roots\|A6_stop_reaches_a_spawn_whose_exchange_was_superseded_and_the_room_shows_it_until_then\|Run06_outside_a_run_an_owner_post_still_supersedes').Count; if ($n -eq 5) { exit 0 } else { exit 1 }` |
| 9 | Test fixture seams exist: `RunSkill` in ExchangePolicyTests, `PostAs`/`PostAsOwner`/`PostAsOwnerIn`/`MakeRoom`/`WaitForStatus` in SpawnerServiceTests, `FakeProcessRunner.HangUntilKilled`/`NoSpecWithin`/`ParticipantOf` | df26ad0 | `if ((Select-String -Path tests/ChopItUp.Hub.Tests/Spawning/ExchangePolicyTests.cs -SimpleMatch 'private static readonly ResolvedSkill RunSkill' -Quiet) -and (Select-String -Path tests/ChopItUp.Hub.Tests/Spawning/SpawnerServiceTests*.cs -SimpleMatch 'Task PostAsOwnerIn(' -Quiet) -and (Select-String -Path tests/ChopItUp.Hub.Tests/Spawning/FakeProcessRunner.cs -SimpleMatch 'HangUntilKilled' -Quiet)) { exit 0 } else { exit 1 }` |
| 10 | The run code reads "the room's exchange" at exactly these `_rooms` sites: OnMessage, AssembleRunState, OpenWorkersExchange, ParkRun, EndRun, OpenConductorExchange, LaunchDue, OnFinished, OnStop, ArmWake (twice), Publish | df26ad0 | `$n = @(Select-String -LiteralPath src/ChopItUp.Hub/Spawning/SpawnerService.cs -Pattern '_rooms\b' \| Where-Object { $_.Line -notmatch '^\s*//' }).Count; if ($n -ge 14 -and $n -le 18) { exit 0 } else { exit 1 }` |
| 11 | README documents the room-level stop at line ~96 | df26ad0 | `if (Select-String -LiteralPath README.md -SimpleMatch 'api/rooms/general/exchange/stop' -Quiet) { exit 0 } else { exit 1 }` |
| 12 | The run tests stay green with no edit (AC8): no Runs test references a second concurrent non-run exchange | — | — (critic: read SpawnerServiceTests.Runs.cs for owner prompts that rely on disjoint supersede outside a run; Run06 is the one known) |

## Tasks

Commands used by every task (repo root `C:\Agent Projects\ChopItUp`):
```powershell
dotnet build ChopItUp.slnx -c Debug -warnaserror -v minimal
dotnet test tests/ChopItUp.Hub.Tests -c Debug --nologo -v minimal --filter "FullyQualifiedName~ExchangePolicyTests"
dotnet test tests/ChopItUp.Hub.Tests -c Debug --nologo -v minimal --filter "FullyQualifiedName~SpawnerServiceTests|FullyQualifiedName~ExchangeApiTests"
dotnet test ChopItUp.slnx -c Debug --nologo -v minimal
```

### Task 1: the policy decides supersede by overlap and routes a model post to one target (sonnet)

Files: `src/ChopItUp.Hub/Spawning/Exchange.cs`, `src/ChopItUp.Hub/Spawning/ExchangePolicy.cs`, `tests/ChopItUp.Hub.Tests/Spawning/ExchangePolicyTests.cs`.

**1a. RED.** Add these tests to `ExchangePolicyTests` (they do not compile until 1b, which counts as RED; run the filter and record the compile errors):

```csharp
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
        var (none, notes) = p.OnRoomMessage([a!], null, Msg(2, "owner", "thanks @claude, carry on"), T0);
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
```

If `RunSkill` is declared after these tests in the file, that is fine (static field). If `OpenForConductor` rejects `"conductor-x"` for any reason, use `"opus"` and adjust the expected set; do not change production code for it.

**1b. GREEN.** In `Exchange.cs`, add to `Exchange` after `InFlight`:

```csharp
    /// <summary>Every participant this exchange ever accepted a turn for, launched or still pending.
    /// An owner prompt that mentions one of them supersedes the exchange; a prompt that mentions none of
    /// them runs beside it.</summary>
    public HashSet<string> Participants { get; } = new(StringComparer.Ordinal);
```

In `ExchangePolicy.cs`:

1. Replace the class summary's D2 sentence "D2: only an owner message opens an exchange, and an owner message always closes the one that was open." with "D2: only an owner message opens an exchange. An owner prompt closes only the open exchanges it shares a mentioned participant with; the rest keep running beside the new one."
2. Replace the whole `OnMessage` method (its summary and body) with the two methods below.
3. In `Accept`, after `x.Pending[id] = fresh;` add `x.Participants.Add(id);`, and change the budget note's last sentence from `A new owner message starts a fresh exchange.` to `An owner message that mentions one of them starts a fresh exchange.` (if a test asserts the old sentence, update it and say so).
4. In `OpenForConductor`, after `x.Pending[conductorId] = pending;` add `x.Participants.Add(conductorId);`.

```csharp
    /// <summary>The room's exchanges after this message, and the notes to post. Returns the exchange
    /// this message opened, or null. <paramref name="room"/> is every exchange the service still holds
    /// for the room, open or not; only open ones are considered. <paramref name="target"/> is where a
    /// model's mentions go and is ignored for a human post: the service picks it (a spawn's own
    /// exchange, else the open exchange involving a model it mentions, else the newest open one).
    /// A human prompt supersedes each open exchange it shares a mentioned participant with before any
    /// refusal is decided, since the owner spoke to that model either way; a run-start that passes
    /// every check supersedes all of them.</summary>
    public (Exchange? Opened, IReadOnlyList<string> Notes) OnRoomMessage(IReadOnlyList<Exchange> room, Exchange? target, Message message, DateTimeOffset now,
        bool acceptMentions = true, SkillResolution? skill = null, RunContext? run = null, bool startsRun = false, bool hasDirectory = false)
    {
        var notes = new List<string>();
        if (!_roster.TryGetValue(message.AuthorId, out var author) || author.Kind == "system") return (null, notes);

        // acceptMentions = false: the author is a spawn of an exchange that is closed, or (inside a run)
        // no longer the room's newest one; its post lands, its mentions do not.
        var mentioned = acceptMentions
            ? _mentions.Find(message.Body)
                .Where(id => id != message.AuthorId && _roster.TryGetValue(id, out var p) && IsSpawnable(p))
                .ToList()
            : new List<string>();

        if (author.Kind != "human")
        {
            // A model, spawn row or app-backed window, never opens an exchange.
            if (target is not { Status: ExchangeStatus.Open }) return (null, notes);
            Accept(target, mentioned, message.Id, now, notes);
            return (null, notes);
        }

        // A post inside an active run that is not itself a valid run-start leaves everything untouched:
        // the service records it as a steer for the conductor's next trigger set.
        if (run is not null) return (null, notes);

        var open = room.Where(x => x.Status == ExchangeStatus.Open).ToList();
        foreach (var x in open.Where(x => mentioned.Any(x.Participants.Contains))) Supersede(x);

        // A run-start invocation needs a directory to bind the run to.
        if (startsRun && !hasDirectory)
        {
            var name = (skill as SkillResolution.Found)?.Skill.Name
                ?? throw new ArgumentException("startsRun requires a Found skill.", nameof(skill));
            notes.Add($"/{name} starts a run, which needs a room bound to a directory; this room has none.");
            return (null, notes);
        }

        // A skill the hub cannot hand over intact spends nothing. Any overlap supersede above
        // still stands.
        switch (skill)
        {
            case SkillResolution.Unknown u:
                notes.Add(u.Known.Count == 0
                    ? $"No skill named '/{u.Name}'; this hub has no skills installed. Import one with --import-skill."
                    : $"No skill named '/{u.Name}'. Installed: {string.Join(", ", u.Known.Select(k => "/" + k))}.");
                return (null, notes);
            case SkillResolution.Tampered t:
                notes.Add($"Skill /{t.Name} does not match what was imported; nothing was spawned. Re-import it with --import-skill before using it.");
                return (null, notes);
            case SkillResolution.Unavailable a:
                notes.Add($"Could not read skill /{a.Name}: {a.Reason}. Nothing was spawned.");
                return (null, notes);
        }

        // A run needs exactly one conductor; zero or many are both refused.
        if (startsRun && mentioned.Count != 1)
        {
            var name = (skill as SkillResolution.Found)?.Skill.Name
                ?? throw new ArgumentException("startsRun requires a Found skill.", nameof(skill));
            notes.Add(mentioned.Count == 0
                ? $"/{name} starts a run and needs exactly one conductor mentioned; none was."
                : $"/{name} starts a run and needs exactly one conductor mentioned; {mentioned.Count} were: {string.Join(", ", mentioned.Select(m => "@" + m))}.");
            return (null, notes);
        }

        if (mentioned.Count == 0)
        {
            // A skill with nobody to run it: say so, or the owner watches an invocation do nothing.
            if (skill is SkillResolution.Found idle)
                notes.Add($"/{idle.Skill.Name} needs a mention to run: nobody was addressed, so no exchange started.");
            return (null, notes);
        }

        // A run takes the whole room: nothing started before it keeps spawning beside the conductor.
        if (startsRun)
            foreach (var x in open.Where(x => x.Status == ExchangeStatus.Open)) Supersede(x);

        var found = skill as SkillResolution.Found;
        var next = new Exchange
        {
            RoomId = message.RoomId, RootMessageId = message.Id, Budget = _limits.Budget,
            Skill = found?.Skill,
        };
        if (found is not null)
            notes.Add($"Skill /{found.Skill.Name} is in force for this exchange; every turn of it is rendered the same instruction."
                + (found.Skill.Truncated ? $" Its text was cut to {SkillStore.MaxSkillChars} characters." : ""));
        Accept(next, mentioned, message.Id, now, notes);
        return (next, notes);
    }

    /// <summary>The one-exchange view of <see cref="OnRoomMessage"/>: <paramref name="current"/> is both
    /// the room's only exchange and a model post's target. Returns <paramref name="current"/> itself
    /// unless the message opened a new exchange.</summary>
    public (Exchange? Next, IReadOnlyList<string> Notes) OnMessage(Exchange? current, Message message, DateTimeOffset now, bool acceptMentions = true, SkillResolution? skill = null,
        RunContext? run = null, bool startsRun = false, bool hasDirectory = false)
    {
        var (opened, notes) = OnRoomMessage(current is null ? [] : [current], current, message, now, acceptMentions, skill, run, startsRun, hasDirectory);
        return (opened ?? current, notes);
    }

    /// <summary>The running spawn finishes (its completion lands on this object, which
    /// is no longer open, so it cannot conclude or spawn); everything queued is dropped.</summary>
    private static void Supersede(Exchange x)
    {
        x.Status = ExchangeStatus.Superseded;
        x.Pending.Clear();
    }
```

The old `OnMessage` had an `artifactAuthor` parameter that its body never read. Before deleting it, run `Select-String -Path src,tests -Recurse -Include *.cs -Pattern 'artifactAuthor:'`. Any hit that passes it to `OnMessage` by name: STOP and report. (Hits on `RefuseConductorPost`'s own parameter are expected and fine.)

**1c. Rewrite the two policy tests that pin the old rule.**

Replace `An_owner_message_mid_exchange_supersedes_it_and_roots_a_new_one` whole with:

```csharp
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
```

In `Unknown_tampered_and_unavailable_each_refuse_without_opening_an_exchange_but_still_supersede_one_that_was_open`, change the refused post body from `"/x @sonnet"` to `"/x @opus"` and the comment `// sonnet was never accepted` to `// the overlap supersede cleared opus's queued turn`. Then append inside the `foreach`, after the `switch`, this disjoint check:

```csharp
            var (other, _) = p.OnMessage(null, Msg(3, "owner", "@opus"), T0);
            p.OnMessage(other, Msg(4, "owner", "/x @sonnet"), T0.AddSeconds(1), skill: refusal);
            Assert.Equal(ExchangeStatus.Open, other!.Status);                   // a disjoint refusal supersedes nothing
```

If a refusal's note assertion inside the `switch` names `@sonnet`, keep the note assertion as it is: refusal notes name the skill, not the mention. If it does name the mention, STOP and report.

**1d.** Run the ExchangePolicyTests filter: all green. Then run the full Hub test project once for information. The service still calls the one-exchange wrapper, so through the service a disjoint or empty owner post no longer supersedes but the new exchange still replaces the old one in `_rooms`. That half-state is Task 2's to finish, so failures in `SpawnerServiceTests*.cs` and `ExchangeApiTests.cs` are expected here: record their names, do not fix them. A failure in any OTHER test file is a STOP. Commit with the message `Row 32 task 1: supersede by mention overlap in the exchange policy` and a body line `Service tests stay red until the next commit wires the spawner.` Tasks 1 and 2 are dispatched to one builder back to back, the full-suite gate is Task 2's, and the PR is squash-merged, so `main` never holds the red commit.

### Task 2: the spawner holds a list of exchanges per room and routes posts to one (sonnet)

Blocked by Task 1. Files: `src/ChopItUp.Hub/Spawning/SpawnerService.cs`, `tests/ChopItUp.Hub.Tests/Spawning/SpawnerServiceTests.cs`, `tests/ChopItUp.Hub.Tests/Spawning/SpawnerServiceTests.Rooms.cs`, `tests/ChopItUp.Hub.Tests/Spawning/SpawnerServiceTests.Runs.cs`.

**2a. RED.** Add to `SpawnerServiceTests.cs` (inside the main partial class that holds `A5_…`):

```csharp
    [Fact]
    public async Task R32_two_disjoint_owner_prompts_run_side_by_side_and_each_spawn_feeds_its_own_exchange()
    {
        var releaseOpus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGpt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, ct) =>
        {
            switch (FakeProcessRunner.ParticipantOf(spec))
            {
                case "opus":
                    await releaseOpus.Task.WaitAsync(ct);
                    await PostAs("opus", "@sonnet check my work");
                    break;
                case "gpt-5.5":
                    await releaseGpt.Task.WaitAsync(ct);
                    await PostAs("gpt-5.5", "done");
                    break;
                default:
                    await PostAs(FakeProcessRunner.ParticipantOf(spec), "ok");
                    break;
            }
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostAsOwner("@opus task A");
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(await _runner.NextSpecAsync(Wait)));
        await PostAsOwner("@gpt-5.5 task B");
        Assert.Equal("gpt-5.5", FakeProcessRunner.ParticipantOf(await _runner.NextSpecAsync(Wait)));   // both in flight at once

        foreach (var _ in Enumerable.Range(0, 100))                                     // the launch's Publish can trail the queued spec
        {
            if (Spawner.Snapshot("general").Exchanges!.Sum(e => e.InFlight.Count) == 2) break;
            await Task.Delay(50);
        }
        var both = Spawner.Snapshot("general");
        Assert.Equal(["open", "open"], both.Exchanges!.Select(e => e.Status));
        Assert.Equal(new[] { "opus", "gpt-5.5" }, both.Exchanges!.SelectMany(e => e.InFlight).ToArray());   // each exchange owns its one spawn

        releaseOpus.SetResult();
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(await _runner.NextSpecAsync(Wait)));
        var mid = Spawner.Snapshot("general");
        var first = mid.Exchanges!.Single(e => e.RootMessageId != mid.RootMessageId);
        var second = mid.Exchanges!.Single(e => e.RootMessageId == mid.RootMessageId);
        Assert.Equal(2, first.TurnsCommitted);                                          // opus + sonnet, in A
        Assert.Equal(1, second.TurnsCommitted);                                         // B untouched

        releaseGpt.SetResult();
        foreach (var _ in Enumerable.Range(0, 100))
        {
            if (Spawner.Snapshot("general").Exchanges!.All(e => e.Status == "concluded")) break;
            await Task.Delay(50);
        }
        Assert.All(Spawner.Snapshot("general").Exchanges!, e => Assert.Equal("concluded", e.Status));
        Assert.Equal(2, (await Messages()).Count(m => m.Body.StartsWith("Exchange concluded")));

        await PostAsOwner("@fable task C");                                             // opening a third prunes the two closed, spawn-less ones
        Assert.Equal("fable", FakeProcessRunner.ParticipantOf(await _runner.NextSpecAsync(Wait)));
        Assert.Single(Spawner.Snapshot("general").Exchanges!);
    }

    [Fact]
    public async Task R32_an_app_backed_post_joins_the_open_exchange_involving_a_model_it_mentions()
    {
        // Hang on the cancellation token only: the fixture's 1 s timeout must never close an exchange mid-test.
        _runner.Handler = async (_, _, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
            return new ProcessResult(null, false, true, "", "", TimeSpan.Zero);
        };
        await PostAsOwner("@opus task A");
        await _runner.NextSpecAsync(Wait);
        await PostAsOwner("@gpt-5.5 task B");
        await _runner.NextSpecAsync(Wait);

        await PostAs("claude", "@opus a thought for you, and @sonnet too");
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(await _runner.NextSpecAsync(Wait)));   // sonnet launches in A
        ExchangeView ViewAt(int i) => Spawner.Snapshot("general").Exchanges![i];
        foreach (var _ in Enumerable.Range(0, 100)) { if (ViewAt(0).TurnsCommitted == 3) break; await Task.Delay(50); }
        Assert.Equal(3, ViewAt(0).TurnsCommitted);                                      // opus, then opus again (queued behind its own spawn) and sonnet
        Assert.Contains("opus", ViewAt(0).Pending);
        Assert.Equal(1, ViewAt(1).TurnsCommitted);                                      // B untouched

        await PostAs("codex", "@fable anyone?");                                        // no overlap: the newest open exchange
        Assert.Equal("fable", FakeProcessRunner.ParticipantOf(await _runner.NextSpecAsync(Wait)));
        foreach (var _ in Enumerable.Range(0, 100)) { if (ViewAt(1).TurnsCommitted == 2) break; await Task.Delay(50); }
        Assert.Equal(2, ViewAt(1).TurnsCommitted);
        Assert.Equal(3, ViewAt(0).TurnsCommitted);
        await Spawner.StopAsync("general");
    }
```

`Exchanges`, `ExchangeView.TurnsCommitted` and friends come from Task 3's snapshot view, but Task 2 needs them to observe per-exchange state, so Task 2 adds the view (step 2b.10) and Task 3 only adds the stop. Fable is a spawnable row with a model in the seed roster; if `PostAs("codex", …)` or `PostAs("claude", …)` is refused because those rows have no token in `HubTestHost`, STOP and report.

Add to `SpawnerServiceTests.Rooms.cs`, next to `M9_A10_…`:

```csharp
    [Fact]
    public async Task R32_a_directory_room_runs_one_spawn_at_a_time_across_two_exchanges()
    {
        await MakeRoom("lab");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, ct) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "opus") await release.Task.WaitAsync(ct);
            return FakeProcessRunner.Ok("""{"type":"result","result":"done"}""");
        };

        await PostAsOwnerIn("lab", "@opus task A");
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(await _runner.NextSpecAsync(Wait)));
        await PostAsOwnerIn("lab", "@gpt-6-astra task B");
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromSeconds(1)));                    // B waits for A's spawn
        Assert.Equal(["open", "open"], Spawner.Snapshot("lab").Exchanges!.Select(e => e.Status));
        release.SetResult();
        Assert.Equal("gpt-6-astra", FakeProcessRunner.ParticipantOf(await _runner.NextSpecAsync(Wait)));
    }
```

Add to `SpawnerServiceTests.Runs.cs` (a turn left queued in an older run exchange never launches, during the run or after it ends):

```csharp
    [Fact]
    public async Task R32_a_run_room_never_launches_a_turn_left_queued_in_an_older_run_exchange()
    {
        WriteSkill("build-thing", RunSkillMd);
        await MakeRoom("lab-r32-orphan");
        int sonnetRuns = 0, opusRuns = 0;
        _runner.Handler = async (spec, _, ct) =>
        {
            switch (FakeProcessRunner.ParticipantOf(spec))
            {
                case "sonnet" when Interlocked.Increment(ref sonnetRuns) == 1:
                    await PostAsIn("sonnet", "lab-r32-orphan", "phase: build @opus write it");
                    break;
                case "sonnet":
                    await PostAsIn("sonnet", "lab-r32-orphan", "phase: build @opus again");
                    break;
                case "opus" when Interlocked.Increment(ref opusRuns) == 1:
                    await PostAsIn("opus", "lab-r32-orphan", "@sonnet @fable done");
                    break;
                case "opus":
                    try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
                    break;
            }
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };
        await PostAsOwnerIn("lab-r32-orphan", "/build-thing @sonnet begin");
        foreach (var who in new[] { "sonnet", "opus", "sonnet", "opus" })
            Assert.Equal(who, FakeProcessRunner.ParticipantOf(await _runner.NextSpecAsync(Wait)));
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(500)));   // fable never launches while the run is active
        await PostAsOwnerIn("lab-r32-orphan", "/stop");
        await WaitForMessageIn("lab-r32-orphan", m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("ended"));
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromSeconds(1)));          // nor after it ends
    }
```

This test's exact spec order is derived, not executed. If the order differs but fable never launches, adjust only the `foreach` sequence and say so in the report. If fable launches, STOP and report. Task 3's helper-name STOP rule applies here too.

Run the SpawnerServiceTests filter: record the compile errors (RED).

**2b. GREEN.** In `SpawnerService.cs`:

1. Change the field to `private readonly Dictionary<string, List<Exchange>> _rooms = new(StringComparer.Ordinal);` with the comment `// Every exchange the loop still holds per room, oldest first. The newest is "the room's exchange" for the run machinery and the top-level snapshot.`
2. Add these helpers directly below `InFlightIn`:

```csharp
    private IReadOnlyList<Exchange> ExchangesIn(string roomId) =>
        _rooms.TryGetValue(roomId, out var list) ? list : [];

    /// <summary>The room's most recently opened exchange: what a run means by the room's current
    /// exchange, and what the snapshot's top-level fields describe.</summary>
    private Exchange? Newest(string roomId) =>
        _rooms.TryGetValue(roomId, out var list) && list.Count > 0 ? list[^1] : null;

    /// <summary>Appends <paramref name="x"/> and drops every exchange that is closed with nothing left
    /// in flight, so a closed exchange stays visible until the next one opens. <paramref name="replaceAll"/>
    /// is for the run's own exchanges: a run room holds exactly one exchange, as the loop did before rooms
    /// held several, so an older run exchange still open with queued entries is never launched, and its
    /// spawn's later mentions count for nothing (it is no longer in the room's list).</summary>
    private void AddExchange(string roomId, Exchange x, bool replaceAll = false)
    {
        if (!_rooms.TryGetValue(roomId, out var list)) _rooms[roomId] = list = new List<Exchange>();
        if (replaceAll) list.Clear();
        else list.RemoveAll(e => e.Status != ExchangeStatus.Open && e.InFlight.Count == 0);
        list.Add(x);
    }

    /// <summary>What the snapshot's top-level fields describe: the newest OPEN exchange, else the newest
    /// one. The shipped exchange bar reads only those fields, so a concluded newest exchange must not
    /// hide an older one that is still running.</summary>
    private Exchange? Displayed(string roomId) =>
        ExchangesIn(roomId).LastOrDefault(e => e.Status == ExchangeStatus.Open) ?? Newest(roomId);

    /// <summary>Where an app-backed model's post goes (no spawn handle): the newest open exchange that
    /// involves a model it mentions, else the newest open exchange, else none.</summary>
    private Exchange? AppBackedTarget(string roomId, Message m)
    {
        var open = ExchangesIn(roomId).Where(e => e.Status == ExchangeStatus.Open).Reverse().ToList();
        var mentioned = _policy.MentionedSpawnable(m);
        return open.FirstOrDefault(e => mentioned.Any(e.Participants.Contains)) ?? open.FirstOrDefault();
    }
```

3. In `OnMessage`, replace from `_rooms.TryGetValue(m.RoomId, out var current);` through the `acceptMentions` block (the `if (_inFlight.TryGetValue(...)) { ... }`) with the block below, and DELETE the later line `var activeRun = _runs.Active(m.RoomId);` (it moves up here; `now`, `skill`, `startsRun` and `run` stay where they are, below):

```csharp
        var exchanges = ExchangesIn(m.RoomId);
        var newest = Newest(m.RoomId);
        var activeRun = _runs.Active(m.RoomId);
        // A spawn's post belongs to the exchange that launched it. Outside a run its mentions count
        // while that exchange is open and still held for the room, so two side-by-side exchanges never
        // feed each other and a run exchange dropped when the run moved on stays mute after the run
        // ends. Inside a run
        // the room keeps one current exchange (the newest): a conductor's later post, after it rooted
        // a workers exchange, is prose.
        bool acceptMentions = true;
        Exchange? target;
        if (_inFlight.TryGetValue((m.RoomId, m.AuthorId), out var handle))
        {
            handle.Posted = true;
            target = handle.Exchange;
            acceptMentions = activeRun is null
                ? handle.Exchange.Status == ExchangeStatus.Open && exchanges.Contains(handle.Exchange)
                : ReferenceEquals(handle.Exchange, newest);
        }
        else target = activeRun is not null ? newest : AppBackedTarget(m.RoomId, m);
```

4. Replace

```csharp
        var (next, notes) = _policy.OnMessage(current, m, now, acceptMentions, skill, run, startsRun, hasDirectory);
        if (next is null) _rooms.Remove(m.RoomId); else _rooms[m.RoomId] = next;
        foreach (var note in notes) PostNote(m.RoomId, note);
        if (next is not null || current is not null) Publish(m.RoomId);

        if (startsRun && next is not null && !ReferenceEquals(next, current))
        {
            // The policy just built the conductor's first exchange (steps 4-7 all passed: a
            // directory, a resolved skill, exactly one mention). Persist the run itself.
            var conductorId = next.Pending.Keys.Single();
```

with

```csharp
        var hadExchanges = exchanges.Count > 0;
        var (opened, notes) = _policy.OnRoomMessage(exchanges, target, m, now, acceptMentions, skill, run, startsRun, hasDirectory);
        if (opened is not null) AddExchange(m.RoomId, opened);
        foreach (var note in notes) PostNote(m.RoomId, note);
        if (opened is not null || hadExchanges) Publish(m.RoomId);

        if (startsRun && opened is not null)
        {
            // The policy just built the conductor's first exchange (a directory, a resolved skill,
            // exactly one mention). Persist the run itself.
            var conductorId = opened.Pending.Keys.Single();
```

The steer comment below it says "ExchangePolicy already left `current` untouched above"; change `current` to `the room's exchanges`.

5. `AssembleRunState`: `ExchangeOpen: _rooms.TryGetValue(run.RoomId, out var x) && x.Status == ExchangeStatus.Open,` becomes `ExchangeOpen: Newest(run.RoomId) is { Status: ExchangeStatus.Open },`.
6. `OpenWorkersExchange` and `OpenConductorExchange`: `_rooms[run.RoomId] = exchange;` becomes `AddExchange(run.RoomId, exchange, replaceAll: true);`.
7. `ParkRun` and `EndRun`: `if (_rooms.TryGetValue(run.RoomId, out var x) && x.Status == ExchangeStatus.Open)` becomes `if (Newest(run.RoomId) is { Status: ExchangeStatus.Open } x)`.
8. `LaunchDue`: `foreach (var x in _rooms.Values.ToList())` becomes `foreach (var x in _rooms.Values.SelectMany(list => list).ToList())`. `ArmWake`: `foreach (var x in _rooms.Values)` becomes `foreach (var x in _rooms.Values.SelectMany(list => list))`. A run room's list holds exactly one exchange (step 6), so this launches and wakes for exactly what the one-exchange loop did there. In `ArmWake`, its `busy` line's `(_rooms.TryGetValue(run.RoomId, out var rx) && rx.Status == ExchangeStatus.Open)` becomes `Newest(run.RoomId) is { Status: ExchangeStatus.Open }`.
9. `OnFinished`: `ReferenceEquals(h.Exchange, _rooms.GetValueOrDefault(room))` becomes `ReferenceEquals(h.Exchange, Newest(room))`.
10. Snapshot view. Add above `ExchangeSnapshot`:

```csharp
/// <summary>One exchange as the UI and the API see it: its own root, status, budget, turns,
/// in-flight and pending. <see cref="InFlight"/> is this exchange's own live spawns, unlike the
/// snapshot's room-wide list.</summary>
public sealed record ExchangeView(
    long RootMessageId, string Status, int Budget, int TurnsUsed, int TurnsCommitted, int Remaining,
    IReadOnlyList<string> InFlight, IReadOnlyList<string> Pending, string? StoppedBy);
```

Change `ExchangeSnapshot`'s last line to `IReadOnlyList<string> InFlight, IReadOnlyList<string> Pending, long Seq = 0, string? StoppedBy = null, IReadOnlyList<ExchangeView>? Exchanges = null);`, and add to its summary: "`Exchanges` lists every exchange the room still holds, oldest first; the top-level fields describe the newest open one, else the newest."

Replace `Publish`'s body with:

```csharp
    private ExchangeSnapshot Publish(string roomId)
    {
        // InFlight is the ROOM's live spawns (a superseded exchange's spawn included), not the newest
        // exchange's list; Seq lets row 16 order a GET against an event (critique pass 2, M1, m10).
        var views = ExchangesIn(roomId).Select(View).ToList();
        var snapshot = (Displayed(roomId) is { } x
            ? new ExchangeSnapshot(roomId, x.Status.ToString().ToLowerInvariant(), x.RootMessageId, x.Budget, x.TurnsStarted, x.TurnsCommitted,
                Math.Max(0, x.Budget - x.TurnsCommitted), InFlightIn(roomId).Order(StringComparer.Ordinal).ToList(), x.Pending.Keys.ToList(),
                StoppedBy: x.StopCause?.ToString().ToLowerInvariant())
            : Idle(roomId)) with { Seq = ++_seq, Exchanges = views };
        _snapshots[roomId] = snapshot;
        BroadcastAsync(roomId, snapshot);
        return snapshot;
    }

    private static ExchangeView View(Exchange x) => new(
        x.RootMessageId, x.Status.ToString().ToLowerInvariant(), x.Budget, x.TurnsStarted, x.TurnsCommitted,
        Math.Max(0, x.Budget - x.TurnsCommitted), x.InFlight.Order(StringComparer.Ordinal).ToList(), x.Pending.Keys.ToList(),
        x.StopCause?.ToString().ToLowerInvariant());
```

and `Idle` becomes `private static ExchangeSnapshot Idle(string roomId) => new(roomId, "idle", null, 0, 0, 0, 0, [], [], Exchanges: []);`

11. `OnStop`'s no-run branch (the room-level stop): replace from `_rooms.TryGetValue(roomId, out var x);` to the end of the method with:

```csharp
        var open = ExchangesIn(roomId).Where(e => e.Status == ExchangeStatus.Open).ToList();
        var live = _inFlight.Values.Where(h => h.Request.RoomId == roomId).ToList();
        if (open.Count == 0 && live.Count == 0) return null;
        foreach (var handle in live) handle.Cancel.Cancel();
        if (open.Count == 0)
            PostNote(roomId, $"Exchange stopped by the owner: {live.Count} running spawn(s) of an earlier exchange stopped.");
        foreach (var x in open) PostNote(roomId, ExchangePolicy.Stop(x, ExchangeStopCause.Owner));
        return Publish(roomId);
```

Update `OnStop`'s summary sentence "closes the open exchange if there is one" to "closes every open exchange".

After these edits, `Select-String -LiteralPath src/ChopItUp.Hub/Spawning/SpawnerService.cs -Pattern '_rooms\['` returns exactly one hit, inside `AddExchange`, and every remaining `_rooms` use is inside `ExchangesIn`, `Newest`, `AddExchange`, `LaunchDue` or `ArmWake`.

**2c. Rewrite the three service tests that pin the old rule.**

`A6_an_owner_message_mid_exchange_lets_the_running_spawn_finish_ignores_its_mentions_and_re_roots` becomes the overlap case. Replace the whole test with:

```csharp
    [Fact]
    public async Task A6_an_overlapping_owner_message_mid_exchange_lets_the_running_spawn_finish_ignores_its_mentions_and_re_roots()
    {
        var releaseOpus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGpt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opusRuns = 0;
        _runner.Handler = async (spec, _, ct) =>
        {
            switch (FakeProcessRunner.ParticipantOf(spec))
            {
                case "opus" when Interlocked.Increment(ref opusRuns) == 1:
                    await releaseOpus.Task.WaitAsync(ct);
                    await PostAs("opus", "late: @sonnet @fable please");
                    break;
                case "opus":
                    await PostAs("opus", "second take, done");
                    break;
                case "gpt-5.5":
                    await releaseGpt.Task.WaitAsync(ct);   // keeps the NEW exchange open while the old opus posts late
                    await PostAs("gpt-5.5", "taken, done");
                    break;
            }
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostAsOwner("@opus think slowly");
        await _runner.NextSpecAsync(Wait);
        await PostAsOwner("@opus @gpt-5.5 actually, both of you");
        var gpt = await _runner.NextSpecAsync(Wait);
        Assert.Equal("gpt-5.5", FakeProcessRunner.ParticipantOf(gpt));                // the new opus waits for the old one
        var snap = Spawner.Snapshot("general");
        Assert.Equal("open", snap.Status);
        Assert.Equal(2, snap.RootMessageId);
        Assert.Equal(2, snap.TurnsCommitted);
        Assert.Equal("superseded", snap.Exchanges![0].Status);

        releaseOpus.SetResult();
        var opus2 = await _runner.NextSpecAsync(Wait);
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(opus2));                 // exchange 2's own opus turn
        await Task.Delay(300);
        var after = Spawner.Snapshot("general");
        Assert.Equal(2, after.TurnsCommitted);                                        // the old opus's @sonnet @fable bought nothing (B2)
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(500)));

        releaseGpt.SetResult();
        var final = await WaitForStatus("concluded");
        Assert.Equal(2, final.RootMessageId);
        Assert.Equal(2, final.TurnsUsed);
        Assert.Single(await Messages(), m => m.Body.StartsWith("Exchange concluded"));
    }
```

If `WaitForStatus` returns before the newest exchange concludes because it matches on the top-level status only, that is the intended top-level behaviour; keep it.

In `A6_stop_reaches_a_spawn_whose_exchange_was_superseded_and_the_room_shows_it_until_then`: change the comment's `then "never mind" (no mention)` to `then "/nope @opus" (an unknown skill naming opus)` and the post `await PostAsOwner("never mind");` to `await PostAsOwner("/nope @opus");`. Nothing else changes: an unknown skill supersedes the overlapping exchange and opens nothing, which is exactly the state the test needs. If the test host has a skill named `nope`, STOP and report.

In `Run06_outside_a_run_an_owner_post_still_supersedes`: rename to `Run06_outside_a_run_an_overlapping_owner_post_still_supersedes`, and change `await PostAsOwner("never mind");` to `await PostAsOwner("/nope @opus");`.

**2d.** Run the SpawnerServiceTests and ExchangeApiTests filters, then the full suite. Expected: 931 + 7 (Task 1) + 4 (Task 2) = 942 green. Then stress the new async tests ten times against the built binaries and record the pass count (expected 10 of 10): `$pass = 0; for ($i = 0; $i -lt 10; $i++) { dotnet test tests/ChopItUp.Hub.Tests -c Debug --nologo -v minimal --no-build --filter "FullyQualifiedName~R32_|FullyQualifiedName~A6_"; if ($LASTEXITCODE -eq 0) { $pass++ } }; "passes: $pass/10"`. Any other failing test, in particular any test in `SpawnerServiceTests.Runs.cs`: STOP and report with the failure text. Do not edit a run test. Commit: `Row 32 task 2: hold and route side-by-side exchanges in the spawner`.

### Task 3: the owner can stop one exchange (sonnet)

Blocked by Task 2. Files: `src/ChopItUp.Hub/Spawning/SpawnerService.cs`, `src/ChopItUp.Hub/Web/ExchangeApi.cs`, `README.md`, `tests/ChopItUp.Hub.Tests/Spawning/SpawnerServiceTests.cs`, `tests/ChopItUp.Hub.Tests/ExchangeApiTests.cs`.

**3a. RED.** Add to `SpawnerServiceTests.cs`:

```csharp
    [Fact]
    public async Task R32_stopping_one_exchange_kills_only_its_own_spawn_and_reaches_a_superseded_one()
    {
        // Hang on the cancellation token only, so the list records cancellations and never the fixture's 1 s timeout.
        var cancelled = new List<string>();
        _runner.Handler = async (spec, _, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { lock (cancelled) cancelled.Add(FakeProcessRunner.ParticipantOf(spec)); }
            return new ProcessResult(null, false, true, "", "", TimeSpan.Zero);
        };
        await PostAsOwner("@opus task A");
        await _runner.NextSpecAsync(Wait);
        await PostAsOwner("@gpt-5.5 task B");
        await _runner.NextSpecAsync(Wait);
        var snap = Spawner.Snapshot("general");
        var a = snap.Exchanges![0].RootMessageId;
        var b = snap.Exchanges![1].RootMessageId;

        Assert.Equal(ExchangeStopOutcome.NotFound, (await Spawner.StopExchangeAsync("general", 999_999)).Outcome);

        var (outcome, afterA) = await Spawner.StopExchangeAsync("general", a);
        Assert.Equal(ExchangeStopOutcome.Stopped, outcome);
        Assert.Equal("stopped", afterA!.Exchanges!.Single(e => e.RootMessageId == a).Status);
        Assert.Equal("open", afterA.Exchanges!.Single(e => e.RootMessageId == b).Status);
        foreach (var _ in Enumerable.Range(0, 100)) { lock (cancelled) if (cancelled.Count > 0) break; await Task.Delay(50); }
        lock (cancelled) Assert.Equal(["opus"], cancelled);

        foreach (var _ in Enumerable.Range(0, 100))
        {
            if (Spawner.Snapshot("general").InFlight.SequenceEqual(["gpt-5.5"])) break;
            await Task.Delay(50);
        }
        Assert.Equal(ExchangeStopOutcome.NothingToStop, (await Spawner.StopExchangeAsync("general", a)).Outcome);

        // A superseded exchange with a live spawn is still stoppable on its own.
        await PostAsOwner("/nope @gpt-5.5");
        foreach (var _ in Enumerable.Range(0, 100))
        {
            if (Spawner.Snapshot("general").Exchanges!.Single(e => e.RootMessageId == b).Status == "superseded") break;
            await Task.Delay(50);
        }
        Assert.Equal("superseded", Spawner.Snapshot("general").Exchanges!.Single(e => e.RootMessageId == b).Status);
        Assert.Equal(ExchangeStopOutcome.Stopped, (await Spawner.StopExchangeAsync("general", b)).Outcome);
        foreach (var _ in Enumerable.Range(0, 100)) { lock (cancelled) if (cancelled.Count > 1) break; await Task.Delay(50); }
        lock (cancelled) Assert.Equal(["opus", "gpt-5.5"], cancelled);
    }
```

Add to `ExchangeApiTests.cs`:

```csharp
    [Fact]
    public async Task R32_the_snapshot_lists_each_exchange_and_the_per_exchange_stop_answers_200_404_409()
    {
        _runner.Handler = (_, timeout, ct) => FakeProcessRunner.HangUntilKilled(timeout, ct);
        Assert.Equal(0, (await Get("general")).GetProperty("exchanges").GetArrayLength());

        var first = await _host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "@fable task A" });
        await _runner.NextSpecAsync(TimeSpan.FromSeconds(15));
        var second = await _host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "@opus task B" });
        await _runner.NextSpecAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var snap = await Get("general");
        var exchanges = snap.GetProperty("exchanges").EnumerateArray().ToList();
        Assert.Equal(2, exchanges.Count);
        var rootA = exchanges[0].GetProperty("rootMessageId").GetInt64();
        Assert.Equal(["fable"], exchanges[0].GetProperty("inFlight").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(snap.GetProperty("rootMessageId").GetInt64(), exchanges[1].GetProperty("rootMessageId").GetInt64());   // top level = newest

        Assert.Equal(HttpStatusCode.NotFound, (await _host.Client.PostAsync("api/rooms/general/exchanges/999999/stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Client.PostAsync($"api/rooms/nope/exchanges/{rootA}/stop", null)).StatusCode);
        var stop = await _host.Client.PostAsync($"api/rooms/general/exchanges/{rootA}/stop", null);
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        using var body = JsonDocument.Parse(await stop.Content.ReadAsStringAsync());
        Assert.Equal("stopped", body.RootElement.GetProperty("exchanges")[0].GetProperty("status").GetString());
        Assert.Equal("owner", body.RootElement.GetProperty("exchanges")[0].GetProperty("stoppedBy").GetString());
        Assert.Equal(4, body.RootElement.GetProperty("exchanges")[1].GetProperty("budget").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("exchanges")[1].GetProperty("pending").GetArrayLength());
        Assert.Equal("open", body.RootElement.GetProperty("exchanges")[1].GetProperty("status").GetString());

        foreach (var _ in Enumerable.Range(0, 100))
        {
            if ((await Get("general")).GetProperty("inFlight").GetArrayLength() == 1) break;
            await Task.Delay(50);
        }
        Assert.Equal(HttpStatusCode.Conflict, (await _host.Client.PostAsync($"api/rooms/general/exchanges/{rootA}/stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _host.Client.PostAsync("api/rooms/general/exchange/stop", null)).StatusCode);   // room-level stop still stops the rest
    }
```

If the message-post response carries the new message id, you may read root ids from it instead of the snapshot; do not assume it does.

**3b. GREEN.** In `SpawnerService.cs`:

Add next to `ExchangeView`:

```csharp
/// <summary>What a per-exchange stop found. The API maps NotFound to 404, NothingToStop and
/// RunOwnsRoom to 409, Stopped to 200 with the snapshot.</summary>
public enum ExchangeStopOutcome { Stopped, NotFound, NothingToStop, RunOwnsRoom }
```

Add the event record beside `StopEvent`:

```csharp
    private sealed record StopOneEvent(string RoomId, long RootMessageId, TaskCompletionSource<(ExchangeStopOutcome, ExchangeSnapshot?)> Reply) : Event;
```

Add the public method after `StopAsync(string roomId)`:

```csharp
    /// <summary>The owner's stop for ONE exchange: kills that exchange's own in-flight spawns,
    /// closes it if it is still open, and leaves every other exchange in the room alone. Works on a
    /// closed exchange whose spawn is still running. Refused while a run is active or parked here: the
    /// run's own stop is the one control there.</summary>
    public async Task<(ExchangeStopOutcome Outcome, ExchangeSnapshot? Snapshot)> StopExchangeAsync(string roomId, long rootMessageId)
    {
        var reply = new TaskCompletionSource<(ExchangeStopOutcome, ExchangeSnapshot?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_events.Writer.TryWrite(new StopOneEvent(roomId, rootMessageId, reply))) return (ExchangeStopOutcome.NothingToStop, null);
        return await reply.Task;
    }
```

In `Handle`, add after the `StopEvent` case:

```csharp
            case StopOneEvent one:
                (ExchangeStopOutcome, ExchangeSnapshot?) oneReply = (ExchangeStopOutcome.NothingToStop, null);
                try { oneReply = OnStopOne(one.RoomId, one.RootMessageId); }
                finally { one.Reply.TrySetResult(oneReply); }   // a throw here must not hang the HTTP caller
                break;
```

Add after `OnStop`:

```csharp
    private (ExchangeStopOutcome, ExchangeSnapshot?) OnStopOne(string roomId, long rootMessageId)
    {
        if (_runs.Active(roomId) is not null || _runs.Latest(roomId) is { Status: RunStatus.Parked })
            return (ExchangeStopOutcome.RunOwnsRoom, null);
        var x = ExchangesIn(roomId).LastOrDefault(e => e.RootMessageId == rootMessageId);
        if (x is null) return (ExchangeStopOutcome.NotFound, null);
        var live = _inFlight.Values.Where(h => ReferenceEquals(h.Exchange, x)).ToList();
        var open = x.Status == ExchangeStatus.Open;
        if (!open && live.Count == 0) return (ExchangeStopOutcome.NothingToStop, null);
        foreach (var handle in live) handle.Cancel.Cancel();
        PostNote(roomId, open
            ? ExchangePolicy.Stop(x, ExchangeStopCause.Owner)
            : $"Exchange stopped by the owner: {live.Count} running spawn(s) of an earlier exchange stopped.");
        return (ExchangeStopOutcome.Stopped, Publish(roomId));
    }
```

In `ExchangeApi.cs`, map `api.MapPost("/rooms/{roomId}/exchanges/{rootMessageId:long}/stop", StopOneExchange);` after the existing stop, update the class summary's last sentence to add "`POST .../exchanges/{rootMessageId}/stop` stops one exchange and leaves the rest of the room running.", and add:

```csharp
    private static async Task<IResult> StopOneExchange(string roomId, long rootMessageId, MessageStore store, SpawnerService spawner)
    {
        if (!store.RoomExists(roomId)) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        var (outcome, snapshot) = await spawner.StopExchangeAsync(roomId, rootMessageId);
        return outcome switch
        {
            ExchangeStopOutcome.Stopped => Results.Json(snapshot),
            ExchangeStopOutcome.NotFound => Results.NotFound(new { error = $"No exchange rooted at #{rootMessageId} in this room." }),
            ExchangeStopOutcome.RunOwnsRoom => Results.Conflict(new { error = "A run owns this room; stop the run instead." }),
            ExchangeStopOutcome.NothingToStop => Results.Conflict(new { error = "Nothing to stop in that exchange: it is closed and has no running spawn." }),
            var other => throw new InvalidOperationException($"Unhandled ExchangeStopOutcome {other}."),
        };
    }
```

In `README.md`, directly after the line containing `api/rooms/general/exchange/stop` and its code fence, add two sentences in the same style: "That stop ends every exchange in the room. To stop one exchange and leave the others in the room running, POST to `/api/rooms/<room>/exchanges/<root message id>/stop` (the id is in the `exchanges` list of `GET /api/rooms/<room>/exchange`)." Read the surrounding README lines first and match their format (fence or inline). No process labels.

Add to `SpawnerServiceTests.Runs.cs` (it parks through the main fixture by the two-bad-posts route `Run08_F2…` uses, so both halves hit the same host):

```csharp
    [Fact]
    public async Task R32_the_per_exchange_stop_refuses_while_a_run_is_active_or_parked()
    {
        WriteSkill("build-thing", RunSkillMd);
        await MakeRoom("lab-r32-stop");
        var misbehave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, ct) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet")
            {
                await misbehave.Task.WaitAsync(ct);   // the in-run timeout is RunLimits' 30 min, not the fixture's 1 s
                await PostAsIn("sonnet", "lab-r32-stop", "no phase tag at all, first bad post");
                await PostAsIn("sonnet", "lab-r32-stop", "still no phase tag, second bad post");
            }
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };
        await PostAsOwnerIn("lab-r32-stop", "/build-thing @sonnet begin");
        await _runner.NextSpecAsync(Wait);
        var root = Spawner.Snapshot("lab-r32-stop").RootMessageId!.Value;
        Assert.Equal(RunStatus.Active, Runs.Active("lab-r32-stop")!.Status);
        Assert.Equal(ExchangeStopOutcome.RunOwnsRoom, (await Spawner.StopExchangeAsync("lab-r32-stop", root)).Outcome);

        misbehave.SetResult();
        await WaitForMessageIn("lab-r32-stop", m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("parked"));
        Assert.Equal(RunStatus.Parked, Runs.Latest("lab-r32-stop")!.Status);
        Assert.Equal(ExchangeStopOutcome.RunOwnsRoom, (await Spawner.StopExchangeAsync("lab-r32-stop", root)).Outcome);
    }
```

If any of `WriteSkill`, `RunSkillMd`, `PostAsIn`, `WaitForMessageIn` or `Runs` does not exist under that name in the Runs partial class, STOP and report the names that do.

**3c.** Run both filters, then the full suite: 942 + 3 = 945 green. Stress the new async tests ten times with the 2d loop (filter `FullyQualifiedName~R32_`), expected `passes: 10/10`. Commit: `Row 32 task 3: stop one exchange without stopping the room`.

## Ticket graph
`01-supersede-by-overlap` → `02-spawner-holds-many-exchanges` → `03-stop-one-exchange`. Linear; no parallel batch.

## Verification (orchestrator, Phase B)
1. Preflight: `Check-PlanClaims.ps1` against this plan.
2. Full suite green at 945, build 0 warnings.
3. Mutation pass (M24), each reverted after recording, no commit: (a) in `OnRoomMessage` change the overlap supersede to supersede every open exchange: `A_disjoint_owner_prompt…` and `R32_two_disjoint…` must fail; (b) delete `x.Participants.Add(id)` in `Accept`: `An_overlapping_owner_prompt…` must fail; (c) in the service, set `acceptMentions` to `ReferenceEquals(handle.Exchange, newest)` unconditionally: `R32_two_disjoint…` must fail; (d) make `OnStopOne` cancel every in-flight handle in the room: `R32_stopping_one_exchange…` must fail; (e) remove the run-start supersede-all loop: `A_run_start_supersedes…` must fail; (f1) delete the `_runs.Active(roomId) is not null` half of `OnStopOne`'s run check, then (f2) the `Parked` half: `R32_the_per_exchange_stop_refuses…` must fail for each; (h) make `OpenWorkersExchange` pass `replaceAll: false`: `R32_a_run_room_never_launches…` must fail; (g) make `AddExchange` skip its `RemoveAll`: `R32_two_disjoint…` must fail on its final `Assert.Single`.
4. Branch-level `mattpocock-skills:code-review` (Standards + Spec against this plan's Acceptance).
5. Deploy with `tools\Deploy-ChopItUp.ps1` (the live hub is stopped and restarted under the standing ChopItUp carve-out), then `tools\Invoke-M4SelfCheck.ps1 -PublishDir <staging> -TargetDir C:\Self Apps\ChopItUp`, and a token-free `GET http://127.0.0.1:8790/api/rooms/general/exchange` showing an `exchanges` array.

## Critique dispositions
Pass 1 (fable, 6.8, FIX-THEN-SHIP):
1. App-backed test broken by debounce and the 1 s fixture timeout: fixed. The handler hangs on the token only, the test awaits sonnet's spec and polls turn counts.
2. Per-exchange stop test timing-bound: fixed. Token-only hang, polls instead of sleeps.
3. `RunOwnsRoom` arm unbound: fixed. Task 3 adds the active and parked run test, mutation (f).
4. Launching every exchange changes run rooms: fixed by preserving the old semantics. `Schedulable()` restricts run rooms to their newest exchange for both `LaunchDue` and `ArmWake`. No test added: the leftover-queue state is derived, not reachable from any existing fixture without a multi-phase run, and the helper reproduces the one-slot loop exactly.
5. `_rooms\[` gate contradicted `AddExchange`: fixed (exactly one hit).
6. Pruning unbound: fixed. `R32_two_disjoint…` ends with a third prompt and `Assert.Single`, mutation (g). The superseded-with-live-spawn retention is bound by the rewritten A6 test's `Exchanges![0]` assertion.
7. In-flight read raced the launch's Publish: fixed with a poll.
8. Task 1 commits a red service suite: declined with rationale. The commit body says so, Tasks 1 and 2 go to one builder back to back, and the PR squash-merges, so `main` never holds it.
9. Reference exemplar NONE: accepted, no shipped plan exists on disk.
10. No stress run: fixed (ten runs in 2d and 3c).
11. Process vocabulary in new code: fixed. New comments and summaries carry no row, lesson or ledger ids. Existing ids in moved lines and existing test names stay.
12. Two silent interpretations: fixed, named under the rulings.

Pass 2 (opus, 6.6, FIX-THEN-SHIP):
1. B1, a parked or ended run could launch a turn left queued in an older run exchange, and that exchange's spawn could regain mentions: fixed at the source. Run exchanges replace the room's list (`replaceAll: true`), so a run room holds one exchange exactly as before, and outside a run a spawn's mentions also require its exchange to still be in the room's list. `Schedulable()` is removed as redundant. `R32_a_run_room_never_launches…` added to Task 2, mutation (h). The membership half has no test: its only trigger is a spawn post landing between `EndRun` and that spawn's `FinishedEvent`, a race, and it mirrors the removed `ReferenceEquals(handle.Exchange, current)` exactly.
2. M1, interim UI: fixed in part. Top-level fields describe the newest open exchange (`Displayed`). Rows 34-36 go on the board with this plan, 34 next. The room Stop ending every exchange is stated in README and the ping and not changed here: a per-exchange button without per-exchange bars would leave older exchanges with no stop at all.
3. M2, parked half vacuous: fixed with concrete single-host code, mutations (f1) and (f2).
4. m1 counts: fixed (942 after Task 2, 945 after Task 3).
5. m2 view fields unbound: fixed (stoppedBy, budget, pending asserted).
6. m3 budget note: fixed. Concluded-note ambiguity named under Could not verify.
7. m4 catch-all arm: fixed (explicit arms, throwing default).
8. m5 stress count: fixed ($LASTEXITCODE tally).
