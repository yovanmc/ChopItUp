# Row 53 run notes

Plan and folded design review: `design.md`. Done-when criteria: body of 123f01d, folded in `design.md`.

## Punch list (resolved without the owner)
- Tasks 3 and 4 share commit 9a37e12, because `HubHost.cs` registers the store and maps the route in one file.
- Panel task rows store `run_id` NULL: a panel launch counts against no run (`_runs.CountSpawn` is only in `Launch`).
- The local gate is not duplicated: CI is the final gate. The selector falls back to Full here because the tools/ edits are unmapped.
- `tools/Invoke-Row65DryRun.ps1` now also drops `spawn_tasks`, so its "exact v19 shape" claim stays true.

## Evidence (this session)
- Core: `SchemaMigrationTests` and `TaskStoreTests`, 49 pass at 6dbebc6.
- Hub: the whole `ChopItUp.Hub.Tests.Spawning` namespace, 539 pass, on the task 3 tree. `TasksApiTests`, 3 pass at 9a37e12.
- `tools/Invoke-SpawnTasksDryRun.ps1` 18/18 and `tools/Invoke-Row65DryRun.ps1` 15/15 on the dev exe at a1638f5.
- `tools/Invoke-SpawnTasksCheck.ps1` 13/13 (one real Sonnet spawn, `general-1-1-77d74804`, 6498 ms, one post, reported `claude-sonnet-5-5`).

## Could not verify in this environment
- Tool counts from a live Claude stream-json or Codex `--json` spawn: covered by fixtures only. The live check is a plain-room Claude spawn, whose count is NULL by design.
