# Live verification checks

Moved out of `CLAUDE.md` (row 19, task 15e) to keep that file under its 4 KB contract, which the
roadmap gate ratchets. `CLAUDE.md` keeps one pointer line to here.

These spend real model calls against the owner's Claude/Codex subscriptions. They are
orchestrator-run, never CI, never automatic. Every script defaults to a fresh directory under
`$env:TEMP`, never touches `C:\Self Apps`, `%USERPROFILE%\ChopItUp` or any real data directory, and
sweeps its own orphan processes in a `finally` block. Each prints PASS/FAIL per check and ends with
`Results: n/m PASS`.

Spawn check (real CLIs, scratch hub): `pwsh tools\Invoke-M5SpawnCheck.ps1`; CLI contract re-measure: `tools\Probe-SpawnCli.ps1` — both orchestrator-run, both spend.
Memory check (real Sonnet, scratch hub, spends): `pwsh tools\Invoke-M10MemoryCheck.ps1`.
Memory v1.1 check (no model calls, scratch hub, drives /mcp itself): `pwsh tools\Invoke-M18MemoryCheck.ps1`.
Row 23 consolidation dry run (no model calls, scratch hub, fabricated 12-topic corpus, drives
`propose_rewrite` and the approve path itself): `pwsh tools\Invoke-M23DryRun.ps1`.
Row 40 editor dry run (no model calls, scratch hub, fabricated corpus, drives the editor routes, the
trail they leave and the .bak restore): `pwsh tools\Invoke-Row40MemoryEditCheck.ps1`.
Row 42 inert-import dry run (no model calls, scratch hub, fabricated v2 corpus migrated to v13, CLI PATH
stripped so an accidental spawn fails loudly, drives the import route and a live control post):
`pwsh tools\Invoke-Row42ImportCheck.ps1`.
Continue, the turns token and the synthesis turn (row 44, stub Codex, no model calls): `pwsh tools\Invoke-Row44ContinueCheck.ps1`.
Git attribution in room commits (row 46, stub Claude and Codex, no model calls): `pwsh tools\Invoke-Row46AttributionCheck.ps1`.
Row 23 self-check (skill import + `/health` + `/api/skills`, run against the deployed build after
`--import-skill`, points at a scratch stand-in otherwise): `pwsh tools\Invoke-M23MemoryCheck.ps1`.
Consolidation skill (row 23), imported with the hub stopped, into the data directory that hub will
use: `dotnet run --project src/ChopItUp.Hub -- --data .data --import-skill tools\skills\consolidate-memory`.
The owner then posts `/consolidate-memory <topic>` in a room, mentioning a model participant the hub
permits to file one (a `claude`-hosted model row, or the owner's own `human` row); the proposal it
files is approved from the diff on its card, never in the room — the card names the topic, the
entries the rewrite removes, and how many surviving entries would lose their approval record, and
warns when no git trail is available.

Restoring a rewrite: approving a `rewrite` proposal leaves the topic file's pre-consolidation content
at `<file>.rewrite-<id>.bak`, a name no later write reuses. To undo one, stop the hub, copy that
backup over the topic file it names, and restart — the approval record on the (now-superseded)
consolidation stays in `memory_proposals` for the trail, but the file content is exactly what it was
before that approval.

An editor save (row 40) is a rewrite proposal too, so the same recipe applies, and the save's status
line names the `.bak`. With git present prefer `git -C <data>\memory revert <hash>` (hub stopped;
every save is listed by `log --oneline` as `Approve memory proposal #<id> (<topic>): Edit <topic>`);
when copying the `.bak` instead, commit the restore before restarting (`git -C <data>\memory add -A`
then `commit -m "restore <file> from <bak>"`), because the next approval's `add -A` would otherwise
record the restore as part of a model's proposal.

A Debug hub serves static files from `src\ChopItUp.Hub\bin\Debug\net10.0\wwwroot`, not from
`src\ChopItUp.Hub\wwwroot` where `npm run build` writes. Verifying a client change against a hub that
is already running needs the built assets copied into the served directory (or a `dotnet build`, which
re-copies them); a client rebuild alone does not reach it.
Room check (real Sonnet, scratch hub + scratch room dir, spends): `pwsh tools\Invoke-M9RoomCheck.ps1`.
Skill check (real CLIs, scratch hub, spends): `pwsh tools\Invoke-M11SkillCheck.ps1`.
Run check (real CLIs, scratch hub + scratch room dir, spends): a two-phase toy skill (`tools\skills\toy-run`) proves a run reaches its ping unattended, from the hub's own records: `pwsh tools\Invoke-M19RunCheck.ps1`.
Roadmap-in-room check (real CLIs, scratch hub + scratch .NET repo, spends approx 6 spawns incl. one Codex): `pwsh tools\Invoke-M20RoadmapCheck.ps1`.
MCP timeout probe (4 Sonnet calls): `pwsh tools\Probe-McpTimeouts.ps1`.

## Rotating a token (row 28)

`--rotate-token <id>` prints the newly minted value once, to stdout, and writes it to no file —
row 28, D-28-d, reversing an earlier "never print" ruling now that `--print-config` never embeds a
live value either (it writes a `{{TOKEN}}` placeholder). That reversal is bounded: **rotate is
owner-typed only, never agent-run.** A printed token lands in a terminal buffer, a shell history and
often an agent transcript — acceptable for a human typing the command by hand and reading the value
off their own screen, not for a script or an agent invoking it and having the value pass through
whatever logs or forwards that run. Nothing enforces this mechanically; it is a rule for whoever is
driving the hub, not a gate `HostCommands.RotateToken` itself can check.

**Rotate with the hub stopped, then start it.** `RotateToken` gates on `HubLock.IsHeld` and exits 5
against a live hub: a loaded `TokenStore` is a startup singleton, so a rotation under a running hub
writes a file nobody reads and the old token keeps working — refusing is the difference between
rotation and revocation. On a deploy day the order is stop → deploy → rotate → start, and anything
rotated after the start waits for the next one.

## An owner credential from inside a spawn (row 29)

Live check: `pwsh tools\Invoke-Row29PeerCheck.ps1`. It starts a scratch hub on its own data directory
under `$env:TEMP`, binds a room to a scratch directory, and plants that scratch hub's `owner-remote`
token in the room directory the way a spawn would find one. It never touches `C:\Self Apps\ChopItUp\`
and never reads or mints a real token.

An early version of this check asked a real Sonnet spawn to compose the HTTP call that presents the
planted credential. Measured 2026-09-10: the model refused, on both attempts, in its own words, to
use a credential from a file to forge a request with a raw Authorization header. That refusal is a
real finding about the model, worth recording, but it is defense in depth only. The hub does not
require it, and nothing here asks a model to touch the credential anymore.

Instead the check imports a small skill, `tools\skills\peer-check-vehicle`, whose one gate is run
through `run_gate`, the same `IProcessRunner`/`SpawnJobs` path a model spawn goes through. The gate
script, not a model, reads the planted file and presents the header. A real Sonnet spawn is still
spent, but only to call `run_gate` by name, an ordinary action for a run's own conductor, and then to
post its own reply. Spends one Sonnet directory spawn, two when the run has to be started again
because the conductor narrated the gate call without making it.

It asserts only what the hub controls: a `hub` note in the room whose body starts `Refused an
owner-class credential presented from inside @sonnet's spawn (pid `, no message in that room authored
`owner` or `owner-remote` carrying the planted body, and the spawn's own reply landing under its own
name, which is the leg that shows a spawn's own credential still works from inside its job. Neither
the model's words nor the gate script's own output are ever asserted, only the hub's state.

The deploy-day half lives in `tools\Invoke-Row28SelfCheck.ps1`. Five of its seven legs are
agent-runnable end to end: `health.responds-200-expected-schema`, `auth.no-credential-post-refused-401`
and `hub.host-configs-sweep-clean` need no `-OwnerToken`, no `-PublishDir` and no read under
`<InstallDir>\data\`, and `bundle.exe-sha256-matches-publish` / `bundle.wwwroot-matches-publish` need
only `-PublishDir`. The `auth.owner-token-post-accepted-201` leg and the `-ipv6` twin beside it are the
owner's only: they post as the owner from your own shell over `127.0.0.1` and over `[::1]`, which is
what proves the refusal has not locked you out of your own hub on either loopback family, and they SKIP
with a reason naming the owner when `-OwnerToken` is omitted rather than run with no credential.

What neither check can cover: a process that leaves its job through the shell over COM, the Task
Scheduler or WMI is outside the job and can still use a stolen credential, as is anything created in
the first instants of a `cmd.exe` shim's life (measured 2026-09-10: a shim's `conhost.exe` was
outside the job on 10 of 15 runs, while the worker process the command line names was inside on 15 of
15), and the room transcript plus the git trail are the control for those.

## Running /roadmap in a room

Import with its overlay before the hub starts: `ChopItUp.Hub.exe --import-skill <skill dir>
--overlay <overlay dir>`. Class rows to build or judge, hub stopped: `--set-classes
<id>=<classes>` (comma-separated; empty after `=` clears). Bind a room to a repository root:
`POST /api/rooms {name, directory}`.

Start a run: post `/roadmap @<conductor>` in the room (text after the mention is a free-text
hint, never a row selector). Only a human roster row can start a run (`SpawnerService.ResolveSkill`
returns nothing for any other author), so an agent driving the hub needs the `owner-remote`
credential rather than its own MCP identity. Steer an active run by posting in the room; the conductor reads it
before its next phase post. `/stop` ends the run outright; a `phase: ping` post ends it on its
own. Deploy only after a run ends, never inside one: the ping names deploy as the next step.

Owner-remote setup: `--print-config` writes `<data>\host-configs\claude-code-owner-remote.json`
under the hub's DATA directory, not the repo. Merge its `chopitup` entry into the phone-driven
Claude Code session's `.mcp.json` on the hub machine (A1: loopback only, so that session runs
on the hub's own machine).

### Timeouts inside a run

A `run_gate` call defaults to a 25-minute ceiling (`RunLimits.EffectiveGateTimeout`, `SpawnTimeout`
minus a 5-minute reserve), but a hub-spawned Claude CLI cuts a silent MCP call at 300 s regardless
of any timeout knob — only bytes on the wire reset that cut. `run_gate` answers with an MCP
progress notification every 30 s while the gate script runs (`GateProgressInterval`), so a long
gate stays alive across the CLI's cut. If a gate dies anyway, check that progress is actually
reaching the client, not that some timeout needs to be bigger: `Probe-McpTimeouts.ps1` proves the
cadence with a 400-s gate that survives.

### If the run parks

A park from a cap, silence, or two refusals resumes on the owner's next post in the room (a
steer), as long as the run is still active. A ping whose body starts `PARKED:` has already
ENDED the run; re-post `/roadmap` to start a new one.

The room clone's default branch is hub-owned: empty trail commits land on it, and the next
`start-branch` gate resets it from origin. A branch already pushed with an open PR is reused
by the next `finish-branch`, or closed by hand with `gh pr close`. To abandon a run's
unfinished work outright, delete `room/m<row>` locally and on origin from a harness session.

## Exchanges in worktrees, live (row 35)

Rows 34/35 shipped exchange worktrees (`ExchangeWorktrees.cs`) with only a stubbed-CLI unit suite
behind them. `pwsh tools\Invoke-Row35LiveCheck.ps1` proves the real thing: a scratch hub, a scratch
directory room, one Claude spawn (`@sonnet`) and one Codex spawn (`@gpt-5.4-mini`), run sequentially
in the same room (the hub closes one worktree per room at a time). Each leg asks for ONE small file
with fixed content, then asserts with git run directly against the room directory: the `x<root>`
worktree exists while the exchange is open and is gone after; the hub's close note for that root
(merged-with-hash, nothing-new-to-merge, or kept-with-a-reason - whichever `ExchangeWorktrees.CloseAsync`
posts); `git log --oneline -3` contains the merge hash when the note carries one; the requested file
present in the room directory afterward; `chopitup/x<root>` gone from `git branch --list`. Spends at
most one Claude call and one Codex call per leg; never retries internally.

`-SeedOnly` spends no model call: it starts the hub, binds the directory room, and confirms the hub
itself ran `git init` there — no exchange ever opens. Run that leg first: `pwsh
tools\Invoke-Row35LiveCheck.ps1 -SeedOnly`. The full run (`pwsh tools\Invoke-Row35LiveCheck.ps1`) needs
`claude auth status` reporting `loggedIn: true` first (LESSONS Row 26) — a signed-out CLI fails every
Claude leg with no hub-side symptom. `-SkipClaude` / `-SkipCodex` re-run one leg alone.

**The Codex `.git`-file worktree question.** A linked worktree's `.git` is a file, not a folder (row
35's first git lesson), and nothing before this check had run a real `codex exec` inside one — only
that Codex's sandbox tolerates a real `git commit` there (row 35's measured claim 24), not whether it
accepts operating inside the worktree at all. This script's Codex leg is the first live measurement;
its `file.codex-present-on-default-branch` check FAILs by name alone (never a message body) if Codex's
file never reaches the default branch, and that is a real, recorded outcome — row 35 shipped without
answering this, not a defect in the script that found it. Record whichever way it goes here.

A silent-MCP-call caveat also applies to a spawn under this script, the same as inside a run: see
"Timeouts inside a run" above (a hub-spawned Claude CLI cuts a silent MCP call at 300 s regardless of
any timeout knob). Neither leg here calls `run_gate`, so it is unlikely to bite a one-file ask, but a
leg that runs unexpectedly long is the same symptom, not a new one.

**Measured 2026-09-17** (hub at `10d7875`, codex-cli 0.153.3, claude 2.1.267). Full run, default
participants: 21/22 PASS — the Claude leg (`@sonnet`) 8/8 clean; the Codex leg spawned with
`gpt-5.4-mini` exited 1 in ~2 s with empty stderr and no reply, so the hub merged an empty turn and
only `file.codex-present-on-default-branch` FAILed. Reproduced outside the hub with the same
`codex exec --json` arguments inside the linked (`.git`-file) worktree: Codex answered `400
invalid_request_error: The 'gpt-5.4-mini' model is not supported when using Codex with a ChatGPT
account.` as a `turn.failed` event on **stdout**, not stderr — the hub's failure note only appends
stderr (`SpawnerService.cs:1136-1140`), which is why the room only ever saw "exited with code 1
without replying" (a board row will cover surfacing that). Re-run with `-CodexParticipant
gpt-5.6-terra` (the account's configured model): 14/14 PASS, both legs merged cleanly — answering the
`.git`-file worktree question above **yes**; the earlier failure was model support, not worktree
shape. The script's default `-CodexParticipant` is now `gpt-5.6-terra`.

## Deploying a schema change, and rolling one back

For governing context, run `tools/Invoke-M48SelfCheck.ps1` after the Debug build. It uses synthetic
databases and a fake process boundary, exercises both transcript limits independently and together,
and records a TRX evidence set. It makes no model calls and reads no deployed room data.

Written before the row 19 deploy, not after it. `ChopDb.EnsureDatabase` **throws** when the database's
`user_version` is greater than the build's `LatestSchemaVersion`, so the moment the live
`data\chop.db` is migrated to v8 the previously deployed v7 executable refuses to start. A v8
database and a v7 executable cannot coexist. That makes the deploy order load-bearing and makes the
database half of the rollback mandatory rather than optional.

**Deploy order**

1. Stop the live hub. It holds `HubLock`, and a migration running underneath a live v7 process is the
   case nobody wants to debug. Stop it by the PID whose image path is inside the install directory —
   `Deploy-ChopItUp.ps1` refuses to run while one exists and names it.
2. `pwsh tools\Deploy-ChopItUp.ps1`. It publishes into staging, sanity-checks the staged output,
   copies the current install aside to a sibling backup directory, and replaces the executable last.
   It never touches `data\`. Writes under `C:\Self Apps\` raise one approval prompt each; those are
   gates, not failures.
3. Verify the staged output with `tools\Invoke-M4SelfCheck.ps1 -PublishDir <staging> -TargetDir <target>`.
4. Start the hub and confirm `/health` reports the new schema version. The migration happens on that
   first start, and `ChopDb` writes a pre-migration backup of the database and logs its path.

**Rollback**

Before replacing a post-upgrade database, retain that database and any WAL/SHM sidecars together as
a recovery set while the hub is stopped. Preserve newer posts separately before discarding that set.
The binary and database versions must be restored together. Agents must not write the deployed data
directory, so database restoration is an operator action. Prefer a forward repair when possible.

1. Stop the hub.
2. Restore the pre-migration backup `ChopDb` wrote over `data\chopitup.db`. `BackupBeforeMigration`
   names it `<database path>.v<version it is leaving>.<yyyyMMddTHHmmssZ>.bak`, beside the database, so
   the file to restore is the newest `chopitup.db.v<N-1>.*.bak` in that folder, where N is the schema
   version the new build reports on `/health` (for the v9 deploy that is `chopitup.db.v8.*.bak`; an
   older `.v7.` file is a previous deploy's backup and restoring it loses everything since). This step
   is not optional: the old executable cannot open a database at the new schema version.
3. Redeploy the previous executable from the backup-aside directory that `Deploy-ChopItUp.ps1` left
   beside the install: `pwsh tools\Deploy-ChopItUp.ps1 -RestoreFrom <that directory>`, which runs the
   same guarded pipeline in reverse rather than a hand-copy. The directory for the most recent deploy is
   the one named in the shipped ✅ row's Notes on `ROADMAP.md` (the deploy script prints it, and the
   board flip records it); do not rely on a date remembered from an earlier deploy.
4. Start the hub and confirm `/health` reports the old schema version.

## Exporting memory to a Claude Code directory

`--export-memory <dir>` (row 24) renders the hub's memory store into the shape `autoMemoryDirectory`
reads, then stages the whole export in a sibling directory and swaps it into `<dir>` — the target is
never written to in place. Like `--rotate-token`, it refuses (exit 5) while `HubLock` is held: stop the
hub first, because an approval landing mid-export would read a state that never existed.

    ChopItUp.Hub.exe --data <data dir> --export-memory <export dir>

Synthetic-corpus dry run (no model calls, scratch hub, drives the real exe over a fabricated 12-topic
corpus): `pwsh tools\Invoke-M24DryRun.ps1`. Self-check (source-shape + doc rows, then the dry run
itself): `pwsh tools\Invoke-M24ExportCheck.ps1`.

| Exit | Meaning |
|---|---|
| 0 | Exported. The last stdout line, `EXPORT_RESULT: { ... }`, names the target, the count, the previous-export directory (if any) and whether the store changed since the last export. |
| 3 | An IO failure mid-swap — the target is a file, not a directory, or `Directory.Move` itself failed. The report is a FRESH check of what the target actually holds afterward, not the state assumed going in. |
| 4 | No memory store at `--data`. Start the hub once against this data directory first, or check `--data`; nothing is created. |
| 5 | A hub is running against this data directory. Stop it first. |
| 6 | Refused by the guard: zero live entries without `--force`; the rendered index over the vendor's own 200-line/25,000-unit `MEMORY.md` cap (measured on the rendered index itself, never on an entry count); or the target is foreign, drifted, unreadable, or bound to a different store and the right override was not given. |

**D1 — the export owns its directory.** Point `autoMemoryDirectory` at a directory nothing else writes
to, never at one a Claude Code session also writes into. The guard cannot tell a session's own
`MEMORY.md`/memory-file writes apart from any other drift — its only correct answer to drift is to
refuse, not to guess which writer is trusted. Sharing the directory does not fail once and then keep
working: it guarantees the *next* export correctly refuses, because the directory it is asked to
replace no longer matches the manifest this tool wrote for it.

**Recovery names.** A run that replaces a target retains what was there under one of two names, never a
hand-delete of `<dir>` itself:
- `<dir>.chopitup-export-previous` — the plain, reusable name, taken only when the replaced export was
  clean and every file its manifest named is also produced by the new export (a superset). This is the
  ONE name a later run reuses: the run about to replace it again deletes it first, before anything else
  is moved.
- `<dir>.chopitup-export-previous-<yyyyMMddTHHmmssZ>` — a shrinking store, a `--force` over drift, a
  foreign directory, or an `--accept-new-source` all land here, and this tool never auto-deletes it. Two
  such replacements of the same target within one UTC second get a `-2`, `-3`, … suffix appended after
  the timestamp so they never collide; the timestamp prefix, and this whole enumeration, still match it.

A `<dir>.chopitup-export-tmp-<nonce>` beside the target is a stage an earlier run never finished
swapping in: it was never exposed to the target, so it holds only reproducible bytes — a re-render of
the same store. This tool reports one on every later run and leaves it alone forever; deleting it is
the owner's sanctioned cleanup, not something this tool does automatically.

**`--accept-new-source`** is the right answer only when the data directory itself legitimately moved (a
reinstall to a new path, a restored profile) and the target's manifest still names the old root — it
prints both roots and the same affected-paths list a plain refusal would print, then proceeds. It is
never the answer to ordinary drift inside an otherwise-correctly-sourced target (`--force` is that
answer); `--force` in turn can never override a different source at all, because that override IS the
attack the manifest exists to catch — a scratch store whose entries happen to match the real one
refuses identically on content, so only the store's root path tells the two apart.

The manifest holds the absolute path of the source memory directory (`<data>\memory`, not merely
`<data>`) inside the export directory itself, and every later run's source check reads it from there.

**Probe: does a session read the export? (row 26, post-merge, not a merge gate).** `pwsh tools\Invoke-Row26MemoryProbe.ps1 -KeepEvidence`
builds a scratch store of exactly 198 live entries, exports it with the real exe, and runs up to four
`claude -p --model sonnet` calls (never `--bare`, which skips auto-memory) with `--settings` pointing
`autoMemoryDirectory` at the export. Each entry carries an unguessable title nonce (lands on the index
line) and a separate body-only nonce, so an answer can only come from what was actually loaded:
- leg 1, `--tools ""` at 198 entries: which of the `user`, `room-general` and last-line nonces the
  session reports present (reading is by index line, or `metadata.type` is filtered);
- leg 2, a copy with one hand-appended 199th index line (the exporter refuses at 199): whether the
  199th nonce is absent and what error, if any, the session reports;
- leg 3, `--tools Read`: whether the session reads the `room-general` topic file on demand;
- leg 4 only if leg 1 reports nothing: leg 1 without `--setting-sources ""`.
The run needs the standalone CLI signed in (`claude auth status` must say `loggedIn: true`); the
desktop app's session auth does not carry over to a spawned `claude.exe`. Measured 2026-09-16: the
mechanics pass (fixture, export, index at exactly 200 lines, envelope parsing, spend cap) and every
leg returned `Failed to authenticate: OAuth session expired`. Measured 2026-09-17 after `claude auth
login` (22 PASS / 0 FAIL, 3 calls, leg 4 not needed):
- leg 1: all three nonces present (`user`, `room-general` title, entry 198 on the last index line),
  no error. A session loads the whole index and does not filter on `metadata.type`.
- leg 2: entry 198 present, the hand-appended 199th index line absent, no error reported. The
  200-line cap is enforced silently, which is why the exporter refuses at 199.
- leg 3: the `room-general` body-only nonce present. Given `Read`, the session opens the topic
  file the index points at.
So an exported directory is read exactly as the vendor documents: index lines up to the cap, topic
bodies on demand.
