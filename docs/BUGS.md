# Bugs, flakes and chores (worked by the fix-bug skill; never a board row)

- 60 LEAD: the unnamed Hub failure from 2026-09-19 (915/916, test name not retained) remains unidentified. The later named ExchangeApiTests stop/snapshot races, including former items 62 and 63, were corrected and verified in PR #137 (`cd1cf4b`, CI run 35654079714); do not rebuild those fixes. Whether the first failure had the same cause is unknown.
- 61 chore: a client showed `read_messages`/`wait_for_message` `room_id` and `limit` and `propose_memory` `replaces` as required; the hub's own tools/list marks all three optional with defaults (measured 2026-09-19 at 6ab2679 via HubTestHost.ClientFor + ListToolsAsync). LEAD: which client rendered them required, and whether it reads `default` at all.
