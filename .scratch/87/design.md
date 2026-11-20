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

## Pass 1 dispositions (dissect-critic, FIX-THEN-SHIP; no second pass)

These override the sections above where they differ.

- B1 (hold kept launching): a run hold is `ParkRun` plus `resume_at`. It stops the open exchange with `ExchangeStopCause.Run` and cancels the room's in-flight spawns like any park; run rooms are exclusive, so nothing else is in flight there.
- M1 (criterion 4 unreachable for runs): criterion 4 now reads: a plain exchange in a room without a directory, whose spawn hits a limit while another participant's spawn runs, leaves that spawn running and its post lands; a run hold cancels nothing in another room.
- B2 (IANA lookup fails under InvariantGlobalization): no time zone lookup at all. Reset times are read in `TimeProvider.LocalTimeZone` (the CLI is a child on the same machine, so its displayed zone is the hub's zone); the IANA id in parentheses is matched and ignored. Parsing uses only `CultureInfo.InvariantCulture` with explicit formats, which behaves the same in invariant mode. Tests pin the zone with a FakeTimeProvider (N4).
- M2 (UTF-8): Claude, Codex and panel specs set `Utf8Output = true` (both CLIs write UTF-8; this resolves BUGS line 92). A ProcessRunner test drives a child that writes raw UTF-8 bytes for U+2019 and U+00B7 and asserts the decoded text. Regexes anchor on ASCII words (`hit your`, `usage limit`, `resets`, `Try again`), with the apostrophe and separator as classes.
- M3 (Claude channel unverified): the raw output of every spawn that ended without posting and was not cancelled is kept, classified hits and the replied-without-posting branch included. Claude has two shapes: `--output-format json` (one object, plain rooms) and stream-json `result` events (runs); both are read through `ClaudeFinalText`. The `is_error` value is a LEAD: a hit needs the final result text to start with the CLI's phrase and be at most 400 characters, whatever `is_error` says, with a test row for each value. A long model reply that quotes the phrase is not a hit (criterion 7).
- M4 (owner-only GET had no seam): no HTTP route. Each record is a file `spawn-failures\<n>.txt` under the hub's data folder, which holds the room's other private state already; the folder keeps the newest 200 files. Criterion 3 now reads: an unclassified failure's raw output is kept, bounded, in a file under the hub's data folder that the failed-spawn note names. The migration therefore adds only `runs.resume_at`.
- D1 (stale resume_at): every status-changing run UPDATE writes `resume_at`: `Park` takes it (default NULL), and `Resume`, `Extend` and `End` set NULL.
- D2 (ResumeDue spin): every ResumeDue outcome clears `resume_at`; a run over budget by its numbers posts the cap-spent line instead of reopening.
- D3 (InFlight left set): the plain-exchange hold removes the participant from `x.InFlight` before it re-queues it.
- D4 (plain Codex stderr is a transcript): Codex is matched only in its own error channel: `error` and `turn.failed` JSON events, and in human mode only stderr lines that start with `ERROR: ` (a LEAD until a real hit is captured; the raw file captures it).
- D5 (time-only in the past): a time-only reset up to 60 minutes in the past means the limit has not cleared yet: hold 15 minutes. At most three holds in a row on the same stated reset; the fourth parks for the owner.
- D6 (Codex version): the strings come from openai/codex `main` as read on 2026-11-20; the installed CLI is codex-cli 0.169.1. Added no-reset rows: `Your workspace is out of credits`, `You hit your spend cap`, and the named-limit form `You’ve hit your usage limit for <name>`.
- N1: a resume posts `Run #N resumed after the usage-limit reset.` and passes its id as the trigger.
- N2: the whole stream is scrubbed before it is cut to its last 64 KB.
- N3: `RunStore.Hold` refunds one spawn, parks and writes `resume_at` in one UPDATE.
- N5: rollback: a v18 build refuses a v19 database, so the only rollback is restoring the pre-migration backup, which loses messages written after it.
