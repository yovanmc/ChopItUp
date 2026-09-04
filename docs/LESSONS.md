# Lessons

Pull-based. Entries are `### [keywords] M<row> (<date>, <hash>)` + one paragraph, written only when a shipped milestone changes a future decision on the same surface. Grep headings before planning.

### [sqlite, schema, migrations] M1 (2026-09-04, a80ba0c)
A create-only schema still needs its `PRAGMA user_version` stamp inside the same transaction as the DDL and the seeds. Critique pass 1 declined a migration guard here on the reasoning that v1 has nothing to migrate; pass 2 reproduced what that reasoning misses — a first start interrupted between the DDL commit and the stamp leaves the tables present at version 0, so the next start's `if (version < 1)` re-runs the creates against existing tables and bricks the data directory permanently. "Nothing to migrate yet" is never a reason to skip transactional versioning. Make the DDL `IF NOT EXISTS`, the seeds `OR IGNORE`, and the stamp the last statement inside the transaction, so a torn start repairs itself instead of crashing.
