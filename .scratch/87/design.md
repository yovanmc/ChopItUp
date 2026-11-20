# Row 87 design: usage limits hold and resume

Risk lane: unattended spend, a schema migration (v18 to v19), and a new persisted record of raw spawn output.

## Done when (from the spec, made falsifiable)

1. Each seeded limit string (Claude Code and Codex, each with its cited source in a comment and a test row) in a failed spawn's output holds the active run with the parsed reset time, refunds the spawn, and the run's conductor reopens two minutes after the reset on a fake clock.
2. A limit string with no parseable reset time parks the run with a reason naming the limit, sets no resume time, and only an owner post resumes it.
3. A failed spawn whose output matches no limit keeps its raw stdout and stderr, bounded, and the owner can read them through the API; the failed-spawn note names the record.
4. A hold leaves the other host's in-flight spawn running: it finishes and its post lands.
5. A hold survives a hub restart: the restarted hub resumes the run at the same instant.
6. A plain exchange (no run) whose spawn hits a limit posts a note with the reset time, keeps the exchange open and asks that participant again two minutes after the reset, with its turn refunded; with no reset time it posts the note and relaunches nothing.
7. A model reply that merely quotes a limit string is not a hit.
8. A fresh and a migrated database hold the new columns and table at v19, and the migration wrote a backup first.

## Sources (cited strings)

- Claude Code 2.1.328, bundled JS in the native binary: `You've hit your ${limitName}${suffix}`, limit names `5-hour limit`, `weekly limit`, `Opus limit`, `Sonnet limit`, `Fable limit`, `session limit`, `usage credit limit`, `monthly spend limit`; suffix ` · resets ${time}` where time is `3pm (America/Chicago)`, `3:30pm (America/Chicago)`, or past 24 h `Nov 21, 3pm (America/Chicago)` (year added when it differs). No-reset forms: `You're out of usage credits`, `You've hit your monthly spend limit.`
- Codex (openai/codex main, `codex-rs/protocol/src/error.rs`, `UsageLimitReachedError` Display): `You’ve hit your usage limit` (U+2019) with ` Try again at 3:15 PM.` (same local day) or ` Try again at Nov 21st, 2026 3:15 PM.`, ` or try again at ...`, ` Try again later.` (no reset). `codex exec --json` reports it as `{"type":"error","message":...}` or `{"type":"turn.failed","error":{"message":...}}`.

## Detection (UsageLimits.Detect)

- Input: host, the spawn's ProcessResult, the hub's clock and local time zone. Output: `null`, or `Hit(Kind, ResetAt?)`.
- Only the CLI's own failure channel is scanned, never model text (criterion 7):
  - Claude: the stream-json `result` event when `is_error` is true (its `result` text), and stderr.
  - Codex: `error` and `turn.failed` event messages on stdout (`SpawnOutput.CodexFailure` already finds them), and stderr.
- Matching: a regex per source string, anchored on the CLI's phrase (`You['’]ve hit your ...`), so the curly and straight apostrophes both match.
- Reset parsing: Claude `h[:mm]am|pm (IANA zone)` and `Mon D, h[:mm]am|pm (IANA zone)` with an optional year; the zone resolves through `TimeZoneInfo.FindSystemTimeZoneById` (IANA ids work on Windows with ICU). A time-only value earlier than now means tomorrow. Codex `h:mm AM|PM` (hub's local zone, same day, tomorrow when already past) and `Mon Dst, YYYY h:mm AM|PM`. A reset more than 8 days out, or unparseable, counts as no reset (criterion 2).
- Runs only where a spawn ended without posting, was not cancelled and did not time out: before the failed-spawn note in `OnFinished` and in `OnModeFinished`.

## Hold (run)

- Schema v19: `runs.resume_at TEXT NULL`.
- A hit by any spawn of an active run: `RunStore.Refund(runId)` (spawns_used minus one, floor zero), then `HoldRun`:
  - `RunStore.Park(id, reason, capSpent: false, now)` plus `resume_at = reset + 2 min` (null when no reset).
  - Unlike `ParkRun`, it cancels no in-flight spawn and does not stop the open exchange (criterion 4). The parked status already stops new launches and run decisions; a finishing spawn's `OnFinished` finds no active run and drives nothing.
  - Note: `Run #N held: @x hit its Claude 5-hour limit; resumes at <reset + 2 min, hub local time>.` or `Run #N parked: @x hit a usage limit with no reset time given. Post to resume. @owner`.
- Resume: a new `RunEvent.ResumeDue`, raised by the wake when a parked run's `resume_at` has passed: if still parked, not cap-spent and within budget, clear `resume_at` and `OpenConductor(root, pending steers)`; otherwise nothing (the existing refusal path covers an owner post).
- An owner post before the reset resumes it as today and clears `resume_at`.
- `ArmWake` adds the earliest `resume_at` of parked runs. Parked time already does not count as active time (`RunStore.ActiveElapsed`).
- Restart (criterion 5): `resume_at` is persisted; the start's stale-active park does not touch parked runs; the first `ArmWake` arms it.
- A second hit after resume holds again; nothing caps the number of holds, because each hold spends no budget and waits for the provider's own reset.

## Hold (plain exchange and mode legs)

- Exchanges already relaunch from `Exchange.Pending` through `ExchangePolicy.Due` and `NextWake`. `PendingSpawn` gains `NotBefore` (null by default); `Due` skips an entry before it and `NextWake` returns no earlier wake than it.
- A hit in an exchange that belongs to no run (a plain exchange, or a mode leg) does not call `ExchangePolicy.Finished` for that participant. It puts the participant back in `Pending` with the same triggers and reason, `NotBefore = reset + 2 min`, and refunds the turn (`TurnsStarted` minus one). The exchange stays open, and the normal launch path asks the participant again at that time, under every guard the room already has.
- Note: `@x hit its usage limit; asking it again at <time>.`
- No reset time: the note, then the participant finishes as a silent one does today (`Finished(posted: false)`), so nothing relaunches unattended.
- A restart drops every open exchange today, held ones included; the note already told the owner the time.
- A newer exchange that supersedes the held one leaves it to the existing superseding rules: `Due` returns nothing for an exchange that is not open.

## Raw output store (criterion 3)

- Schema v19: table `spawn_failures (id, room_id, participant_id, host, exit_code, stdout, stderr, at)`.
- Written for every failed spawn the detector did not classify. Each stream keeps its last 64 KB, scrubbed with the spawn's token like the notes are. The table keeps the newest 200 rows (delete older on insert).
- `GET /api/spawn-failures/{id}` owner-only returns the record; the failed-spawn note ends with `Raw output: failure #id.`

## Risks for the critic

- A false hit holds a run for hours: mitigated by scanning only the failure channel and a test row per non-limit failure text.
- Time zone parsing errors move the resume: tests pin the zone; a reset in the past or over 8 days counts as unparseable.
- Unattended relaunch of a plain exchange spends after the reset, once.
