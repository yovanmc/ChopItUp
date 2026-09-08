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
Room check (real Sonnet, scratch hub + scratch room dir, spends): `pwsh tools\Invoke-M9RoomCheck.ps1`.
Skill check (real CLIs, scratch hub, spends): `pwsh tools\Invoke-M11SkillCheck.ps1`.
Run check (real CLIs, scratch hub + scratch room dir, spends): a two-phase toy skill (`tools\skills\toy-run`) proves a run reaches its ping unattended, from the hub's own records: `pwsh tools\Invoke-M19RunCheck.ps1`.
Roadmap-in-room check (real CLIs, scratch hub + scratch .NET repo, spends approx 6 spawns incl. one Codex): `pwsh tools\Invoke-M20RoadmapCheck.ps1`.
MCP timeout probe (4 Sonnet calls): `pwsh tools\Probe-McpTimeouts.ps1`.

## Running /roadmap in a room

Import with its overlay before the hub starts: `ChopItUp.Hub.exe --import-skill <skill dir>
--overlay <overlay dir>`. Class rows to build or judge, hub stopped: `--set-classes
<id>=<classes>` (comma-separated; empty after `=` clears). Bind a room to a repository root:
`POST /api/rooms {name, directory}`.

Start a run: post `/roadmap @<conductor>` in the room (text after the mention is a free-text
hint, never a row selector). Steer an active run by posting in the room; the conductor reads it
before its next phase post. `/stop` ends the run outright; a `phase: ping` post ends it on its
own. Deploy only after a run ends, never inside one: the ping names deploy as the next step.

Owner-remote setup: `--print-config` writes `<data>\host-configs\claude-code-owner-remote.json`
under the hub's DATA directory, not the repo. Merge its `chopitup` entry into the phone-driven
Claude Code session's `.mcp.json` on the hub machine (A1: loopback only, so that session runs
on the hub's own machine).

### If the run parks

A park from a cap, silence, or two refusals resumes on the owner's next post in the room (a
steer), as long as the run is still active. A ping whose body starts `PARKED:` has already
ENDED the run; re-post `/roadmap` to start a new one.

The room clone's default branch is hub-owned: empty trail commits land on it, and the next
`start-branch` gate resets it from origin. A branch already pushed with an open PR is reused
by the next `finish-branch`, or closed by hand with `gh pr close`. To abandon a run's
unfinished work outright, delete `room/m<row>` locally and on origin from a harness session.

## Deploying a schema change, and rolling one back

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

1. Stop the hub.
2. Restore the pre-migration backup `ChopDb` wrote over `data\chopitup.db`. `BackupBeforeMigration`
   names it `<database path>.v<version it is leaving>.<yyyyMMddTHHmmssZ>.bak`, beside the database, so
   the file to restore is the newest `chopitup.db.v7.*.bak` in that folder. This step is not optional:
   the old executable cannot open a database at the new schema version.
3. Redeploy the previous executable from the backup-aside directory that `Deploy-ChopItUp.ps1` left
   beside the install: `pwsh tools\Deploy-ChopItUp.ps1 -RestoreFrom <that directory>`, which runs the
   same guarded pipeline in reverse rather than a hand-copy. For the v8 deploy of 2026-09-07 that
   directory is `C:\Self Apps\ChopItUp.backup-20260907-195240`.
4. Start the hub and confirm `/health` reports the old schema version.
