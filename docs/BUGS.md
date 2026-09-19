# Bugs, flakes and chores (worked by the fix-bug skill; never a board row)

- 60 flake: one Hub test failed once in a full-solution run (915/916, 2026-09-19, name not captured by the filtered output); the same binaries passed 916/916 on re-run. LEAD: the failing test's name.
- 61 chore: a client showed `read_messages`/`wait_for_message` `room_id` and `limit` and `propose_memory` `replaces` as required; the hub's own tools/list marks all three optional with defaults (measured 2026-09-19 at 6ab2679 via HubTestHost.ClientFor + ListToolsAsync). LEAD: which client rendered them required, and whether it reads `default` at all.
