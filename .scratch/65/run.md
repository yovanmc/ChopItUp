# Row 65 run notes

- Tier 2, risk lane (schema migration v19 to v20). Design: .scratch/65/design.md, design1 FIX-THEN-SHIP folded at 3bc0b8b.
- Focused checks: SpawnOutputTests 22/22, Core SchemaMigration+MessageStore 64/64, SpawnerServiceUsageTests 9/9, Hub Spawning folder 508/508.
- Dry run `tools/Invoke-Row65DryRun.ps1` on the Debug exe: 15/15 PASS.
- Acceptance `tools/Invoke-Row65UsageCheck.ps1` (one real Sonnet call): 12/12 PASS, block 1045 bytes, modelUsage key claude-sonnet-5-5, spawn id matches the hub log.
- Could not verify in this environment: Codex `--json` usage and Claude `stream-json` usage are covered by fixtures from measured captures, not a live hub spawn.
