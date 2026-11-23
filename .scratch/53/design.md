# Row 53 design: task usage telemetry

Risk lane (schema migration v20 to v21, new table). Tier 2. Done-when criteria: body of 123f01d, copied below.

Done when:
1. A fresh database and a migrated v20 database are at v21 with an empty
   `spawn_tasks` table, and the migration wrote a backup first.
2. Each launch at both sites inserts one task row before the process starts:
   spawn id, room, participant, host, requested model and effort as the hub
   passed them (effort NULL means no flag), exchange root, turn, run id, a
   retry flag, and the start time. The outer "could not be started" catch
   (no handle, no process) writes no row.
3. Finish completes the row once: end time, duration from the runner's
   elapsed time, exit code, termination reason, tool call count, count of
   posts the spawn made itself, and the usage block.
4. Termination is exactly one of worktree_refused, launch_failed, timed_out,
   cancelled, output_limit, usage_limit, failed, completed, in that
   precedence. usage_limit means a detected limit on a spawn that posted
   nothing. A row with no end whose spawn is not in flight reads as
   interrupted, and nothing rewrites it.
5. Tool calls are counted only from streams that list them: every Claude
   `tool_use` block in stream-json, and each distinct Codex `--json` item that
   is not an agent message, reasoning, error or todo list. Plain Claude json
   and plain Codex give NULL (unknown), never 0.
6. Retry is 1 for the relaunch a usage-limit requeue scheduled, else 0.
   CLI-internal retries are not reported and are not counted.
7. GET /api/rooms/{roomId}/tasks returns the room's tasks newest first with
   token metrics read from the usage block (summed over `modelUsage` when
   present, else `usage`; NULL when no block), reported models (the
   `modelUsage` keys), and the interrupted reading. Unknown room is 404.
8. The `Message` record, prompts, MCP tools and the client are unchanged.
9. Acceptance: one real Claude spawn on a scratch hub produces a task row
   through the route with requested model sonnet, termination completed, a
   positive duration, one post, token metrics and a reported Sonnet model.

## Seam facts
- Table `spawn_tasks` (v21): `spawn_id TEXT PRIMARY KEY, room_id TEXT NOT NULL, participant_id TEXT NOT NULL, host TEXT NOT NULL, requested_model TEXT, effort TEXT, root_message_id INTEGER NOT NULL, turn INTEGER NOT NULL, run_id INTEGER, retry INTEGER NOT NULL, started_at TEXT NOT NULL, ended_at TEXT, duration_ms INTEGER, exit_code INTEGER, termination TEXT, tool_calls INTEGER, posts INTEGER, usage TEXT`, plus an index on `(room_id, started_at)`. `ApplyV21` creates it with `IF NOT EXISTS` and stamps 21 in one transaction. No backfill.
- Store: `TaskStore` in `src/ChopItUp.Core/Storage/`. `Start(TaskStart)` inserts (`INSERT OR IGNORE`, so a repeated id never overwrites). `Finish(spawnId, TaskEnd)` updates only `WHERE ended_at IS NULL`, so a row completes once. `ForRoom(roomId, limit)` reads newest first.
- Write twice, both on the loop thread:
  - Start: in `Launch` right after `_inFlight[...] = handle` (SpawnerService.cs:1205), and in the panel launch after `_inFlight[...] = handle` (Modes.cs:252). Both run before the process task starts.
  - Finish: at the top of `OnFinished`, beside row 65's usage write, so explicit, run, relay and panel spawns are all covered. The tool count is computed off the loop thread in the two launch tasks, next to the usage parse, and rides on `FinishedEvent`.
- Interrupted: the route marks a row with `ended_at IS NULL` whose spawn id is not in the spawner's in-flight set as `interrupted`. The in-flight set is read through the existing loop snapshot path (`InLoopAsync`). The row itself is never rewritten, so a hub stop or crash leaves honest evidence.
- Requested model: `participant.Model` as passed to the CLI (an alias such as `sonnet`), named `requested_model`. Reported models stay the `modelUsage` keys. Codex has none, so its reported list is empty.
- Effort: the `effort` local from `EffortPolicy.AtLaunch` (SpawnerService.cs:1102). The panel launch passes no effort flag, so it stores NULL.
- Duration: `ProcessResult.Elapsed` (the runner's stopwatch). `started_at` uses the injected clock at launch and `ended_at` the injected clock at finish, for ordering only.
- Termination: a pure `TaskTermination.Of(result, postedNothing, limitHit)` in `src/ChopItUp.Hub/Spawning/`:
  1. worktree_refused: exit code null and stderr starts with the `WorktreeRefused` prefix
  2. launch_failed: exit code null, not timed out, not cancelled
  3. timed_out
  4. cancelled
  5. output_limit
  6. usage_limit: the spawn posted nothing and `UsageLimits.Detect` (pure) finds a limit
  7. failed: nonzero exit code
  8. completed
  `DetectLimit` is pure, so calling it at the top of `OnFinished` adds no side effect. `KeepRawOutput` is not called by this row.
- Tool calls: `SpawnOutput.ToolCalls(host, stream, stdout)` returns null unless the stream lists calls. `stream` is true for Claude `stream-json` (directory rooms) and Codex `--json` (directory rooms and panels). Plain Claude json (plain rooms and Claude panels) and plain Codex give null.
- Posts: `PostedMessageIds.Count` from row 65 (the spawn's own posts). A mode answer the hub posts is not counted as a spawn post.
- Retry: `PendingSpawn.NotBefore` is set only by `Requeue` (SpawnerService.Limits.cs:84), which only the usage-limit paths call (SpawnerService.Limits.cs:76, SpawnerService.Modes.cs:292). `SpawnRequest` gains `Retry`, set from `pending.NotBefore is not null` where requests are built (ExchangePolicy.cs:495, and the mode launch request at SpawnerService.Modes.cs:231 if it can carry one). The run conductor's ask-again after a silent turn is a new turn, so retry is 0.
- Token metrics: read on request from the stored block. Sum `inputTokens`, `outputTokens`, `cacheReadInputTokens`, `cacheCreationInputTokens` over `modelUsage` when present, else take `input_tokens`, `output_tokens`, `cache_read_input_tokens` (or Codex `cached_input_tokens`), `cache_creation_input_tokens` (or Codex `cache_write_input_tokens`) from `usage`. A missing field is null.
- Route: `GET /api/rooms/{roomId}/tasks?limit=` in a new `TasksApi`, with the same open-GET policy as `GET /api/rooms/{roomId}/run` (RunsApi.cs:17). Limit defaults to 50 and is capped at 200.
- Readers unchanged: `Message`, prompts, MCP tools and the client.
- Version literals: bump only the named stamp checks (`schema -eq 20` and `schema-20` check names in tools/, `LatestSchemaVersion` in tests). Older version helpers in `SchemaMigrationTests` also drop `spawn_tasks`.
- Failure: every task write is wrapped, logged and swallowed. A spawn never fails because of telemetry.

## Tasks and commands
1. Pure parts, test first: `TaskTermination` (one parameterized test over every branch), `SpawnOutput.ToolCalls` (Claude stream-json, Codex items, plain shapes null), token metric reading.
2. Migration and `TaskStore`, test first: fresh and v20 to v21 with backup, torn v21, store start, finish once, read order.
3. Spawner wiring, test first, `FakeProcessRunner`: a completed plain spawn, a directory stream-json spawn with tool calls, a timed-out spawn, a launch failure, a usage-limit requeue relaunch with retry 1, a panel leg, a hub stopped mid-spawn reading interrupted.
4. Route test: newest first, metrics, interrupted, unknown room 404.
5. Literal bump, dry run `tools/Invoke-SpawnTasksDryRun.ps1` (v20 fabricated state to v21 through the real exe), live check `tools/Invoke-SpawnTasksCheck.ps1` (one real Sonnet spawn).

## Premises
- Claude multi-turn stream-json (one model): top-level `usage` equals the single `modelUsage` entry, and `tool_use` blocks appear in assistant lines. Measured 2026-11-22 at 28d29e3f with one Haiku call using Bash (num_turns 2, one tool_use).
- Codex `--json` with an MCP call: items are `agent_message`, `command_execution` and `mcp_tool_call`, each with an id, started and completed. Measured 2026-11-22 at 28d29e3f with one gpt-5.6-sol call on a scratch hub.
- Claude json and Codex `turn.completed` usage shapes: measured for row 65 (2026-11-22).
- inferred: with several models in one spawn, the `modelUsage` sum is the total. The first task tests the sum rule on a two-model fixture.

## Deviations
- None.

## Reviews
### design1 (dissect-critic, opus): FIX-THEN-SHIP, no REFRAME. Folded as below. These folds override the seam facts and tasks above where they differ.

- MAJOR, a throwing tool-count parse strands `_inFlight` and `_live`: FIXED. Both launch tasks call a never-throws `ToolCountOf` wrapper (catch all, log, return null), like `UsageOf`. `SpawnOutput.ToolCalls` also checks `ValueKind` before every property read. A test feeds `{"type":"item.completed","item":[1]}` and other hostile lines and expects null or a count, never a throw, and a spawner test proves the spawn still finishes.
- DEFECT, output_limit unreachable: FIXED. Precedence is now worktree_refused, output_limit, launch_failed, timed_out, cancelled, usage_limit, failed, completed. launch_failed also requires `!OutputLimitExceeded`. The parameterized test builds the overflow result exactly as ProcessRunner does (`ExitCode` null, `TimedOut` false, `Cancelled` false, `OutputLimitExceeded` true, stdout empty).
- DEFECT, overflow reads tool_calls 0: FIXED. `ToolCalls` returns null when the output limit was exceeded or the kept stdout is empty, and 0 only for a kept stream that lists no calls.
- DEFECT, interrupted race: FIXED. The route reads the in-flight spawn ids and the task rows inside one `InLoopAsync` callback, so no launch or finish can land between them. While the hub stops, `InLoopAsync` throws and the route answers 503.
- DEFECT, token semantics differ by host: FIXED. The route returns normalized metrics: `inputTokens` is uncached input, `cacheReadTokens` and `cacheWriteTokens` are cache counts, `outputTokens` is output. Claude fields map directly (its input excludes cache). For Codex, `inputTokens` = `input_tokens` minus `cached_input_tokens`, `cacheReadTokens` = `cached_input_tokens`, `cacheWriteTokens` = `cache_write_input_tokens`. inferred: Codex input includes its cached count (OpenAI usage semantics); the measured capture is consistent (cached 11,392 of input 15,789). `cache_write_input_tokens` is in the row 65 Codex capture (`shape\codex.jsonl`, value 0).
- DEFECT, a hub stop drops finish data: FIXED in the reader. For an interrupted row the route reports posts as the count of `messages` rows with that `spawn_id` (exact, because a spawn whose finish never ran cannot have a hub-published answer). The other finish fields stay NULL.
- NIT, retry on the run path: ACCEPTED. Criterion 6 now says: a run's conductor reopened after a usage-limit hold (`ResumeDue`) is retry 0, like the synthesis turn, the conductor ask-again, /continue and an owner reopen.
- NIT, loci: ACCEPTED. `_inFlight` assignments are at SpawnerService.cs:1204 and SpawnerService.Modes.cs:253. Every launch, mode legs included, builds its request at ExchangePolicy.cs:495, which sets `Retry`.
- NIT, CLI-internal retries: ACCEPTED. Reworded: CLI-internal retries are not counted. Nothing claims the CLIs report them.
- NIT, Codex types: ACCEPTED. Allow-list `command_execution`, `file_change`, `mcp_tool_call`, `web_search`, counted once per item id (started and completed pair). An item with no id counts once per completed event. The Claude count includes denied `tool_use` blocks and `post_message` calls.
- NIT, ties: ACCEPTED. Order by `started_at DESC, rowid DESC`.
- NIT, termination representation: ACCEPTED. String constants, never an enum on the wire.
- NIT, silent failures: ACCEPTED. A swallowed task write and a Finish that updates zero rows are logged to stderr.
- NIT, `DetectLimit` on every spawn: ACCEPTED. Called only when the spawn posted nothing.
- NIT, test names: ACCEPTED. Task 3 tests a runner throw (launch_failed row) and a throw before the handle (no row), and a panel overflow (output_limit, tool_calls NULL). Criterion 8 is checked by the diff: no change under `src/ChopItUp.Core/Model/Message.cs`, `src/ChopItUp.Hub/Mcp/` or `src/ChopItUp.Hub/client/`, and the golden prompt tests stay green.
- NIT, dry run data: ACCEPTED. The dry run takes a scratch data and rooms folder under %TEMP% only.

Criteria as folded (these supersede the copy at the top for the review):
- 4. Precedence: worktree_refused, output_limit, launch_failed, timed_out, cancelled, usage_limit, failed, completed.
- 5. adds: NULL when the output limit was exceeded or no stdout was kept. Codex allow-list as above.
- 6. Retry is 1 only for the relaunch a usage-limit `Requeue` scheduled (plain and mode exchanges). Every other relaunch, including a run conductor resumed after a hold, is 0. CLI-internal retries are not counted.
- 7. adds: normalized token metrics as above; posts for an interrupted row come from `messages.spawn_id`; rows and in-flight ids are read in one loop callback; 503 while the hub stops.
