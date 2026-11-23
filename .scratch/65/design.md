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
