# Row 44 — Continue an exchange

**Goal:** an exchange gets 8 turns by default, the owner can set the number per message, the participant the owner addressed gets one synthesis turn whenever another model posted last so the owner always gets a wrap-up, and a concluded or stopped exchange can be continued with the same participants and skill from a button or by typing `/continue`.

**Architecture:** `SpawnLimits.Default.Budget` becomes 8. A Core `ExchangeCommands` class owns the reserved `/continue` command and the turn ceiling; `Mentions.Leading` and its client twin read a `turns: N` token as part of the leading run (the same linear sticky walk the mention reader already does, no second regex) and report it beside the recipients. `Exchange` learns who the owner addressed first (`Addressee`), which hand-offs the budget refused (`Refused`), the last model post, and whether the synthesis turn was queued. `ExchangePolicy.Accept` records refused hand-offs; `ExchangePolicy.Finished` queues one synthesis turn for the addressee when another participant posted last, taking a free turn when one is left and adding one otherwise; the service ignores a synthesis spawn's own hand-offs (a guard, not a sentence). `ExchangePolicy.Continue` reopens a closed owner-rooted exchange with more turns and re-queues the message's own mentions, else the refused hand-offs (each carrying the message that made it), else the addressee. `SpawnerService.OnMessage` routes a human `/continue` to the exchange the message replies to, else the room's newest owner-rooted exchange in room order. The snapshot carries `continuable`; `ExchangeBar` renders a Continue button that posts `/continue` as a reply to the exchange's root, so the button and the phone share one code path and one visible trail. The prompt gets one why-line each for a synthesis and a continuation, and the last-turn sentence defers to the addressee.

**Author model:** Fable 5.1 (session model matches the HIGH routing; no mismatch).

**Blast radius: HIGH.** The spawn loop's dispatch rules change (who gets spawned, how many times: every wrong rule spends real model calls or silently drops a hand-off), the spawn prompt and the MCP instructions are cross-process contracts, and the shared C#/TS mention grammar changes on both sides. Tier evidence (`Check-BlastRadius.ps1 -RepoPath . -Files <the 22 planned paths>`, 2026-09-18, exit 3):

```
TIER-EVIDENCE HIGH src/ChopItUp.Hub/Skills/SkillImport.cs:116 delete-replace: Directory.Move(candidateReplaced, candidateTarget);
TIER-EVIDENCE HIGH src/ChopItUp.Hub/Spawning/SpawnerService.cs:1140 delete-replace: PostNote(room, $"@{id} replied without posting to the room (exit code {exit}). Its reply:\n\n{Trunca…
TIER-EVIDENCE HIGH src/ChopItUp.Hub/Spawning/SpawnerService.cs:996 secrets: File.WriteAllText(mcpPath, SpawnCommands.ClaudeMcpConfigJson(McpUrl(), token, mcpToolTimeoutMs));
TIER-EVIDENCE HIGH src/ChopItUp.Hub/client/src/App.tsx:641 secrets: const stored = writeOwnerToken(token);
TIER-EVIDENCE HIGH tests/ChopItUp.Hub.Tests/Spawning/SpawnerServiceTests.cs:211 serialization: JsonSerializer.Serialize(new { type = "result", result = "Here is my answer, and by the way the toke…
TIER-EVIDENCE: HIGH triggers in 4 of 22 files (delete-replace, secrets, serialization)
```

All five lines are pre-existing code no task edits (the `-Base main` scan in Phase B reads added lines only and is expected to exit 0); the tier rests on the dispatch-rule and cross-process-contract argument. No persisted format changes: `Exchange` is in-memory only (plan decision 3, `Exchange.cs:22`), so no schema guard test applies.

Written for builder-subagent execution; if something doesn't match, STOP and report rather than guess.

**Size WARN:** above the 60 KB line. Not split: the override, the synthesis turn and `/continue` share one `Exchange` state machine, and the test code is the executable spec the builders run verbatim. Row 42 shipped at 63 KB under the same WARN.

**Lessons consulted** (`docs/LESSONS.md`): Row 43 (the two mention readers are twins only on the fixture's alphabet: every grammar change lands in `tests/mention-cases.json` with cases both sides run; spell classes in ASCII, never `\w`), Row 42 (a dry run is a gate only once its negative leg was seen RED with the mechanism reverted; barriers are hub notes in an exact sequence, never a timer or a count), Row 34 (a stub `codex.cmd` first on the scratch hub's PATH holds an exchange open with no model call; only Codex rows take the shim), M24 (call the deciding function directly in policy tests, keep one real-path service test proving it is wired, revert each mechanism once and record which test fails), M27 (a note that names an actor takes that actor as a parameter), M11 (a wording change is a literal sweep), M23/M16 (the UIA helper is the interactive gate; the browser pane drops input while hidden), M10 (`Invoke-RestMethod` arrays go through `ForEach-Object { $_ }`), Row 26 (`claude auth status` before any live leg; the dry run below spends nothing).

**Defect at HEAD `52808e9`** ([V 2026-09-18 52808e9]): `SpawnLimits.Default.Budget` is 4 (`SpawnLimits.cs:13`) and `Accept` refuses at `TurnsCommitted >= Budget` (`ExchangePolicy.cs:254`). Live General #46 (owner → `@opus`, ask Terra one question) ran opus, terra, terra, and Terra's `@opus` reply (#50) was refused by the budget (#51 `Budget of 4 turns is used up … not spawning @opus`), then #52 `Exchange concluded: 4 of 4 turns used.`: the owner read Terra's raw answer, and Opus's earlier summary (#49) ends with "Say the word and I'll put the question back to them", which nothing in the hub can act on. Decision D4 (approved 2026-09-17): budget 8, per-message override, Continue with a reserved synthesis turn.

## Decisions this plan takes

- **D-a Budget 8, hard ceiling 16.** `SpawnLimits.Default.Budget = 8`; `ExchangeCommands.MaxTurns = 16` (Core, read by the reader and the policy). Run worker exchanges (`OpenForWorkers`) take the same default, so they go 4 → 8 too; `RunLimits.SpawnCap` is unchanged and no test asserts the run number: the overlay has workers mention nobody, so a run never reaches it. The conductor's own exchange stays `Budget = 1`. Declined: making the budget configuration (D7: caps are hard code).
- **D-b `turns: N` is a token of the leading run.** `turns:` (any letter case), an optional single space or tab, 1 to 3 digits, not followed by a letter, digit, `_`, `.` or `-`, standing anywhere inside the leading run the mention reader already walks (after the optional `/name` or `phase:` prefix, among the `@word` tokens and their separators, which may span lines); the first such token wins and the run goes on past it. So `turns: 3 @opus`, `@opus turns:3 @sonnet`, `/grill turns: 5 @opus`, `/continue turns: 2` and `@opus,\nturns: 3 @sonnet` count; `@opus how many turns: 16 did we burn?` is prose (pass 1) and so is anything after the first non-token word. One walker reads mentions and the token on each side (`Mentions.Leading`, `recipientsOf`), linear time by construction (pass 2 B1: an anchored regex over the run backtracks catastrophically); the shared fixture pins both readers on every case, including a 14-mention punctuation canary. A value outside 1..16 (or `turns: 0`) gets one hub note, `turns: must be a whole number from 1 to 16; the default 8 applies.`, and the message still dispatches with the default. The token applies to an owner prompt that opens an exchange and to `/continue` (the number of turns added); on any other message it is prose. Declined: a composer control or an MCP parameter (the phone hand types through `post_message`, so a text token is the one shape both hands share).
- **D-c The addressee and the synthesis turn (pass 1 F11 taken).** For an exchange an owner prompt opens outside a run (`Joinable`), `Addressee` is the first spawnable leading recipient of the root message. Nothing is held back inside the budget: mentions and hand-offs are accepted against the plain budget as at HEAD, and a refused hand-off is recorded (`Refused`, first refusal per id, with the message that made it) so `/continue` can replay it. When the exchange would conclude (nothing pending, nothing in flight) and the last model post in it was by someone other than the addressee, and no synthesis was queued in this leg, the hub queues the addressee once with reason `Synthesis`: it takes a free turn when `TurnsCommitted < Budget`, otherwise `Budget` grows by one and the exchange remembers that it did, so a stop or supersede that drops the queued synthesis takes that turn back (pass 2 m1: no phantom turn in the stop note or in `/continue`'s arithmetic). No synthesis is queued when the spawn that just finished is the addressee itself and it posted nothing (a timed-out or crashed addressee is not retried on the hub's dime; pass 2 m7). The note: `Exchange started at #{root}: the hand-offs ended with @{last}'s post; queuing @{addressee}'s synthesis turn.` The conclusion after a synthesis reads `Exchange concluded: {n} of {B} turns used; the last was @{addressee}'s synthesis.` The synthesis spawn's own leading mentions are ignored by the service (`acceptMentions = false` for a handle whose request reason is `Synthesis`); its post is still a member and the last model post. A stop clears a queued synthesis like any pending spawn; run and conductor exchanges have no addressee. Declined: the held-back last turn (pass 1 F3, F4, F11).
- **D-d `/continue` is a reserved owner command**, parsed like `/stop` (`SlashCommands.TryParse`, first line), refused as a skill name by `SkillImport`, handled in `SpawnerService.OnMessage` before the policy sees the message and only for a human author; a model's `/continue` is prose. Target: the exchange holding the message it replies to (row 36's `JoinableFor`), else the room's last owner-rooted exchange in room order (`ExchangesIn(room).LastOrDefault(x => x.Joinable)`, the list `Reopen` moves a reopened exchange to the end of; then `_joinable[room]` last; pass 1 F6). Refusals, one note each: an active run in the room (`A run is active in this room (#{id}); /continue applies to plain exchanges.`), a reply to a message in no held exchange (`Reply to #{n}: that message is in no exchange this hub still holds, so there is nothing to continue.`), no exchange at all (`Nothing to continue in this room: no exchange this hub remembers.`), a run's exchange (`Exchange started at #{root} belongs to a run and cannot be continued.`), still open (`Exchange started at #{root} is still open with {n} turn(s) left; /continue once it has concluded.`; pass 2 M3), a spawn still finishing (`Exchange started at #{root} is still finishing @a; /continue again once it has.`), a message that named someone but nobody the hub can spawn (the row 43 unknown-word notes for each unknown leading word, then `/continue named nobody the hub can spawn; nothing was queued.`; pass 2 M5). A parked run's room resumes the run on any human post before this branch is reached (AC15 of row 19), `/continue` included; that is the existing rule and stays. Success: `Budget += turns override ?? default`, status Open, stop cause cleared, `TurnsCommitted = TurnsStarted`, the synthesis mark and last post reset, the `/continue` message joins the exchange; the queue is the message's own spawnable leading mentions if any, else the refused hand-offs in refusal order, each triggered by the message that made it plus the `/continue` message (pass 2 M4), else the addressee; every queued spawn carries reason `Continuation`; the note `Exchange started at #{root} continued: {extra} more turn(s), {B} in all; queued @a, @b.` lists what was accepted (`Pending` after the accept; pass 2 m2). Superseded exchanges can be continued too. A hub restart forgets exchanges (`Exchange.cs:22`), so `/continue` after one gets the "nothing to continue" note and the bar shows no button. The budget refusal note's tail says `once it has concluded, /continue extends it.` so the owner is never invited to an action the hub then refuses (pass 2 M3).
- **D-e The button posts the command.** `continuable` on the wire (`ExchangeView` and the snapshot top level) is the hub's decision: `Joinable`, not Open, nothing of its own in flight, no active run in the room. The top level follows `Displayed` (newest open, else newest) like every other top-level field; the per-strip value is the one the bar reads (pass 2 m6 noted, unchanged). `ExchangeBar` renders `Continue exchange` on a continuable strip; App posts `/continue` as a reply to that strip's root through the existing `postMessage`. Declined: a dedicated `POST …/continue` endpoint (two code paths for one action; the hub cannot post as the owner, so the trigger message would have been a hub note, which the prompt cannot cite as a member).
- **D-f Prompt sentences.** A `Synthesis` spawn's why-line: `Why you are here: the hand-offs of this exchange ended with @{last}'s message #{trigger}; this is your synthesis turn as the participant the owner addressed. Answer the owner on the original ask (message #{root}) in a few lines; a mention in this reply hands nothing on.` A `Continuation` spawn's why-line: with one trigger, `Why you are here: the owner continued this exchange with message #{trigger} after it ended; pick up where it left off.`; with two (a replayed refusal), `Why you are here: message #{refusing} mentioned you when the budget was spent; the owner continued this exchange with message #{continue}, so answer that mention now.` (pass 2 M4). The last-turn block (`RemainingAfter == 0`) keeps its conductor branch and its owner-ask sentence for the addressee or an exchange with no addressee; when the holder is not the addressee it reads `This is the last hand-off turn of the exchange: give your findings in a few lines; @{addressee} wraps up for the owner afterwards, so do not ask the owner whether to continue.` (pass 2 M2: one paid wrap-up, not two). A synthesis spawn gets no last-turn sentence. The `Mention` why-line, the started-at sentence and the turn line stay on one line and unchanged (the golden capture is byte-identical; its input is a run conductor with reason `Mention` and no addressee).
- **D-g Texts.** README caps paragraph (4 → 8, the ceiling, the token, the synthesis turn, `/continue`), `OVERLAY.md:12` and `OVERLAY.md:37` (`4 turns` → `8 turns`; the live copy is row 58's re-import, already OWNER), `Participation.cs` one sentence under Taking part, `ExchangePolicy.cs:36` doc comment, the refusal note's last sentence.

## Acceptance

- **AC1** WHEN an owner prompt opens an exchange with no `turns:` token, THE SYSTEM SHALL give it 8 turns; WHEN the leading run carries `turns: N` with N in 1..16, THE SYSTEM SHALL give it N turns, both readers SHALL read the same recipients and the same token on every fixture case, and reading a 14-mention punctuation line SHALL take milliseconds on both sides; WHEN N is outside 1..16, THE SYSTEM SHALL post the range note once and dispatch with the default; WHEN the token sits after prose, it SHALL be prose.
- **AC2** WHEN a hand-off is refused by the budget, THE SYSTEM SHALL record it with its message (first refusal per id) and say in the note that `/continue` extends the exchange once it has concluded; WHEN a synthesis spawn's reply begins with `@id`, THE SYSTEM SHALL spawn nothing for it and still count the reply as the exchange's last model post.
- **AC3** WHEN an owner-rooted exchange would conclude and the last model post in it was by a participant other than the addressee, and no synthesis was queued in this leg, and the finishing spawn is not a silent addressee, THE SYSTEM SHALL queue exactly one synthesis spawn of the addressee (a free turn if one is left, else one added to the budget and taken back if the synthesis is dropped by a stop), post the queuing note, and conclude after it with the synthesis conclusion note; WHEN the addressee posted last, or the exchange was stopped, or a synthesis already ran in this leg, THE SYSTEM SHALL conclude as at HEAD with no extra spawn; WHEN the last hand-off turn's holder is not the addressee, its prompt SHALL tell it not to ask the owner whether to continue.
- **AC4** WHEN a human posts `/continue` outside an active run, THE SYSTEM SHALL reopen the replied-to exchange (else the room's last owner-rooted one) with `turns:` more turns (default 8), queue its own spawnable leading mentions, else the refused hand-offs each triggered by the refusing message and the `/continue`, else the addressee, and post the continued note naming what was accepted; WHEN there is nothing to continue, the exchange is open, a spawn is still finishing, a run owns the exchange, a run is active, or the message named nobody spawnable, THE SYSTEM SHALL post the matching note(s) and spawn nothing; a model's `/continue` SHALL be prose.
- **AC5** WHEN a strip's exchange is continuable (owner-rooted, closed, nothing of its own in flight, no active run), THE SYSTEM SHALL render `Continue exchange` beside the marker; pressing it SHALL post `/continue` as a reply to that root and disable the control until the post returns; WHEN a run is live or the exchange is open, no Continue SHALL render; WHEN the draft's leading run carries a `turns:` token, the composer strip SHALL show `N turns` (or the out-of-range wording) beside the recipient chips.
- **AC6** WHEN spawn prompts are rendered, THE SYSTEM SHALL state the synthesis why-line, both continuation why-lines and the non-addressee last-turn sentence exactly as D-f words them, and the golden capture SHALL be byte-identical to HEAD's.
- **AC7** WHEN `tools\Invoke-Row44ContinueCheck.ps1` runs the built hub with a stub `codex.cmd` first on PATH, THE SYSTEM SHALL pass these legs in order: `health.ok`; `turns.override`; `turns.range-note`; `continue.open-refused`; `continue.after-stop`; `continue.nothing`; `stop.cleanup`; and with the `/continue` branch removed from `OnMessage`, legs 4, 5 and 6 SHALL fail (recorded in the script header). The synthesis, its guard and the refusal recording are gated by the mechanism reverts in Task 3, each naming the test that goes red.
- **AC8** WHEN the verify-skill helper drives the real bar over UIA (stub Codex exchange, per-exchange stop, press Continue), THE SYSTEM SHALL expose the `Continue exchange` button, the owner's `/continue` post and the hub's continued note in the thread, and the strip back to open; `Test-CaptureSane.ps1` SHALL pass on the captures.

## Claim ledger

| # | Claim | Verified at (commit) | Recheck (pwsh, exit 0 = holds) |
|---|-------|----------------------|--------------------------------|
| 1 | Baseline: Desktop 108/108, Core 286/286, Hub 890/890 (full suite green, measured 2026-09-18 this session, Hub 7 min 16 s); vitest 229/229 in 16 files | 52808e9 | `dotnet test ChopItUp.slnx -c Debug --nologo -v minimal` + `npx vitest run` in `src/ChopItUp.Hub/client` |
| 2 | `SpawnLimits.Default` is `Budget: 4` and `SpawnLimitsTests` asserts the tuple `(4, 2 s, 10 s, 5 min, 60, 24_000)` | 52808e9 | `pwsh -NoProfile -Command "$a=Get-Content 'src/ChopItUp.Hub/Spawning/SpawnLimits.cs' -Raw; $b=Get-Content 'tests/ChopItUp.Hub.Tests/Spawning/SpawnLimitsTests.cs' -Raw; if($a -match 'Budget: 4,' -and $b -match 'Assert.Equal\(\(4, TimeSpan'){exit 0}else{exit 1}"` |
| 3 | `Exchange.Budget` is `required int Budget { get; init; }` and `Exchange` is documented as not persisted | 52808e9 | `pwsh -NoProfile -Command "$s=Get-Content 'src/ChopItUp.Hub/Spawning/Exchange.cs' -Raw; if($s -match 'public required int Budget \{ get; init; \}' -and $s -match 'It is not persisted'){exit 0}else{exit 1}"` |
| 4 | `ExchangePolicy.Accept` refuses on `x.TurnsCommitted >= x.Budget` and posts the literal `Budget of {x.Budget} turns is used up for the exchange started at #{x.RootMessageId}` | 52808e9 | `pwsh -NoProfile -Command "$s=Get-Content 'src/ChopItUp.Hub/Spawning/ExchangePolicy.cs' -Raw; if($s -match 'if \(x\.TurnsCommitted >= x\.Budget\) \{ refused\.Add\(id\); continue; \}' -and $s -match [regex]::Escape('Budget of {x.Budget} turns is used up for the exchange started at #{x.RootMessageId}')){exit 0}else{exit 1}"` |
| 5 | `ExchangePolicy.Finished(Exchange x, string participantId)` is static, returns `string?`, and is called at SpawnerService.cs lines 1102 and 1152 only; `Started(Exchange x, SpawnRequest request)` is static | 52808e9 | `pwsh -NoProfile -Command "$n=(Select-String -Path 'src/ChopItUp.Hub/Spawning/SpawnerService.cs' -Pattern 'ExchangePolicy\.Finished\(').Count; $s=Get-Content 'src/ChopItUp.Hub/Spawning/ExchangePolicy.cs' -Raw; if($n -eq 2 -and $s -match 'public static string\? Finished\(Exchange x, string participantId\)' -and $s -match 'public static void Started\(Exchange x, SpawnRequest request\)'){exit 0}else{exit 1}"` |
| 6 | `RunCommands.IsStop` reuses `SlashCommands.TryParse`; `SkillImport` refuses `RunCommands.StopName` with the literal `is a reserved name`; `SkillImportTests` covers it around line 114 | 52808e9 | `pwsh -NoProfile -Command "$r=Get-Content 'src/ChopItUp.Core/Skills/RunCommands.cs' -Raw; $i=Get-Content 'src/ChopItUp.Hub/Skills/SkillImport.cs' -Raw; $t=Get-Content 'tests/ChopItUp.Hub.Tests/Skills/SkillImportTests.cs' -Raw; if($r -match 'SlashCommands\.TryParse\(body, out var command\)' -and $i -match 'RunCommands\.StopName' -and $i -match 'is a reserved name' -and $t -match 'RunCommands\.StopName'){exit 0}else{exit 1}"` |
| 7 | `Mentions.Leading` skips only a `/name` token or a `phase:` tag, then loops `Token.Match` from `start` (sticky `\G`, separator class `[ \t\r\n\f\v,:;]`); the client twin `recipientsOf` mirrors it with `SLASH`/`PHASE`/`TOKEN` (sticky) and returns `{ recipients, unknown, references }` | 52808e9 | `pwsh -NoProfile -Command "$m=Get-Content 'src/ChopItUp.Core/Messaging/Mentions.cs' -Raw; $p=Get-Content 'src/ChopItUp.Hub/client/src/participants.ts' -Raw; if($m -match 'for \(var m = Token\.Match\(text, start\)' -and $m -match [regex]::Escape('\G[ \t\r\n\f\v,:;]*@') -and $p -match 'TOKEN\.lastIndex = start;' -and $p -match 'return \{ recipients, unknown, references \};'){exit 0}else{exit 1}"` |
| 8 | `tests/mention-cases.json` holds 32 cases read by `MentionsTests.Leading_and_the_reference_remainder_match_the_shared_fixture` ([Theory]/[MemberData], one test per case) and by `participants.test.ts` | 52808e9 | `pwsh -NoProfile -Command "$n=((Get-Content 'tests/mention-cases.json' -Raw \| ConvertFrom-Json).cases).Count; $t=Get-Content 'tests/ChopItUp.Core.Tests/Messaging/MentionsTests.cs' -Raw; if($n -eq 32 -and $t -match 'Leading_and_the_reference_remainder_match_the_shared_fixture' -and $t -match '\[MemberData\(nameof\(Cases\)\)\]'){exit 0}else{exit 1}"` |
| 9 | `SpawnerService.OnMessage` computes `var skill = ResolveSkill(m);` before the `/stop` branch, and the `/stop` branch tests `RunCommands.IsStop(m.Body)` with a human author; the parked-run resume branch precedes `_policy.OnRoomMessage` | 52808e9 | `pwsh -NoProfile -Command "$s=Get-Content 'src/ChopItUp.Hub/Spawning/SpawnerService.cs' -Raw; $a=$s.IndexOf('var skill = ResolveSkill(m);'); $b=$s.IndexOf('if (RunCommands.IsStop(m.Body) &&'); $c=$s.IndexOf('DriveRun(parkedRun, new RunEvent.HumanPosted(m.Id));'); $d=$s.IndexOf('_policy.OnRoomMessage(exchanges, target, m, now'); if($a -gt 0 -and $a -lt $b -and $b -lt $c -and $c -lt $d){exit 0}else{exit 1}"` |
| 10 | `SpawnerService` keeps `_joinable` (last `JoinableKept = 50` owner-rooted exchanges per room, appended at open only), `JoinableFor(roomId, messageId)`, `Reopen(roomId, x)` (moves x to the end of `_rooms[roomId]`, adding it if absent; never touches `_joinable`) and `Remember(roomId, x)` | 52808e9 | `pwsh -NoProfile -Command "$s=Get-Content 'src/ChopItUp.Hub/Spawning/SpawnerService.cs' -Raw; if($s -match 'internal const int JoinableKept = 50;' -and $s -match 'private Exchange\? JoinableFor\(string roomId, long messageId\)' -and $s -match 'private void Reopen\(string roomId, Exchange x\)' -and $s -match 'list\.Remove\(x\);\s*list\.Add\(x\);'){exit 0}else{exit 1}"` |
| 11 | `SpawnRequest` is `(RoomId, ParticipantId, TriggerIds, RootMessageId, TurnNumber, RemainingAfter)`; `Due` builds it with `x.Budget - x.TurnsCommitted` as `RemainingAfter`; `PendingSpawn` has `TriggerIds` and `LastTriggerAt` only | 52808e9 | `pwsh -NoProfile -Command "$e=Get-Content 'src/ChopItUp.Hub/Spawning/Exchange.cs' -Raw; $p=Get-Content 'src/ChopItUp.Hub/Spawning/ExchangePolicy.cs' -Raw; if($e -match 'public sealed record SpawnRequest\(string RoomId, string ParticipantId, IReadOnlyList<long> TriggerIds, long RootMessageId, int TurnNumber, int RemainingAfter\);' -and $p -match 'x\.TurnsStarted \+ due\.Count \+ 1, x\.Budget - x\.TurnsCommitted\)'){exit 0}else{exit 1}"` |
| 12 | `SpawnPromptInput` ends with `SpawnPrompt.StandingText? Standing = null);`; the why-line, the started-at sentence and the turn line are one `Append` chain ending `turn(s) remain after yours.\n`; the last-turn owner-ask sentence is `summarise the exchange in a few lines, and ask the owner whether to continue.`; the golden test is `R14_a_prompt_with_no_standing_text_is_byte_for_byte`; `Render` reads `limits` only for `TranscriptChars` | 52808e9 | `pwsh -NoProfile -Command "$s=Get-Content 'src/ChopItUp.Hub/Spawning/SpawnPrompt.cs' -Raw; $t=Get-Content 'tests/ChopItUp.Hub.Tests/Spawning/SpawnPromptTests.cs' -Raw; if($s -match 'SpawnPrompt\.StandingText\? Standing = null\);' -and $s -match [regex]::Escape('.Append(input.RemainingAfter).Append(" turn(s) remain after yours.\n");') -and $s -match [regex]::Escape('summarise the exchange in a few lines, and ask the owner whether to continue.') -and $t -match 'R14_a_prompt_with_no_standing_text_is_byte_for_byte' -and (Test-Path 'tests/ChopItUp.Hub.Tests/Spawning/golden-prompt-ddfa572.txt')){exit 0}else{exit 1}"` |
| 13 | `ExchangeView` and `ExchangeSnapshot` are positional records in SpawnerService.cs; `View(Exchange x)` and `Publish(string roomId)` build them; `types.ts` mirrors both with `stoppedBy` last on `ExchangeView` | 52808e9 | `pwsh -NoProfile -Command "$s=Get-Content 'src/ChopItUp.Hub/Spawning/SpawnerService.cs' -Raw; $t=Get-Content 'src/ChopItUp.Hub/client/src/types.ts' -Raw; if($s -match 'public sealed record ExchangeView\(' -and $s -match 'private static ExchangeView View\(Exchange x\) => new\(' -and $s -match 'private ExchangeSnapshot Publish\(string roomId\)' -and $t -match 'stoppedBy: ExchangeSnapshot\[.stoppedBy.\];\s*\}'){exit 0}else{exit 1}"` |
| 14 | `ExchangeBar.tsx` takes `onStop: (root: number \| null) => void`, renders `Stop exchange` as `button.quiet.danger`, and its tests render through `renderToStaticMarkup` and walk buttons with `findButtons` | 52808e9 | `pwsh -NoProfile -Command "$b=Get-Content 'src/ChopItUp.Hub/client/src/ExchangeBar.tsx' -Raw; $t=Get-Content 'src/ChopItUp.Hub/client/src/ExchangeBar.test.tsx' -Raw; if($b -match 'onStop: \(root: number \| null\) => void;' -and $b -match 'Stop exchange' -and $t -match 'function findButtons'){exit 0}else{exit 1}"` |
| 15 | `App.tsx` posts through `api.postMessage(roomId, body, replyToId)` inside `send` and wires `<ExchangeBar … onStop={stopFromBar} />`; `RecipientStrip` is rendered from `Composer.tsx` with `draft` only | 52808e9 | `pwsh -NoProfile -Command "$a=Get-Content 'src/ChopItUp.Hub/client/src/App.tsx' -Raw; $c=Get-Content 'src/ChopItUp.Hub/client/src/Composer.tsx' -Raw; if($a -match 'api\.postMessage\(roomId, body, replyToId\)' -and $a -match 'onStop=\{stopFromBar\}' -and $c -match '<RecipientStrip draft=\{draft\} />'){exit 0}else{exit 1}"` |
| 16 | `tools/Invoke-Row43MentionCheck.ps1` exists with the frame the new script mirrors (`Add-Check`, `Results: n/m PASS`, stub `codex.cmd`, `ChopTokenHelpers.ps1`) | 52808e9 | `pwsh -NoProfile -Command "$s=Get-Content 'tools/Invoke-Row43MentionCheck.ps1' -Raw; if($s -match 'function Add-Check' -and $s -match 'ChopTokenHelpers' -and $s -match 'codex\.cmd'){exit 0}else{exit 1}"` |
| 17 | The four-turn literal occurs exactly 4 times across README.md, OVERLAY.md and ExchangePolicy.cs: README:86 `4 turns per exchange`, OVERLAY:12 `at most 4 turns`, OVERLAY:37 `≤ 4 turns`, ExchangePolicy:36 `D5: four turns`; every `of 4` in tests comes from a test-local `Budget: 4` limits object. Phase B post-condition: 0 hits | 52808e9 | `pwsh -NoProfile -Command "$n=(Select-String -Path 'README.md','tools/skills/roadmap-hub/OVERLAY.md','src/ChopItUp.Hub/Spawning/ExchangePolicy.cs' -Pattern '4 turns\|four turns').Count; if($n -eq 4){exit 0}else{exit 1}"` |
| 18 | Live evidence: General #51 is the budget refusal and #52 `Exchange concluded: 4 of 4 turns used.` (read over MCP this session); the row rests on claims 2 and 4, not on this | — | — (live hub; unautomatable) |
| 19 | The loop drains every queued event, then `LaunchDue`, then `ArmWake` (SpawnerService.cs:277-280); a synthesis queued in `OnFinished` with `LastTriggerAt = now` is not due until `Debounce` (2 s in production) has passed, so the timer `ArmWake` arms launches it, later still if `MinSpacing` (10 s) since the addressee's last start has not passed; the policy test asserts `NextWake` = finish + `Debounce` | 52808e9 | `pwsh -NoProfile -Command "$s=Get-Content 'src/ChopItUp.Hub/Spawning/SpawnerService.cs' -Raw; if($s -match 'Guarded\(LaunchDue, nameof\(LaunchDue\)\);\s*Guarded\(ArmWake, nameof\(ArmWake\)\);'){exit 0}else{exit 1}"` |
| 20 | The loop is single-threaded and the spawn's post reaches `OnMessage` through `_inFlight[(room, author)]` whose `SpawnHandle` carries the `SpawnRequest` (`handle.Request`) and `Posted`; `OnFinished` calls `Finished` before `CloseIdleWorktrees`, so a queued synthesis keeps a worktree exchange open | 52808e9 | `pwsh -NoProfile -Command "$s=Get-Content 'src/ChopItUp.Hub/Spawning/SpawnerService.cs' -Raw; $a=$s.IndexOf('var note = ExchangePolicy.Finished(h.Exchange, id);'); $b=$s.IndexOf('CloseIdleWorktrees(room);', $a); if($s -match 'private sealed class SpawnHandle' -and $s -match 'h\.Request\.RoomId' -and $s -match 'handle\.Posted = true;' -and $a -gt 0 -and $b -gt $a){exit 0}else{exit 1}"` |
| 21 | Hub notes are stored messages with room-sequential ids (`PostNote` → `_store.Post`), so in a fresh test room a note posted before the owner's next post takes the next id; `SpawnPromptTests.Input` is `Input(int turn, int remainingAfter, params Message[] transcript)`; `ExchangeApiTests` builds its own `Budget: 4` limits | 52808e9 | `pwsh -NoProfile -Command "$s=Get-Content 'src/ChopItUp.Hub/Spawning/SpawnerService.cs' -Raw; $t=Get-Content 'tests/ChopItUp.Hub.Tests/Spawning/SpawnPromptTests.cs' -Raw; $e=Get-Content 'tests/ChopItUp.Hub.Tests/ExchangeApiTests.cs' -Raw; if($s -match 'private void PostNote\(string roomId, string text\)' -and $t -match 'private static SpawnPromptInput Input\(int turn, int remainingAfter, params Message\[\] transcript\)' -and $e -match 'Budget: 4,'){exit 0}else{exit 1}"` |

## Tasks

Sequential chain unless stated. Every task: RED first (the named test fails for the stated reason), then GREEN, then `dotnet build ChopItUp.slnx -c Debug -warnaserror -v minimal` clean and the task's named test filter green, one commit per task on branch `row44-continue-exchange`. The whole Hub test project is green again at the end of Task 3 (Task 2 knowingly leaves `SpawnerServiceTests.A2` red until Task 3 rewrites it). Commit messages carry the task number and end with the co-author trailer the repo uses. Collection assertions on `OrderedDictionary` key collections and on `List<long>` members are written as their own `Assert.Equal([..], x.Keys)` lines, never inside a tuple (pass 2 M1b/c).

### Task 1 — Core: the `turns:` token in the leading walk, `ExchangeCommands`, the reserved `/continue`, and the twin readers (sonnet)

Files: `src/ChopItUp.Core/Skills/ExchangeCommands.cs` (new), `src/ChopItUp.Core/Messaging/Mentions.cs`, `src/ChopItUp.Hub/client/src/participants.ts`, `tests/mention-cases.json`, `tests/ChopItUp.Core.Tests/Messaging/MentionsTests.cs`, `src/ChopItUp.Hub/client/src/participants.test.ts`, `src/ChopItUp.Hub/Skills/SkillImport.cs`, `tests/ChopItUp.Core.Tests/Skills/ExchangeCommandsTests.cs` (new), `tests/ChopItUp.Hub.Tests/Skills/SkillImportTests.cs`.

**RED:** the eleven fixture cases below fail in `MentionsTests` (the `turns:` token ends the leading run at HEAD and `LeadingMentions` has no `Turns`) and in `participants.test.ts`; `ExchangeCommandsTests` does not compile.

New file `src/ChopItUp.Core/Skills/ExchangeCommands.cs`:

```csharp
namespace ChopItUp.Core.Skills;

/// <summary>What the `turns:` token in a message's leading run said: absent, a value in range, or a
/// value the hub refuses (out of range or zero). The reader never clamps: a wrong number is reported so
/// the owner sees the note, and the caller applies the default.</summary>
public enum TurnsToken { None, Valid, OutOfRange }

/// <summary>Row 44: the reserved `/continue` command (parsed through <see cref="SlashCommands"/> like
/// `/stop`, refused as a skill name by <c>SkillImport</c>) and the ceiling of the `turns: N` token that
/// <c>Mentions.Leading</c> reads as part of the leading run.</summary>
public static class ExchangeCommands
{
    public const string ContinueName = "continue";
    public const int MaxTurns = 16;

    public static bool IsContinue(string? body) =>
        SlashCommands.TryParse(body, out var command) && string.Equals(command.Name, ContinueName, StringComparison.Ordinal);
}
```

`Mentions.cs` (`using ChopItUp.Core.Skills;`): `LeadingMentions` gains two members and the loop reads the token as a second sticky step of the same walk, so the reader stays linear (each step consumes input or ends the run):

```csharp
    public sealed record LeadingMentions(IReadOnlyList<string> Recipients, IReadOnlyList<string> Unknown, TurnsToken Turns = TurnsToken.None, int TurnsValue = 0)
    {
        public static readonly LeadingMentions None = new([], []);
    }

    /// <summary>Row 44: `turns: N` inside the leading run, read in the same sticky walk as <see cref="Token"/>;
    /// the first one wins and the run goes on past it. Letters are spelled per case so V8 needs no flag;
    /// the digit class is ASCII like every other class here (row 43).</summary>
    private static readonly Regex Turns = new(@"\G[ \t\r\n\f\v,:;]*[Tt][Uu][Rr][Nn][Ss]:[ \t]?(?<n>[0-9]{1,3})(?![A-Za-z0-9_.\-])", RegexOptions.CultureInvariant);

    public LeadingMentions Leading(string body)
    {
        if (string.IsNullOrEmpty(body)) return LeadingMentions.None;
        var text = body.Replace("\r\n", "\n");
        var start = 0;
        if (SlashCommands.TryParse(text, out var command)) start = 1 + command.Name.Length;
        else if (PhaseTag.TryParse(text, out _, out var tagLength)) start = tagLength;
        var recipients = new List<string>();
        var unknown = new List<string>();
        var turns = TurnsToken.None;
        var turnsValue = 0;
        var at = start;
        while (true)
        {
            var t = Turns.Match(text, at);
            if (t.Success)
            {
                at = t.Index + t.Length;
                if (turns == TurnsToken.None)
                {
                    var n = int.Parse(t.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
                    turns = n >= 1 && n <= ExchangeCommands.MaxTurns ? TurnsToken.Valid : TurnsToken.OutOfRange;
                    turnsValue = turns == TurnsToken.Valid ? n : 0;
                }
                continue;
            }
            var m = Token.Match(text, at);
            if (!m.Success) break;
            at = m.Index + m.Length;
            var word = m.Groups["word"].Value.TrimEnd('.', ',', ':', ';', '!', '?');
            if (_canonical.TryGetValue(word, out var id)) { if (!recipients.Contains(id)) recipients.Add(id); }
            else if (!unknown.Contains(word, StringComparer.OrdinalIgnoreCase)) unknown.Add(word);
        }
        return new LeadingMentions(recipients, unknown, turns, turnsValue);
    }
```

Update the `Leading` summary: after "after an optional command prefix" add "and reading any `turns: N` token in the run as the exchange's turn count (row 44)".

`participants.ts`: `Recipients` gains `turns: { turns: number; valid: boolean } | null`; next to `TOKEN` add

```ts
// Row 44: `turns: N` inside the leading run, read in the same sticky walk as TOKEN. Twin of Mentions.Turns.
const TURNS = /[ \t\r\n\f\v,:;]*[Tt][Uu][Rr][Nn][Ss]:[ \t]?([0-9]{1,3})(?![A-Za-z0-9_.-])/y;
export const MAX_TURNS = 16;
```

and replace the `TOKEN.lastIndex = start; for (…)` loop with:

```ts
  let turns: Recipients['turns'] = null;
  let at = start;
  for (;;) {
    TURNS.lastIndex = at;
    const t = TURNS.exec(text);
    if (t !== null) {
      at = TURNS.lastIndex;
      if (turns === null) {
        const n = Number(t[1]);
        turns = { turns: n, valid: n >= 1 && n <= MAX_TURNS };
      }
      continue;
    }
    TOKEN.lastIndex = at;
    const m = TOKEN.exec(text);
    if (m === null) break;
    at = TOKEN.lastIndex;
    const word = m[1]!.replace(/[.,:;!?]+$/, '');
    const p = roster.get(word.toLowerCase());
    if (p && p.kind !== 'system') {
      if (!recipients.includes(p)) recipients.push(p);
    } else if (!unknown.some((u) => u.toLowerCase() === word.toLowerCase())) unknown.push(word);
  }
```

and the return becomes `return { recipients, unknown, references, turns };` (the `valid: false` shape keeps the out-of-range number for the strip's wording; `null` is absent).

`tests/mention-cases.json`: append eleven cases (existing 32 untouched). A case may carry `"turns"`: `{ "token": "valid", "value": N }`, `{ "token": "out-of-range" }` or `{ "token": "none" }`; a case without the field asserts nothing about turns.

```json
    { "name": "turns before the mentions",        "body": "turns: 3 @opus @sonnet go",                  "recipients": ["opus", "sonnet"], "unknown": [],       "references": [],         "turns": { "token": "valid", "value": 3 } },
    { "name": "turns between mentions, no space",  "body": "@opus turns:3 @sonnet go",                   "recipients": ["opus", "sonnet"], "unknown": [],       "references": [],         "turns": { "token": "valid", "value": 3 } },
    { "name": "turns after a slash token",         "body": "/grill turns: 5 @opus what about X",         "recipients": ["opus"],           "unknown": [],       "references": [],         "turns": { "token": "valid", "value": 5 } },
    { "name": "turns on the next line of the run", "body": "@opus,\nturns: 3 @sonnet go",                "recipients": ["opus", "sonnet"], "unknown": [],       "references": [],         "turns": { "token": "valid", "value": 3 } },
    { "name": "turns out of range still skipped",  "body": "turns: 99 @opus go",                          "recipients": ["opus"],           "unknown": [],       "references": [],         "turns": { "token": "out-of-range" } },
    { "name": "turns zero is out of range",        "body": "Turns:0 @opus",                               "recipients": ["opus"],           "unknown": [],       "references": [],         "turns": { "token": "out-of-range" } },
    { "name": "turns glued to a word is prose",    "body": "@opus xturns:3 @sonnet",                       "recipients": ["opus"],           "unknown": [],       "references": ["sonnet"], "turns": { "token": "none" } },
    { "name": "turns after prose is prose",        "body": "@opus how many turns: 16 did we burn?",        "recipients": ["opus"],           "unknown": [],       "references": [],         "turns": { "token": "none" } },
    { "name": "first turns token wins",            "body": "turns: 2 turns: 9 @opus",                      "recipients": ["opus"],           "unknown": [],       "references": [],         "turns": { "token": "valid", "value": 2 } },
    { "name": "continue with a mention and turns", "body": "/continue @sonnet turns: 2 more",             "recipients": ["sonnet"],         "unknown": [],       "references": [],         "turns": { "token": "valid", "value": 2 } },
    { "name": "perf canary: 14 mentions with punctuation runs", "body": "@nobody,,,,,, @nobody,,,,,, @nobody,,,,,, @nobody,,,,,, @nobody,,,,,, @nobody,,,,,, @nobody,,,,,, @nobody,,,,,, @nobody,,,,,, @nobody,,,,,, @nobody,,,,,, @nobody,,,,,, @nobody,,,,,, @nobody,,,,,, go", "recipients": [], "unknown": ["nobody"], "references": [], "turns": { "token": "none" } }
```

`MentionsTests.cs`: the fixture record gains an optional `Turns` member (`TurnsCase? Turns` with `Token` and `Value`); the theory's data adds it as a seventh column and the test asserts, when non-null, `leading.Turns`/`leading.TurnsValue` map `valid`/`out-of-range`/`none` to the enum and `Value` (0 when absent). The theory also asserts the case runs under 200 ms (`Stopwatch` around `Leading`; the canary is what this guards). `participants.test.ts`: the same, against `recipientsOf(body).turns` (`none` ↔ `null`, `out-of-range` ↔ `valid === false`, `valid` ↔ `{ turns: value, valid: true }`), with the same 200 ms bound via `performance.now()`.

`SkillImport.cs`: directly after the `RunCommands.StopName` refusal add the same shape for `ExchangeCommands.ContinueName` with the text `'{name}' is a reserved name (the exchange continue command) and cannot be installed as a skill.` (add `using ChopItUp.Core.Skills;` if the file lacks it). `SkillImportTests.cs`: duplicate the test around lines 114-126 for `ExchangeCommands.ContinueName` (name it `Refuses_the_reserved_continue_name`).

New `tests/ChopItUp.Core.Tests/Skills/ExchangeCommandsTests.cs` (mirror the namespace of the neighbouring test files; create the folder if `Skills` does not exist there):

```csharp
using ChopItUp.Core.Messaging;
using ChopItUp.Core.Skills;

namespace ChopItUp.Core.Tests.Skills;

public sealed class ExchangeCommandsTests
{
    private static readonly Mentions Reader = new(["opus", "sonnet"]);

    [Theory]
    [InlineData("turns: 3 @opus", TurnsToken.Valid, 3)]
    [InlineData("@opus turns:16 go", TurnsToken.Valid, 16)]
    [InlineData("Turns: 1", TurnsToken.Valid, 1)]
    [InlineData("/continue turns: 4", TurnsToken.Valid, 4)]
    [InlineData("/continue @sonnet turns: 2 more", TurnsToken.Valid, 2)]
    [InlineData("@opus, @sonnet: turns: 6 go", TurnsToken.Valid, 6)]
    [InlineData("@opus,\nturns: 3 @sonnet", TurnsToken.Valid, 3)]
    [InlineData("turns: 0 @opus", TurnsToken.OutOfRange, 0)]
    [InlineData("turns: 17 @opus", TurnsToken.OutOfRange, 0)]
    [InlineData("turns: 999 @opus", TurnsToken.OutOfRange, 0)]
    [InlineData("@opus go\nturns: 3", TurnsToken.None, 0)]
    [InlineData("@opus xturns: 3", TurnsToken.None, 0)]
    [InlineData("@opus turns:  3", TurnsToken.None, 0)]
    [InlineData("@opus turns: 3x", TurnsToken.None, 0)]
    [InlineData("@opus how many turns: 16 did we burn?", TurnsToken.None, 0)]
    [InlineData("/Grill turns: 3", TurnsToken.None, 0)]
    [InlineData("turns: 2 turns: 9", TurnsToken.Valid, 2)]
    [InlineData("", TurnsToken.None, 0)]
    public void Turns_token_is_read_in_the_leading_run_first_wins_and_never_clamped(string body, TurnsToken expected, int value)
    {
        var leading = Reader.Leading(body);
        Assert.Equal(expected, leading.Turns);
        Assert.Equal(value, leading.TurnsValue);
    }

    [Theory]
    [InlineData("/continue", true)]
    [InlineData("/continue @sonnet more", true)]
    [InlineData("/continue\nsecond line", true)]
    [InlineData("please /continue", false)]
    [InlineData("/continued", false)]
    [InlineData("x\n/continue", false)]
    [InlineData(null, false)]
    public void Continue_is_the_first_line_slash_form_only(string? body, bool expected) =>
        Assert.Equal(expected, ExchangeCommands.IsContinue(body));
}
```

Commands: `dotnet test tests/ChopItUp.Core.Tests -c Debug --nologo -v minimal` (expect 286 + 11 fixture cases + 25 theory rows = 322; the builder reports the exact count), `dotnet test tests/ChopItUp.Hub.Tests --filter "FullyQualifiedName~SkillImportTests" -c Debug --nologo -v minimal`, and in `src/ChopItUp.Hub/client`: `npx vitest run participants` then `npm run typecheck`.

### Task 2 — Policy: budget 8, refused hand-offs, the synthesis turn, `Continue` (sonnet)

Files: `src/ChopItUp.Hub/Spawning/SpawnLimits.cs`, `src/ChopItUp.Hub/Spawning/Exchange.cs`, `src/ChopItUp.Hub/Spawning/ExchangePolicy.cs`, `tests/ChopItUp.Hub.Tests/Spawning/SpawnLimitsTests.cs`, `tests/ChopItUp.Hub.Tests/Spawning/ExchangePolicyTests.cs`.

**RED:** the new policy tests below fail to compile (no `Addressee`, no `Continue`, no four-argument `Finished`); `SpawnLimitsTests` fails on 8 once edited. **Gate for this task:** `dotnet build … -warnaserror` clean and the filter `FullyQualifiedName~ExchangePolicyTests|FullyQualifiedName~SpawnLimitsTests` green; `SpawnerServiceTests.A2` is expected red until Task 3 and is named as such in this task's commit message.

`SpawnLimits.cs`: `Budget: 8`; summary: `Budget = model turns per exchange (D5, row 44: 8; a turns: token in the leading run overrides it up to ExchangeCommands.MaxTurns)`. `SpawnLimitsTests`: the tuple's first element becomes 8.

`Exchange.cs`:

```csharp
/// <summary>Why a spawn is queued (row 44): a mention (the ordinary case), the addressee's synthesis
/// turn the hub queued itself, or a turn the owner's /continue re-queued.</summary>
public enum SpawnReason { Mention, Synthesis, Continuation }
```

`PendingSpawn` gains `public SpawnReason Reason { get; init; } = SpawnReason.Mention;`. `Exchange`: `Budget` becomes `{ get; set; }` (summary: "set at open, raised by /continue and by a synthesis turn that found no free turn"); add

```csharp
    /// <summary>Row 44: the first spawnable participant the owner's root message addressed, for an
    /// exchange an owner prompt opened outside a run; null for run and conductor exchanges. Gets one
    /// synthesis turn when another participant's post would otherwise have been the last.</summary>
    public string? Addressee { get; init; }

    /// <summary>Row 44: hand-offs the budget refused, in refusal order, each with the message that made
    /// it; what /continue re-queues when the owner names nobody. Cleared by a continue, and by a reply
    /// that reopened the exchange and was accepted.</summary>
    public OrderedDictionary<string, long> Refused { get; } = new(StringComparer.Ordinal);

    /// <summary>Row 44: a synthesis turn was queued in this leg; a second one never is. Reset by a
    /// continue or an accepted reopening reply.</summary>
    public bool SynthesisUsed { get; set; }

    /// <summary>Row 44: the queued synthesis found no free turn and grew the budget by one; cleared when
    /// it launches, and undone by a stop or supersede that drops it while still pending.</summary>
    public bool SynthesisGrewBudget { get; set; }

    /// <summary>Row 44: the last model post that landed in this exchange (author and id), read by
    /// <see cref="ExchangePolicy.Finished"/> to decide whether the addressee still owes a wrap-up.</summary>
    public (string AuthorId, long MessageId)? LastModelPost { get; set; }
```

`SpawnRequest` gains a trailing `SpawnReason Reason = SpawnReason.Mention`.

`ExchangePolicy.cs` changes, in order:

1. Class summary line 36: `D5: eight turns by default (a turns: token in the leading run overrides, row 44); whoever holds the last one is told so, and the addressee gets a synthesis turn when another participant posted last.`
2. `OnRoomMessage`, model branch: before `Accept(target, …)` add `target.LastModelPost = (message.AuthorId, message.Id);`.
3. `OnRoomMessage`, after the `Unknown/Tampered/Unavailable` switch and before the unknown-word loop:

```csharp
        // Row 44 (D-b): the turns token is read for every human prompt that may open an exchange; a
        // wrong number is noted once and the default applies, so the message still dispatches.
        if (leading.Turns == TurnsToken.OutOfRange)
            notes.Add($"turns: must be a whole number from 1 to {ExchangeCommands.MaxTurns}; the default {_limits.Budget} applies.");
        var budget = leading.Turns == TurnsToken.Valid ? leading.TurnsValue : _limits.Budget;
```

   and in the `new Exchange { … }` initializer use `Budget = budget` and `Addressee = startsRun ? null : mentioned[0]`. (`using ChopItUp.Core.Skills;` is already imported.)
4. `Supersede`: before `x.Pending.Clear()` call `DropQueuedSynthesis(x)`. `Stop`: the same before its `x.Pending.Clear()`.

```csharp
    /// <summary>Row 44: a synthesis still pending is being dropped with the rest of the queue; if it had
    /// grown the budget, that turn goes back, so the stop note and /continue count only real turns.</summary>
    private static void DropQueuedSynthesis(Exchange x)
    {
        if (!x.SynthesisGrewBudget || !x.Pending.Values.Any(p => p.Reason == SpawnReason.Synthesis)) return;
        x.Budget--;
        x.SynthesisGrewBudget = false;
    }
```

5. `Started`: after `x.TurnsStarted++` add `if (request.Reason == SpawnReason.Synthesis) x.SynthesisGrewBudget = false;` (the turn is spent now, never taken back).
6. `Join`: the rollback tuple stays; the new marks are reset only after an accepted reopening (pass 1 F5):

```csharp
        var (status, cause, committed) = (x.Status, x.StopCause, x.TurnsCommitted);
        x.Status = ExchangeStatus.Open;
        x.StopCause = null;
        x.TurnsCommitted = x.TurnsStarted;   // a stop or supersede dropped queued turns that never ran; only launched turns stay spent
        Accept(x, mentioned, messageId, now, notes);
        if (x.Pending.Count == 0) { (x.Status, x.StopCause, x.TurnsCommitted) = (status, cause, committed); return; }
        // Row 44: a new leg: the addressee may owe a fresh wrap-up, and what was refused before has been replayed by the owner's own words.
        x.SynthesisUsed = false;
        x.LastModelPost = null;
        x.Refused.Clear();
```

7. `Accept` gains a trailing `SpawnReason reason = SpawnReason.Mention`, records refusals, and the note gets its new tail:

```csharp
    private static void Accept(Exchange x, IReadOnlyList<string> mentioned, long messageId, DateTimeOffset now, List<string> notes, SpawnReason reason = SpawnReason.Mention)
    {
        var refused = new List<string>();
        foreach (var id in mentioned)
        {
            if (x.Pending.TryGetValue(id, out var pending))
            {
                pending.TriggerIds.Add(messageId);
                pending.LastTriggerAt = now;
                continue;
            }
            if (x.TurnsCommitted >= x.Budget) { refused.Add(id); x.Refused.TryAdd(id, messageId); continue; }
            var fresh = new PendingSpawn { LastTriggerAt = now, Reason = reason };
            fresh.TriggerIds.Add(messageId);
            x.Pending[id] = fresh;
            x.Participants.Add(id);
            x.TurnsCommitted++;
        }
        if (refused.Count > 0)
            notes.Add($"Budget of {x.Budget} turns is used up for the exchange started at #{x.RootMessageId}; not spawning {string.Join(", ", refused.Select(r => "@" + r))}. An owner message that mentions one of them starts a fresh exchange; once it has concluded, /continue extends it.");
    }
```

8. `Due`: build the request with `pending.Reason` as the last argument.
9. `Finished` becomes the four-argument method; the two-argument overload is deleted and every test call site gains `, T0` (and `posted: true` by default):

```csharp
    /// <summary>A spawn ended, however it ended. <c>Note</c> is the conclusion note, or the synthesis
    /// note when this exchange still owes the addressee a wrap-up (row 44, D-c), or null when nothing
    /// changed; <c>Concluded</c> says which. The synthesis turn is queued as an ordinary pending spawn
    /// (reason Synthesis, triggered by the last model post, debounced like any other), takes a free turn
    /// when one is left and adds one to the budget otherwise, and is marked at once so it fires at most
    /// once per leg. <paramref name="posted"/>: whether the spawn that just ended posted anything; an
    /// addressee that ended silent is not retried as a synthesis.</summary>
    public static (string? Note, bool Concluded) Finished(Exchange x, string participantId, DateTimeOffset now, bool posted = true)
    {
        x.InFlight.Remove(participantId);
        if (x.Status != ExchangeStatus.Open || x.Pending.Count > 0 || x.InFlight.Count > 0) return (null, false);
        if (x.Addressee is { } a && !x.SynthesisUsed && x.LastModelPost is { } last && last.AuthorId != a && !(participantId == a && !posted))
        {
            var synthesis = new PendingSpawn { LastTriggerAt = now, Reason = SpawnReason.Synthesis };
            synthesis.TriggerIds.Add(last.MessageId);
            x.Pending[a] = synthesis;
            x.Participants.Add(a);
            if (x.TurnsCommitted >= x.Budget) { x.Budget++; x.SynthesisGrewBudget = true; }
            x.TurnsCommitted++;
            x.SynthesisUsed = true;
            return ($"Exchange started at #{x.RootMessageId}: the hand-offs ended with @{last.AuthorId}'s post; queuing @{a}'s synthesis turn.", false);
        }
        x.Status = ExchangeStatus.Concluded;
        return (x.SynthesisUsed
            ? $"Exchange concluded: {x.TurnsStarted} of {x.Budget} turns used; the last was @{x.Addressee}'s synthesis."
            : $"Exchange concluded: {x.TurnsStarted} of {x.Budget} turns used.", true);
    }
```

10. New `Continue`:

```csharp
    /// <summary>What a /continue found (row 44, D-d). The service maps each refusal to one note.</summary>
    public enum ContinueOutcome { Continued, RunExchange, StillOpen, StillFinishing, NobodySpawnable }

    /// <summary>Row 44 (D-d): the owner's /continue on <paramref name="x"/>. Adds the message's turns
    /// token (else the default) to the budget, reopens, and queues: the message's own spawnable leading
    /// mentions if any, else the hand-offs the budget refused (each triggered by the message that made
    /// it and by this one), else the addressee. A message that named someone but nobody spawnable
    /// queues nothing. Pure: the service resolved which exchange this is and posts the notes.</summary>
    public (ContinueOutcome Outcome, IReadOnlyList<string> Notes) Continue(Exchange x, Message message, DateTimeOffset now)
    {
        var notes = new List<string>();
        if (!x.Joinable) { notes.Add($"Exchange started at #{x.RootMessageId} belongs to a run and cannot be continued."); return (ContinueOutcome.RunExchange, notes); }
        if (x.Status == ExchangeStatus.Open) { notes.Add($"Exchange started at #{x.RootMessageId} is still open with {Math.Max(0, x.Budget - x.TurnsCommitted)} turn(s) left; /continue once it has concluded."); return (ContinueOutcome.StillOpen, notes); }
        if (x.InFlight.Count > 0) { notes.Add($"Exchange started at #{x.RootMessageId} is still finishing {string.Join(", ", x.InFlight.Order(StringComparer.Ordinal).Select(id => "@" + id))}; /continue again once it has."); return (ContinueOutcome.StillFinishing, notes); }

        var leading = _mentions.Leading(message.Body);
        foreach (var word in leading.Unknown)
            notes.Add(word.Equals("hub", StringComparison.OrdinalIgnoreCase)
                ? "The hub cannot be addressed; it only posts notes."
                : $"No participant named @{word}. Address one of: {_addressable}.");
        var mentioned = leading.Recipients
            .Where(id => id != message.AuthorId && _roster.TryGetValue(id, out var p) && IsSpawnable(p)).ToList();
        if (mentioned.Count == 0 && (leading.Recipients.Count > 0 || leading.Unknown.Count > 0))
        {
            notes.Add("/continue named nobody the hub can spawn; nothing was queued.");
            return (ContinueOutcome.NobodySpawnable, notes);
        }
        if (leading.Turns == TurnsToken.OutOfRange)
            notes.Add($"turns: must be a whole number from 1 to {ExchangeCommands.MaxTurns}; the default {_limits.Budget} applies.");
        var extra = leading.Turns == TurnsToken.Valid ? leading.TurnsValue : _limits.Budget;

        var replayed = mentioned.Count == 0 ? x.Refused.ToList() : [];
        List<string> queue = mentioned.Count > 0 ? mentioned
            : replayed.Count > 0 ? replayed.Select(kv => kv.Key).ToList()
            : x.Addressee is { } a ? [a] : [];
        System.Diagnostics.Debug.Assert(queue.Count > 0, "a joinable exchange always has an addressee");

        x.Budget += extra;
        x.Status = ExchangeStatus.Open;
        x.StopCause = null;
        x.TurnsCommitted = x.TurnsStarted;
        x.SynthesisUsed = false;
        x.SynthesisGrewBudget = false;
        x.LastModelPost = null;
        x.Refused.Clear();
        x.MessageIds.Add(message.Id);
        Accept(x, queue, message.Id, now, notes, SpawnReason.Continuation);
        foreach (var (id, refusingId) in replayed)
            if (x.Pending.TryGetValue(id, out var pending)) pending.TriggerIds.Insert(0, refusingId);
        notes.Add($"Exchange started at #{x.RootMessageId} continued: {extra} more turn(s), {x.Budget} in all; queued {string.Join(", ", x.Pending.Keys.Select(id => "@" + id))}.");
        return (ContinueOutcome.Continued, notes);
    }
```

**Tests** (append to `ExchangePolicyTests.cs`; `Limits` there keeps `Budget: 4`; every existing `ExchangePolicy.Finished(a!, "opus")` call in the file gains `, T0` and, where the return was asserted, reads `.Note`):

```csharp
    // --- Row 44: budget, refused hand-offs, synthesis, continue ---------------------------------------

    [Fact]
    public void R44_an_owner_prompt_records_the_addressee_and_a_turns_token_sets_the_budget()
    {
        var p = Policy();
        var (x, notes) = p.OnMessage(null, Msg(1, "owner", "turns: 3 @sonnet @opus go"), T0);
        Assert.Equal((3, "sonnet", 2), (x!.Budget, x.Addressee, x.TurnsCommitted));
        Assert.Empty(notes);
        var (y, n2) = p.OnMessage(null, Msg(2, "owner", "@opus turns: 0 go"), T0);
        Assert.Equal(4, y!.Budget);
        Assert.Equal("turns: must be a whole number from 1 to 16; the default 4 applies.", Assert.Single(n2));
        var (z, _) = p.OnMessage(null, Msg(3, "owner", "@opus how many turns: 16 did we burn?"), T0);
        Assert.Equal((4, "opus"), (z!.Budget, z.Addressee));
    }

    [Fact]
    public void R44_a_refused_hand_off_is_recorded_once_and_the_note_names_continue()
    {
        var p = Policy();
        var (x, _) = p.OnMessage(null, Msg(1, "owner", "@opus @sonnet @fable @gpt-5.5 all of you"), T0);
        Assert.Equal((4, 4), (x!.Budget, x.TurnsCommitted));
        var (_, n2) = p.OnMessage(x, Msg(2, "opus", "@gpt-6-astra your take"), T0);
        Assert.Equal("Budget of 4 turns is used up for the exchange started at #1; not spawning @gpt-6-astra. An owner message that mentions one of them starts a fresh exchange; once it has concluded, /continue extends it.", Assert.Single(n2));
        p.OnMessage(x, Msg(3, "sonnet", "@gpt-6-astra @gpt-5.6-sol again"), T0);
        Assert.Equal(["gpt-6-astra", "gpt-5.6-sol"], x.Refused.Keys);
        Assert.Equal([2L, 3L], x.Refused.Values);
    }

    [Fact]
    public void R44_finished_queues_the_addressees_synthesis_when_someone_else_posted_last()
    {
        var p = Policy();
        var (x, _) = p.OnMessage(null, Msg(1, "owner", "@opus ask sonnet"), T0);
        ExchangePolicy.Started(x!, p.Due(x!, T0.AddSeconds(2), NoStarts, Nobody).Single());
        p.OnMessage(x, Msg(2, "opus", "@sonnet your view?"), T0.AddSeconds(3));
        Assert.Equal((null, false), ExchangePolicy.Finished(x!, "opus", T0.AddSeconds(3)));
        ExchangePolicy.Started(x!, p.Due(x!, T0.AddSeconds(6), NoStarts, Nobody).Single());
        p.OnMessage(x, Msg(3, "sonnet", "here is my view, no hand-off"), T0.AddSeconds(7));

        var (note, concluded) = ExchangePolicy.Finished(x!, "sonnet", T0.AddSeconds(8));

        Assert.False(concluded);
        Assert.Equal("Exchange started at #1: the hand-offs ended with @sonnet's post; queuing @opus's synthesis turn.", note);
        Assert.Equal(ExchangeStatus.Open, x!.Status);
        var synthesis = Assert.Single(x.Pending);
        Assert.Equal("opus", synthesis.Key);
        Assert.Equal(SpawnReason.Synthesis, synthesis.Value.Reason);
        Assert.Equal([3L], synthesis.Value.TriggerIds);
        Assert.Equal((4, 3, true, false), (x.Budget, x.TurnsCommitted, x.SynthesisUsed, x.SynthesisGrewBudget));   // a free turn was left
        Assert.Equal(T0.AddSeconds(8) + Limits.Debounce, p.NextWake(x, T0.AddSeconds(8), NoStarts, Nobody));   // claim 19: the timer launches it
        Assert.Empty(p.Due(x, T0.AddSeconds(9), NoStarts, Nobody));
        var due = p.Due(x, T0.AddSeconds(20), NoStarts, Nobody).Single();
        Assert.Equal((SpawnReason.Synthesis, 3, 1), (due.Reason, due.TurnNumber, due.RemainingAfter));
        ExchangePolicy.Started(x, due);
        p.OnMessage(x, Msg(4, "opus", "summary for the owner"), T0.AddSeconds(21));
        var (end, done) = ExchangePolicy.Finished(x, "opus", T0.AddSeconds(22));
        Assert.True(done);
        Assert.Equal("Exchange concluded: 3 of 4 turns used; the last was @opus's synthesis.", end);
    }

    [Fact]
    public void R44_a_synthesis_with_no_free_turn_adds_one_and_a_stop_takes_it_back()
    {
        var p = Policy();
        var (x, _) = p.OnMessage(null, Msg(1, "owner", "turns: 1 @opus @sonnet both"), T0);
        Assert.Equal((1, 1), (x!.Budget, x.TurnsCommitted));
        Assert.Equal(["opus"], x.Pending.Keys);   // sonnet refused by the plain budget
        ExchangePolicy.Started(x, p.Due(x, T0.AddSeconds(2), NoStarts, Nobody).Single());
        p.OnMessage(x, Msg(2, "opus", "@sonnet over to you"), T0);   // refused: budget 1 is spent
        Assert.Equal(("Exchange concluded: 1 of 1 turns used.", true), ExchangePolicy.Finished(x, "opus", T0));   // opus posted last: no synthesis

        var (y, _) = p.OnMessage(null, Msg(3, "owner", "turns: 2 @opus @sonnet both"), T0);
        ExchangePolicy.Started(y!, p.Due(y!, T0.AddSeconds(2), NoStarts, Nobody).First(d => d.ParticipantId == "opus"));
        p.OnMessage(y, Msg(4, "opus", "my part"), T0);
        ExchangePolicy.Finished(y!, "opus", T0);
        ExchangePolicy.Started(y!, p.Due(y!, T0.AddSeconds(2), NoStarts, Nobody).Single());
        p.OnMessage(y, Msg(5, "sonnet", "my part, last"), T0);
        var (note, concluded) = ExchangePolicy.Finished(y!, "sonnet", T0);
        Assert.False(concluded);
        Assert.Contains("queuing @opus's synthesis turn", note);
        Assert.Equal((3, 3, true), (y!.Budget, y.TurnsCommitted, y.SynthesisGrewBudget));   // no free turn: one added
        Assert.Equal("Exchange stopped by the owner: 2 of 2 turns used.", ExchangePolicy.Stop(y, ExchangeStopCause.Owner));   // the pending synthesis is dropped and its turn taken back
        Assert.Equal((2, false), (y.Budget, y.SynthesisGrewBudget));

        var (z, _) = p.OnMessage(null, Msg(6, "owner", "turns: 2 @opus @sonnet both"), T0);
        ExchangePolicy.Started(z!, p.Due(z!, T0.AddSeconds(2), NoStarts, Nobody).First(d => d.ParticipantId == "opus"));
        p.OnMessage(z, Msg(7, "opus", "my part"), T0);
        ExchangePolicy.Finished(z!, "opus", T0);
        ExchangePolicy.Started(z!, p.Due(z!, T0.AddSeconds(2), NoStarts, Nobody).Single());
        p.OnMessage(z, Msg(8, "sonnet", "my part, last"), T0);
        ExchangePolicy.Finished(z!, "sonnet", T0);
        ExchangePolicy.Started(z!, p.Due(z!, T0.AddSeconds(4), NoStarts, Nobody).Single());   // the synthesis launched: its turn is spent for good
        Assert.False(z!.SynthesisGrewBudget);
        p.OnMessage(z, Msg(9, "opus", "wrap-up"), T0);
        Assert.Equal(("Exchange concluded: 3 of 3 turns used; the last was @opus's synthesis.", true), ExchangePolicy.Finished(z, "opus", T0));
    }

    [Fact]
    public void R44_no_synthesis_after_a_stop_when_the_addressee_posted_last_or_when_the_addressee_ended_silent()
    {
        var p = Policy();
        var (c, _) = p.OnMessage(null, Msg(1, "owner", "@opus ask sonnet"), T0);
        ExchangePolicy.Started(c!, p.Due(c!, T0.AddSeconds(2), NoStarts, Nobody).Single());
        p.OnMessage(c, Msg(2, "opus", "@sonnet go"), T0);
        ExchangePolicy.Finished(c!, "opus", T0);
        ExchangePolicy.Started(c!, p.Due(c!, T0.AddSeconds(6), NoStarts, Nobody).Single());
        p.OnMessage(c, Msg(3, "sonnet", "my view"), T0);
        ExchangePolicy.Stop(c!, ExchangeStopCause.Owner);
        Assert.Equal((null, false), ExchangePolicy.Finished(c!, "sonnet", T0));
        Assert.Equal((ExchangeStatus.Stopped, false), (c!.Status, c.SynthesisUsed));

        var (d, _) = p.OnMessage(null, Msg(4, "owner", "@opus hi"), T0);
        ExchangePolicy.Started(d!, p.Due(d!, T0.AddSeconds(2), NoStarts, Nobody).Single());
        p.OnMessage(d, Msg(5, "opus", "done, no hand-off"), T0);
        Assert.Equal(("Exchange concluded: 1 of 4 turns used.", true), ExchangePolicy.Finished(d!, "opus", T0));

        var (e, _) = p.OnMessage(null, Msg(6, "owner", "@opus @sonnet both"), T0);
        ExchangePolicy.Started(e!, p.Due(e!, T0.AddSeconds(2), NoStarts, Nobody).First(d => d.ParticipantId == "sonnet"));
        p.OnMessage(e, Msg(7, "sonnet", "my part"), T0);
        ExchangePolicy.Finished(e!, "sonnet", T0);
        ExchangePolicy.Started(e!, p.Due(e!, T0.AddSeconds(4), NoStarts, Nobody).Single());   // opus launches and ends without posting
        Assert.Equal(("Exchange concluded: 2 of 4 turns used.", true), ExchangePolicy.Finished(e!, "opus", T0, posted: false));   // no retry on the hub's dime
    }

    [Fact]
    public void R44_continue_reopens_with_more_turns_and_requeues_the_refused_hand_offs_with_their_triggers()
    {
        var p = Policy();
        var (x, _) = p.OnMessage(null, Msg(1, "owner", "@opus ask around"), T0);
        ExchangePolicy.Started(x!, p.Due(x!, T0.AddSeconds(2), NoStarts, Nobody).Single());
        p.OnMessage(x, Msg(2, "opus", "@sonnet your view?"), T0);
        ExchangePolicy.Finished(x!, "opus", T0);
        ExchangePolicy.Started(x!, p.Due(x!, T0.AddSeconds(6), NoStarts, Nobody).Single());
        p.OnMessage(x, Msg(3, "sonnet", "@opus back"), T0);
        ExchangePolicy.Finished(x!, "sonnet", T0);
        ExchangePolicy.Started(x!, p.Due(x!, T0.AddSeconds(10), NoStarts, Nobody).Single());
        p.OnMessage(x, Msg(4, "opus", "@sonnet once more"), T0);
        ExchangePolicy.Finished(x!, "opus", T0);
        ExchangePolicy.Started(x!, p.Due(x!, T0.AddSeconds(14), NoStarts, Nobody).Single());
        var (_, n5) = p.OnMessage(x, Msg(5, "sonnet", "@fable your take"), T0);   // refused: 4 of 4 committed
        Assert.Contains("not spawning @fable", Assert.Single(n5));
        var (synthesis, _) = ExchangePolicy.Finished(x!, "sonnet", T0.AddSeconds(15));   // sonnet posted last
        Assert.Contains("queuing @opus's synthesis turn", synthesis);
        Assert.Equal(5, x!.Budget);
        ExchangePolicy.Started(x, p.Due(x, T0.AddSeconds(18), NoStarts, Nobody).Single());
        p.OnMessage(x, Msg(6, "opus", "summary"), T0);
        Assert.Equal(("Exchange concluded: 5 of 5 turns used; the last was @opus's synthesis.", true), ExchangePolicy.Finished(x, "opus", T0));

        var (outcome, notes) = p.Continue(x, Reply(7, "owner", "/continue", 1), T0.AddSeconds(20));

        Assert.Equal(ContinueOutcome.Continued, outcome);
        Assert.Equal("Exchange started at #1 continued: 4 more turn(s), 9 in all; queued @fable.", Assert.Single(notes));
        Assert.Equal((ExchangeStatus.Open, 9, 6, 5, false), (x.Status, x.Budget, x.TurnsCommitted, x.TurnsStarted, x.SynthesisUsed));
        Assert.Empty(x.Refused);
        Assert.Contains(7L, x.MessageIds);
        var due = p.Due(x, T0.AddSeconds(30), NoStarts, Nobody).Single();
        Assert.Equal(("fable", SpawnReason.Continuation, 6, 3), (due.ParticipantId, due.Reason, due.TurnNumber, due.RemainingAfter));
        Assert.Equal([5L, 7L], due.TriggerIds);   // the refusing message first, then the /continue
    }

    [Fact]
    public void R44_continue_queues_its_own_mentions_first_then_the_addressee_and_honours_a_turns_token()
    {
        var p = Policy();
        var (x, _) = p.OnMessage(null, Msg(1, "owner", "@opus hi"), T0);
        ExchangePolicy.Started(x!, p.Due(x!, T0.AddSeconds(2), NoStarts, Nobody).Single());
        p.OnMessage(x, Msg(2, "opus", "done"), T0);
        ExchangePolicy.Finished(x!, "opus", T0);

        var (o1, n1) = p.Continue(x!, Msg(3, "owner", "/continue turns: 2 @sonnet what do you add?"), T0);
        Assert.Equal(ContinueOutcome.Continued, o1);
        Assert.Equal("Exchange started at #1 continued: 2 more turn(s), 6 in all; queued @sonnet.", Assert.Single(n1));
        Assert.Equal(["sonnet"], x!.Pending.Keys);
        var due = p.Due(x, T0.AddSeconds(2), NoStarts, Nobody).Single();
        Assert.Equal([3L], due.TriggerIds);
        ExchangePolicy.Started(x, due);
        p.OnMessage(x, Msg(4, "sonnet", "I add this"), T0);
        var (note, _) = ExchangePolicy.Finished(x, "sonnet", T0);   // sonnet posted last: synthesis for opus
        Assert.Contains("queuing @opus's synthesis turn", note);
        ExchangePolicy.Started(x, p.Due(x, T0.AddSeconds(6), NoStarts, Nobody).Single());
        p.OnMessage(x, Msg(5, "opus", "wrap-up"), T0);
        Assert.True(ExchangePolicy.Finished(x, "opus", T0).Concluded);

        var (o2, n2) = p.Continue(x, Msg(6, "owner", "/continue"), T0);   // nothing refused, no mention: the addressee
        Assert.Equal(ContinueOutcome.Continued, o2);
        Assert.Equal("Exchange started at #1 continued: 4 more turn(s), 10 in all; queued @opus.", Assert.Single(n2));
        Assert.Equal(["opus"], x.Pending.Keys);

        var (o3, n3) = p.Continue(x, Msg(7, "owner", "/continue turns: 99"), T0);
        Assert.Equal(ContinueOutcome.StillOpen, o3);
        Assert.Equal("Exchange started at #1 is still open with 6 turn(s) left; /continue once it has concluded.", Assert.Single(n3));
    }

    [Fact]
    public void R44_continue_refuses_a_run_exchange_one_still_finishing_and_a_mention_of_nobody_spawnable()
    {
        var p = Policy();
        var (run, _) = p.OnRoomMessage([], null, Msg(1, "owner", "/build-thing @opus go"), T0, skill: new SkillResolution.Found(RunSkill, "go"), startsRun: true, hasDirectory: true);
        var (o1, n1) = p.Continue(run!, Msg(2, "owner", "/continue"), T0);
        Assert.Equal(ContinueOutcome.RunExchange, o1);
        Assert.Equal("Exchange started at #1 belongs to a run and cannot be continued.", Assert.Single(n1));

        var (x, _) = p.OnMessage(null, Msg(3, "owner", "@opus hi"), T0);
        ExchangePolicy.Started(x!, p.Due(x!, T0.AddSeconds(2), NoStarts, Nobody).Single());
        ExchangePolicy.Stop(x!, ExchangeStopCause.Owner);
        var (o2, n2) = p.Continue(x!, Msg(4, "owner", "/continue"), T0);
        Assert.Equal(ContinueOutcome.StillFinishing, o2);
        Assert.Equal("Exchange started at #3 is still finishing @opus; /continue again once it has.", Assert.Single(n2));
        ExchangePolicy.Finished(x!, "opus", T0);

        var (o3, n3) = p.Continue(x!, Msg(5, "owner", "/continue @sonet @claude more"), T0);
        Assert.Equal(ContinueOutcome.NobodySpawnable, o3);
        Assert.Equal(2, n3.Count);
        Assert.StartsWith("No participant named @sonet. Address one of: ", n3[0]);
        Assert.Equal("/continue named nobody the hub can spawn; nothing was queued.", n3[1]);
        Assert.Equal(ExchangeStatus.Stopped, x!.Status);

        var (o4, _) = p.Continue(x, Msg(6, "owner", "/continue"), T0);
        Assert.Equal(ContinueOutcome.Continued, o4);
        Assert.Null(x.StopCause);
    }

    [Fact]
    public void R44_a_reply_whose_mentions_are_all_refused_keeps_the_refused_list_for_continue()
    {
        var p = Policy();
        var (x, _) = p.OnMessage(null, Msg(1, "owner", "turns: 1 @opus go"), T0);
        ExchangePolicy.Started(x!, p.Due(x!, T0.AddSeconds(2), NoStarts, Nobody).Single());
        p.OnMessage(x, Msg(2, "opus", "@fable your take"), T0);   // refused: budget 1 spent
        ExchangePolicy.Finished(x!, "opus", T0);
        Assert.Equal(ExchangeStatus.Concluded, x!.Status);
        Assert.Equal(["fable"], x.Refused.Keys);

        var (opened, notes) = p.OnRoomMessage([x], null, Reply(3, "owner", "@sonnet keep going", 1), T0, joins: x);   // 1 of 1 started: refused, rolled back

        Assert.Null(opened);
        Assert.Contains("not spawning @sonnet", Assert.Single(notes));
        Assert.Equal(ExchangeStatus.Concluded, x.Status);
        Assert.Equal(["fable", "sonnet"], x.Refused.Keys);
        var (outcome, n2) = p.Continue(x, Reply(4, "owner", "/continue", 1), T0);
        Assert.Equal(ContinueOutcome.Continued, outcome);
        Assert.Equal("Exchange started at #1 continued: 4 more turn(s), 5 in all; queued @fable, @sonnet.", Assert.Single(n2));
        Assert.Equal([2L, 4L], x.Pending["fable"].TriggerIds);
        Assert.Equal([3L, 4L], x.Pending["sonnet"].TriggerIds);
    }
```

The existing test `A_model_mention_inside_an_open_exchange_is_a_turn_until_the_budget_is_used_up` keeps its numbers; only the refusal sentence's tail changes where a test asserts the whole sentence. The builder greps `starts a fresh exchange` in tests and reports each edit.

Commands: `dotnet test tests/ChopItUp.Hub.Tests --filter "FullyQualifiedName~ExchangePolicyTests|FullyQualifiedName~SpawnLimitsTests" -c Debug --nologo -v minimal`.

### Task 3 — Service and prompt: `/continue` routing, the synthesis guard, `continuable`, the sentences, real-path tests (sonnet)

Files: `src/ChopItUp.Hub/Spawning/SpawnerService.cs`, `src/ChopItUp.Hub/Spawning/SpawnPrompt.cs`, `tests/ChopItUp.Hub.Tests/Spawning/SpawnerServiceTests.cs`, `tests/ChopItUp.Hub.Tests/Spawning/SpawnerServiceTests.Continue.cs` (new), `tests/ChopItUp.Hub.Tests/Spawning/SpawnPromptTests.cs`.

**RED:** the new service tests below fail (a `/continue` post draws `No skill named '/continue'`; no synthesis spawn arrives); the prompt tests fail on the missing sentences.

`SpawnerService.cs`:

1. `ExchangeView` gains `bool Continuable = false` as its last positional parameter; `ExchangeSnapshot` gains `bool Continuable = false` after `Exchanges`. `View` becomes `View(Exchange x, bool runActive)` (static stays) and passes `Continuable: x.Joinable && x.Status != ExchangeStatus.Open && x.InFlight.Count == 0 && !runActive`; `Publish` computes `var runActive = _runs.Active(roomId) is not null;` once, maps `View(x, runActive)`, and sets the top level's `Continuable` from the displayed exchange the same way.
2. The `acceptMentions` site (line 394): a synthesis spawn's post lands and is a member, but hands nothing on (D-c):

```csharp
            acceptMentions = handle.Request.Reason != SpawnReason.Synthesis && (activeRun is null
                ? handle.Exchange.Status == ExchangeStatus.Open && exchanges.Contains(handle.Exchange)
                : ReferenceEquals(handle.Exchange, newest));
```

3. `OnMessage`: directly after the parked-run resume block and before `var hasDirectory = …`, insert:

```csharp
        // Row 44 (D-d): the owner's /continue never reaches the policy's message path (it would resolve
        // as an unknown skill). Decided here, after the run branches above: a parked run resumed on it
        // like on any human post, an active run refuses it, and outside runs it targets the exchange the
        // message replies to, else the last owner-rooted one in room order (the room list is what Reopen
        // moves a reopened exchange to the end of; the remembered list only knows the opening order).
        if (ExchangeCommands.IsContinue(m.Body) && _roster.FirstOrDefault(p => p.Id == m.AuthorId)?.Kind == "human")
        {
            if (activeRun is not null)
            {
                PostNote(m.RoomId, $"A run is active in this room (#{activeRun.Id}); /continue applies to plain exchanges.");
                return;
            }
            Exchange? continued;
            if (m.ReplyToId is { } continueOf)
            {
                continued = JoinableFor(m.RoomId, continueOf);
                if (continued is null)
                {
                    PostNote(m.RoomId, $"Reply to #{continueOf}: that message is in no exchange this hub still holds, so there is nothing to continue.");
                    return;
                }
            }
            else continued = ExchangesIn(m.RoomId).LastOrDefault(x => x.Joinable)
                ?? (_joinable.TryGetValue(m.RoomId, out var remembered) ? remembered.LastOrDefault() : null);
            if (continued is null)
            {
                PostNote(m.RoomId, "Nothing to continue in this room: no exchange this hub remembers.");
                return;
            }
            var (outcome, continueNotes) = _policy.Continue(continued, m, now);
            if (outcome == ExchangePolicy.ContinueOutcome.Continued) Reopen(m.RoomId, continued);
            foreach (var note in continueNotes) PostNote(m.RoomId, note);
            Publish(m.RoomId);
            return;
        }
```

   (`using ChopItUp.Core.Skills;` if absent.) `Reopen` moves the exchange to the end of `_rooms[room]`, adding it when pruned (claim 10), and on a non-worktree exchange is a field reset only.
4. The two `ExchangePolicy.Finished(…)` call sites (lines 1102 and 1152) use the four-argument form: the clock the surrounding code uses (`DateTimeOffset.UtcNow` where `LaunchDue` reads the wall clock; the builder reads both sites and reports which clock each uses) and `posted: h.Posted` at 1152 (at 1102 the spawn never launched, so `posted: false`). At 1152 the result is destructured: the note is posted when non-null as today, and the run branch (`if (_runs.Active(room) is { } activeRun && …)`) runs only when `Concluded` is true. Same at 1102.
5. The prompt input at the build site (line ~975) passes `Reason: request.Reason, Addressee: x.Addressee`.

`SpawnPrompt.cs`: `SpawnPromptInput` gains `SpawnReason Reason = SpawnReason.Mention, string? Addressee = null` after `Standing`. The why-line becomes a three-way choice that stays on the same line as the started-at sentence and the turn line (claim 12: R14 is byte-for-byte), and the last-turn block gains a non-addressee branch:

```csharp
        switch (input.Reason)
        {
            case SpawnReason.Synthesis:
                sb.Append("Why you are here: the hand-offs of this exchange ended with ").Append(LastPoster(input)).Append("'s message #").Append(input.TriggerIds[^1])
                  .Append("; this is your synthesis turn as the participant the owner addressed. Answer the owner on the original ask (message #").Append(input.RootMessageId)
                  .Append(") in a few lines; a mention in this reply hands nothing on. ");
                break;
            case SpawnReason.Continuation when input.TriggerIds.Count > 1:
                sb.Append("Why you are here: message #").Append(input.TriggerIds[0]).Append(" mentioned you when the budget was spent; the owner continued this exchange with message #")
                  .Append(input.TriggerIds[^1]).Append(", so answer that mention now. ");
                break;
            case SpawnReason.Continuation:
                sb.Append("Why you are here: the owner continued this exchange with message #").Append(input.TriggerIds[^1]).Append(" after it ended; pick up where it left off. ");
                break;
            default:
                sb.Append("Why you are here: message(s) ").Append(string.Join(", ", input.TriggerIds.Select(id => "#" + id))).Append(" mentioned you. ");
                break;
        }
        sb.Append("This exchange started at message #").Append(input.RootMessageId).Append(". Turn ").Append(input.TurnNumber).Append(" of ").Append(input.Budget).Append("; ").Append(input.RemainingAfter).Append(" turn(s) remain after yours.\n");
        if (input.RemainingAfter == 0 && input.Reason != SpawnReason.Synthesis)
        {
            if (input.Run is { SelfIsConductor: true })
                … the existing conductor sentence, unchanged …
            else if (input.Addressee is { } addressee && addressee != input.Self.Id)
                sb.Append("This is the last hand-off turn of the exchange: give your findings in a few lines; @").Append(addressee)
                  .Append(" wraps up for the owner afterwards, so do not ask the owner whether to continue.\n");
            else
                … the existing last-turn sentence, unchanged …
        }
```

   `LastPoster(input)` returns `"@" + author id` of the transcript message whose id is `TriggerIds[^1]`, or `a participant` when the transcript window no longer holds it.

**Service tests** (`SpawnerServiceTests.cs`, `Fast` keeps `Budget: 4`, `Timeout: 1 s`):

- Rewrite `A2_the_budget_stops_the_chain_and_the_last_turn_is_told_so` (handler unchanged: every spawn posts `@{other} your move`). After `@opus play ping-pong with sonnet`: four prompts (`Turn 4 of 4; 0 turn(s) remain after yours.` on the fourth, which is sonnet's, so it carries `This is the last hand-off turn of the exchange` and `@opus wraps up for the owner afterwards` and NOT `ask the owner whether to continue`; the third has neither); the refusal note `Budget of 4 turns is used up for the exchange started at #1; not spawning @opus. An owner message that mentions one of them starts a fresh exchange; once it has concluded, /continue extends it.`; then `Exchange started at #1: the hand-offs ended with @sonnet's post; queuing @opus's synthesis turn.`; a fifth prompt containing `Turn 5 of 5; 0 turn(s) remain after yours.` and `this is your synthesis turn as the participant the owner addressed` and NOT `This is the last`; opus's `@sonnet your move` from that spawn spawns nothing; the conclusion `Exchange concluded: 5 of 5 turns used; the last was @opus's synthesis.`; `_runner.Count == 5`; `NoSpecWithin(500 ms)`. Wait for the three notes in that order with `WaitForMessage`.
- New `R44_the_addressee_gets_a_synthesis_turn_and_its_hand_off_is_ignored`: handler: opus posts `@sonnet your view?` on its first spawn and `@sonnet thanks; summary for the owner` on any later one; sonnet posts `My view, no hand-off`. Expect exactly three specs; the third prompt contains `ended with @sonnet's message #` and `this is your synthesis turn`; notes in order: the queuing note, then `Exchange concluded: 3 of 4 turns used; the last was @opus's synthesis.`; `_runner.Count == 3`; `NoSpecWithin(500 ms)`.
- New `R44_continue_requeues_the_refused_hand_off_and_prompts_it_as_a_continuation`: run the A2 sequence to its conclusion, then `PostAsOwner("/continue")` (its id is 10: root 1, four model posts, refusal note, queuing note, synthesis post, conclusion note, then this). Expect the note `Exchange started at #1 continued: 4 more turn(s), 9 in all; queued @opus.` (sonnet's refused `@opus` in message 5 is what was recorded), a sixth spec whose prompt contains `Turn 6 of 9; 3 turn(s) remain after yours.` and `Why you are here: message #5 mentioned you when the budget was spent; the owner continued this exchange with message #10, so answer that mention now.`, `Snapshot("general").Status == "open"` and `.Budget == 9` meanwhile; then the handler's ping-pong runs on: opus `@sonnet` (7), sonnet `@opus` (8), opus `@sonnet` (9), sonnet `@opus` refused, sonnet posted last so a synthesis (budget 10) as spec 10 whose `@sonnet` is ignored, conclusion `Exchange concluded: 10 of 10 turns used; the last was @opus's synthesis.`; `_runner.Count == 10`. The builder walks the sequence in the test's comment and corrects the `#10` only if the measured id differs, saying so in the commit.
- New `R44_the_snapshot_says_when_an_exchange_is_continuable_and_a_turns_token_sets_the_budget`: `turns: 2 @opus hi` with a handler that posts nothing → first prompt has `Turn 1 of 2`; after it concludes, `Spawner.Snapshot("general").Continuable` and `.Exchanges.Single().Continuable` are true; then `turns: 99 @opus hi` → note `turns: must be a whole number from 1 to 16; the default 4 applies.` and the prompt `Turn 1 of 4`.

New file `SpawnerServiceTests.Continue.cs`: a separate class `ContinueWhileOpenTests` with the same boilerplate as `SpawnerServiceTests` but limits `Held = Fast with { Timeout = TimeSpan.FromSeconds(30) }` (pass 2 m5: a held spawn must outlive the assertions), holding one test: `R44_continue_with_nothing_or_on_an_open_exchange_posts_the_refusal_note`: fresh room: `PostAsOwner("/continue")` → `Nothing to continue in this room: no exchange this hub remembers.` (that note is message #2); then `@opus hi` (#3) with a handler that awaits a `TaskCompletionSource`; `/continue` → `Exchange started at #3 is still open with 3 turn(s) left; /continue once it has concluded.` and `Snapshot("general").Continuable == false`; release the handler; the exchange concludes; no second spec. The active-run case is covered by the policy test (`RunExchange`) plus the `activeRun` note branch, which the builder covers in `SpawnerServiceTests.Runs.cs` only if `RunHostFixture` makes a `/continue` post during an active run a five-line test; otherwise report.

Existing literals: the builder runs the whole class, lists every failing existing test, and adjusts only assertions the new rules change (each listed in the commit message with the reason). Expected: A2 only, plus any test asserting the whole refusal sentence.

**Mechanism reverts (M24)** — after GREEN, the builder reverts each mechanism once, runs the named test, records the RED in the commit message, restores:

| Revert | Test that goes RED |
|---|---|
| The synthesis block in `Finished` (return the conclusion directly) | `R44_the_addressee_gets_a_synthesis_turn_and_its_hand_off_is_ignored` (two specs, no queuing note) and A2 (four specs) |
| `handle.Request.Reason != SpawnReason.Synthesis &&` at the `acceptMentions` site | `R44_the_addressee_gets_a_synthesis_turn_and_its_hand_off_is_ignored` (a fourth spec) |
| The `/continue` branch in `OnMessage` | `R44_continue_requeues_the_refused_hand_off…` and `R44_continue_with_nothing_or_on_an_open_exchange…` (`No skill named '/continue'`) |
| `x.Refused.TryAdd(id, messageId)` in `Accept` | `R44_continue_reopens_with_more_turns_and_requeues_the_refused_hand_offs_with_their_triggers` (queues @opus, not @fable) |

**Prompt tests** (`SpawnPromptTests.cs`): add an overload `Input(int turn, int remainingAfter, SpawnReason reason, string? addressee, params Message[] transcript)` beside the existing one (pass 2 m3: an optional parameter cannot follow `params`); add

```csharp
    [Fact]
    public void R44_synthesis_and_continuation_spawns_get_their_own_why_line_on_the_same_line_as_the_turn_count()
    {
        var synthesis = SpawnPrompt.Render(Input(3, 0, SpawnReason.Synthesis, "opus", Msg(1, "owner", "@opus hi"), Msg(2, "sonnet", "my view")), SpawnLimits.Default);
        Assert.Contains("Why you are here: the hand-offs of this exchange ended with @sonnet's message #2; this is your synthesis turn as the participant the owner addressed. Answer the owner on the original ask (message #1) in a few lines; a mention in this reply hands nothing on. This exchange started at message #1. Turn 3 of 4; 0 turn(s) remain after yours.\n", synthesis);
        Assert.DoesNotContain("mentioned you", synthesis);
        Assert.DoesNotContain("This is the last", synthesis);
        var continued = SpawnPrompt.Render(Input(4, 4, SpawnReason.Continuation, "opus", Msg(1, "owner", "@opus hi"), Msg(5, "owner", "/continue")), SpawnLimits.Default);
        Assert.Contains("Why you are here: the owner continued this exchange with message #5 after it ended; pick up where it left off. This exchange started at message #1. Turn 4 of 4; 4 turn(s) remain after yours.\n", continued);
        var replayed = SpawnPrompt.Render(Input(4, 4, SpawnReason.Continuation, "opus", Msg(1, "owner", "@opus hi"), Msg(3, "sonnet", "@opus back"), Msg(5, "owner", "/continue")) with { TriggerIds = [3, 5] }, SpawnLimits.Default);
        Assert.Contains("Why you are here: message #3 mentioned you when the budget was spent; the owner continued this exchange with message #5, so answer that mention now. This exchange started at message #1.", replayed);
        var plain = SpawnPrompt.Render(Input(1, 3, Msg(1, "owner", "@opus hi"), Msg(2, "codex", "x")), SpawnLimits.Default);
        Assert.Contains("Why you are here: message(s) #2 mentioned you. This exchange started at message #1. Turn 1 of 4; 3 turn(s) remain after yours.\n", plain);
    }

    [Fact]
    public void R44_the_last_hand_off_turn_defers_to_the_addressee_instead_of_asking_the_owner()
    {
        var other = SpawnPrompt.Render(Input(4, 0, SpawnReason.Mention, "sonnet", Msg(1, "owner", "@sonnet hi"), Msg(2, "sonnet", "@opus your view")), SpawnLimits.Default);   // Self is opus (the helper's fixed Self)
        Assert.Contains("This is the last hand-off turn of the exchange: give your findings in a few lines; @sonnet wraps up for the owner afterwards, so do not ask the owner whether to continue.", other);
        Assert.DoesNotContain("ask the owner whether to continue.\n", other);
        var self = SpawnPrompt.Render(Input(4, 0, SpawnReason.Mention, "opus", Msg(1, "owner", "@opus hi")), SpawnLimits.Default);
        Assert.Contains("This is the last turn of the exchange", self);
        Assert.Contains("ask the owner whether to continue", self);
        var none = SpawnPrompt.Render(Input(4, 0, Msg(1, "owner", "@opus hi")), SpawnLimits.Default);
        Assert.Contains("ask the owner whether to continue", none);
    }
```

  R14 (golden) must stay green untouched: if it goes red, the why-line was split from the turn line; fix the append chain, never the fixture.

Commands: `dotnet test tests/ChopItUp.Hub.Tests --filter "FullyQualifiedName~SpawnerServiceTests|FullyQualifiedName~ContinueWhileOpenTests|FullyQualifiedName~SpawnPromptTests|FullyQualifiedName~ExchangeApiTests" -c Debug --nologo -v minimal`, then the full Hub project (green again here).

### Task 4 — Client: Continue button, the strip's turns chip (opus)

Blocked by Task 3 (wire field). Files: `src/ChopItUp.Hub/client/src/types.ts`, `ExchangeBar.tsx`, `ExchangeBar.test.tsx`, `App.tsx`, `App.test.tsx` (if App's stop hooks are tested there), `RecipientStrip.tsx`, `RecipientStrip.test.tsx`, `styles.css` (only if the button needs spacing the `.quiet` class lacks).

**RED:** the new bar and strip tests fail (no `Continue exchange` button; no turns chip).

- `types.ts`: `ExchangeSnapshot` gains `continuable?: boolean` (optional: older hubs omit it), `ExchangeView` gains `continuable?: boolean` with a doc comment naming the hub's rule (owner-rooted, closed, nothing of its own in flight, no active run).
- `ExchangeBar.tsx`: props gain `continuingRoots: ReadonlySet<number>` and `onContinue: (root: number) => void`; `StripFields` picks `continuable` too; `StripControl` gains `continueDisabled` and `onContinue`; in the tail, after the working chips and before the stop button, render `{continuable === true && !runStoppable && (<button type="button" className="quiet" disabled={continueDisabled} onClick={onContinue} aria-label={label === null ? undefined : `Continue exchange ${label}`}>Continue exchange</button>)}`. The top-level (no `exchanges`) strip passes `continuable` from the snapshot and its root from `rootMessageId` (skip the button when `rootMessageId` is null). Comment: the hub decides `continuable`; the client only honours the run gate the stop already honours.
- `App.tsx`: state `continuingRoots` (same `withStopping` helper shape as `stoppingRoots`, renamed generically or duplicated: the builder picks the smaller diff); `continueFromBar = useCallback(async (root) => { begin; try { merge([await api.postMessage(roomId, '/continue', root)]); setError(null); } catch (failure) { if (!refused(failure, 'The exchange was not continued.')) setError(api.describeError(failure)); } finally { end; } })`; pass `continuingRoots` and `onContinue={continueFromBar}` to `<ExchangeBar>`. The composer's reply state is untouched (no `dispatchReply`).
- `RecipientStrip.tsx`: read `turns` from the same `recipientsOf(draft.trim())` call; when non-null render one more chip in the list: `{turns} turns` (class `recipient-chip turns`) when valid, else `turns: {turns} is out of range (1 to 16); the default 8 applies` (class `recipient-chip unknown`); the strip renders when a turns chip exists even with no recipients, and the preview line adds `Sets the exchange to N turns.` only when recipients exist. (The client's `16` and `8` mirror the hub's hard-coded caps, D-a.)
- Tests: `ExchangeBar.test.tsx`: `a continuable strip offers Continue exchange and wires its root` (a closed exchange with nothing in flight has no Stop, so exactly one button, text `Continue exchange`, `onClick` calls `onContinue(41)`), `a live run hides Continue`, `an open exchange has no Continue`, `a continue in flight disables only that strip`, `a hub that sends no continuable renders no Continue`, `several strips give each Continue its own accessible name`. `RecipientStrip.test.tsx`: `a turns token adds a turns chip`, `an out-of-range turns token warns`, `turns with no recipient still shows the chip and no sends-to line`, `a turns token after prose adds nothing`. `App.test.tsx`: if `stopExchangeAt` has a test there, mirror one for the continue post (`api.postMessage` called with `'/continue'` and the root); otherwise report.

Commands (in `src/ChopItUp.Hub/client`): `npx vitest run`, `npm run typecheck`, `npm run build` (the hub embeds `wwwroot`; the builder confirms the build output lands where `ChopItUp.Hub.csproj` expects, as the row 43 build did).

### Task 5 — Texts and the dry run (sonnet)

Blocked by Task 3; may run beside Task 4 in a worktree. Files: `README.md`, `tools/skills/roadmap-hub/OVERLAY.md`, `src/ChopItUp.Hub/Mcp/Participation.cs`, `docs/verification.md`, `tools/Invoke-Row44ContinueCheck.ps1` (new).

- `README.md:86`: `Caps, all hard-coded: 8 turns per exchange by default (a `turns: N` token among the leading mentions sets 1 to 16), a 2 second debounce …`. After the reply-join paragraph add one paragraph: the participant the owner addressed first gets a synthesis turn when another model posted last (a free turn if one is left, else one more); `/continue` (typed, or the strip's Continue button, which posts it as a reply to the exchange's root) reopens a concluded or stopped exchange with 8 more turns (`/continue turns: N` for another number), re-running the hand-offs the budget refused, or the mentions the `/continue` message carries, or the addressee; a hub restart forgets exchanges, so `/continue` after one says so. Claim 17's sweep must find 0 hits after this task.
- `OVERLAY.md:12`: `at most 8 turns`; `OVERLAY.md:37`: `(≤ 8 turns; each participant posts one position)`.
- `Participation.cs` Rules, under Taking part after the mention bullet: `- The owner can type /continue (a reply to a message of the exchange, or bare for the room's latest one) to reopen a concluded or stopped exchange with more turns; from anyone else it is prose.`
- `docs/verification.md`: next to the row 42 script line (line 21) add `Continue, the turns token and the synthesis turn (row 44, stub Codex, no model calls): pwsh tools\Invoke-Row44ContinueCheck.ps1`.
- `tools/Invoke-Row44ContinueCheck.ps1`: mirror `Invoke-Row43MentionCheck.ps1`'s frame (params with presence guards, scratch root under `$env:TEMP\chopitup_row44dryrun_<guid>`, stub `codex.cmd` first on PATH, `ChopTokenHelpers.ps1` seeding `owner`, `Add-Check`, `Results: n/m PASS`, exit 0 only at 7/7, every array through `ForEach-Object { $_ }`, finally block stopping the hub by PID). Legs, each a barrier on a hub note or the snapshot's `exchanges[]` entry for the message's own root, never a timer:
  1. `health.ok`.
  2. `turns.override`: owner posts `turns: 2 @gpt-5.6-terra hold this open`; the `exchanges[]` entry rooted at that message shows `budget 2` and `gpt-5.6-terra` in flight (poll the snapshot until in flight; the stub holds it).
  3. `turns.range-note`: owner posts `turns: 99 @gpt-5.6-sol hold this open`; the note `turns: must be a whole number from 1 to 16; the default 8 applies.` arrives and that root's entry shows `budget 8`.
  4. `continue.open-refused`: owner posts `/continue` as a reply to leg 2's root; the note `Exchange started at #<root2> is still open with 1 turn(s) left; /continue once it has concluded.` arrives and no new spawn appears.
  5. `continue.after-stop`: `POST /api/rooms/general/exchanges/<root2>/stop`, wait for `stopped` on that entry with `inFlight` empty (the stop kills the stub's process tree; poll the entry), assert the entry's `continuable` is true, then `/continue` as a reply to root2; expect the note `Exchange started at #<root2> continued: 8 more turn(s), 10 in all; queued @gpt-5.6-terra.` and the entry back to `open` with `budget 10` and `gpt-5.6-terra` pending or in flight.
  6. `continue.nothing`: `/continue` as a reply to the hub's range note from leg 3 (a hub note is in no exchange): note `Reply to #<noteId>: that message is in no exchange this hub still holds, so there is nothing to continue.`
  7. `stop.cleanup`: room stop, hub PID ended and waited for, scratch root removed in the outer finally.
  Header records the negative run: with the `/continue` branch removed from `OnMessage`, legs 4, 5, 6 fail (the builder comments the branch out, runs, restores, and pastes the three failing check names into the header). Run it: `pwsh -NoProfile -File tools\Invoke-Row44ContinueCheck.ps1 -HubExe <path to the Debug hub exe>` after `dotnet build`.

## Critique dispositions

Pass 1 (opus, 6.5, FIX-THEN-SHIP): F1 dissolved by F11 (tests rewritten with `Started`/`Finished` interleaved); F2 fixed (Task 2's gate named, A2 red until Task 3); F3 dissolved by F11; F4 fixed (service guard on the synthesis spawn's mentions, revert named); F5 fixed (marks reset only after an accepted reopening, with a test); F6 fixed (room order first); F7 fixed (claim 19 reworded, `NextWake` asserted); F8 fixed (OVERLAY.md:37, claim 17 counts four); F9 fixed (`turns` field on fixture cases); F10 fixed (mechanism-revert table); F11 taken (synthesis turn outside the budget). Minors: prose `turns:` (fixed), 2-arg `Finished` (deleted), runs 4 → 8 (declared in D-a with reason), test counts (fixed), mention-order artifact and dead `<= 1` (dissolved), client `16`/`8` (accepted, mirrors hard caps), per-strip accessible name (fixed), phone fold (row 55).

Pass 2 (fable, 5.9, FIX-THEN-SHIP): B1 (anchored leading-region regex backtracks catastrophically, measured 13 s at 10 mentions) fixed: the token is read inside the mention reader's sticky walk, no second regex, canary case with a time bound on both sides; M1 (five assertions cannot compile or pass) fixed: collection asserts on their own lines, message 3 puts both ids in the leading run, `#3`, three notes; M2 (two paid wrap-ups) fixed: the non-addressee last-turn sentence defers to the addressee; M3 (refusal note invites a refused action) fixed: "once it has concluded" in the tail and in the still-open note; M4 (replayed hand-off loses its trigger) fixed: refusing message first in `TriggerIds`, its own why-line; M5 (`/continue @sonet` spawns the wrong participant) fixed: unknown-word notes and `NobodySpawnable`; M6 (reader and parser disagree on lines) dissolved: one walker, multi-line case pinned. Minors: m1 phantom turn (fixed: `SynthesisGrewBudget` undone on stop/supersede, cleared at launch); m2 note lists `Pending` (fixed); m3 `params` overload (fixed); m4 ticket drift (fixed); m5 held-handler timeout (fixed: `ContinueWhileOpenTests` with a 30 s timeout); m6 top-level `continuable` follows `Displayed` (accepted, stated in D-e); m7 silent addressee retry (fixed: `posted` parameter); m8 wording (fixed: "in room order"). The framing alternative (reply-join extends the budget by itself) was weighed and declined: D4 names a Continue button and the phone needs a typed form.

## Could not verify in this environment

- The live hub (port 8790) is not restarted or touched by this plan; the deploy in Phase B is the first live run of the new budget. Real model behaviour on the synthesis why-line and the deferral sentence is unverified until a real exchange runs; the dry run uses a stub that never posts, and the guard makes a disobedient synthesis harmless rather than impossible to see.
- The UIA helper for AC8 (`.claude/skills/verify-chopitup/helpers/Invoke-ContinueCapture.ps1`, gitignored) is written and run in Phase B, not by a builder; it needs the interactive desktop.
- Whether the phone hand (`owner-remote` over MCP) posts `/continue` with a `reply_to` is not exercised here; the bare form is the fallback and is covered.
- The message id `#10` in the continuation service test is derived by counting, not measured; the builder measures it.
