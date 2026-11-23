# Row 65 design: keep the spawn result usage on the message row

Risk lane (schema migration v19 to v20). Tier 2. Done-when criteria: the body of a433de3 (copied below).

Done when:
1. A fresh database and a migrated v19 database are at v20 with nullable
   `messages.spawn_id` and `messages.spawn_usage`, the migration wrote a
   backup first, and rows from before v20 keep both NULL.
2. A Claude spawn (plain `json` or directory `stream-json`) that posted one or
   more messages stores its spawn id on every message it posted and the usage
   block on exactly one of them, the last message it posted.
3. The stored block is a JSON object holding only `host`, `usage` and
   `modelUsage` from the result line. No other result field is stored.
4. Reported-model attribution only: the models are the keys of the stored
   `modelUsage`. Nothing infers a model or route from the participant row.
5. Missing data is explicit: the spawn id is stored and `spawn_usage` stays
   NULL ("not reported") for a plain Codex spawn, a timed-out or cancelled
   spawn, stdout with no parseable result line or no usage fields, and a block
   over 16 KB. A spawn that posted nothing writes nothing.
6. A Codex directory spawn (`--json`) stores `host` and `usage` from its last
   `turn.completed` event, with no `modelUsage`.
7. One narrow store read returns a message's spawn id and usage. The
   `Message` record, prompts, MCP `read_messages` and the client are unchanged.
8. Acceptance: one real Claude spawn through a scratch hub stores a real usage
   block on its posted message row, read back from the database.

## Seam facts
- Posts: a spawn posts through MCP `post_message`. `SpawnerService.OnMessage` (`SpawnerService.cs:487`) and `OnModeMessage` (`SpawnerService.Modes.cs:58`) find the in-flight handle by (room, author) and set `Posted`. A new `SpawnHandle.PostedMessageIds` list is appended at both sites. `Exchange.MessageIds` is not used, because it spans every spawn of the exchange.
- Finish: both finish producers (`SpawnerService.cs:1289`, `SpawnerService.Modes.cs:264`) write a `FinishedEvent`, and the loop calls `OnFinished` (`SpawnerService.cs:319`). The write goes at the top of `OnFinished`, after `_inFlight.Remove` and before the mode branch, so explicit, run and non-panel mode spawns are covered.
- Ordering: `OnFinished` runs on the loop thread after the process exited. `OnMessage` also runs on the loop thread, so every post the spawn made is already in `PostedMessageIds`. A post that arrives after the handle left `_inFlight` is not this spawn's by the existing routing either.
- Out of scope: panel legs (`SpawnerService.Modes.cs:250`). They have no chat tools, and the hub posts their answer later (`SpawnerService.Modes.cs:341`), so they have no posted ids. Queued as a BUGS line, not handled.
- Parse: `SpawnOutput.Usage(host, stdout)` returns the stored JSON string or null.
  - Claude: the single JSON object (`json`) or the last `type: result` line (`stream-json`), the same rule as `ClaudeFinalText`. It copies only `usage` and `modelUsage` when either is an object.
  - Codex: the last `type: turn.completed` line's `usage` object. Plain Codex stdout is not JSON, so it yields null.
  - It returns null when nothing qualifies or when the serialized result exceeds 16,384 UTF-8 bytes. It never throws.
  - The input is the scrubbed stdout (the token never reaches the block).
- Store: `MessageStore.SetSpawnUsage(string spawnId, IReadOnlyList<long> ids, string? usage)` runs one transaction. It sets `spawn_id` on each listed id where `spawn_id IS NULL`, then `spawn_usage` on the last id where `spawn_usage IS NULL`. `MessageStore.GetSpawnUsage(long id)` returns `(string? SpawnId, string? Usage)` or null for an unknown id.
- Timed out or cancelled: `Usage` is not called and null is written as the usage, because a killed process prints no final result line. The spawn id is still written.
- Failure: an exception from the write is logged to stderr and swallowed. A spawn's posts and the rest of `OnFinished` never depend on it.
- Migration: `ApplyV20` on the `ApplyV19` pattern. It adds each column only when `pragma_table_info('messages')` lacks it, and stamps 20, all in one transaction. The existing ladder takes the backup first. No backfill. Older builds refuse a v20 store by the existing version guard.
- Readers unchanged: `Message` and every `SELECT id, room_id, ...` list in `MessageStore` stay as they are, so prompts, MCP and the client see nothing new.
- Version literals: grep `tools/` and `tests/` for `19` stamp checks per the schema lesson and bump each to 20.

## Tasks and commands
1. Parser, test first: `SpawnOutputTests` rows for each criterion 2 to 6 input class, using trimmed fixtures from the measured shapes (no session id, uuid or credential field kept in a fixture).
   `dotnet test tests/ChopItUp.Hub.Tests -v minimal --filter FullyQualifiedName~SpawnOutputTests` expects all pass.
2. Migration and store, test first: `SchemaMigrationTests` (fresh and v19 to v20, columns by name, backup written, old rows NULL) and a store test for `SetSpawnUsage` and `GetSpawnUsage`.
   `dotnet test tests/ChopItUp.Core.Tests -v minimal --filter "FullyQualifiedName~SchemaMigrationTests|FullyQualifiedName~MessageStore"` expects all pass.
3. Spawner wiring, test first: `SpawnerService` tests with `FakeProcessRunner`: two posts then a result line stores the id on both and usage on the last; a timeout stores the id and NULL usage; no post writes nothing; a Codex plain spawn stores the id and NULL usage.
4. Version literal bump across `tools/` and tests.
5. Dry run `tools/Invoke-Row65DryRun.ps1`: a v19 state with fabricated messages migrated to v20 through the real exe. It checks the backup, the stamp, both columns and that every old message keeps NULL in both.
6. Acceptance (real spend, one call): a scratch hub with a real Claude participant on a cheap model, one mention, and the posted row read back from the scratch database.

## Premises
- Claude `json` result: a single object with top-level `usage` and `modelUsage`, and also `session_id`, `uuid`, `credential_expires_at`. Measured 2026-11-22 at aa8281a8, one Haiku call.
- Claude `stream-json` with `--verbose`: only the final `result` line carries top-level `usage` and `modelUsage`. Measured 2026-11-22 at aa8281a8, one Haiku call.
- Codex plain mode: stdout is the final text only, and `tokens used` goes to stderr unstructured. Measured 2026-11-22 at aa8281a8, one call.
- Codex `--json`: ends with a `turn.completed` line whose `usage` holds input, cached input, cache write, output and reasoning output token counts, and no model name. Measured 2026-11-22 at aa8281a8, one call.
- The measured Claude block is about 1.6 KB. inferred: a spawn that uses several models stays far below 16 KB.

## Deviations
- None.

## Reviews
### design1 (dissect-critic, opus): FIX-THEN-SHIP, no REFRAME. Folded as below. These folds override the seam facts and tasks above where they differ.

- MAJOR, shutdown or crash after a post loses `spawn_id`: FIXED. `OnMessage` and `OnModeMessage` stamp `spawn_id` on the posted row at the sites that set `Posted` (`UPDATE messages SET spawn_id=$s WHERE id=$id AND spawn_id IS NULL`). Only the usage is written at finish. A shutdown or crash after the post event is handled now loses only the usage, which reads as "not reported". Residual limit: a post event still queued when the loop stops (`StopAsync` completes the channel at `SpawnerService.cs:240`) loses the stamp too, because no loop code runs for it. Criterion 5 gains the case "the hub stopped before the spawn finished: spawn id stored, usage NULL". Task 3 tests it by stopping the hub while a spawn that already posted hangs.
- DEFECT, plain Codex stdout is model text: FIXED. `SpawnOutput.Usage(string host, bool structured, string stdout)`. Plain Codex (no `--json`) passes `structured: false` and gets null with no parse. A `SpawnOutputTests` row feeds a plain reply holding a `turn.completed` line and expects null.
- DEFECT, parse location and scrub: FIXED. Both launch tasks (`SpawnerService.cs` launch and the mode panel launch at `SpawnerService.Modes.cs:257`) compute `Usage` after the run, for both room kinds, on `Scrub(result.StandardOutput, token)` (no scrub when the token is empty, as for panels). The string rides on `FinishedEvent` beside `TrailReport`. The loop thread never parses stdout for usage. Task 3 covers a plain-room and a directory-room spawn.
- DEFECT, hub-posted answers: FIXED. `PublishModeAnswer` takes the answering spawn's id and usage and stores both on the row it posts. The non-panel `answer` and the panel `synthesis` have the handle in hand in `OnModeFinished`. Panel first passes are published later from `ModeLeg.Answers`, so `ModeLeg` gains `Spawns` (participant to spawn id and usage), filled where `Answers` is filled. A hub-posted row is the only row of that spawn, so it holds the usage.
- DEFECT, timeout discards a block: FIXED. `Usage` is always called, timed out or cancelled included. It returns null when no result line was printed. Task 3 tests a timed-out result whose stdout already ends with a result line, and expects the block stored.
- DEFECT, acceptance does not check the link: FIXED. Criterion 8 asserts the stored `spawn_id` equals the id on the scratch hub's `spawn <id>: <participant> starting` stderr line, and that the configured model is a key of the stored `modelUsage`. The one real call uses the plain `json` shape. `stream-json` and Codex `--json` are covered by fixtures built from the measured captures.
- NIT, `host` source: ACCEPTED. `host` is the hub's own record of which CLI it launched, not a reported value. Criterion 4 forbids inferring a model or route, which `host` is not.
- NIT, `client_key`: ACCEPTED. The model controls `client_key` and can omit it. `spawn_id` is the hub's record. `client_key` stays a forensic fallback.
- NIT, last message: ACCEPTED. "Last" means the highest message id the spawn posted.
- NIT, post racing a kill: ACCEPTED. The ordering claim holds only for posts that arrive while the handle is in `_inFlight`, which matches the existing exchange routing.
- NIT, check names: ACCEPTED. The bump renames `schema-19` check names to `schema-20` along with the values, and `SchemaMigrationTests` literals that mean "latest" become `ChopDb.LatestSchemaVersion`.
- NIT, `.scratch` committed: ACCEPTED. The last commit before the merge deletes `.scratch/65/`, checked with `git ls-files .scratch` empty.
- NIT, premises unbacked: ACCEPTED. The four captures sit in the session scratchpad (`shape\claude.json`, `claude-stream.jsonl`, `codex-plain.out/.err`, `codex.jsonl`), outside the repo because they hold session ids and a credential expiry. Fixtures are trimmed copies. A Codex exec can end more than one turn only on resume, which no spawn uses, and the parser takes the last `turn.completed`. Rollback: `docs/verification.md` restore from the migration backup.

Criteria as folded (these supersede the copy at the top for the review):
- 2. A spawn that posted stores its spawn id on each posted row when the post is handled, and its usage block on the highest-id row it posted at finish. A mode or panel answer the hub posts for a spawn stores that spawn's id and usage on that row.
- 5. adds: the hub stopped or crashed before the spawn finished (spawn id stored, usage NULL), and plain Codex is never parsed. A timed-out or cancelled spawn stores the block when its stdout holds a result line, else NULL.
- 8. The acceptance run asserts `spawn_id` equals the hub's logged spawn id and the configured model is a `modelUsage` key.
