# Row 19 — Runs

**Goal:** a conductor participant, re-spawned by the hub every time the exchange it opened concludes, drives a multi-phase run inside one directory room, under hub-enforced phase tags, class rules, caps and a gate tool — so a build process can run to its ping with no owner post between phases.

**Architecture.** A *run* is a persistent row (`runs`, schema v8) bound to one directory room, started by an owner slash-invocation of a skill whose fingerprinted frontmatter declares `run: true`. Exchanges stay what they are today — ephemeral, four turns, one per room. Inside a run the hub adds a loop, and that loop is **a pure state machine of its own**, `RunPolicy`, sitting beside `ExchangePolicy`: it takes `(RunState, RunEvent)` and returns a `RunDecision`. `SpawnerService` raises the events, assembles `RunState`, and carries out the decisions — including keeping the two per-phase counters that `RunState` reports, because they are cheap in-memory state with no durable meaning. **It makes no run *decisions*.** Everything the hub enforces — the phase grammar, the class rules, the four caps, effort by class, the run-state prompt section, `run_gate` — is a hub-side check on hub-side state, never a reading of what a model wrote.

**Author model:** Opus 5. **Routing mismatch declared:** the workflow routes HIGH-tier planning to Fable; this session is Opus. Critique pass 1 ran on `fable`, pass 2 on `opus`.

**Blast radius: HIGH,** and both passes sharpened why. This row creates the hub's **first unattended spend loop** (80 spawns, 8 hours, no human between phases) while row 13 has not shipped; and it is **the first row to put a trust anchor — the skill-tree manifest and the gate verdict — behind a fence that pass 2 showed is bypassable** (`ClaudeBuiltins` includes `Bash`, and `ClaudeDenyRules` denies only git verbs, so a Claude spawn reaches the data dir through `Bash` even though its file tools cannot). See Residual risk.

*Written for builder-subagent execution; if something doesn't match, STOP and report rather than guess.*

**Binding input:** `docs/superpowers/plans/grill-notes-row11-harness-in-room.md`. This row implements D1, D2, D4, D6, D8, D9, D10, A2, A3, and amends M5-D2/D5/D7 inside a run only. A step here that contradicts a D/A row of that file is wrong until the owner re-rules.

**Critique status:** pass 1 (fable) FIX-THEN-SHIP 5.0 — 6 blockers, 12 majors. Pass 2 (opus) FIX-THEN-SHIP 6.3 — 5 blockers, 16 further findings, none of them re-reports. All 44 findings accepted, none declined; dispositions are the last two sections.

---

## Scope: one row, no fallback seam

16 tasks, 15 acceptance criteria, over the workflow's 60 KB plan threshold. It stays one row, and **the fallback seam earlier drafts offered has been deleted rather than repaired** — that is pass 2's explicit ruling, and it is right.

Pass 1 killed the first rationale (it leaned on owner ruling D11, which chose among one, two and three rows and never ruled on sub-splitting row 19). Pass 2 then killed the replacement *seam*: the offered 19a/19b split put ticket 15 in 19a while both of its blockers (12 and 14) sat in 19b, and the only ordering the ticket graph actually permits leaves 19a with no real-CLI check — which D11 forbids outright ("each row ships something usable alone **and has a real-CLI check like M5/M9/M10**").

So the argument is now narrow and honest. Tasks 1–10 and 13 are one mechanism: a run that starts but cannot re-spawn its conductor is not a run; a loop with no caps is one the owner cannot walk away from, which is the entire point; a run with no `phase:` enforcement is prose. Pass 1 found six blockers and five were gaps *between* those pieces — exactly the seams a split would ship as two halves that each look complete and together do not terminate. Tasks 11, 12 and 14 **are** separable on file boundaries, and pass 2 said so; they are not split anyway, because the only spec-legal seam does not exist. Weak-and-cheap is not a reason to split when the split itself is illegal.

---

## Lessons consulted

Grepped `docs/LESSONS.md` headings for this row's surface.

| Entry | Applied where |
|---|---|
| **M11 [schema-literals, check-scripts, live-check]** | Task 1's v7→v8 literal sweep is a task step: six `.ps1` sites assert `$health.schema -eq 7`, measured this session. The converse rule governs task 15, and pass 1's B6 plus pass 2's F-26 are both this lesson — a check coupled to wording the product never promised. |
| **M1 [sqlite, schema, migrations]** | `ApplyV8` stamps `PRAGMA user_version = 8` last inside the same transaction as its DDL; every CREATE is `IF NOT EXISTS`; a torn v8 re-runs clean. |
| **M2 [sqlite, wal, testing]** | The migration guard test asserts against a hand-written v7-shaped database, never one `ChopDb` produced. |
| **M4 [process, async-io, ci-flake]** | Task 12's `run_gate` runs its child through the existing `IProcessRunner`, which carries the parameterless-`WaitForExit()` fix. A raw `Process` there is a defect. |
| **M5 [windows, spawn, cli-shims]** | Task 12 resolves `pwsh` through `CliResolver`. Re-measured: `codex` resolves only as `C:\Users\cayov\.local\bin\codex.cmd`. |
| **M5 [ci, path, seams, tests]** | Task 12's `pwsh` lookup and task 2's clock both go behind DI seams with fakes in `HubTestHost`, or they fail on the CI runner as the 11 spawn tests did. Pass 2's F-13 is this lesson missed once already. |
| **M5 [claude-code, headless, tool-surface]** | Task 12 adds `run_gate` to a second allowlist constant for in-run Claude spawns; the built-in set is untouched, so MCP tools stay directly callable. |
| **M10 [powershell, invoke-restmethod]** | Task 15 pipes every array response through `ForEach-Object { $_ }` before filtering, and treats one failed tool leg as a re-run before a defect. |
| **M9 [signalr, cross-room-ui]** | Task 14 states the cross-room question explicitly rather than leaving it to the builder. |

---

## Acceptance

1. **AC1 — start.** WHEN the owner (or `owner-remote`) posts, in a room with a directory, a slash invocation of an installed skill declaring `run: true`, mentioning exactly one spawnable row, and no run is active there, THE SYSTEM SHALL insert one `active` run naming that row as conductor, open an exchange holding only that conductor, and post one hub note naming the run id, the conductor and the caps.
2. **AC2 — refusals at start.** WHEN that post arrives in a room with no directory, or names zero or more than one conductor, or arrives while a run is already active in that room, THE SYSTEM SHALL post a one-line refusal naming which, insert no run, and launch no spawn.
3. **AC3 — the loop.** WHEN the exchange that is the room's **current** exchange concludes inside a run, nothing is in flight, and no cap has tripped, THE SYSTEM SHALL open a fresh exchange holding only that run's conductor, with no owner post involved; and WHEN an exchange concludes that the conductor has already superseded by rooting a newer one, THE SYSTEM SHALL leave the newer exchange alone.
4. **AC4 — the conductor roots.** WHEN the conductor posts a valid phase-tagged message mentioning spawnable rows, THE SYSTEM SHALL open a new exchange rooted at that post; and WHEN any other model row posts, THE SYSTEM SHALL open nothing.
5. **AC5 — steer.** WHEN a human row posts inside an active run, THE SYSTEM SHALL leave the open exchange open, drop no pending spawn, open no exchange of its own whatever it mentions, and include that message in the trigger set of the next conductor spawn — waking the conductor immediately when nothing is open and nothing is in flight.
6. **AC6 — phase tag and class rules.** WHEN a conductor post inside a run lacks a valid first-line phase tag, or mentions the conductor itself, or is kind `build` without mentioning a `plumbing`- or `visible`-class row, or is kind `critique` without an `artifact:` line, or names an artifact that is neither recorded nor in the room tree, or is kind `critique` mentioning no `judge`-class row other than that artifact's recorded author, THE SYSTEM SHALL post a refusal naming which rule failed, open no exchange, and ask the conductor once more; and WHEN a **second** post in the same phase fails any rule, THE SYSTEM SHALL park the run and ping the owner.
7. **AC7 — effort by class.** WHEN the hub launches a spawn that is its run's conductor or holds the `judge` class, THE SYSTEM SHALL pass `--effort high` (Claude) or `-c model_reasoning_effort=high` (Codex); and WHEN it launches any other row, or any spawn outside a run, THE SYSTEM SHALL pass no effort flag.
8. **AC8 — the four caps.** WHEN an active run reaches 80 spawns, or 8 hours of **time it spent active** (parked time excluded), or enters the same phase tag a 4th time, THE SYSTEM SHALL park the run naming the cap, stop its open exchange and in-flight spawns, and launch nothing further; and WHEN an in-run spawn is launched, THE SYSTEM SHALL give it a 30-minute timeout rather than the 5-minute default.
9. **AC9 — run state in the prompt.** WHEN the hub renders the prompt for any spawn inside a run, THE SYSTEM SHALL include the run id, conductor, current phase tag, entries into that phase, exchanges opened, spawns used against each cap, the recorded author of every artifact path, **every gate result the run has recorded**, and the run skill's text.
10. **AC10 — `run_gate`.** WHEN an in-flight spawn of an active run calls `run_gate` with a gate its run's skill declares, and the whole skill tree matches the manifest recorded at import, THE SYSTEM SHALL run it as `pwsh -NoProfile` from a hub-private verified copy, with the working directory set to the room's directory and only the arguments that gate declares, and return the exit code with capped output; and WHEN the caller is not an in-flight spawn of an active run, the gate is undeclared, the tree does not match, or another gate is already running in that room, THE SYSTEM SHALL refuse naming which, execute nothing, **and record the refusal**.
11. **AC11 — stop.** WHEN the owner posts `/stop` in a room with an active **or parked** run, or presses the room's stop control, THE SYSTEM SHALL set the run to `ended`, stop its in-flight spawns, and post one note naming how much of each cap it used — whether or not an exchange is open at the time, and **without resuming the run first**.
12. **AC12 — restart.** WHEN the hub starts and the `runs` table holds an `active` row, THE SYSTEM SHALL set it to `parked` with a reason before serving any request, and launch nothing for it.
13. **AC13 — conductor silence.** WHEN a conductor spawn ends without posting, THE SYSTEM SHALL ask it once more with the same trigger set; and WHEN that second attempt is also silent, THE SYSTEM SHALL count a phase entry and either ask again or park — never leave the run active with nothing driving it.
14. **AC14 — the run ends itself.** WHEN the conductor posts a `phase: ping` message, THE SYSTEM SHALL set the run to `ended`, close the open exchange, and post the counters note — without requiring it to mention anyone.
15. **AC15 — resume.** WHEN a human row that is not `/stop` and not a run-start invocation posts in a room whose most recent run is `parked` for a reason other than a spent hard cap, THE SYSTEM SHALL set it back to `active`, add the parked interval to the run's excluded time, and open the conductor's exchange with that post as trigger; and WHEN it is parked because a hard cap is spent, THE SYSTEM SHALL refuse the resume in one line saying so.

**Real-CLI acceptance (task 15):** a two-phase toy skill, invoked once by the owner, reaches its ping with no owner post between the phases — proven from the hub's own record: two distinct phase tags, at least three exchanges, exactly one human message in the room, and the run `ended` rather than `parked`.

**Termination obligation.** An active run always has an open exchange, an in-flight spawn, or an armed wake **that fires in bounded time**. Any path that would leave none of the three parks the run with reason "the run stalled". Task 3 states this as `RunPolicy`'s invariant; task 9 arms the bounded stall wake that makes it reachable in production rather than only in a property test (pass 2's F-7).

---

## Design decisions

**P1 — a run starts because the skill says so.** A run begins when the invoked skill's SKILL.md frontmatter carries `run: true`. That frontmatter is inside the bytes `SkillHashes` fingerprints, so the declaration inherits row 11's tamper check, and row 20 ports the roadmap skill without a hub change. It declares *kind*, not limits, so D4's ban on skill-declared budgets is untouched.

**P2 — the hub opens the conductor's exchange; a model post still cannot open one on its own.** The **re-spawn** is the hub constructing an `Exchange` through `ExchangePolicy.OpenForConductor` — no message roots it, so `OnMessage`'s human-only rule is untouched. The **rooting** does relax `OnMessage`, for exactly one row, in exactly one room, only while a run is active there, and only for a post that passes the D8 checks. Every other model post opens nothing.

**P3 — phase grammar is hub vocabulary.** First line, `phase: <kind>` or `phase: <kind>/<name>`, kind in `{plan, build, critique, verify, ping}`, **trailing text on the same line ignored**. Pass 1's M8: the first draft required end-of-line after the tag, rejecting `phase: build @sonnet go` — the most natural thing a conductor writes, probed false against the draft regex. A leading `**` or `#` is deliberately *not* tolerated: the hub does not guess at markdown, and task 7 shows the conductor the exact shape.

**P4 — artifact authorship comes from git, never a self-report,** and from the **spawn's whole diff**, not one commit (pass 1 M5: a Codex spawn that commits its own work leaves the hub's commit empty). Paths are normalized identically on record and lookup, or the author rule is vacuous against `./x`, `x\y`, `` `x` `` and `X`.

**P5 — the whole skill tree is fingerprinted and executed from a verified copy.** Pass 1's M6, measured: the roadmap gate this row exists to run, `Check-RoadmapBudget.ps1`, reads **and writes** `baselines.json` from `$PSScriptRoot` — an unhashed sibling. Hashing only the named script would let a spawn rewrite a baseline and make the gate pass a violating board. Task 12 manifests every file, verifies at call time, and runs from a copy, which also closes the hash-then-run window. **Pass 2's F-15 corrected the copy's cost:** moving `$PSScriptRoot` silently voids the gate's own legitimate baseline write, so `run_gate` sets `ROADMAP_GATE_BASELINE` to a hub-private durable path and names the cleanup site.

**P6 — the run strip is the exchange bar's sibling, not its replacement.**

**P7 — the run loop is a pure state machine.** `RunPolicy` takes `(RunState, RunEvent)` and returns a `RunDecision`, so termination is provable by enumerating a transition table. Pass 2's blockers were that three of the first table's rows could not be built from the state the policy was given and one had no caller — so the table in task 3 is now written against the events `SpawnerService` actually raises, with every predicate consumed by some arm and every decision carried out by some caller. **The counters live in the service** (Architecture, above): pass 2's F-2 found the first draft declared, consumed and reset them while nothing incremented them, which silently disabled both D8's second-refusal park and A2's second-silence entry.

---

## Residual risk — larger than earlier drafts said

Earlier drafts scoped this to Codex. **Pass 2 showed that is wrong, and it is corrected here.**

`ClaudeBuiltins = "Read,Edit,Write,Glob,Grep,Bash"` (`SpawnCommands.cs:75`) and `ClaudeDenyRules` denies git write verbs, `.git` writes, credential folders and `Read/Write/Edit(<dataDir>/**)` — **it does not deny `Bash`** (`:110-135`, read this session). So an in-run **Claude** spawn reaches the data folder through `Bash(cat …)` and can run a script through `Bash(pwsh -File …)`, straight past the file-tool fence. `CodexInDirectory` passes no deny list at all, and `--approve-for-me` excludes `--sandbox`, so `-C roomDir` is a working directory rather than a wall.

Row 11 shipped that fence when nothing depended on it. **This row is the first to put a trust anchor behind it** — the skill-tree manifest and the gate verdict — which is what makes the gap load-bearing now.

With P5's manifest and verified copy:
- Subversion **via the gate's own tree** is closed *against the file tools*, and against `Bash` only to the extent that a rewritten file fails the manifest before it is copied. A spawn that rewrites the tree between two `run_gate` calls is caught; the window inside one call is closed by the copy.
- Subversion **via module resolution** is not closed: a gate doing `Import-Module` resolves through the owner's `PSModulePath`. Not probed; inferred.
- Subversion **via the room tree the gate inspects** is not closed and cannot be: a gate whose job is to judge the room's files judges files the spawns are meant to write.
- Denial of service is not closed.

**Row 13 owns all of it**, and its board Notes should gain the `Bash` finding — the row currently reads as if Codex is the only open side. What this plan owes row 13 is this paragraph.

---

## Could not verify in this environment

- **No multi-phase run has been executed.** Task 15's job; it spends real model turns. Until it runs, every claim about the loop is a claim about unit tests with a fake process runner.
- **The 8-hour wall clock is asserted through an injected clock, never by waiting.**
- **No Codex-hosted roster row carries a class** (pass 2's F-25): `opus` is `visible,judge`, `sonnet` `plumbing`, `fable` `judge`, and every `gpt-*` row defaults `Classes` to null. Task 15's toy run therefore drives a Claude-hosted row only, so **task 12f's Codex tool-timeout raise, task 11's Codex effort flag, and the whole Codex residual-risk path get no live coverage this row.** Fixing the seed roster is not this row's job; saying so is.
- **`--effort high` on Claude was not re-probed this session** — carried from grill-ledger F2 (2.1.220, 2026-09-06). The Codex half **was** measured here (ledger claim 6).
- **The Claude CLI's MCP tool timeout is unknown.** Task 12f measures it rather than assuming.
- **`PSModulePath` writability from a spawn is inferred, not probed.**
- **The 30-minute in-run timeout is asserted as a value passed, not as a wall-clock outcome.**
- **Task 15's gate, phase, file and artifact checks are hub-*recorded* but model-*triggered*** (pass 2's F-17): a FAIL is ambiguous between a hub defect and a conductor that did not comply. The script carries a diagnostic to tell them apart; the ambiguity itself cannot be removed by a check.

---

## Tasks

16 tasks, one ticket each, dependency order. TDD-gated: failing test first, RED output pasted, then green. `dotnet build ChopItUp.slnx -c Debug -warnaserror -v minimal` stays at 0 warnings after every task.

### Task 1 — schema v8: the `runs` tables and `RunStore`

**Files:** `src/ChopItUp.Core/Storage/ChopDb.cs`, new `RunStore.cs`, new `src/ChopItUp.Core/Model/Run.cs`, `SchemaMigrationTests.cs`, new `RunStoreTests.cs`, six `.ps1` sweep sites.

**1a.** `ChopDb.cs:10` → `LatestSchemaVersion = 8`. **1b.** After `:118`, one ladder line `if (GetUserVersion(conn) < 8) ApplyV8(conn);`

**1c. `ApplyV8`** — one transaction, `IF NOT EXISTS`, stamp last:

```csharp
/// <summary>v8 (row 19): runs. A run is the only hub state that outlives an exchange, so unlike
/// Exchange it is a table. ux_runs_one_active_per_room is the invariant the row rests on — two active
/// runs in one room would give a conductor two loops to be re-spawned by. parked_seconds excludes
/// time a parked run was not running from the D9 wall clock, without which an overnight restart-park
/// re-parks the moment it is resumed (AC15). run_phases counts re-entries per full phase tag;
/// run_artifacts records authorship read out of the spawn's own git diff; run_gate_runs is the
/// hub-written record the live check reads, and its run_id is NULLABLE because the refusals AC10
/// requires it to record include "there is no run here". Stamp last (LESSONS, M1).</summary>
private static void ApplyV8(SqliteConnection conn)
{
    using var tx = conn.BeginTransaction();
    using (var ddl = conn.CreateCommand())
    {
        ddl.Transaction = tx;
        ddl.CommandText = """
            CREATE TABLE IF NOT EXISTS runs (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                room_id         TEXT NOT NULL REFERENCES rooms(id),
                conductor_id    TEXT NOT NULL REFERENCES participants(id),
                skill_name      TEXT NOT NULL,
                arguments       TEXT NOT NULL DEFAULT '',
                status          TEXT NOT NULL,
                reason          TEXT,
                cap_spent       INTEGER NOT NULL DEFAULT 0,
                phase           TEXT NOT NULL DEFAULT '(start)',
                root_message_id INTEGER NOT NULL,
                started_at      TEXT NOT NULL,
                parked_at       TEXT,
                parked_seconds  INTEGER NOT NULL DEFAULT 0,
                ended_at        TEXT,
                spawns_used     INTEGER NOT NULL DEFAULT 0,
                exchanges       INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_runs_one_active_per_room ON runs(room_id) WHERE status = 'active';
            CREATE INDEX IF NOT EXISTS ix_runs_room ON runs(room_id, id);
            CREATE TABLE IF NOT EXISTS run_phases (
                run_id  INTEGER NOT NULL REFERENCES runs(id),
                phase   TEXT NOT NULL,
                entries INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (run_id, phase)
            );
            CREATE TABLE IF NOT EXISTS run_artifacts (
                run_id    INTEGER NOT NULL REFERENCES runs(id),
                path      TEXT NOT NULL,
                author_id TEXT NOT NULL REFERENCES participants(id),
                at        TEXT NOT NULL,
                PRIMARY KEY (run_id, path)
            );
            CREATE TABLE IF NOT EXISTS run_gate_runs (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id    INTEGER REFERENCES runs(id),
                room_id   TEXT NOT NULL,
                gate      TEXT NOT NULL,
                caller_id TEXT NOT NULL,
                exit_code INTEGER,
                outcome   TEXT NOT NULL,
                at        TEXT NOT NULL
            );
            """;
        ddl.ExecuteNonQuery();
    }
    using (var stamp = conn.CreateCommand())
    {
        stamp.Transaction = tx;
        stamp.CommandText = "PRAGMA user_version = 8;";
        stamp.ExecuteNonQuery();
    }
    tx.Commit();
}
```

Three fields exist because a critique pass found the code unbuildable without them. `phase` is `NOT NULL DEFAULT '(start)'` — pass 2's F-8: task 10's second-silence path counts a phase entry, and the first conductor spawn of a fresh run can go silent twice while no phase has been announced, so a nullable phase is either a `CS8604` under `-warnaserror` or an empty-string key a builder invents. `(start)` counts and reads honestly in the AC9 prompt section. `parked_seconds`/`parked_at` are pass 2's F-4. `run_gate_runs.run_id` is nullable per pass 2's F-12.

**1d. `Run.cs`** — records only: `RunStatus` constants (`active`/`parked`/`ended`); `Run(Id, RoomId, ConductorId, SkillName, Arguments, Status, Reason, CapSpent, Phase, RootMessageId, StartedAt, ParkedAt, ParkedSeconds, EndedAt, SpawnsUsed, Exchanges)`; `RunArtifact(Path, AuthorId, At)`; `GateRun(Gate, CallerId, int? ExitCode, Outcome, At)`.

**1e. `RunStore.cs`** — `public sealed class RunStore(ChopDb db)`, raw ADO, `db.Open()` per call, `$name` parameters, `Timestamps.Stamp`. **Every mutating member takes `DateTimeOffset now`, including `Start`** — pass 2's F-13: `Start` was the one member without it, which made `started_at` un-back-datable and task 9's wall-clock test unwritable.

Members: `Start(roomId, conductorId, skillName, arguments, rootMessageId, now)`, `Active(roomId)`, `Latest(roomId)`, `ById`, `ListActive`, `Park(id, reason, capSpent, now)` (also stamps `parked_at`), `Resume(id, now)` (parked → active, reason cleared, **`parked_seconds += now − parked_at`**, `parked_at` cleared; throws when `cap_spent`), `End(id, reason, now)`, `CountSpawn`, `CountExchange`, `EnterPhase(id, phase, now)` (non-nullable `phase`; upsert `ON CONFLICT (run_id, phase) DO UPDATE SET entries = entries + 1`; sets `runs.phase`; returns the new count), `PhaseEntries(id)` → `IReadOnlyDictionary<string,int>` **for the whole run, not one tag** (pass 2's F-3: the policy needs entries for the tag being *entered*, and the old single-value shape only carried the tag being left), `RecordArtifact`, `Artifacts`, `ArtifactAuthor`, `RecordGateRun(runId: long?, roomId, gate, callerId, exitCode: int?, outcome, now)`, `GateRuns(id)`.

**Elapsed is computed here,** as `ActiveElapsed(Run r, DateTimeOffset now) => (now − r.StartedAt) − TimeSpan.FromSeconds(r.ParkedSeconds)`, so exactly one place defines it.

**Path normalization** is one private static `Normalize` used by `RecordArtifact` and `ArtifactAuthor` alike, so they cannot disagree: strip surrounding backticks and quotes, `\` → `/`, drop a leading `./`, trim, compare ordinal-ignore-case; store the normalized form.

**1f–1g. Tests.** `SchemaMigrationTests`: `A_v7_database_migrates_to_v8_keeping_every_row` (hand-written v7 SQL, `user_version = 7`, assert `LatestSchemaVersion`, participants' `classes` intact, `runs` present and empty) plus a torn-v8 test in the file's existing shape. `RunStoreTests`: **seed a room and participant first** — `PRAGMA foreign_keys=ON` at `ChopDb.cs:74`, and `Start`'s rethrow must distinguish the unique-index violation from an FK error rather than labelling both "already active". Then: round-trip; second `Start` throws "already active"; `Park`/`End` clear `Active`; `Resume` restores **and accumulates `parked_seconds`**; `Resume` on a `cap_spent` park throws; counters increment and return; `EnterPhase` returns 1,2,3 per tag independently and `PhaseEntries` returns both tags; `RecordArtifact` twice keeps the last author; `ArtifactAuthor` matches four spellings of one path; `RecordGateRun` with a null `run_id` succeeds.

**1h. The sweep (M11).** `-eq 7` → `-eq 8` at `Invoke-M10MemoryCheck.ps1:71`, `Invoke-M11SkillCheck.ps1:147`, `Invoke-M2DryRun.ps1:146` (and its comment on 145), `Invoke-M4SelfCheck.ps1:321`, `Invoke-M5SpawnCheck.ps1:62`, `Invoke-M9RoomCheck.ps1:73`. Rename the three stale `hub.health-schema-6` labels to `hub.health-schema`. `Invoke-M2DryRun.ps1:249`'s `tokenKeys -eq 14` is correct and stays.

**Verify:** full suite green; `grep -rn "schema -eq 7" tools/` silent.

---

### Task 2 — `RunLimits`, the clock seam, the phase grammar, and `run: true`

**Files:** new `RunLimits.cs`, new `src/ChopItUp.Core/Model/PhaseTag.cs`, `HubHost.cs`, `HubTestHost.cs`, `SkillStore.cs`, `SkillsApi.cs`, `client/src/types.ts`, tests.

**2a. `RunLimits`** — D9's numbers, hard-coded beside `SpawnLimits.Default`:

```csharp
public sealed record RunLimits(int Spawns, TimeSpan WallClock, TimeSpan SpawnTimeout, int PhaseEntries)
{
    public static readonly RunLimits Default = new(
        Spawns: 80, WallClock: TimeSpan.FromHours(8),
        SpawnTimeout: TimeSpan.FromMinutes(30), PhaseEntries: 3);
}
```

**2b. The clock seam (pass 2's F-13 — this does not exist today and nothing else in the row works without it).** Measured this session: no `TimeProvider`, `IClock`, `Func<DateTimeOffset>` or test-clock seam exists anywhere in `src/` or `tests/`; `SpawnerService` calls `DateTimeOffset.UtcNow` inline. Add .NET's own `TimeProvider` — not a hand-rolled interface — as an optional parameter on `HubHost.Build` and `HubTestHost.StartAsync` beside `SpawnLimits`, defaulting to `TimeProvider.System`, registered in DI, injected into `SpawnerService` and passed as `now` to every `RunStore` call. Add `Microsoft.Extensions.TimeProvider.Testing` to the test project if it is not already there, and say so in the report. Every `DateTimeOffset.UtcNow` on a run path becomes `_clock.GetUtcNow()`; existing non-run paths may stay as they are.

**Without this, task 9's prescribed verification is unwritable and the fold's headline fix — the idle-run park — has no test route.**

**2c. `PhaseTag`,** with pass 1's M8 fix (trailing text ignored):

```csharp
private static readonly Regex Pattern = new(
    @"^phase:[ \t]+(?<kind>[a-z]+)(?:/(?<name>[a-z0-9][a-z0-9-]{0,31}))?(?=[ \t]|\r?\n|$)",
    RegexOptions.Compiled | RegexOptions.CultureInvariant);
```

Kinds `{plan, build, critique, verify, ping}`; `TryParse` returns false on an unknown kind. `ToString()` is `kind` or `kind/name` — the value `run_phases` counts. `Artifact(body)` scans every line for the first `artifact:` prefix and returns the trimmed remainder or null.

**2d. `run: true` and `gates:` in frontmatter.** Extend `SkillStore.StripFrontmatter` (which already reads `name:` and `description:`). `gates:` is a comma-separated list of `[a-z0-9][a-z0-9-]{0,31}` names, malformed entries dropped as `ParticipantClasses.Parse` does. **A gate may declare arguments** (pass 2's F-14) as `gates: budget(--RoadmapPath ROADMAP.md), count-files` — the argument string is part of the fingerprinted frontmatter, so it is as protected as the name, and `run_gate` passes exactly those tokens and no others. Widen with optional parameters so every existing construction site compiles:

```csharp
public sealed record GateDeclaration(string Name, IReadOnlyList<string> Arguments);
public sealed record ResolvedSkill(string Name, string Title, string Body, bool Truncated,
    bool IsRun = false, IReadOnlyList<GateDeclaration>? Gates = null);
public sealed record SkillSummary(string Name, string Title, string Description, int Chars, bool IsRun = false);
```

`GET /api/skills` gains `isRun`; `types.ts`'s `Skill` gains `isRun: boolean` — and fix its doc comment, which says "Four fields, matching SkillSummary exactly" and would become a lie. A gate name maps to `data/skills/<skill>/scripts/<gate>.ps1`, **derived from the name**, never taken from frontmatter.

**Verify:** `phase: build` parses; **`phase: build @sonnet make hello.txt` parses** (the M8 regression); `critique/pass-2` parses to kind + name; `phase:build`, `phase: Build`, `**phase: build**`, `phase: nonsense`, second-line tags all fail; `run: true` reads back; a gate with arguments parses; a malformed gate entry is dropped; a `HubTestHost` built with a fake `TimeProvider` reports the fake's time.

---

### Task 3 — `RunPolicy`: the run as one pure state machine (P7)

**Files:** new `src/ChopItUp.Hub/Spawning/RunPolicy.cs`, new `tests/ChopItUp.Hub.Tests/Spawning/RunPolicyTests.cs`.

Nothing else in this row may hold run *decisions*. Pass 2 rewrote this table: three rows of the previous one could not be built from the state the policy was given, one had no caller, and two predicates were never read.

```csharp
/// <summary>Everything the hub knows about a run when it decides what happens next. Assembled by the
/// service; the policy reads it and nothing else. Elapsed EXCLUDES parked time (RunStore.ActiveElapsed).
/// AnythingInFlight is scoped to THIS ROOM (SpawnerService.InFlightIn), never the global
/// AnySpawnInFlight. PhaseEntries is the whole map, because the arm that matters needs the count for
/// the tag being ENTERED, which is on the event, not the tag being left.</summary>
public sealed record RunState(
    long RunId, string RoomId, string ConductorId, string Status, bool CapSpent,
    string Phase, IReadOnlyDictionary<string, int> PhaseEntries,
    int SpawnsUsed, int Exchanges, TimeSpan Elapsed,
    int RefusalsThisPhase, int SilencesThisPhase,
    bool ExchangeOpen, bool AnythingInFlight);

public abstract record RunEvent
{
    /// <summary>The room's CURRENT exchange reached Concluded. The service does not raise this for an
    /// exchange the conductor has already superseded by rooting a newer one (pass 2 F-1).</summary>
    public sealed record ExchangeConcluded(long LastMessageId) : RunEvent;
    public sealed record ConductorPosted(long MessageId, PhaseTag? Tag, IReadOnlyList<string> Mentioned, string? Refusal) : RunEvent;
    public sealed record SpawnSilent(string ParticipantId, IReadOnlyList<long> TriggerIds) : RunEvent;
    public sealed record HumanPosted(long MessageId) : RunEvent;
    public sealed record StopRequested : RunEvent;
    public sealed record Tick : RunEvent;
}

public abstract record RunDecision
{
    public sealed record Nothing : RunDecision;
    public sealed record OpenConductor(long RootMessageId, IReadOnlyList<long> TriggerIds) : RunDecision;
    public sealed record OpenWorkers(long RootMessageId, IReadOnlyList<string> Mentioned, PhaseTag Tag) : RunDecision;
    /// <summary>Post the note and stop. Split from RefuseAndAsk because AC6 wants a re-ask and AC15
    /// does not, and one decision carrying two obligations pushes the choice back into the service,
    /// which P7 forbids (pass 2 F-11).</summary>
    public sealed record Refuse(string Note) : RunDecision;
    public sealed record RefuseAndAsk(string Note, long TriggerMessageId) : RunDecision;
    public sealed record Park(string Reason, bool CapSpent) : RunDecision;
    public sealed record End(string Reason) : RunDecision;
}

public sealed class RunPolicy(RunLimits limits)
{
    public RunDecision Decide(RunState s, RunEvent e, IReadOnlyList<long> pendingSteers);
}
```

**Transition table — one test per row.** `CapSpent` is a property of **which cap**, never of which path reached it (pass 2's F-10): the spawn, wall-clock and phase-entry caps are hard (D9 calls them hard caps), so every park naming one is `capSpent: true` wherever it is reached; a refusal park and a silence park are `capSpent: false`.

| # | Event | Condition | Decision |
|---|---|---|---|
| 1 | any | `SpawnsUsed >= limits.Spawns` | `Park("the run used its N spawns", true)` |
| 2 | any | `Elapsed >= limits.WallClock` | `Park("the run passed N of active time", true)` |
| 3 | `ConductorPosted` | `Refusal is not null`, `RefusalsThisPhase == 0` | `RefuseAndAsk(note, messageId)` |
| 4 | `ConductorPosted` | `Refusal is not null`, `RefusalsThisPhase >= 1` | `Park("two refused posts in phase X", false)` |
| 5 | `ConductorPosted` | valid, `Tag.Kind == ping` | `End("the conductor pinged")` |
| 6 | `ConductorPosted` | valid, `PhaseEntries[Tag.ToString()] >= limits.PhaseEntries` | `Park("phase X entered N times", true)` |
| 7 | `ConductorPosted` | valid, otherwise | `OpenWorkers(messageId, mentioned, tag)` |
| 8 | `ExchangeConcluded` | `!ExchangeOpen && !AnythingInFlight` | `OpenConductor(root, [lastMessageId, ...steers])` |
| 9 | `ExchangeConcluded` | otherwise | `Nothing` |
| 10 | `SpawnSilent` | conductor, `SilencesThisPhase == 0` | `OpenConductor(root, sameTriggers)` |
| 11 | `SpawnSilent` | conductor, `PhaseEntries[Phase] >= limits.PhaseEntries` | `Park("the conductor did not post", false)` |
| 12 | `SpawnSilent` | conductor, otherwise | `OpenConductor(root, sameTriggers)` — the service counts a phase entry |
| 13 | `SpawnSilent` | not the conductor | `Nothing` |
| 14 | `HumanPosted` | `Status == active` | `Nothing` (the service records the steer) |
| 15 | `HumanPosted` | `Status == parked`, `!CapSpent` | `OpenConductor(root, [messageId])` |
| 16 | `HumanPosted` | `Status == parked`, `CapSpent` | `Refuse("this run is parked because a cap is spent; /stop and start a new one")` |
| 17 | `StopRequested` | — | `End("stopped by the owner")` |
| 18 | `Tick` | active, `!ExchangeOpen`, `!AnythingInFlight`, steers pending | `OpenConductor(root, steers)` |
| 19 | `Tick` | active, `!ExchangeOpen`, `!AnythingInFlight`, no steers | `Park("the run stalled", false)` |
| 20 | `Tick` | otherwise | `Nothing` |

Rows 1 and 2 are evaluated first for **every** event **except `StopRequested`** — an owner stopping a cap-exhausted run must get `End`, not `Park`, or AC11 cannot be satisfied on the state that most needs it.

Rows 8/9 are pass 2's F-1: the old table read neither `ExchangeOpen` nor the supersede case, so the conductor's own exchange concluding — which happens *after* it has already rooted the worker exchange, because the loop drains every queued event before `LaunchDue` (`SpawnerService.cs:161-164`) and `OnFinished` removes from `_inFlight` on its first line — replaced `_rooms[room]` and dropped the workers. The run would then loop conductor→conductor to the spawn cap while `run.exchanges-at-least-three` passed spuriously.

Row 18 is pass 2's F-6: task 6's headline behaviour had no arm, and `pendingSteers` was a parameter nothing consumed.

**Verify:** one test per row. Plus the termination property test: for every `RunState` with `Status == active`, `!ExchangeOpen` and `!AnythingInFlight`, `Decide(s, Tick, steers)` returns `OpenConductor`, `Park` or `End` — never `Nothing`. **That test is necessary and not sufficient** — pass 2's F-2 is that it asserts over hand-built states, so the counters it reads must also be pinned end-to-end in tasks 8 and 10.

---

### Task 4 — starting a run, and every refusal at the start

**Files:** `ExchangePolicy.cs`, `SpawnerService.cs`, tests.

`ExchangePolicy` stays pure: `RunContext(long RunId, string ConductorId, string CurrentPhase)`, and `OnMessage` gains optional `run`, `startsRun`, `hasDirectory`, `artifactAuthor` parameters so every existing call site compiles unchanged.

**The human branch, in this exact order** (pass 2's F-9 — the previous draft's ordering let `/stop` and a run-start invocation both fall into the resume path, and a run-start into a parked room satisfied both resume and `Start`, racing the unique index):

1. `/stop` → task 13 owns it; return before anything else, active or parked.
2. `startsRun` → if a run is **active** here, note `"A run is already active in this room (#id); /stop it first."`; if the latest run is **parked**, note `"A run is parked in this room (#id); resume it with a message or /stop it before starting another."` Either way return. **A run-start invocation never resumes.**
3. `run is not null` (any other human post inside an active run) → return without touching `current`; the service records the steer (task 6). Pass 1's M2.
4. `startsRun && !hasDirectory` → the directory refusal.
5. The existing skill-refusal switch (Unknown / Tampered / Unavailable).
6. `startsRun && mentioned.Count != 1` → the zero-or-many-conductors refusal.
7. Otherwise the existing behaviour.

Resume (task 9f) is reached only by a human post that is none of the above — which is exactly AC15's wording.

In `SpawnerService.OnMessage`, resolve the skill **once** and pass that value everywhere. On a started run: `_runs.Start(..., _clock.GetUtcNow())`, `_runs.CountExchange`, and the note — whose **first eleven characters must be the literal `Run #<id> started`** (pass 2's F-26), because task 15 greps for it and nothing else pins it.

**Verify:** a test per refusal arm; the run row is active after a valid post; **`/stop` in a parked room ends rather than resumes**; a run-start invocation in a parked room refuses without touching the run; a human post mentioning a model inside a run opens no exchange and leaves the queue intact.

---

### Task 5 — the loop, skill continuity, and artifact authorship

**Files:** `ExchangePolicy.cs`, `SpawnerService.cs`, `GitTrail.cs`, tests.

**5a. `ExchangePolicy.OpenForConductor(roomId, conductorId, rootMessageId, triggerIds, now, skill)`** — pure; conductor as the only pending entry, `TurnsCommitted = 1`. **It sets `Skill`** (pass 1's M9): the skill is rendered into each prompt, never posted into the room, so omitting it leaves every spawn after the first exchange with no instruction at all, and row 20's roadmap skill lost on every re-spawn. The service re-resolves by `run.SkillName` at each launch, honouring the hash pin, and **parks the run on Tampered** rather than continuing without an instruction.

**5b. The hook** is `SpawnerService.OnFinished` at `:403-405`. It raises `ExchangeConcluded` **only when the concluded exchange is the room's current one** — `ReferenceEquals(h.Exchange, _rooms.GetValueOrDefault(room))`, the same distinction `acceptMentions` already draws at `:206` — which together with table rows 8/9 is pass 2's F-1 fix, belt and braces. `LastMessageId` comes from `_store.ReadLast(roomId, 1)`; `Exchange` tracks no such id (pass 1's m2, pass 2's F-24). Decisions are carried out; launching is left to `LaunchDue` on the next pass, never inline, or a directory room's exclusivity is bypassed.

**5c. Artifact authorship (P4).** `GitTrail.ChangedFilesAsync(string range, CancellationToken)` runs `git diff --name-only <range>`. In the after-spawn block (`:337-344`), diff **`headBefore..agent.Hash`**, not the single agent commit — pass 1's M5, the Codex-commits-its-own-work case. When `headBefore` is null, fall back to `git show --name-only --pretty=format: <agent.Hash>`.

**That block runs inside `handle.Run = Task.Run(...)`, off the spawner loop** (pass 2's F-21). The run lookup there is `_runs.Active(roomId)` — a database read, thread-safe — **never** a read of `_rooms` or `_inFlight`, which are plain dictionaries the loop alone mutates. This also puts a `RunStore` write on that thread concurrently with the loop's; `busy_timeout=5000` (`ChopDb.cs:74`) makes it survivable, and that is now a stated assumption rather than an accident.

**Verify:** `ChangedFilesAsync` over a real temp repo including a range where HEAD moved mid-spawn; concluding the current exchange in a run queues the conductor and raises `exchanges`; **concluding a superseded exchange leaves the newer one intact** (F-1); concluding with no run opens nothing; the second conductor prompt contains the skill fence; a Tampered re-read parks the run.

---

### Task 6 — steer

**Files:** `ExchangePolicy.cs`, `SpawnerService.cs`, tests.

The supersede at `ExchangePolicy.cs:64-70` is gated on `run is null` — inside a run the owner steers (D2, amending M5-D5). Task 4 step 3 already returns before the exchange-creating code, so this gate is the second line of defence rather than the first; **say in the test which one is load-bearing** (pass 2's F-23), and keep the outside-a-run supersede test, which is the regression that matters.

`SpawnerService` holds `Dictionary<string, List<long>> _steers` per room, appends, and posts `"Steer noted; @<conductor> is given it when the current exchange concludes."` Steers drain into the next `OpenConductor` trigger set and clear. When nothing is open and nothing is in flight the service raises `Tick` immediately — table row 18 turns that into `OpenConductor`.

**Verify:** an owner post inside a run leaves the exchange open and its queue intact; the same post outside a run still supersedes; a steer into an idle run wakes the conductor **through table row 18**; the conductor's next trigger set contains the steer id.

---

### Task 7 — the run-state section in the prompt

**Files:** `SpawnPrompt.cs`, `SpawnerService.cs`, tests.

One optional `RunView? Run = null` on `SpawnPromptInput`, in `MemoryCore`'s shape: run id, conductor, `SelfIsConductor`, phase, entries and cap, exchanges, spawns and cap, elapsed (active time) and cap, artifacts with authors, **and the run's `run_gate_runs` rows** (pass 2's F-16 — without them the conductor's only view of a gate outcome is the transcript, where a hub note and a model claiming "the gate passed" are two messages of equal standing). Rendered after the memory section and before the skill fence.

For the conductor only, the section **shows the exact post shape** — the literal two-line form, `phase: <kind>` optionally `/<name>` with the rest of the instruction on the same line, `artifact: <path>` on its own line for a critique — the kinds, the class rules, never mention yourself, and that `phase: ping` ends the run. Show it; do not describe it.

**Verify:** the section appears for an in-run spawn and not otherwise; the conductor paragraph only for the conductor; an artifact's author and a recorded gate result are both named; existing prompt tests unchanged.

---

### Task 8 — the phase tag, the D8 class rules, and the refusal counter

**Files:** `ExchangePolicy.cs`, `SpawnerService.cs`, tests.

`ExchangePolicy.RefuseConductorPost(message, run, mentioned, artifactAuthor, artifactExists)` returns null or the refusal, checking: a valid phase tag; not mentioning itself; **`ping` needs no mention and skips the rest**; every other kind needs a mention; `build` needs a `plumbing` or `visible` row (via `ParticipantClasses.Has`, never a string compare); `critique` needs an `artifact:` line, an artifact that is **either recorded or present in the room tree**, at least one `judge`, and a judge that is not the recorded author.

Wire into the model branch: the conductor of an active run may root; every other model post still opens nothing.

**The refusal path is the service's, and the service increments the counter** (pass 2's F-2 — the previous draft declared, consumed and reset `RefusalsThisPhase` and never wrote it, which silently disabled D8's second-refusal park):

- `OnMessage` opens **nothing** on a refusal; it records `(runId, phase) → Refusals++` and posts what `RunPolicy` returned. `RefuseAndAsk` re-opens the conductor's exchange; `Refuse` does not.
- **Only the first phase-tagged post of a conductor spawn roots.** Later posts from the same spawn are prose and are never refused — models post twice routinely and refusing the correction parks a self-correcting conductor (pass 1's M1). Detect via `handle.Exchange != current`, the distinction `:206` already draws.
- On an **accepted** post the service calls `_runs.EnterPhase(runId, tag)` (pass 1's B2), and **resets both counters on every `EnterPhase`, not only on a tag change** (pass 2's F-22 — the silence path re-enters the *same* tag, so a change-only reset leaves the silence counter latched).

**Verify:** one test per refusal arm; `ping` with no mention accepted; a valid post opens what it asks for; a non-conductor model post opens nothing; a refused-then-valid pair in one spawn opens exactly one worker exchange; three spellings of one recorded path resolve to one author; **end-to-end: a second bad post in the same phase parks the run** — the pin whose failure mode is the counter staying zero, which the policy-level test cannot see.

---

### Task 9 — caps, park semantics, the stall wake, restart and resume

**Files:** `SpawnerService.cs`, `HubHost.cs`, tests.

**9a. Carrying out `Park`:** write the row (`reason`, `cap_spent`, `parked_at`), **stop the room's open exchange via `ExchangePolicy.Stop` and cancel its in-flight handles as `OnStop` does**, post the note naming the cap and `@owner`, `Publish`. Pass 1's M3: a park that touches neither `_rooms` nor `_inFlight` lets an in-flight conductor's later post resolve `run == null`, take the plain model branch, and launch more spawns from a parked run at the 5-minute timeout.

**9b. The wake (pass 2's F-7).** `ArmWake` currently reads exchanges only (`:426-445`). For every active run it must arm **two** wakes, not one: the wall clock at `StartedAt + ParkedSeconds + WallClock`, **and a bounded stall wake** — `SpawnTimeout` after the last event on that run — whenever the run has nothing open and nothing in flight. The previous draft armed only the 8-hour wake, so table row 19's `Park("the run stalled")` was unreachable in production: by the time the wake fired, row 2 matched first and parked as a wall-clock cap. A stalled run was indistinguishable from an exhausted one and cost the owner a full night.

**9c. The 30-minute in-run timeout.** `:341` passes `_limits.Timeout`; in a run it passes `_runLimits.SpawnTimeout`. Capture `inRun` **at launch**, not in the task body — a run can end while a spawn is alive. The two notes at `:377-378` interpolate the same captured value.

**9d.** `_runs.CountSpawn` in `Launch` inside the existing try, after `ExchangePolicy.Started` — a spawn that fails to start still consumed an attempt.

**9e. Restart (AC12).** In `HubHost.Build`, after the database opens and before serving: park every active run, reason "the hub restarted while this run was active", `capSpent: false`, stamping `parked_at` so the resume excludes the downtime. **No notes** — the message signal is not wired at that point.

**9f. Resume (AC15).** Reached only by a human post that task 4's ordering did not already claim. If `cap_spent`, `Refuse` (table row 16); otherwise `_runs.Resume` — which accumulates `parked_seconds` — then carry out `OpenConductor`.

**Pass 2's F-4 is why `parked_seconds` exists at all.** A3's designed flow is: hub restarts overnight → AC12 parks → the owner resumes with a post. With `Elapsed` measured from `StartedAt`, the first event after that resume matches table row 2 and parks as a **spent** wall-clock cap, which 9f then makes permanently unresumable. The resume path was inert precisely where A3 requires it and converted a recoverable park into a dead run.

**Ruling, logged not asked:** a resume after a spent hard cap is refused, because D9 calls these hard caps and a resume that immediately re-parks is worse than a refusal that says so. Reversible if the owner disagrees.

**Verify:** with `RunLimits(Spawns: 2, WallClock: 1min, SpawnTimeout: 1s, PhaseEntries: 2)` and a fake `TimeProvider` — each cap parks with its own note and the right `cap_spent`; a parked run with an in-flight conductor launches nothing when that conductor posts; **an idle run parks on the stall wake, not on the wall clock, and says "stalled"**; **a run parked, advanced past the wall clock while parked, then resumed, does not immediately re-park**; a stored active run is parked by the time `/health` answers.

---

### Task 10 — conductor silence (A2)

**Files:** `SpawnerService.cs`, tests.

`OnFinished` raises `SpawnSilent` when `!h.Posted` **and increments `(runId, phase) → Silences`** (pass 2's F-2: the second write site that did not exist). Table rows 10–13 decide. First silence: ask again with the same triggers. Otherwise, if entries are below the cap: **count a phase entry and ask again**; at the cap: park. The previous draft stopped after the second silence without parking, leaving the run active with nothing driving it — the stall pass 1's B3 closed, reintroduced by the fold.

Ticket 10's definition of done is "**at most twice per phase entry**", never "exactly twice per phase".

**Verify:** a never-posting conductor is asked twice per entry, raises the entry count each time, and with a small cap leaves the run **parked**, never active. Assert on launch counts and run status.

---

### Task 11 — effort by class (AC7, D10)

**Files:** `SpawnCommands.cs`, `SpawnerService.cs`, `SpawnCommandsTests.cs`.

Pass 1's B5: the previous draft asserted AC7, cited the probe, and had no task at all.

Each of the four builders gains an optional `string? effort = null`, appending `"--effort", effort` (Claude) or `"-c", $"model_reasoning_effort={effort}"` (Codex), and **nothing** when null. `Launch` computes `high` when `run is not null && (participant.Id == run.ConductorId || ParticipantClasses.Has(participant, ParticipantClasses.Judge))`, else null. Never `xhigh` or `max` (D10).

Measured this session (ledger claim 6): the Codex form binds under `--ignore-user-config`, banner-confirmed at `high` and `low`, exit 0 both. F10 resolved.

**Verify:** argument-list assertions for both hosts × {in-run conductor, in-run judge, in-run plumbing, out-of-run}; the last two contain no effort token. Note in the report that the Codex arm has **no live coverage** this row (no Codex roster row carries a class — Could not verify).

---

### Task 12 — `run_gate`

**Files:** new `Mcp/RunTools.cs`, `SkillStore.cs`, `SkillImport.cs`, `SpawnCommands.cs`, `HubHost.cs`, new `RunToolsTests.cs`.

**12a. A whole-tree manifest (P5).** At import, walk `data/skills/<name>/` and record `relative path → sha256` for **every** file in the `skills` table (never beside the skill — D-i). Hash the **installed** files after the rename swap, as `SkillImport` already does for SKILL.md, not the source bytes (pass 1's m4). A declared gate whose script is missing is a hard import failure naming the file; a skill named `stop` is refused (task 13).

**12b. `SkillStore.VerifyTree(skill)`** → `Ok` / `Missing(path)` / `Tampered(path)`, re-hashing every entry and detecting extra files. `ReadGate(skill, gate)` → `NotDeclared` / `Missing` / `Tampered(path)` / `Ok(GateDeclaration)`.

**12c. Execute from a verified copy — and give the gate its state back.** On `Ok`, copy the tree to a fresh folder under the data dir and run from there, closing the hash-then-run window. **Two things the previous draft got wrong (pass 2's F-15):**
- Moving `$PSScriptRoot` silently voids a gate's own legitimate sibling writes. `Check-RoadmapBudget.ps1` reads and writes `Join-Path $PSScriptRoot 'baselines.json'` (`:77`, `:333`, `:370`), so from a throwaway copy the ratchet **stops ratcheting with no error**. `run_gate` therefore sets `ROADMAP_GATE_BASELINE` — the script's own documented seam, verified this session — to a hub-private durable path, and unsets it after.
- Nobody owned deleting the copies. Delete in a `finally`, per call, and say so.

**12d. `RunTools.RunGate(room_id, gate)`** checks, each with its own refusal: caller resolves; an active run exists in the room; **the caller is in flight for that room, read from `Snapshot(roomId).InFlight`** — `_inFlight` is loop-thread-only (`:83`) and enumerating it from an HTTP thread is a data race (pass 1's M11), while `Snapshot` is an immutable republished value; no other gate is running there (a per-room lock); `VerifyTree` and `ReadGate` are `Ok`; the room has a directory.

Then `IProcessRunner` (never a raw `Process`) with `pwsh` via `CliResolver` (never a bare name), `-NoProfile -NonInteractive -File <verified copy>` **followed by exactly the arguments the gate declares** (pass 2's F-14: `Check-RoadmapBudget.ps1:70-71` declares `[Parameter(Mandatory)][string]$RoadmapPath`, and under `-NonInteractive` a mandatory-parameter prompt throws — so with no argv the gate P5 is built around cannot run at all, while task 15's parameterless toy gate passes, which is the test asserting the thing it stubbed). Cwd is the room directory; timeout `RunLimits.SpawnTimeout`. Returns `{gate, exit_code, timed_out, stdout, stderr}`, stdout capped at 8,000 **tail-kept**, stderr 2,000.

**Every call — run or refusal — writes a `run_gate_runs` row and posts a hub note in a fixed form**: `run_gate <gate> by @<id>: exit <n>` for a run, `run_gate <gate> by @<id>: refused (<reason-slug>)` for a refusal (pass 2's F-12: the previous draft's note had no shape for a refusal, which has no exit code, and its table's `run_id` was `NOT NULL` while two refusal arms have no run id).

**12e. Exposure.** `ClaudeRunToolsAllowed = ClaudeDirectoryToolsAllowed + ",mcp__chopitup__run_gate"`, passed as a parameter to `ClaudeInDirectory` for in-run spawns. Codex needs no allowlist change; 12d's in-flight check is what gates it.

**12f. The Codex tool timeout (pass 1's M7).** `CodexInDirectory` passes `tool_timeout_sec=60` (`:168`) against a 30-minute gate. For in-run Codex spawns pass `RunLimits.SpawnTimeout` in seconds. **Open question — measure, do not assume:** whether the Claude CLI imposes its own MCP tool timeout and how it is raised. Report the measurement.

**12g.** Register `.WithTools<RunTools>()` at `HubHost.cs:88-90`.

**Verify:** each refusal arm by its own message and record; a declared gate exits 3 with known text on both streams; **a gate with a mandatory parameter runs when its declaration supplies it, and is refused rather than hung when it does not**; a tampered **sibling** file makes it refuse, proven by the absence of a side effect; a second concurrent call refuses; a `run_gate_runs` row and the fixed-form note exist for both a run and a refusal, the latter with a null `run_id`.

---

### Task 13 — `/stop` and the stop control

**Files:** new `src/ChopItUp.Core/Skills/RunCommands.cs`, `SpawnerService.cs`, tests.

`/stop` is checked **before** skill resolution and before task 9f's resume (task 4 step 1), or the owner is either told "No skill named /stop" while the run spends, or — worse — **resumes the run they were trying to stop** (pass 2's F-9). `RunCommands.IsStop(body)` reuses `SlashCommands.TryParse`; `SkillImport` refuses a skill named `stop`.

On `/stop` with an active **or parked** run: `RunPolicy` returns `End` (table row 17, which is evaluated ahead of the cap rows), the service ends the run, cancels in-flight spawns, clears the steers, posts the counters note, and **returns without falling through to `ResolveSkill`** (pass 1's m1).

`POST /api/rooms/{roomId}/exchange/stop` ends the run too. Extend `SpawnerService.OnStop`, not the API method — and note `OnStop` currently returns null (409) when nothing is in flight and no exchange is open, which with an active run must still end the run and return a snapshot.

**Verify:** `/stop` ends an active run and a parked one; stops the spawn; posts the counters; produces no unknown-skill note; the API stop does the same including with nothing in flight; `/stop` with no run behaves exactly as before; a skill named `stop` cannot be imported.

---

### Task 14 — the run strip in the UI (`opus` builder — owner-visible)

**Files:** new `client/src/RunBar.tsx`, `App.tsx`, `types.ts`, `api.ts`, `styles.css`, new `Web/RunsApi.cs`, `RunsApiTests.cs`.

`GET /api/rooms/{roomId}/run` returns the active-or-most-recent run or 204: `{ id, roomId, conductorId, skillName, status, reason, capSpent, phase, phaseEntries, phaseEntryCap, exchanges, spawnsUsed, spawnCap, startedAt, endedAt, elapsedMinutes, wallClockCapMinutes, artifacts, gateRuns }`.

`RunBar` sits above `ExchangeBar` and renders nothing without a run. Three states, distinguishable at a glance from across the room: `active` (phase prominent, counters a quiet trailing line), `parked` (**the reason is the loudest thing in the strip**), `ended` (muted, one line). Follow `ExchangeBar`'s conventions: a total `Record<RunSnapshot['status'], string>` map so a new status is a compile error, `role="status" aria-live="polite"`, `memo`, `.run-*` classes.

**The live-check scope (pass 2's F-20).** `RunLimits` is injectable only through `HubHost.Build`/`HubTestHost`, **not through the shipped hub's CLI**, so reaching `parked` against a real hub means 8 hours, 80 spawns, or three real phase re-entries — impossible in a builder task. So: the browser leg proves `active` and the 204; **the `parked` and `ended` renders are proved in `RunsApiTests` plus a component test against a hand-written parked row.** Do not fake a browser park.

**The cross-room question (LESSONS M9), decided here.** This row: the strip is per-room and rides the existing per-room refresh; a park in a room the browser is not showing is not surfaced until the owner opens it. Acceptable **because the park note mentions `@owner` and M9's rail already raises an unread badge on any room with a new message** — the owner gets a signal, just not a run-specific one. Not this row: a run-specific rail badge. Record that reasoning in the ticket.

---

### Task 15 — the real-CLI check: a two-phase toy skill

**Files:** new `tools/Invoke-M19RunCheck.ps1`, `tools/skills/toy-run/SKILL.md`, `tools/skills/toy-run/scripts/count-files.ps1`, `CLAUDE.md`, new `docs/verification.md`.

**15a. The toy skill** declares `run: true` and `gates: count-files`, and shows the conductor **the exact shape**:

```
phase: build
@<plumbing row> create hello.txt in this directory with one line in it, then run the count-files gate.

phase: ping
The run is done.
```

Short on purpose: the shorter it is, the less the check depends on a model reading carefully.

**15b.** Reuse `Invoke-M11SkillCheck.ps1`'s scaffolding verbatim — `param` shape, `Add-Check`, `Invoke-Api`, `Wait-Exchange`, `Get-Messages`, fresh-directory guards, hub `Start-Process` with `--rooms-root`, the orphan sweep in `finally`, `"Results: $passed/$total PASS"`, exits 0/1/2 — plus `Wait-Run`. The room is created **with a directory**.

**15c. The checks:** `health.schema-is-8`; `run.started`; `run.start-note` (matches the literal `Run #<id> started` task 4 now pins); `run.no-directory-refused`; `run.two-phases` (two distinct recorded tags, one kind `build` and one `ping`); `run.exchanges-at-least-three`; **`run.one-human-message`** (the acceptance check); `run.file-created`; `run.artifact-author`; `run.ended-not-parked`; `run.no-failure-notes`; `gate.ran-in-run` (a `run_gate_runs` row for `count-files`, exit 0, plus its fixed-form note).

**15d. State the ambiguity rather than hiding it (pass 2's F-17).** `gate.ran-in-run`, `run.two-phases`, `run.file-created` and `run.artifact-author` are asserted on hub-written records but are **model-triggered**: a FAIL is ambiguous between a hub defect and a conductor that did not comply, and M10's "tolerate one re-run" covers API flake, not non-compliance. Print a diagnostic on those FAILs — whether the worker exchange opened and concluded at all — so the orchestrator can tell the two apart without reading the transcript. Say this in a comment at the head of the script.

**15e. `CLAUDE.md` is at 3,965 bytes against a 4 KB contract** and the gate ratchets it. Move the six check-script lines into a new `docs/verification.md`, leave one pointer line, and add the new script there. Do not solve this by shortening the line.

**Verify:** `pwsh tools\Invoke-M19RunCheck.ps1` exits 0, every check PASS. Orchestrator-run; spends real model calls.

---

### Task 16 — deploy, and the rollback sentence (pass 2's F-19)

**Files:** `docs/verification.md`, `ROADMAP.md` Notes; no source.

`CLAUDE.md:29` says "Merged-but-not-deployed is not done", and `ChopDb.EnsureDatabase` **throws** when `version > LatestSchemaVersion` (`:104-107`). So the moment the live `data\chop.db` reaches v8, the previously-deployed v7 exe **refuses to start** — a schema row with no stated deploy order and no rollback is a row that can strand the owner's live hub. The check scripts are safe (they default to fresh `$env:TEMP` data dirs), so this is a deploy gap, not a check gap.

The orchestrator runs, in order, and records the result in the board Notes:
1. Stop the live hub (it holds `HubLock`; a migration under a running v7 process is the case nobody wants to debug).
2. `pwsh tools\Deploy-ChopItUp.ps1` — which stages, self-checks with `tools\Invoke-M4SelfCheck.ps1`, and preserves the co-located `data\`. Expect one harness approval prompt per write under `C:\Self Apps\`; that is a gate, not a stall.
3. Start the hub and confirm `/health` reports `schema: 8`.
4. **Rollback, written down before it is needed:** restore the pre-migration backup `ChopDb` wrote (`BackupBeforeMigration`, path in its log line) over `data\chop.db`, redeploy the previous exe from the backup-aside directory `Deploy-ChopItUp.ps1` leaves, and restart. A v8 database and a v7 exe cannot coexist, so the database restore is not optional.

**Verify:** `/health` reports `schema: 8` against the deployed install, and the backup path is recorded in the board Notes so the rollback is executable by someone who was not here.

---

## Claim ledger

Established this session (2026-09-07) against `6589cd3` unless a row says otherwise.

**Every recheck below is pipe-free by construction.** Pass 2's F-5: the previous ledger carried raw `|` inside four cells, and the workflow's own `Check-PlanClaims.ps1` split those rows into 5 or 6 cells and degraded the recheck to a broken fragment that could only ever exit non-zero. Running the rechecks by hand — which is what earlier drafts did — is structurally blind to that, because the defect is in the table parser, not the commands. This ledger is verified by running `Check-PlanClaims.ps1 -ParseOnly` and confirming every row parses into 4 cells.

| # | Claim | Verified at | Recheck (pwsh, exit 0 = holds) |
|---|-------|-------------|--------------------------------|
| 1 | Baseline: 421 tests green (112 Core + 309 Hub), 0 failed, exit 0 — measured, not copied | 6589cd3 | `dotnet test ChopItUp.slnx -c Debug --nologo -v minimal; if ($LASTEXITCODE -ne 0) { exit 1 }` |
| 2 | `LatestSchemaVersion` is 7 at `ChopDb.cs:10`; the ladder is linear `if (GetUserVersion(conn) < N) ApplyVN(conn);` at `:112-118`, ending at 7 | 6589cd3 | `if ((Select-String -Path src/ChopItUp.Core/Storage/ChopDb.cs -Pattern 'LatestSchemaVersion = 7').Count -ne 1) { exit 1 }` |
| 3 | Six `.ps1` sites assert `$health.schema -eq 7`; task 1h sweeps exactly these | 6589cd3 | `if (@(Select-String -Path tools/*.ps1 -Pattern 'schema -eq 7').Count -ne 6) { exit 1 }` |
| 4 | `Invoke-M2DryRun.ps1:249` asserts `tokenKeys -eq 14` and stays 14: this row adds no roster row | 6589cd3 | `if (-not (Select-String -Path tools/Invoke-M2DryRun.ps1 -Pattern 'tokenKeys -eq 14')) { exit 1 }` |
| 5 | `OnMessage` opens an exchange only for `author.Kind == "human"` (`:62`), supersedes at `:64-70`, returns early for models at `:110`. Tasks 4, 6, 8 edit exactly those | 6589cd3 | `if (-not (Select-String -Path src/ChopItUp.Hub/Spawning/ExchangePolicy.cs -Pattern 'author.Kind == "human"')) { exit 1 }` |
| 6 | **F10 RESOLVED (was UNVERIFIED in the grill ledger).** `codex exec --ephemeral --ignore-user-config -c model_reasoning_effort=<level>` binds: the banner reported `high` and `low` on demand, exit 0 both, codex-cli 0.153.3 at `C:\Users\cayov\.local\bin\codex.cmd`. D10's Codex half is implementable as written | measured 2026-09-07 | `if ("$(& codex exec --ephemeral --ignore-user-config -c model_reasoning_effort=low --skip-git-repo-check -m gpt-6-astra --color never 'say OK' 2>&1)" -notmatch 'reasoning effort: low') { exit 1 }` (spends a Codex call) |
| 7 | The flag `claude --effort` **exists** (recheck executed this session, exit 0). Its accepted value list `low, medium, high, xhigh, max` is **carried from grill-ledger F2 and was NOT re-measured** — the recheck below proves the flag, not the values | flag 2026-09-07; values F2, 2026-09-06 | `if ("$(claude --help 2>&1)" -notmatch '--effort') { exit 1 }` |
| 8 | No effort or reasoning flag is passed by any spawn builder today; no `run_gate`, `runs` table or `phase:` parsing exists in `src/` — the one `run_gate` hit is a doc comment at `SkillStore.cs:72` naming row 19 | 6589cd3 | `if (Select-String -Path (Get-ChildItem src -Recurse -Filter *.cs).FullName -Pattern @('model_reasoning_effort','--effort')) { exit 1 }` |
| 9 | `SpawnLimits.Default` = 4 / 2s / 10s / 5min / 60 / 24,000, injected through `HubHost.Build`'s optional seam — the seam `RunLimits` and the clock reuse | 6589cd3 | `if (-not (Select-String -Path src/ChopItUp.Hub/Spawning/SpawnLimits.cs -Pattern 'Timeout: TimeSpan.FromMinutes\(5\)')) { exit 1 }` |
| 10 | `_limits.Timeout` reaches the runner at exactly one site (`:341`) and two notes (`:377-378`). Task 9c changes all three | 6589cd3 | `if (@(Select-String -Path src/ChopItUp.Hub/Spawning/SpawnerService.cs -Pattern '_limits.Timeout').Count -ne 3) { exit 1 }` |
| 11 | The conclusion seam is `SpawnerService.cs:403-405`: `Finished` returns a note, it is posted, `Publish` runs. Task 5b hooks after `Publish` | 6589cd3 | `if (-not (Select-String -Path src/ChopItUp.Hub/Spawning/SpawnerService.cs -Pattern 'ExchangePolicy.Finished\(h.Exchange, id\)')) { exit 1 }` |
| 12 | **Exchanges are not persisted** — no `exchanges` table in any migration; `Exchange.cs:17` says so. This is why AC12 parks rather than resumes, and why AC15's resume always goes through `OpenForConductor` | 6589cd3 | `if (Select-String -Path src/ChopItUp.Core/Storage/ChopDb.cs -Pattern 'CREATE TABLE IF NOT EXISTS exchanges') { exit 1 }` |
| 13 | `ParticipantClasses.Has` reads the cell as a comma-separated SET, so task 8's rules use `Has`, never a string compare on `p.Classes` | 6589cd3 | `if (-not (Select-String -Path src/ChopItUp.Core/Model/ParticipantClasses.cs -Pattern 'public static bool Has')) { exit 1 }` |
| 14 | `ParticipantStore` exposes `OwnerId()`/`HumanIds()` and has **no write method**; `RunStore` follows `MemoryProposalStore`'s shape for writes | 6589cd3 | `if (-not (Select-String -Path src/ChopItUp.Core/Storage/ParticipantStore.cs -Pattern 'public IReadOnlyList<string> HumanIds')) { exit 1 }` |
| 15 | `SkillHashes` fingerprints `SKILL.md` **only** — nothing else under `data/skills/<name>/`. This is why task 12a manifests the whole tree | 6589cd3 | `if (-not (Select-String -Path src/ChopItUp.Hub/Skills/SkillStore.cs -Pattern 'SHA256.HashData\(bytes\)')) { exit 1 }` |
| 16 | `ClaudeInDirectory` passes `--settings` with a deny list; `CodexInDirectory` passes none. F6's asymmetry confirmed in code | 6589cd3 | `if (-not (Select-String -Path src/ChopItUp.Hub/Spawning/SpawnCommands.cs -Pattern 'ClaudeDenyRules')) { exit 1 }` |
| 17 | MCP tools register as `.WithTools<RoomTools>().WithTools<MemoryTools>()` at `HubHost.cs:88-90`; task 12g appends `RunTools` | 6589cd3 | `if (-not (Select-String -Path src/ChopItUp.Hub/Hosting/HubHost.cs -Pattern 'WithTools<MemoryTools>')) { exit 1 }` |
| 18 | `CommitOutcome(Hash, Created, FilesChanged, Reason)` carries a hash and a **count**, not paths — so task 5c's `ChangedFilesAsync` is new work | 6589cd3 | `if (Select-String -Path src/ChopItUp.Hub/Git/GitTrail.cs -Pattern 'ChangedFilesAsync') { exit 1 }` |
| 19 | `SlashCommands.TryParse` matches only the first line, so `/stop` parses and task 13 is a name comparison, not new parsing | 6589cd3 | `if (-not (Select-String -Path src/ChopItUp.Core/Skills/SlashCommand.cs -Pattern 'public static bool TryParse')) { exit 1 }` |
| 20 | `HubTestHost.StartAsync` already takes optional `SpawnLimits?` and `CliLocator?` seams with fakes, so tasks 2, 9 and 12 extend a pattern rather than invent one | 6589cd3 | `if (-not (Select-String -Path tests/ChopItUp.Hub.Tests/HubTestHost.cs -Pattern 'SpawnLimits\? limits = null')) { exit 1 }` |
| 21 | **Correction to grill-ledger F5.** F5 says the 60-message cap "is not enforced". Half right: `Trim` reads only `TranscriptChars`, but `SpawnerService.cs:286` calls `ReadLast(roomId, _limits.TranscriptMessages)` and `MessageStore.ReadLast(string, int)` takes it as a row limit — the cap **is** enforced, at the read. No task may "fix" it | 6589cd3 | `if (-not (Select-String -Path src/ChopItUp.Hub/Spawning/SpawnerService.cs -Pattern 'ReadLast\(request.RoomId, _limits.TranscriptMessages\)')) { exit 1 }` |
| 22 | `data/skills/` sits under the data dir and `RoomPathRules.ForHub` refuses any room directory under it, so task 12's cwd is always disjoint from the gate's home | 6589cd3 | `if (-not (Select-String -Path src/ChopItUp.Hub/Rooms/RoomPaths.cs -Pattern "the hub's data folder")) { exit 1 }` |
| 23 | **(pass 1 M8) The first draft's phase regex rejected the natural conductor post.** Probed with the exact draft pattern: `phase: build` true, `phase: build @sonnet make hello.txt` **false**, `**phase: build**` false. Task 2c's lookahead is the fix. The recheck below uses a pipe-free equivalent of that lookahead | measured 2026-09-07 | `$rx = [regex]'^phase:[ \t]+(?<kind>[a-z]+)(?:/(?<name>[a-z0-9-]{1,32}))?(?![^\s])'; if (-not $rx.IsMatch('phase: build @sonnet go')) { exit 1 }` |
| 24 | **(pass 1 M6) The roadmap gate reads AND WRITES an unhashed sibling.** `Check-RoadmapBudget.ps1:77` defaults `$BaselinePath` to `Join-Path $PSScriptRoot 'baselines.json'`, reads it at `:333`, writes it at `:370`. A per-script hash leaves verdict subversion open (P5), and executing from a copy voids the legitimate write unless `ROADMAP_GATE_BASELINE` is set (task 12c) | measured 2026-09-07 | `if (-not (Select-String -Path "$env:USERPROFILE\.claude\skills\roadmap\preflight\Check-RoadmapBudget.ps1" -Pattern 'PSScriptRoot .baselines.json')) { exit 1 }` |
| 25 | **(pass 1 M7) Codex in-run spawns cap MCP tool calls at 60 s** — `SpawnCommands.cs:168` passes `tool_timeout_sec=60`, against a 30-minute gate. Task 12f raises it | 6589cd3 | `if (-not (Select-String -Path src/ChopItUp.Hub/Spawning/SpawnCommands.cs -Pattern 'tool_timeout_sec=60')) { exit 1 }` |
| 26 | **(pass 2 F-16) `Bash` is NOT denied to a Claude spawn.** `ClaudeBuiltins = "Read,Edit,Write,Glob,Grep,Bash"` (`SpawnCommands.cs:75`) and `ClaudeDenyRules` denies only git write verbs, `.git` writes, credential folders and `Read/Write/Edit(<dataDir>/**)` (`:110-135`). The data-dir fence binds the file tools and not the shell, so the residual risk is not Codex-only | 6589cd3 | `if (-not (Select-String -Path src/ChopItUp.Hub/Spawning/SpawnCommands.cs -Pattern 'ClaudeBuiltins = "Read,Edit,Write,Glob,Grep,Bash"')) { exit 1 }` |
| 27 | **(pass 2 F-13) No clock seam exists anywhere.** No `TimeProvider`, `IClock` or test-clock parameter in `src/` or `tests/`; `SpawnerService` calls `DateTimeOffset.UtcNow` inline. Task 2b adds it, and without it task 9's prescribed verification cannot be written | 6589cd3 | `if (Select-String -Path (Get-ChildItem src,tests -Recurse -Filter *.cs).FullName -Pattern 'TimeProvider') { exit 1 }` |
| 28 | **(pass 2 F-14) `Check-RoadmapBudget.ps1` declares `[Parameter(Mandatory)][string]$RoadmapPath`**, so under `pwsh -NonInteractive -File` with no argv it throws rather than running. Gates need declared arguments (task 2d, 12d) or the gate P5 is built around cannot run at all | measured 2026-09-07 | `if (-not (Select-String -Path "$env:USERPROFILE\.claude\skills\roadmap\preflight\Check-RoadmapBudget.ps1" -Pattern 'Mandatory')) { exit 1 }` |
| 29 | **(pass 2 F-25) No Codex-hosted roster row carries a class.** `opus` is `visible,judge`, `sonnet` `plumbing`, `fable` `judge`; every `gpt-*` seed row defaults `Classes` to null. Task 15's toy run therefore exercises a Claude-hosted row only, leaving the Codex effort flag, the Codex tool-timeout raise and the Codex residual-risk path without live coverage this row | 6589cd3 | `if (-not (Select-String -Path src/ChopItUp.Core/Storage/ChopDb.cs -Pattern 'OwnerRemoteParticipantId')) { exit 1 }` |
| 30 | The D9 caps, the phase vocabulary, the class rules and the run-state field list are **owner rulings**, not measurements | — (D8, D9, D10; owner 2026-09-06) | — |
| 31 | That a competent model conducts two phases from the toy skill without owner input is **unproven until task 15 runs**. It is the row's acceptance and no unit test substitutes | — | — |
| 32 | The Claude CLI's own MCP tool timeout is **unknown**; task 12f measures it | — | — |
| 33 | Whether a spawn can write the owner's `PSModulePath` is **inferred** from claims 16 and 26, not probed | — | — |

---

## Critique dispositions

**Pass 1 (fable, FIX-THEN-SHIP, 5.0)** — 6 blockers, 12 majors, 5 minors, all accepted, none declined. Summary: B1 `ping` refused so no run could end normally → AC14 plus a mention-exempt `ping` arm. B2 `EnterPhase` never called → task 8 calls it on every accepted post. B3 loop non-termination → P7's `RunPolicy`, the termination obligation, and the run tick. B4 no resume → AC15, `RunStore.Resume`, `cap_spent`. B5 AC7 had no task → task 11. B6 live gate checks unsatisfiable → `run_gate_runs` plus a fixed-form note. M1 refusal re-spawn double-opened → refusals open nothing. M2 human post fell through → task 4's ordered branch. M3 park closed nothing → task 9a. M4 author rule vacuous → normalization in `RunStore`. M5 authorship missed self-committed work → diff the spawn's range. M6 per-gate hash insufficient → P5 whole-tree manifest. M7 Codex 60 s tool cap → task 12f. M8 grammar rejected the natural post → task 2c. M9 skill lost on re-spawn → `OpenForConductor` sets `Skill`. M10 scope rationale and seam unsound → rewritten. M11 `_inFlight` data race → `Snapshot`. M12 ticket graph had a skippable leaf → re-cut. m1–m5 folded into tasks 12, 13, 1 and 14.

**Pass 2 (opus, FIX-THEN-SHIP, 6.3)** — 5 blockers, 16 further findings, no re-reports. All accepted, none declined. Two were independently re-verified by measurement before folding (F-5 by running the harness parser, F-16 by reading `SpawnCommands.cs`).

| # | Finding | Disposition |
|---|---|---|
| **F-1** | `ExchangeConcluded` ignored `ExchangeOpen`, so the conductor's own exchange concluding clobbered the worker exchange it had just rooted — the run would loop conductor→conductor to the spawn cap while a live check passed spuriously | **Fixed twice over.** Table rows 8/9 read `!ExchangeOpen && !AnythingInFlight`, and task 5b raises the event only for the room's current exchange. |
| **F-2** | `RefusalsThisPhase`/`SilencesThisPhase` declared, consumed and reset, **never incremented** — silently disabling D8's second-refusal park and A2's second-silence entry | **Fixed.** Write sites named in tasks 8 and 10; the Architecture paragraph corrected (it said the service holds no counters while task 8 put them there); an end-to-end pin added whose failure mode is the counter staying zero. |
| **F-3** | The re-entry cap could not be evaluated: the policy got entries for the tag being *left* | **Fixed.** `RunState.PhaseEntries` is the whole map; `RunStore.PhaseEntries(id)` returns it. |
| **F-4** | `Elapsed` from `StartedAt` made AC15's resume destroy itself on its primary case — an overnight restart-park re-parks as a *spent* cap on the first event, and 9f then makes it permanently unresumable | **Fixed.** `parked_seconds`/`parked_at`; `Elapsed` excludes parked time; AC8 reworded to "time it spent active". |
| **F-5** | Four ledger rows carried raw `|`; the workflow's own `Check-PlanClaims.ps1` split them into 5–6 cells and degraded the rechecks to fragments that can only exit non-zero. Hand-running the commands is structurally blind to this | **Fixed and verified by running the parser.** Every recheck is now pipe-free by construction, and the ledger header says why. |
| **F-6** | Task 6's headline behaviour (steer into an idle run) had no arm; `pendingSteers` was a parameter nothing consumed | **Fixed.** Table row 18. |
| **F-7** | `Park("the run stalled")` was unreachable in production — the only `Tick` source was the 8-hour wake, by which time the wall-clock row matched first | **Fixed.** Task 9b arms a bounded stall wake as well as the wall clock. |
| **F-8** | `EnterPhase` could be called before any phase existed (null, or an ad-hoc empty string) | **Fixed.** `runs.phase` is `NOT NULL DEFAULT '(start)'`; `EnterPhase`'s parameter is non-nullable. |
| **F-9** | `/stop` on a parked run **resumed** it; a run-start invocation into a parked room raced the unique index | **Fixed.** Task 4's human branch is an explicit ordered list: stop, start, steer, then refusals; resume is reached only by what none of those claim. |
| **F-10** | `cap_spent` was `true` for the phase cap on one path and `false` for the same cap on another, producing a resume/re-park loop | **Fixed.** `capSpent` is a property of which cap, never of which path; stated above the table. |
| **F-11** | `Refuse` carried two different obligations, pushing the choice back into the service | **Fixed.** Split into `Refuse` and `RefuseAndAsk`. |
| **F-12** | `run_gate_runs.run_id NOT NULL` could not record the refusals AC10 requires, and the fixed-form note had no shape for a refusal | **Fixed.** Nullable `run_id` plus `room_id`; a refusal note form specified. |
| **F-13** | No clock seam exists anywhere, so task 9's "with a fake clock" named something that does not exist and the fold's headline fix had no test route | **Fixed.** Task 2b adds `TimeProvider` through the existing `HubHost.Build`/`HubTestHost` seam; `RunStore.Start` gains `now`. Ledger claim 27. |
| **F-14** | `run_gate` passed no arguments while the gate all of P5 is built around has a mandatory parameter — so it could not run at all, while the parameterless toy gate passed | **Fixed.** Gates declare their arguments in fingerprinted frontmatter; `run_gate` passes exactly those. Ledger claim 28. |
| **F-15** | Executing from a verified copy silently voids the roadmap gate's own baseline write, and nobody owned deleting the copies | **Fixed.** `run_gate` sets `ROADMAP_GATE_BASELINE` to a hub-private durable path; cleanup in a `finally`, per call. |
| **F-16** | `Bash` is not denied, so the integrity story is bypassable on the **Claude** side too — the residual risk was wrongly scoped to Codex | **Fixed as an honesty correction.** Residual-risk section rewritten and moved up into the blast-radius paragraph; AC9 now carries gate results so the conductor reads verdicts from hub state. Row 13's Notes should gain this. Ledger claim 26. |
| **F-17** | Task 15's checks are hub-recorded but **model-triggered**, so a FAIL is ambiguous; "nothing reads a model's wording" was true and beside the point | **Fixed.** Stated in task 15d and in Could-not-verify, with a diagnostic on the ambiguous checks. |
| **F-18** | The fallback seam contradicted the ticket graph, and the only legal seam leaves 19a with no real-CLI check, which D11 forbids | **Fixed by deletion,** per pass 2's explicit ruling. One row; the Scope section now says why on task interdependence and spec-legality rather than on D11's reach. |
| **F-19** | No deploy ordering and no rollback on a schema row: once the live database reaches v8 the deployed v7 exe refuses to start | **Fixed.** New task 16, with the ordering and the rollback written down before it is needed. |
| **F-20** | Task 14's live check had no route to `parked` — `RunLimits` is not injectable through the shipped CLI | **Fixed.** The browser leg proves `active` and the 204; `parked`/`ended` are proved in API and component tests. |
| **F-21** | Task 5c's artifact recording runs off the spawner loop with an unspecified run lookup | **Fixed.** The lookup is `_runs.Active(roomId)`, a database read, never `_rooms`/`_inFlight`; the SQLite concurrency assumption is stated. |
| **F-22** | The silence counter never reset, because reset was on a tag *change* and the silence path re-enters the same tag | **Fixed.** Reset on every `EnterPhase`. |
| **F-23** | Task 4's early return makes task 6's supersede gate unreachable | **Fixed.** Task 6 says which is load-bearing and keeps the outside-a-run regression. |
| **F-24** | `ExchangeConcluded(LastMessageId)` had no named source | **Fixed.** `_store.ReadLast(roomId, 1)`, in task 5b. |
| **F-25** | No Codex roster row carries a class, so the entire Codex path has no live coverage | **Accepted as a limitation, not fixed.** Seeding a Codex class is not this row's job and would change the roster the live checks assert against; it is now in Could-not-verify and in task 11's report. |
| **F-26** | `run.start-note` asserted wording no task pinned | **Fixed.** Task 4 pins the literal `Run #<id> started` prefix. |
| **F-27** | `AnythingInFlight` was unscoped | **Fixed.** Documented as per-room (`InFlightIn`), never the global `AnySpawnInFlight`. |

**Tier:** both passes attacked it; HIGH holds and pass 2 strengthened the reasoning (F-16 makes the exposure not Codex-only). No change.

**Plan size:** over the 60 KB threshold, deliberately, and the harness WARNs about it on every parse. See Scope: 16 tasks each carrying the code a builder needs. Shrinking the file without splitting the row moves risk out of the plan and into the builders — which is what produced five of pass 1's six blockers and three of pass 2's five.
