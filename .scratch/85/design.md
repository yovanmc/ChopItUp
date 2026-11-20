# Row 85 design: background jobs and the GPU lock

Spec: `.scratch/goal-runs/spec.md` row 85. Seam from row 84: `SpawnerService.Jobs.cs` (`TrackJob` before the process starts, `FinishJob` exactly once, `kill` called off the loop, jobs only for a room with an active or parked run). Code read at 28d54db.

## Sensitive surface

- Execution authority: a job is a shell command the hub runs with its own rights, outside both CLI sandboxes and outside the Claude deny list. It outlives the spawn that asked for it.
- Credentials: the job must not carry the spawn bearer or the shell owner bearer, and an owner bearer presented from inside it must be refused.
- Migration v17 to v18 (a new table), behind the existing backup-first ladder (`ChopDb.EnsureDatabase`).

## Components

### 1. `jobs` table and `JobStore` (Core, v18)

`ApplyV18`: `CREATE TABLE IF NOT EXISTS jobs` plus the stamp, in one transaction, so a torn attempt finishes. Columns:
`id INTEGER PRIMARY KEY AUTOINCREMENT, room_id TEXT NOT NULL, run_id INTEGER NOT NULL, author_id TEXT NOT NULL, command TEXT NOT NULL, cancel_command TEXT, gpu INTEGER NOT NULL, timeout_minutes INTEGER NOT NULL, folder TEXT NOT NULL, status TEXT NOT NULL, exit_code INTEGER, started_at TEXT NOT NULL, ended_at TEXT`.
Status values: `running`, `exited`, `timed-out`, `cancelled`, `stopped` (run end or `/stop`), `lost` (hub stop or restart), `failed` (could not start).
`JobStore`: `Start(...) -> long`, `End(id, status, exitCode, now)` (only from `running`, so a second end is a no-op), `Get(id)`, `Running()`.
The job id the tools and notes use is `j<rowid>`.

### 2. `GpuLock` (Hub)

A named `Mutex`, default `Global\ChopItUp.GpuJob`, name injectable so tests never touch the live name. A mutex, not a semaphore: a hub that dies holding it abandons it, and the next waiter acquires (`AbandonedMutexException` counts as acquired). A semaphore held by a dead process stays taken while any other handle is open, which would wedge the other hub.
A mutex is thread-affine, so each GPU job holds it on a dedicated thread: `WaitHandle.WaitAny([mutex, cancel])`, then hold until the job's release event, then `ReleaseMutex` on that same thread. `AcquireAsync(CancellationToken) -> IDisposable`.

### 3. `JobRunner` (Hub singleton and hosted service)

`StartAsync(JobRequest) -> JobView` from the tool, then one background task per job:
1. Insert the row, create `<artifacts>\jobs\j<id>\`, write the command to `job.ps1` there.
2. `spawner.TrackJob(room, id, timeout, kill)` before anything runs. Row 84's contract: a room with no active or parked run kills it at once, and the hub expires it at timeout plus 2 min.
3. GPU jobs: `GpuLock.AcquireAsync`, bounded by the same timeout and the cancel token. The wait counts against the timeout, so a queued job never waits past its own limit.
4. Start `pwsh -NoProfile -NonInteractive -File job.ps1`, cwd the job folder, stdout and stderr pumped into `job.log`. `SpawnJobs.Track` is the next statement after `Start` (the `ProcessRunner` discipline), with the room and the starter on the entry, so the owner-peer check treats the job as inside a spawn and it dies with the hub (kill-on-close).
   Environment: the hub's own, with `CHOPITUP_TOKEN` and `CHOPITUP_SHELL_TOKEN` removed (`HubHost` already clears the shell variable at start, this is the second lock), plus `CHOPITUP_ARTIFACTS` and `CHOPITUP_JOB_DIR`.
5. Wait for exit, the timeout or a stop. A stop (cancel, timeout, run end, hub stop) records its reason, runs `cancel_command` first when given (the same pwsh form, cwd the job folder, tracked in `SpawnJobs`, 60 s limit), then kills the tree and closes the job object.
6. Record every file under the job folder as authored by the starter in `run_artifacts` (`artifact:jobs/j<id>/...`). Row 79's scan already skips `jobs\`, so no spawn claims them.
7. End the row, then `spawner.FinishJob(room, id, note)`. The note: `Job j<id> by @<starter> <reason>: exit <code>.` then the last 40 log lines in a fenced block. `FinishJob` posts it and raises `JobFinished` (row 84).

`kill` for `TrackJob` is "stop with reason `stopped`". `Cancel(id)` is "stop with reason `cancelled`". Both return once the stop has run, and the exit path posts the note.

Hub stop (`StopAsync`): every live job is stopped with reason `lost` in parallel, waited for up to 10 s. A row that did not end stays `running`.
Hub start (`StartAsync`): every `running` row is a job a previous hub process lost. Its process died with that hub's job object. Run its `cancel_command` (bounded, so a GPU service stops rendering), end the row as `lost`, and report it:
- If the room's active or parked run is the job's run, `TrackJob` with a no-op kill, then `FinishJob` with the lost note, so the conductor hears of it through `JobFinished`.
- Otherwise post the note only.

### 4. MCP tools: `JobTools`

`start_job {room_id, command, gpu, timeout_minutes?, cancel_command?}`, `job_status {room_id, id}`, `cancel_job {room_id, id}`.
Gate, in order, each refusal posted as a fixed-form hub note (`start_job by @x: refused (<slug>)`) and thrown as `McpException`, with nothing started:
- the caller is a spawnable participant (`ExchangePolicy.IsSpawnable`). An app-backed host's row is a host-file row and is refused here (`not-a-spawn`).
- the room exists and has a directory (`no-directory`).
- the room has an active run (`no-active-run`), for `start_job` only.
- the caller is in flight in this room (`spawner.Snapshot(room).InFlight`, `not-in-flight`).
- `timeout_minutes` is 1 to 480, default 120 (`bad-timeout`). `command` is not blank (`no-command`).
- the artifacts folder is usable (`RoomArtifacts.Ensure`, `no-artifacts-folder`).
`job_status` and `cancel_job` also refuse a job id of another room (`unknown-job`).
`job_status` returns status, exit code, start, end, the folder and the last 40 log lines.

Claude in-run spawns: `ClaudeRunToolsAllowed` gains the three tools. Codex calls any MCP tool under `--approve-for-me`.

### 5. README

The spawning section states that a background job runs with the hub's full rights, git writes and network included, outside both CLI sandboxes, and that the hub kills it on cancel, timeout, run end and hub stop.

## Decisions

- **Run only.** `start_job` needs an active run, matching row 84's seam (a room with no run kills the job). A worktree exchange outside a run has nobody to wake.
- **Caller check by participant, not by peer process.** A spawnable participant's bearer exists only in the hub's memory and in the spawn it hands it to, so "spawnable and in flight here" is the spawn. A participant in flight in two rooms at once could name the other room. That adds no authority: the same bearer already posts in every room, and the spawn already has a shell with the hub's rights.
- **pwsh `-File` from a written `job.ps1`**, not `-Command` with the text on the command line: no quoting layer, and the folder keeps a record of what ran.
- **Job output authorship** covers the job's own folder only. A job that writes elsewhere in the artifacts folder is credited to whichever spawn's scan sees the change, which is the row 79 rule.
- **No job count cap.** The spec names none. The run's spawn budget and wall clock bound how many spawns can start jobs.

## Done when

1. A job that runs longer than the spawn timeout finishes after its spawn ended, posts its exit code and last 40 log lines, and opens the conductor.
2. Two GPU jobs from two hubs that share a lock name run one after the other: a fake service records no overlap.
3. Cancel, timeout, `/stop` and a hub restart each leave no live job process, run `cancel_command` when given, and post a note naming the job and why it ended.
4. `start_job` from an app-backed host and from a spawnable participant not in flight in that room are refused with a note, and nothing starts. A timeout outside 1 to 480 is refused. `job_status` and `cancel_job` refuse another room's job.
5. An owner credential presented from inside a job's process is refused.
6. The job's environment holds neither spawn nor shell bearer, it runs in `<artifacts>\jobs\j<id>\`, and it logs to `job.log` there.
7. Files under the job's folder are recorded as authored by the starter.
8. A fresh and a migrated database hold the `jobs` table at v18, and the migration wrote a backup first.
9. The README states the job's rights.

## Tests

- `JobStoreTests` and `SchemaMigrationTests` (v18 fresh and migrated, backup first).
- `GpuLockTests`: two holders on one name serialize; an abandoned mutex is acquired; cancel while waiting returns without the lock.
- `JobRunnerTests` with real pwsh jobs: env has no bearer (the job prints its env), log and folder, exit note with the tail, authorship rows, cancel and timeout and run-end stops with a `cancel_command` marker file and a dead pid, restart recovery (seed a `running` row, start a runner, see the note, the marker and `JobFinished`), two runners on one lock name against a fake HTTP service that records overlap, and the job pid's `SpawnJobs.Membership` is Inside with the room.
- `JobToolsTests` through a real MCP client from inside a fake spawn (the `RunToolsTests` pattern): the happy path wakes the conductor after the spawn ended, plus every refusal.
- `SpawnCommandsTests`: the run allow list names the three tools.
- Migration dry run: `tools/Invoke-M2DryRun.ps1` expects v18 and the table.

## Pass 1 dispositions (dissect-critic, FIX-THEN-SHIP)

These override the sections above where they differ.

- **M1, M2: the GPU lock is a lock file, not a mutex.** `GpuLock` opens `%LOCALAPPDATA%\ChopItUp\gpu.lock` (path injectable, tests use a temp path) with `FileShare.None`, polling once a second until it opens or the wait is cancelled. The OS closes the handle when a process dies, there is no thread affinity, and the file holds the holder record as JSON: hub pid, job id, job folder, `cancel_command`. A clean release truncates the record before closing. An acquirer that finds a record left by a holder that is gone runs that record's `cancel_command` (cwd its job folder when it exists) before its own job starts, so a dead hub's render is interrupted before the next one begins. This deviates from the spec's "named semaphore": a semaphore count held by a dead process is never returned.
  - `cancel_command` runs only when the job's process started (the row stores `pid` and `process_started_at`). A GPU job runs it only while it holds the lock.
  - Restart recovery never runs a GPU job's `cancel_command` directly. It tries the lock once without waiting: if it gets it and the record is stale, it runs the record's interrupt and clears it. If another hub holds the lock, that hub already handled any stale record when it acquired.
  - A non-GPU lost job runs its `cancel_command` at recovery only when its process had started.
- **M3: notes are durable.** New column `noted_at`. `FinishJob` gains an `onPosted` callback that `OnJobFinished` calls once the note has a message id (or once it drops a duplicate report for a job the hub already noted). The runner sets `noted_at` there. Hub stop: `JobRunner` is registered after `SpawnerService`, so it stops first. It stops each live job with reason `lost`, ends the row and does NOT call `FinishJob`, so nothing raises `JobFinished` during shutdown. Hub start recovery reports every row that is `running` (ended as `lost` first) or ended with no `noted_at`.
- **M4: one state per job.** `Pending -> Running -> Stopping -> Ended`, under the job's lock. A stop in `Pending` ends the job without starting a process and without `cancel_command`. The process starts only if the state is still `Pending` under the lock. The stop is single-flight and the first reason wins. Row 84's `kill` becomes `Action<JobStopReason>`: `KillJobs` passes `Stopped`, `ExpireJobs` passes `TimedOut`. `OnJobFinished` drops a second report for a job id this run already finished, without posting a second note.
- **M5: tests.** Two `HubTestHost`s on one lock path serialize two real pwsh GPU jobs against a fake HTTP service that records the largest overlap (file handles serialize across handles, in one process or two). A cross-process test kills a pwsh child that holds the lock file with a record mid-render, and the next acquirer interrupts the fake service before its own render: no overlap. The restart test simulates a crash: host A's job objects close (the process dies) with the row still `running`, then host B starts on the same data dir and the test asserts the dead pid, one cancel marker, the lost note and `JobFinished` on the parked run. A real kill of a hub process with a live job is not tested: a job needs an in-flight spawn, which needs model CLIs. Kill-on-close is covered by `SpawnJobsTests`.
- **D1:** wait for the root to exit, dispose the tracker (kill-on-close), then drain the log pumps with a 5 s grace, as `ProcessRunner` does.
- **D2:** the job folder is `jobs\j<id>-<8 hex>` created fresh, so two hubs or a fresh database never reuse a folder. Recovery records authorship too. A critique that names an `artifact:jobs/...` file with no recorded author is refused. A spawn writing into another job's folder while it runs is residue (a spawn can write anywhere in the artifacts folder).
- **D3:** `SpawnJobEntry` gains `Kind` (`Spawn` or `Job`). While any job-kind entry is live, the middleware resolves the peer of every `/mcp` and guarded `/api` bearer, and refuses any bearer whose peer is inside a job-kind entry (`credential refused: presented from inside a background job`). A job therefore cannot act as its starter even with a pasted bearer. The check rides `IOwnerPeerCheck` (a new member, off under `--owner-peer-check off`).
- **D4:** `start_job` posts a fixed-form start note: id, starter, gpu, timeout and the command capped at 500 characters. `job_status` returns the command from the database.
- **D5:** every `tools/` schema-17 pin moves to 18 in its own mechanical commit.
- **D6:** the tail is the last 40 lines, each cut to 300 characters, the whole cut to 4,000, in a fence one backtick longer than the longest backtick run in it.
- **D7:** recovery runs at the start of `JobRunner.ExecuteAsync`, one try/catch per row.
- **D8:** kept. The roadmap workflow commits run notes on the run branch and the last commit before the merge deletes `.scratch/85/`. The design holds no private data.
- **N1:** the job environment drops the `CHOPITUP`, `OPENAI_API_KEY` and `ANTHROPIC_API_KEY` prefixes (the `PanelExecution` set), then adds `CHOPITUP_ARTIFACTS` and `CHOPITUP_JOB_DIR`.
- **N2:** rollback is the backup the migration writes. A v17 exe refuses a v18 database.
- **N3:** accepted. The owner stops a job through `/stop` of the run.

Added done-when: 10. A bearer presented from inside a job's process is refused, owner-class or not. 11. A stale GPU holder record is interrupted before the next GPU job starts.
