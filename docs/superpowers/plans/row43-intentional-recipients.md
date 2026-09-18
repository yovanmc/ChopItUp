# Row 43 — Intentional recipients

**Goal:** only the `@id` tokens at the start of a message address anyone; an id anywhere else is a reference, a leading word that matches nobody gets a hub note, and the composer shows who a draft will reach before it is sent.

**Architecture:** `Mentions` (Core) gains a second reader, `Leading`, that scans the recipient region — the start of the body after an optional command prefix (a first-line `/skill` token or a `phase:` tag token) — for consecutive `@word` tokens and reports the roster ids it finds (recipients) and the words it does not (unknown). `ExchangePolicy` swaps its two `Find` calls for `Leading`, keeps `Find` only to name inline references in one hub note, and posts one note per unknown leading word. The spawn prompt, the MCP server instructions, the README and the room overlay state the rule in one sentence each. The web client mirrors the same grammar in `participants.ts` and renders recipient chips plus a one-line dispatch preview above the textarea; a shared JSON fixture keeps the C# and TypeScript readers in agreement.

**Author model:** Fable 5.1 (session model matches the HIGH routing; no mismatch).

**Blast radius: HIGH by judgment, with no scanner trigger.** No persisted format changes, but three cross-process contracts change together: the spawn prompt every CLI spawn reads, the MCP server instructions every host reads at initialize, and the room overlay the imported `roadmap` skill hands to conductors. A wrong rule either spends real turns on references (today's defect) or stops the run flow (`phase: build @sonnet …` must keep dispatching; a refused conductor post parks the run after repeated refusals). Tier evidence (`Check-BlastRadius.ps1 -RepoPath . -Files <the 11 existing source, test-fixture and doc files Tasks 1-4 edit; new files are unscannable>`, 2026-09-18, exit 0):

```
TIER-EVIDENCE: no HIGH trigger in 11 files; MEDIUM or LOW by judgment
```

`SpawnerService.cs` is edited by Task 2 only at `AppBackedTarget` (one call renamed); a `-Files` scan including it reports two pre-existing HIGH lines (996 secrets, 1140 delete-replace) that no task touches, and the Phase-B `-Base main` scan (added lines only) is expected to exit 0. The tier rests on the cross-process-contract argument alone; the critic attacked it in pass 1 and agreed HIGH stands.

Written for builder-subagent execution; if something doesn't match, STOP and report rather than guess.

**Lessons consulted** (`docs/LESSONS.md`): M27 (a note that names an actor takes that actor as a parameter: the unknown-word note lists the roster it was built from, never a literal), M24 (revert each mechanism once and record which test fails; a guard that passes with the mechanism reverted binds nothing), Row 42 (a dry run is a gate only after its negative leg has been seen RED with the mechanism reverted; the barrier is a note the hub posts, never a timer), Row 34 (a stub `codex.cmd` first on the scratch hub's PATH holds an exchange open with no model call; only Codex rows take the shim), M16/M23 (the browser pane drops input while hidden; the interactive gate is the UIA helper), M11 (a wording change is a literal sweep: the prompt sentence lives in the golden capture too).

**Defect at HEAD `b467de6`** ([V 2026-09-18 b467de6]): `Mentions.Find` (`src/ChopItUp.Core/Messaging/Mentions.cs:26-36`) returns every roster id anywhere in the body, and `ExchangePolicy` uses it for both the human/model path (`ExchangePolicy.cs:75`) and the conductor path (`MentionedSpawnable`, line 250). General #46 (owner: "@opus Please ask @gpt-5.6-terra one concrete question …") spawned Terra at once: #47 is Terra's answer at 21:49:55, before Opus's question #48 at 21:50:03, and Opus's own summary #49 says so. Grill decision D5 (`.scratch/grill-notes-goal-2026-09-17.md`, owner-approved): leading mentions only, unmatched `@word` noted.

## Decisions this plan takes

- **D-a Recipient region.** After an optional prefix, consecutive `@word` tokens separated by ASCII whitespace (line breaks included), commas, colons or semicolons; the region ends at the first character run that is not such a token, so `@opus\n@sonnet\nnow` and `phase: build/x\n@sonnet do it` both address (the code is permissive across lines; every contract text says "at the start of the message" and never "on the same line"). A word is `[A-Za-z0-9]` then any of `[A-Za-z0-9_.-]`, and must not be followed by a letter, digit, `_`, `.` or `-` of any script (`@opüs` is no token at all; `@opus's` is `opus`); a trailing `.`, `,`, `:`, `;`, `!` or `?` on a word is sentence punctuation, not part of the id. The prefix is either the `/name` token that `SlashCommands.TryParse` recognises on the first line (`/grill @opus @sonnet what about X`) or the `phase: <kind>[/<name>]` token that `PhaseTag.TryParse` recognises (`phase: build/x @sonnet Brief: …`, the overlay's lite-path shape). Nothing else is skipped: `[usability trial] @sonnet …`, `(@opus) please` and `"@opus" said` address nobody, and the composer preview says so before it is sent. Declined: skipping bracketed, quoted or markdown prefixes (the hub does not guess at markdown, `PhaseTag.cs:9-10`). The composer preview mirrors the reader only: for a slash draft it shows who the mention addresses and not whether the skill exists or is `/stop` (the skill menu offers installed skills only, and the hub's own note is the truth for `/stop` and an unknown name); declined to teach the strip the skill store.
- **D-b Unknown leading word.** A human post outside a run whose leading run holds a word that matches no addressable id gets one hub note per word: `No participant named @word. Address one of: @opus, @sonnet, …` (the list is the roster's spawnable ids, built once from the roster the policy received; `@claude`/`@codex` are left out because a phone user who followed the note with them would get silence). `@hub` gets its own sentence: `The hub cannot be addressed; it only posts notes.` Other leading recipients in the same message still act. Model posts are not noted (the prompt tells them the rule; a mistyped hand-on concludes the exchange, which the owner sees), and conductor posts inside a run are not noted (their refusal path already says `needs a mention of who does the work`).
- **D-c Inline reference note.** A human post outside a run with no leading recipients at all (spawnable or not) and at least one spawnable roster id inline gets one note: `Nobody was addressed: @opus appears inside the text, so it was read as a reference. Start the message with @opus to send it.` When the same message invokes a skill, the existing refusal line carries the reference instead of a second note: `/grill needs a mention to run: nobody was addressed, so no exchange started. @opus appears inside the text, so it was read as a reference.` (and the run-start line `… needs exactly one conductor mentioned; none was.` gets the same suffix). Both notes are decided after every earlier refusal has returned (unknown skill, tampered skill, no directory, reply-join), so a message never gets two notes for one cause and a row-36 reply that joins an exchange as a comment gets none. Row 49 (relay mode) retires the reference note when an un-addressed message gets an answer instead.
- **D-d Non-spawnable recipients** (`@owner`, `@claude`, `@codex`) stay silently accepted server-side as today (row 47 owns the host-mention note); the composer shows them as passive chips so the owner sees they are not spawned. `AppBackedTarget` (SpawnerService.cs:876-881) chooses WHICH open exchange an app-backed post lands in by the ids it mentions anywhere; that is target selection, not dispatch, and keeps the whole-body reader through a new `ReferencedSpawnable` policy method.
- **D-e Thread rendering unchanged.** `markdown.ts` keeps highlighting every roster id; references and recipients look the same in history. Declined for this row: a distinct style for recipients (no owner ask; row 41/39 polish territory).
- **D-f MCP `post_message` reply unchanged.** The phone hand gets its feedback from the two notes above, which it reads like any message; adding a `recipients` field was declined as duplicate feedback (row 47 owns MCP honesty).
- **D-g Overlay text changes in the repo; the live copy needs a re-import.** `tools/skills/roadmap-hub/OVERLAY.md` is edited so an import carries the rule. Its lite path's first dispatch (`phase: build/<slug> @<row>`, line 25) already complies, but its re-dispatch template (line 33: "post `phase: build/<slug>` again with the punch list") puts no mention on the tag line, and its full-path critique shape (line 36) puts an `artifact:` line between the tag and the judge. Both are fixed in the repo by Task 3. The live hub's imported copy is content-hashed and an agent never writes the deployed data directory, so re-importing it is a Class C owner item, gating the next room run, not this row's merge: with the hub stopped, `ChopItUp.Hub.exe --import-skill <roadmap skill dir> --overlay tools\skills\roadmap-hub` (`docs/verification.md:115`). Until then a live conductor that follows the old template is refused once, re-spawned with the note, and normally corrects itself; repeated refusals park the run, which is visible, not silent.

## Acceptance

- **AC1** WHEN a message begins with one or more `@id` tokens, directly or after a first-line `/skill` token or a `phase:` tag token, THE SYSTEM SHALL treat exactly those ids as the message's recipients: spawnable ones open or continue an exchange exactly as a mention does at HEAD (budget, debounce, self-mention drop, supersede and join rules unchanged).
- **AC2** WHEN a roster id appears anywhere after the first token that is not an `@word` (inline in a sentence, after a bracketed prefix, inside a code span, quoted), THE SYSTEM SHALL spawn nothing for it; AND WHEN such a message is a human post outside a run, not a reply that joins an exchange, with no leading recipients at all (spawnable or not) and no earlier refusal, THE SYSTEM SHALL post exactly one hub note naming each inline spawnable id as a reference (carried on the existing refusal line when the message invokes a skill or starts a run).
- **AC3** WHEN a human post outside a run has a leading `@word` that matches no addressable id, THE SYSTEM SHALL post one hub note per word naming it and listing the addressable ids (`@hub` gets its own sentence), and SHALL still act on the other leading recipients of the same message; a model's or a conductor's unknown word gets no note.
- **AC4** WHEN a spawned model's reply begins with `@id`, THE SYSTEM SHALL hand the turn on as at HEAD; WHEN its reply names ids only inline, THE SYSTEM SHALL open no further turn and conclude the exchange with the turns used so far. WHEN a conductor posts `phase: build/x @sonnet …`, THE SYSTEM SHALL dispatch the worker as at HEAD; WHEN a conductor's `phase: build` post names a row only inline, THE SYSTEM SHALL refuse it with the existing `needs a mention of who does the work` line.
- **AC5** WHEN a spawn prompt, the MCP server instructions, the README and the room overlay are read, THE SYSTEM SHALL state that only mentions at the start of a message (after a `/skill` or `phase:` token) address anyone and that an id elsewhere is a reference; the golden prompt capture SHALL differ from HEAD's only on the lines carrying that sentence.
- **AC6** WHEN the composer draft has leading recipients, THE SYSTEM SHALL show one chip per recipient (display name, host accent; passive styling for a non-spawnable row) and a preview line naming them; WHEN a leading word matches nobody, a chip SHALL say so; WHEN roster ids appear only inline, the preview SHALL say nobody is addressed and name the reference; WHEN the draft is empty or has neither, no strip SHALL render. The strip SHALL expose `role="status"` with the name `Recipients` and each chip an accessible name, so the UIA gate can find them. The C# and TypeScript readers SHALL agree on every case in the shared fixture, including the Unicode and BOM cases.
- **AC7** WHEN `tools\Invoke-Row43MentionCheck.ps1` runs the built hub with a stub `codex.cmd` first on PATH, THE SYSTEM SHALL pass these legs in order: `health.ok`; `leading.opens-exchange`; `inline.no-exchange-reference-note`; `unknown.note`; `bracket.no-exchange`; `slash.dispatches`; `stop.cleanup`; and with the leading rule reverted to `Find`, the `inline`, `bracket` and `unknown` legs SHALL fail (recorded in the script header with the three check names).
- **AC8** WHEN the verify-skill helper drives the real composer over UIA (type a leading draft, an inline draft, an unknown draft, clear), THE SYSTEM SHALL expose the `Recipients` status element with the chips and preview text AC6 names for each state and none after clearing, and `Test-CaptureSane.ps1` SHALL pass on the captures.

## Claim ledger

| # | Claim | Verified at (commit) | Recheck (pwsh, exit 0 = holds) |
|---|-------|----------------------|--------------------------------|
| 1 | Baseline: Desktop 108/108, Core 254/254, Hub 870/870 (full suite green, measured 2026-09-18 this session, Hub run 8 min 7 s); vitest 190/190 in 14 files | b467de6 | `dotnet test ChopItUp.slnx -c Debug --nologo -v minimal` then `npx vitest run` in `src/ChopItUp.Hub/client` |
| 2 | `_mentions.Find(message.Body)` appears exactly twice in ExchangePolicy.cs (lines 75 and 250) | b467de6 | `pwsh -NoProfile -Command "$n=(Select-String -Path 'src/ChopItUp.Hub/Spawning/ExchangePolicy.cs' -Pattern '_mentions\.Find\(message\.Body\)').Count; if($n -eq 2){exit 0}else{exit 1}"` |
| 3 | No other production code calls `Mentions.Find` (SlashCommand.cs names it in a comment only) | b467de6 | `pwsh -NoProfile -Command "$h=Get-ChildItem src -Recurse -Filter *.cs \| Select-String -Pattern '\.Find\(' \| Where-Object { $_.Path -notmatch 'ExchangePolicy\.cs$' -and $_.Line -match 'mentions' -and $_.Line -notmatch '^\s*///' }; if($h.Count -eq 0){exit 0}else{exit 1}"` |
| 4 | SpawnPrompt.cs:113 carries the literal `Mention a participant with @ and its id to hand it the turn` | b467de6 | `pwsh -NoProfile -Command "if((Get-Content 'src/ChopItUp.Hub/Spawning/SpawnPrompt.cs' -Raw) -match 'Mention a participant with @ and its id to hand it the turn'){exit 0}else{exit 1}"` |
| 5 | Participation.cs `Rules` carries `Address a participant with @ and its id: {MENTIONS}` | b467de6 | `pwsh -NoProfile -Command "if((Get-Content 'src/ChopItUp.Hub/Mcp/Participation.cs' -Raw) -match 'Address a participant with @ and its id: \{MENTIONS\}'){exit 0}else{exit 1}"` |
| 6 | `PhaseTag` tolerates trailing text on the tag line (its summary says `Trailing text on the same line is ignored on purpose`) and `TryParse(string body, out PhaseTag? tag)` is its only parser | b467de6 | `pwsh -NoProfile -Command "$s=Get-Content 'src/ChopItUp.Core/Model/PhaseTag.cs' -Raw; if($s -match 'Trailing text on the same line is ignored on purpose' -and $s -match 'public static bool TryParse\(string body, out PhaseTag\? tag\)'){exit 0}else{exit 1}"` |
| 7 | `SlashCommands` matches `^/(?<name>[a-z0-9][a-z0-9-]{0,63})` on the first line, so the token to skip is `/` + `Name` | b467de6 | `pwsh -NoProfile -Command "if((Get-Content 'src/ChopItUp.Core/Skills/SlashCommand.cs' -Raw) -match [regex]::Escape('^/(?<name>[a-z0-9][a-z0-9-]{0,63})')){exit 0}else{exit 1}"` |
| 8 | Test R14 byte-compares `tests/ChopItUp.Hub.Tests/Spawning/golden-prompt-ddfa572.txt` with `SpawnPrompt.Render(GoldenInput(), SpawnLimits.Default)`; golden line 5 carries the mention sentence | b467de6 | `pwsh -NoProfile -Command "$g=Get-Content 'tests/ChopItUp.Hub.Tests/Spawning/golden-prompt-ddfa572.txt'; $t=Get-Content 'tests/ChopItUp.Hub.Tests/Spawning/SpawnPromptTests.cs' -Raw; if($g[4] -match 'Mention a participant with @ and its id' -and $t -match 'R14_a_prompt_with_no_standing_text_is_byte_for_byte'){exit 0}else{exit 1}"` |
| 9 | `Composer.tsx` imports nothing roster-related beyond `displayName`; `participants.ts` exports `setRoster`, `mentionPattern`, `hostOf`, `displayName` and keeps the roster in a module-level `Map` | b467de6 | `pwsh -NoProfile -Command "$c=Get-Content 'src/ChopItUp.Hub/client/src/Composer.tsx' -Raw; $p=Get-Content 'src/ChopItUp.Hub/client/src/participants.ts' -Raw; if($c -match \"import \{ displayName \} from './participants'\" -and $p -match 'export function mentionPattern' -and $p -match 'export function setRoster' -and $p -match 'let roster = new Map'){exit 0}else{exit 1}"` |
| 10 | Client tests use `renderToStaticMarkup` (vitest 5.0.0); no `@testing-library` package is installed | b467de6 | `pwsh -NoProfile -Command "$j=Get-Content 'src/ChopItUp.Hub/client/package.json' -Raw; if($j -notmatch 'testing-library' -and $j -match '\"vitest\"'){exit 0}else{exit 1}"` |
| 11 | `ChatApi.PostMessage` is authored from the bearer and `BearerTokenMiddleware` refuses anything but the owner or owner-remote on that surface, so a stub CLI cannot post as a model over `/api` | b467de6 | `pwsh -NoProfile -Command "if((Get-Content 'src/ChopItUp.Hub/Web/ChatApi.cs' -Raw) -match 'the owner or owner-remote'){exit 0}else{exit 1}"` |
| 12 | `.mention` styling with `data-host` accents starts at styles.css:893; `.reply-chip` at 1152 is the composer's existing chip pattern | b467de6 | `pwsh -NoProfile -Command "$s=Get-Content 'src/ChopItUp.Hub/client/src/styles.css'; if($s[892] -match '^\.mention \{' -and $s[1151] -match '^\.reply-chip \{'){exit 0}else{exit 1}"` |
| 13 | The overlay's lite path posts the worker mention on the phase line: `phase: build/<slug> @<one plumbing-class row` | b467de6 | `pwsh -NoProfile -Command "if((Get-Content 'tools/skills/roadmap-hub/OVERLAY.md' -Raw) -match 'phase: build/<slug> @<one plumbing-class row'){exit 0}else{exit 1}"` |
| 14 | `MentionsTests.cs` holds 6 `[Fact]`s over `Find`, all of which keep passing (Find is unchanged) | b467de6 | `pwsh -NoProfile -Command "$n=(Select-String -Path 'tests/ChopItUp.Core.Tests/Messaging/MentionsTests.cs' -Pattern '\[Fact\]').Count; if($n -eq 6){exit 0}else{exit 1}"` |
| 15 | `_policy.MentionedSpawnable(m)` is called at SpawnerService.cs:618 and :879 and nowhere else in `src` | b467de6 | `pwsh -NoProfile -Command "$n=(Get-ChildItem src -Recurse -Filter *.cs \| Select-String -Pattern 'MentionedSpawnable\(' \| Where-Object { $_.Path -notmatch 'ExchangePolicy\.cs$' }).Count; if($n -eq 2){exit 0}else{exit 1}"` |
| 16 | `HubTestHost.ClientFor(participant)` returns a real MCP client for that participant's token; `SpawnerServiceTests.PostAs` posts through it and `FakeProcessRunner.Handler` plays the model | b467de6 | `pwsh -NoProfile -Command "$h=Get-Content 'tests/ChopItUp.Hub.Tests/HubTestHost.cs' -Raw; $t=Get-Content 'tests/ChopItUp.Hub.Tests/Spawning/SpawnerServiceTests.cs' -Raw; if($h -match 'ClientFor\(' -and $t -match 'await using var client = await _host\.ClientFor\(participant\);'){exit 0}else{exit 1}"` |
| 17 | The stub CLI shape (`codex.cmd` = `ping -n 600 127.0.0.1` then `exit /b 0`, first on PATH) is recorded as prose in `docs/LESSONS.md` under the Row 34 heading, and `CliResolver` takes `name.exe` anywhere on PATH before any shim, then wraps a `.cmd` shim as `cmd.exe /d /c <shim>` (CliResolver.cs:31-42), so a `codex.cmd` stub is launchable for every `gpt-*` row and for no `claude` row | b467de6 | `pwsh -NoProfile -Command "$l=Get-Content 'docs/LESSONS.md' -Raw; $c=Get-Content 'src/ChopItUp.Hub/Spawning/CliResolver.cs' -Raw; if($l -match 'ping -n 600 127\.0\.0\.1' -and $c -match 'ShimExtensions' -and $c -match '\"/d\", \"/c\", shim'){exit 0}else{exit 1}"` |
| 18 | `ExchangePolicy` receives the roster in its constructor and builds `Mentions` from every non-system id (ExchangePolicy.cs:48) | b467de6 | `pwsh -NoProfile -Command "if((Get-Content 'src/ChopItUp.Hub/Spawning/ExchangePolicy.cs' -Raw) -match '_mentions = new Mentions\(roster\.Where\(p => p\.Kind != \"system\"\)\.Select\(p => p\.Id\)\);'){exit 0}else{exit 1}"` |
| 19 | The 2026-09-17 usability trial in room `general` shows the defect: #46 (owner) mentions Terra inline, #47 (Terra) lands 8 s before #48 (Opus's actual question); read this session over MCP | — | — |
| 20 | Six existing tests need a body change under the new rule and Task 2 rewrites exactly these: `SpawnerServiceTests.cs` `"I think so. @gpt-6-astra, a second opinion?"` and `"late: @sonnet @fable please"` (inline mentions that spawned), `ExchangePolicyTests.cs` `"thanks @claude, carry on"` (inline; survives either way, claude is not spawnable) and `ExchangePolicyTests.cs:20` `"@opus @claude @codex @owner @hub @gpt-6-astra go"` (asserts `Assert.Empty(notes)`, which the new `@hub` sentence would break); plus two the build found (the claim first said four): `ExchangePolicyTests.cs` R36 `"@opus and also this @sonnet"` (a reply-join whose inline `@sonnet` joined) and `SpawnerServiceTests.cs` R32 `"@opus a thought for you, and @sonnet too"` (an app-backed post whose inline `@sonnet` spawned). `SpawnerServiceTests.cs:187` also posts `@hub` but asserts only "no spec, status idle" and survives | 81be7dd | `pwsh -NoProfile -Command "$a=Get-Content 'tests/ChopItUp.Hub.Tests/Spawning/SpawnerServiceTests.cs' -Raw; $b=Get-Content 'tests/ChopItUp.Hub.Tests/Spawning/ExchangePolicyTests.cs' -Raw; if($a -match 'I think so\. @gpt-6-astra, a second opinion\?' -and $a -match 'late: @sonnet @fable please' -and $a -match '@opus a thought for you, and @sonnet too' -and $b -match 'thanks @claude, carry on' -and $b -match '@opus @claude @codex @owner @hub @gpt-6-astra go' -and $b -match '@opus and also this @sonnet'){exit 0}else{exit 1}"` |
| 21 | `AppBackedTarget` (SpawnerService.cs:876-881) picks the newest open exchange whose participants intersect `_policy.MentionedSpawnable(m)` — target selection by mention, not dispatch | b467de6 | `pwsh -NoProfile -Command "if((Get-Content 'src/ChopItUp.Hub/Spawning/SpawnerService.cs' -Raw) -match 'var mentioned = _policy\.MentionedSpawnable\(m\);\s*return open\.FirstOrDefault\(e => mentioned\.Any\(e\.Participants\.Contains\)\)'){exit 0}else{exit 1}"` |
| 22 | `Check-Slop.ps1` scans added lines outside `.claude/ .scratch/ docs/ ROADMAP.md NORTHSTAR.md CLAUDE.md LESSONS.md`; `tools/` is scanned, and OVERLAY.md lines 12 and 36 already carry `dissect-critic` and `docs/superpowers`, which its regexes flag | b467de6 | `pwsh -NoProfile -Command "$o=Get-Content 'tools/skills/roadmap-hub/OVERLAY.md'; if($o[11] -match 'dissect-critic' -and $o[35] -match 'docs/superpowers'){exit 0}else{exit 1}"` |

Claims 4, 5, 8, 15, 20 and 22 describe HEAD before the branch and invert on it by design: Tasks 2 and 3 replace those literals, move one call site to `ReferencedSpawnable` and insert an overlay line above the two positional lookups. On the branch the truth for each is the test Task 2, 3 or 6 names; a `plan-claims` re-run against the branch reports those six as failures and nothing else.

## Tasks

Branch `row43-intentional-recipients` off `main`. One commit per task, TDD (RED executed and quoted in the report). Build with `dotnet build ChopItUp.slnx -c Debug -warnaserror -v minimal` (0 warnings). Never build while a Debug hub runs. `python` is the Store stub on this machine: use `py` if a script needs Python (none planned).

### Task 1 — `Mentions.Leading` + shared fixture (sonnet)

**Files:** `src/ChopItUp.Core/Messaging/Mentions.cs`, `src/ChopItUp.Core/Model/PhaseTag.cs`, `tests/mention-cases.json` (new; owned by this task, read by Tasks 2 and 4), `tests/ChopItUp.Core.Tests/Messaging/MentionsTests.cs`.

1. **Fixture first.** Create `tests/mention-cases.json`:

```json
{
  "roster": ["owner", "claude", "codex", "opus", "sonnet", "fable", "gpt-6-astra", "gpt-5.6-sol", "gpt-5.5", "owner-remote"],
  "cases": [
    { "name": "single leading",            "body": "@opus what next?",                                   "recipients": ["opus"],                 "unknown": [],           "references": [] },
    { "name": "several, commas, newline",  "body": "@opus, @sonnet\n@gpt-5.6-sol: please weigh in",     "recipients": ["opus", "sonnet", "gpt-5.6-sol"], "unknown": [], "references": [] },
    { "name": "sentence punctuation",      "body": "@opus. Then nothing.",                                "recipients": ["opus"],                 "unknown": [],           "references": [] },
    { "name": "case and canonical id",     "body": "@Opus @GPT-6-ASTRA go",                               "recipients": ["opus", "gpt-6-astra"],  "unknown": [],           "references": [] },
    { "name": "dotted id beats prefix",    "body": "@gpt-5.5 please",                                     "recipients": ["gpt-5.5"],              "unknown": [],           "references": [] },
    { "name": "slash prefix",              "body": "/grill @opus @sonnet what about X",                   "recipients": ["opus", "sonnet"],       "unknown": [],           "references": [] },
    { "name": "slash then newline",        "body": "/roadmap @opus\nlite",                                "recipients": ["opus"],                 "unknown": [],           "references": [] },
    { "name": "phase prefix same line",    "body": "phase: build/x @sonnet Brief: .scratch/b.md",         "recipients": ["sonnet"],               "unknown": [],           "references": [] },
    { "name": "phase kind only",           "body": "phase: critique @fable\nartifact: docs/p.md",         "recipients": ["fable"],                "unknown": [],           "references": [] },
    { "name": "phase with inline only",    "body": "phase: build\nSee @sonnet's note",                    "recipients": [],                       "unknown": [],           "references": ["sonnet"] },
    { "name": "bracket prefix",            "body": "[usability trial] @sonnet describe the room",         "recipients": [],                       "unknown": [],           "references": ["sonnet"] },
    { "name": "inline ask",                "body": "@opus Please ask @gpt-5.6-sol one question",          "recipients": ["opus"],                 "unknown": [],           "references": ["gpt-5.6-sol"] },
    { "name": "inline only",               "body": "please ask @opus something",                          "recipients": [],                       "unknown": [],           "references": ["opus"] },
    { "name": "code span",                 "body": "`@opus` is an id",                                    "recipients": [],                       "unknown": [],           "references": ["opus"] },
    { "name": "email",                     "body": "me@opus.com writes",                                  "recipients": [],                       "unknown": [],           "references": [] },
    { "name": "unknown then known",        "body": "@nobody @opus hi",                                    "recipients": ["opus"],                 "unknown": ["nobody"],   "references": [] },
    { "name": "longer word is unknown",    "body": "@claude-2 and @claudette",                            "recipients": [],                       "unknown": ["claude-2"], "references": [] },
    { "name": "duplicate leading",         "body": "@opus @opus @Opus go",                                "recipients": ["opus"],                 "unknown": [],           "references": [] },
    { "name": "crlf",                      "body": "@opus\r\n@sonnet\r\nnow",                             "recipients": ["opus", "sonnet"],       "unknown": [],           "references": [] },
    { "name": "empty",                     "body": "",                                                    "recipients": [],                       "unknown": [],           "references": [] },
    { "name": "bare at",                   "body": "@ owner hi",                                          "recipients": [],                       "unknown": [],           "references": [] },
    { "name": "possessive",                "body": "@opus's turn now",                                    "recipients": ["opus"],                 "unknown": [],           "references": [] },
    { "name": "parenthesised",             "body": "(@opus) please",                                      "recipients": [],                       "unknown": [],           "references": ["opus"] },
    { "name": "quoted",                    "body": "\"@opus\" said so",                                   "recipients": [],                       "unknown": [],           "references": ["opus"] },
    { "name": "dashed suffix is unknown",  "body": "@opus-1 go",                                          "recipients": [],                       "unknown": ["opus-1"],   "references": [] },
    { "name": "phase then newline",        "body": "phase: build/x\n@sonnet do it",                       "recipients": ["sonnet"],               "unknown": [],           "references": [] },
    { "name": "slash stop then newline",   "body": "/stop\n@opus take it",                                "recipients": ["opus"],                 "unknown": [],           "references": [] },
    { "name": "hub is unknown here",       "body": "@hub hi",                                             "recipients": [],                       "unknown": ["hub"],      "references": [] },
    { "name": "non-ascii letter",          "body": "@opüs hi",                                            "recipients": [],                       "unknown": [],           "references": [] },
    { "name": "bom before at",             "body": "\uFEFF@opus hi",                                      "recipients": [],                       "unknown": [],           "references": ["opus"] },
    { "name": "email without tld",         "body": "me@opus writes",                                      "recipients": [],                       "unknown": [],           "references": [] }
  ]
}
```

(The BOM case is the six-character JSON escape `\uFEFF`, never a raw U+FEFF byte: an editor that strips BOMs would silently change a raw one.)

`references` = `Find(body)` minus `recipients` (order of first appearance). In "longer word is unknown", `@claudette` is not leading (the region ended at `and`) and is not a roster id, so it appears nowhere. "hub is unknown here": the reader is built from non-system ids, so `hub` is an unknown word to it; the policy (Task 2) turns that particular word into its own sentence. The last two cases exist because .NET `\w`/`\s` are Unicode and JavaScript's are ASCII: both readers use explicit ASCII classes and a Unicode negative lookahead so they cannot disagree there.

2. **RED.** In `MentionsTests.cs` add a `[Theory]` over the fixture (`MemberData` that loads `tests/mention-cases.json` from the repo root, found by walking up to `ChopItUp.slnx` the way `SpawnPromptTests.GoldenPath` does; no csproj change, no copy) asserting `Leading(body)` returns `(recipients, unknown)` and `Find(body).Except(recipients)` equals `references`. Run: `dotnet test tests/ChopItUp.Core.Tests --nologo -v minimal --filter FullyQualifiedName~MentionsTests` → compile error (no `Leading`) = RED.

3. **GREEN.** In `PhaseTag.cs` add an overload `public static bool TryParse(string body, out PhaseTag? tag, out int prefixLength)` where `prefixLength` is the length of the matched tag token on the (CRLF-normalised) first line (`match.Length`), 0 when no tag; the existing overload delegates to it. In `Mentions.cs` add:

```csharp
/// <summary>Row 43 (D5): who a message addresses. Only the run of @word tokens at the start of the
/// body counts — after an optional command prefix, the <c>/name</c> token <see cref="SlashCommands"/>
/// recognises or the <c>phase:</c> tag <see cref="PhaseTag"/> recognises. Tokens are separated by
/// ASCII whitespace (line breaks included), commas, colons or semicolons; a trailing sentence mark on
/// a word is not part of the id. Recipients are canonical roster ids in first-appearance order without
/// duplicates; Unknown are the leading words that matched nobody, verbatim. Everything after the first
/// non-@ token is prose, and <see cref="Find"/> still sees it as a reference. Character classes are
/// spelled out in ASCII (never <c>\w</c>/<c>\s</c>) so the client's twin in participants.ts, whose
/// engine defines those classes differently, reads every body the same way.</summary>
public sealed record LeadingMentions(IReadOnlyList<string> Recipients, IReadOnlyList<string> Unknown)
{
    public static readonly LeadingMentions None = new([], []);
}

private static readonly Regex Token = new(@"\G[ \t\r\n\f\v,:;]*@(?<word>[A-Za-z0-9][A-Za-z0-9_.\-]*)(?![\p{L}\p{N}_.\-])", RegexOptions.CultureInvariant);

public LeadingMentions Leading(string body)
{
    if (string.IsNullOrEmpty(body)) return LeadingMentions.None;
    var text = body.Replace("\r\n", "\n");
    var start = 0;
    if (SlashCommands.TryParse(text, out var command)) start = 1 + command.Name.Length;
    else if (PhaseTag.TryParse(text, out _, out var tagLength)) start = tagLength;
    var recipients = new List<string>();
    var unknown = new List<string>();
    for (var m = Token.Match(text, start); m.Success; m = Token.Match(text, m.Index + m.Length))
    {
        var word = m.Groups["word"].Value.TrimEnd('.', ',', ':', ';', '!', '?');
        if (_canonical.TryGetValue(word, out var id)) { if (!recipients.Contains(id)) recipients.Add(id); }
        else if (!unknown.Contains(word, StringComparer.OrdinalIgnoreCase)) unknown.Add(word);
    }
    return new LeadingMentions(recipients, unknown);
}
```

`_canonical` is the existing case-insensitive dictionary; with an empty roster it is empty and every leading word is unknown (keep that: the note in task 2 then lists nobody, which is the truth). `Regex.Match(string, int)` honours `\G` at `startat`. The word class is greedy, so `@opus.` captures `opus.` and the trim removes the dot, `@gpt-5.5` keeps its inner dot, `@opus's` stops at the apostrophe and yields `opus`, and the negative lookahead makes `@opüs` no token at all (the region then ends there). The first character class guarantees a non-empty word after the trim. Add `using ChopItUp.Core.Model; using ChopItUp.Core.Skills;` (both are Core namespaces; no new project reference).

4. Run the Core tests: the 6 `Find` facts still pass (claim 14), the theory passes for all 31 cases (count the `"name"` keys in the fixture: 31). Expected: `Passed! - Failed: 0, Passed: 285` (254 + 31).

5. **Revert check (M24):** temporarily make `Leading` return `Find(body)` as recipients; at least the "inline ask", "bracket prefix" and "inline only" cases must fail. Restore. Quote the failing case names in the commit message body.

### Task 2 — Policy + spawner wiring, the two notes (sonnet) — blocked by 1

**Files:** `src/ChopItUp.Hub/Spawning/ExchangePolicy.cs`, `tests/ChopItUp.Hub.Tests/Spawning/ExchangePolicyTests.cs`, `tests/ChopItUp.Hub.Tests/Spawning/SpawnerServiceTests.cs` (new facts in the existing partial; or a new partial `SpawnerServiceTests.Recipients.cs` beside `SpawnerServiceTests.Import.cs`).

1. **RED (policy).** In `ExchangePolicyTests.cs` add, using the existing `Policy()`/`Msg` helpers and `ChopDb.SeedRoster`:
   - `Row43_AC1_only_leading_mentions_open_an_exchange`: `Msg(10, "owner", "@opus Please ask @gpt-5.6-sol one question")` → `x.Pending.Keys == ["opus"]`, `TurnsCommitted == 1`, notes empty.
   - `Row43_AC2_an_inline_mention_alone_opens_nothing_and_is_noted_as_a_reference`: `"please ask @opus something"` → `x == null`, single note equal to `Nobody was addressed: @opus appears inside the text, so it was read as a reference. Start the message with @opus to send it.`
   - `Row43_AC2_a_bracket_prefix_is_prose`: `"[usability trial] @sonnet describe"` → null, the reference note names `@sonnet`.
   - `Row43_AC2_a_non_spawnable_recipient_is_addressed_so_no_reference_note`: `"@claude what did @opus say?"` → null, notes empty (claude is a leading recipient, so nobody-was-addressed is false).
   - `Row43_AC2_a_skill_with_inline_only_carries_the_reference_on_its_idle_line`: pass `skill: new SkillResolution.Found(<a ResolvedSkill named grill>, "…")` with body `"/grill see what @opus thinks"` → null, ONE note equal to `/grill needs a mention to run: nobody was addressed, so no exchange started. @opus appears inside the text, so it was read as a reference.` (find how existing tests build a `Found` — grep `SkillResolution.Found(` in this file).
   - `Row43_AC2_an_unknown_skill_with_inline_only_gets_the_skill_note_alone`: `skill: new SkillResolution.Unknown("nosuchskill", [...])`, body `"/nosuchskill see what @opus thinks"` → exactly one note, the existing `No skill named '/nosuchskill'…` line.
   - `Row43_AC2_a_reply_that_joins_gets_no_reference_note`: open an exchange with `"@opus"`, then an owner `Msg(2, "owner", "thanks, @opus was right") with { ReplyToId = 1 }` (the helper takes three arguments; `Message` is a record) passed as `joins:` the open exchange → the message is recorded as a member, no spawn, notes empty.
   - `Row43_AC3_an_unknown_leading_word_is_noted_and_the_rest_still_act`: `"@nobody @opus hi"` → `Pending.Keys == ["opus"]`, single note `No participant named @nobody. Address one of: @opus, @sonnet, @fable, @gpt-6-astra, @gpt-5.6-sol, @gpt-5.6-terra, @gpt-5.6-luna, @gpt-5.5, @gpt-5.4-mini.` — build the expected list from `ChopDb.SeedRoster.Where(ExchangePolicy.IsSpawnable).Select(p => "@" + p.Id)` in the test rather than typing it, so the seed roster stays the single source (M27); the list names only rows the hub can spawn, because a phone user who follows it with `@claude …` would otherwise get silence.
   - `Row43_AC3_hub_gets_its_own_sentence`: `"@hub @opus hi"` → `Pending.Keys == ["opus"]`, single note `The hub cannot be addressed; it only posts notes.`
   - `Row43_AC3_a_model_author_is_not_noted`: open with `"@opus"`, then `Msg(2, "opus", "@nobody @sonnet your view?")` → sonnet added, notes empty.
   - `Row43_AC4_a_model_reply_hands_on_only_when_leading`: open with `"@opus"`, then `Msg(2, "opus", "@sonnet your view?")` adds sonnet; a fresh policy: open with `"@opus"`, then `Msg(2, "opus", "thanks, maybe @sonnet knows")` → `Pending.Keys == ["opus"]`, `TurnsCommitted == 1`, notes empty (no reference note for a model).
   - `Row43_AC4_a_conductor_post_needs_the_mention_after_the_tag`: through the existing `RefuseConductorPost` helper at line ~569: `"phase: build/x @sonnet do it"` → null (valid; `sonnet` carries class `plumbing` in `ChopDb.SeedRoster`, ChopDb.cs:38); `"phase: build/x\nSee @sonnet's note"` → the `needs a mention of who does the work` line.
   - `Row43_D_d_referenced_spawnable_reads_the_whole_body`: `Policy().ReferencedSpawnable(Msg(1, "claude", "I agree with @opus"))` → `["opus"]`.
   Run the Hub policy tests: these fail (inline still spawns; no notes; no `ReferencedSpawnable`) = RED.

2. **GREEN.** In `ExchangePolicy.OnRoomMessage`, keeping every existing return in place:
   - Replace the `mentioned` computation (line 75) with `var leading = acceptMentions ? _mentions.Leading(message.Body) : Mentions.LeadingMentions.None;` then `mentioned = leading.Recipients.Where(id => id != message.AuthorId && _roster.TryGetValue(id, out var p) && IsSpawnable(p)).ToList();`.
   - Leave the model branch (line 80-85), the run early return (line 88) and the reply-join block (line 95-105) untouched: none of them may add a row-43 note.
   - Directly AFTER the skill `switch` (after line 138, before the `startsRun && mentioned.Count != 1` check): (i) for every `word` in `leading.Unknown`: `notes.Add(word.Equals("hub", StringComparison.OrdinalIgnoreCase) ? "The hub cannot be addressed; it only posts notes." : $"No participant named @{word}. Address one of: {_addressable}.")` where `_addressable` is a field built once in the constructor from the roster's spawnable ids (`roster.Where(IsSpawnable)`) in roster order (`string.Join(", ", …Select(p => "@" + p.Id))`); (ii) `var referenced = leading.Recipients.Count == 0 ? _mentions.Find(message.Body).Where(id => id != message.AuthorId && _roster.TryGetValue(id, out var p) && IsSpawnable(p)).ToList() : [];` and `var referenceSuffix = referenced.Count == 0 ? "" : referenced.Count == 1 ? $" @{referenced[0]} appears inside the text, so it was read as a reference." : $" {string.Join(", ", referenced.Select(r => "@" + r))} appear inside the text, so they were read as references.";`.
   - The run-start refusal (line 141-149): append `referenceSuffix` to the `none was.` arm only.
   - The `mentioned.Count == 0` block (line 151-156): the idle line becomes `$"/{idle.Skill.Name} needs a mention to run: nobody was addressed, so no exchange started.{referenceSuffix}"`; when there is no `Found` skill and `referenced.Count > 0`, add `$"Nobody was addressed:{referenceSuffix.TrimEnd()[1..]} Start the message with @{referenced[0]} to send it."` — i.e. the note reads `Nobody was addressed: @opus appears inside the text, so it was read as a reference. Start the message with @opus to send it.` (write it as one interpolated string rather than slicing if that reads better; the test pins the exact text).
   - `MentionedSpawnable` (line 250): `_mentions.Leading(message.Body).Recipients.Where(...)` — self kept, as its comment demands. Add beside it `public IReadOnlyList<string> ReferencedSpawnable(Message message) => _mentions.Find(message.Body).Where(id => _roster.TryGetValue(id, out var p) && IsSpawnable(p)).ToList();` with a summary saying it is the whole-body reader kept for target SELECTION (`AppBackedTarget`), never for dispatch.
   - In `SpawnerService.AppBackedTarget` (SpawnerService.cs:879) change `_policy.MentionedSpawnable(m)` to `_policy.ReferencedSpawnable(m)`; the other call (line 618, the conductor) stays on `MentionedSpawnable`. This is the only SpawnerService edit in the plan.
   - Update the class summary comment's "D8: a mention is the only trigger" sentence to say a LEADING mention (row 43, D5) and point at `Mentions.Leading`.
   Policy tests green. Then the whole Hub suite: four existing tests need a body change (claim 20) — `SpawnerServiceTests.cs` `"I think so. @gpt-6-astra, a second opinion?"` (the A1_A3_A7 chain) and `"late: @sonnet @fable please"` (the late-post supersede test), `ExchangePolicyTests.cs` `"thanks @claude, carry on"`, and `ExchangePolicyTests.cs:20` `"@opus @claude @codex @owner @hub @gpt-6-astra go"` (its `Assert.Empty(notes)` would now see the hub sentence) — rewrite each body to a leading form without the hub word (`"@gpt-6-astra I think so; a second opinion?"`, `"@sonnet @fable late: please"`, `"@claude thanks, carry on"`, `"@opus @claude @codex @owner @gpt-6-astra go"`), keep every assertion, and name all six in the commit message. Two more (found by the build, claim 20 amended): `ExchangePolicyTests.cs` R36 `"@opus and also this @sonnet"` becomes `"@opus @sonnet and also this"` (a reply that joins is read like any other post: its recipients are its leading ids, so the join keeps both) and `SpawnerServiceTests.cs` R32 `"@opus a thought for you, and @sonnet too"` becomes `"@opus @sonnet a thought for you both"` (both leading; `Accept` only re-queues ids in the leading set, so an inline-only `@opus` would drop out of `Pending` and fail the test's `TurnsCommitted` assertion; the assertions stay). The `@hub` behaviour is covered by the new `Row43_AC3_hub_gets_its_own_sentence`. No other existing test may change; if a seventh one fails, STOP and report it rather than editing it.

3. **RED then GREEN (service, end to end through the real MCP client and the fake runner).** In a new partial `SpawnerServiceTests.Recipients.cs` (same class, `IAsyncLifetime` already set up in the base file):
   - `Row43_AC4_a_leading_hand_on_spawns_and_an_inline_one_concludes`: `_runner.Handler` plays opus by posting `"@sonnet over to you"` through `PostAs("opus", …)` on the first spec, and `"thanks — @sonnet may know"` on a second scenario; `PostAsOwner("@opus go")`; await `_runner.NextSpecAsync(Wait)` twice in the first scenario (opus then sonnet) and assert the second spec's participant is sonnet; in the second scenario assert `WaitForMessage(m => m.Author == "hub" && m.Body.StartsWith("Exchange concluded: 1 of"))` arrives and `_runner.Count == 1` after it.
   - `Row43_AC2_an_inline_owner_mention_posts_the_reference_note_and_no_spawn`: `PostAsOwner("please ask @opus about X")` → `WaitForMessage(hub note starting "Nobody was addressed: @opus")`, then `_runner.Count == 0`.
   - `Row43_AC3_an_unknown_leading_word_posts_its_note`: `PostAsOwner("@nobody @opus hi")` → the `No participant named @nobody.` note AND one spawn (opus).
   The spec → participant mapping: find how existing facts read the participant off a `ProcessSpec` (grep `ParticipantId\|WorkDir\|Arguments` in `SpawnerServiceTests.cs`).

4. Full `dotnet test` green: Core 285, Hub 870 + 13 policy facts + 3 service facts = 886, Desktop 108. Commit.

### Task 3 — The rule in every contract text (sonnet) — blocked by 1

**Files:** `src/ChopItUp.Hub/Spawning/SpawnPrompt.cs`, `tests/ChopItUp.Hub.Tests/Spawning/golden-prompt-ddfa572.txt`, `tests/ChopItUp.Hub.Tests/Spawning/SpawnPromptTests.cs`, `src/ChopItUp.Hub/Mcp/Participation.cs`, `tests/ChopItUp.Hub.Tests/ParticipationTests.cs`, `README.md`, `tools/skills/roadmap-hub/OVERLAY.md`.

1. **RED.** `ParticipationTests`: add `Row43_AC5_instructions_state_the_leading_rule` asserting `Contains("Address a participant by starting your message with @ and its id")` and `Contains("elsewhere in the text is a reference")`. `SpawnPromptTests`: add `Row43_AC5_the_prompt_states_the_leading_rule` asserting the rendered `GoldenInput()` prompt (its run view has `selfIsConductor: true`, so both the participant sentence and the conductor block render) contains `To hand the turn to a participant, start your reply with @ and its id` and `Put the mention right after the phase tag`. Run → RED.

2. **GREEN, exact replacements:**
   - `SpawnPrompt.cs:113`: replace `Mention a participant with @ and its id to hand it the turn; each mention of a spawnable participant costs one turn of the budget, and only the participants listed above can be mentioned. Never mention yourself. ` with `To hand the turn to a participant, start your reply with @ and its id (several may follow each other at the start, line breaks between them are fine); an @id elsewhere in your reply is a reference and hands nothing on. Each leading mention of a spawnable participant costs one turn of the budget, and only the participants listed above can be mentioned. Never mention yourself. `
   - `SpawnPrompt.cs:294` (the conductor branch's `sb.Append("<kind> is one of plan, build, critique, verify, ping. Never mention yourself. ");`): change that one literal to end `Never mention yourself. Put the mention right after the phase tag (phase: build/<name> @<id> ...); an @id later in the post is a reference and dispatches nobody. ` — line 295's `build needs a mention…` literal stays as it is.
   - Regenerate the golden: a one-off test or `dotnet run`-free snippet is NOT needed — write the capture from the test itself once: temporarily change R14 to `File.WriteAllText(GoldenPath(), SpawnPrompt.Render(GoldenInput(), SpawnLimits.Default))`, run it once, revert the test. Then `git diff --stat tests/ChopItUp.Hub.Tests/Spawning/golden-prompt-ddfa572.txt` must show changes on line 5 and the conductor lines only (AC5); paste `git diff` of that file in the report. `.gitattributes` beside it marks it `-text`; do not touch that.
   - `Participation.cs` Rules: replace `- Address a participant with @ and its id: {MENTIONS}. A message with no mention is for the\n  room.` with `- Address a participant by starting your message with @ and its id: {MENTIONS}. Several ids\n  may follow each other at the start. An @id elsewhere in the text is a reference and reaches\n  nobody; a leading @word that matches nobody gets a hub note. A message with no leading\n  mention is for the room.` (keep the two-space continuation indent the raw string uses).
   - `README.md` "Spawning (M5)" first paragraph: `A spawn starts when an owner message carries `@id` for a roster row that has a model set. A spawned model can hand the turn on the same way, mentioning another spawnable id while turns remain.` → `A spawn starts when an owner message begins with `@id` for a roster row that has a model set (several ids may follow each other at the start; after a `/skill` token they still count). An `@id` anywhere later in the text is a reference and spawns nothing; the hub says so in a note when a message addresses nobody but names someone inline, and when a leading `@word` matches no participant. A spawned model hands the turn on the same way, starting its reply with another spawnable id while turns remain.`
   - `OVERLAY.md` — three edits, each as a NEW line or a rewritten line that carries none of the tokens `Check-Slop.ps1` flags (claim 22: never write `dissect`, `docs/superpowers`, `the owner`, `ruling`, `sign-off` on an added line; lines 12 and 36 as they stand carry two of them, so leave line 12 byte-identical and rewrite line 36 without its path):
     (a) after bullet 3 (line 12) insert a new bullet: `- A mention addresses someone only at the start of a post, right after the phase tag: \`phase: build/<name> @<id> …\`. An @id anywhere later in a post is a reference and dispatches nobody.`
     (b) line 33 (lite path, spawn 3): change `Not met → post \`phase: build/<slug>\` again with the punch list` to `Not met → post \`phase: build/<slug> @<the same worker row>\` again with the punch list`.
     (c) line 36 (full path): rewrite the critique sentence as `Critique \`phase: critique/pass-1 @<judge not the author>\` with \`artifact: <the plan's path>\` on its own line.` and the pass-2 sentence to the same shape (`phase: critique/pass-2 @<another judge, or the same judge told what pass 1 found>`), naming no directory.
     Then run `pwsh -NoProfile -File ~\.claude\skills\roadmap\preflight\Check-Slop.ps1 -RepoPath . -Base main` from the repo root: exit 0 is part of this task's DONE.
   - `docs/verification.md:120` says `post /roadmap @<conductor> in the room (text after the mention is a free-text…` — still true; leave it.
   - The golden file keeps its name (`ddfa572` is the commit the capture was first taken at; `GoldenPath()` hardcodes it and nothing is gained by renaming).

3. Full `dotnet test` green (the golden test R14 is the guard that the regeneration matches the code): Hub 886 + 2 = 888 when built on top of Task 2, or 872 on its own branch. Commit.

### Task 4 — Composer recipient chips and dispatch preview (opus) — blocked by 1

**Files:** `src/ChopItUp.Hub/client/src/participants.ts`, `src/ChopItUp.Hub/client/src/participants.test.ts` (new), `src/ChopItUp.Hub/client/src/RecipientStrip.tsx` (new), `src/ChopItUp.Hub/client/src/RecipientStrip.test.tsx` (new), `src/ChopItUp.Hub/client/src/Composer.tsx`, `src/ChopItUp.Hub/client/src/styles.css`.

1. **RED (grammar mirror).** `participants.test.ts`: `setRoster` with the fixture's roster (kind `model`, host `claude` for opus/sonnet/fable, `codex` for gpt-*, `human` for owner/owner-remote, and `claude`/`codex` rows as kind `model` with `model: null` — mirror `ChopDb.SeedRoster`'s shapes: check `src/ChopItUp.Core/Storage/ChopDb.cs` for the seed rows); read `tests/mention-cases.json` with `readFileSync(new URL('../../../../tests/mention-cases.json', import.meta.url), 'utf8')` (vitest runs under Node; `client/package.json` is `"type": "module"`, so `__dirname` does not exist — the URL form is the only one); `test.each(cases)` asserting `recipientsOf(body)` gives `recipients.map(p => p.id)`, `unknown`, `references.map(p => p.id)`. → RED (no export).

2. **GREEN.** In `participants.ts` add:

```ts
export interface Recipients {
  recipients: Participant[];
  unknown: string[];
  references: Participant[];
}

const SLASH = /^\/([a-z0-9][a-z0-9-]{0,63})(?=[ \t]|\n|$)/;
const PHASE = /^phase:[ \t]+(plan|build|critique|verify|ping)(?:\/[a-z0-9][a-z0-9-]{0,31})?(?=[ \t]|\n|$)/;
// Sticky (`y`) is `\G`'s twin; `u` is what makes `\p{L}`/`\p{N}` work. Classes are spelled out in ASCII
// on purpose: `\w`/`\s` mean different things to V8 and to .NET, and the two readers must agree.
const TOKEN = /[ \t\r\n\f\v,:;]*@([A-Za-z0-9][A-Za-z0-9_.-]*)(?![\p{L}\p{N}_.-])/uy;

/** Twin of `Mentions.Leading` (Core, row 43) — keep the two in step through tests/mention-cases.json.
 *  Only the run of @word tokens at the start of the draft, after an optional `/skill` or `phase:`
 *  token, addresses anyone; a roster id anywhere else is a reference. */
export function recipientsOf(draft: string): Recipients {
  const text = draft.replace(/\r\n/g, '\n');
  let start = 0;
  const slash = SLASH.exec(text);
  const phase = slash ? null : PHASE.exec(text);
  if (slash) start = 1 + slash[1]!.length;
  else if (phase) start = phase[0].length;
  const recipients: Participant[] = [];
  const unknown: string[] = [];
  TOKEN.lastIndex = start;
  for (let m = TOKEN.exec(text); m !== null; m = TOKEN.exec(text)) {
    const word = m[1]!.replace(/[.,:;!?]+$/, '');
    const p = roster.get(word.toLowerCase());
    if (p && p.kind !== 'system') { if (!recipients.includes(p)) recipients.push(p); }
    else if (!unknown.some((u) => u.toLowerCase() === word.toLowerCase())) unknown.push(word);
  }
  const references: Participant[] = [];
  const pattern = referencePattern();
  if (pattern) {
    pattern.lastIndex = 0;
    for (let m = pattern.exec(text); m !== null; m = pattern.exec(text)) {
      const p = roster.get(m[1]!.toLowerCase());
      if (p && !recipients.includes(p) && !references.includes(p)) references.push(p);
    }
  }
  return { recipients, unknown, references };
}
```

   `referencePattern()` is a second module-level regex built in `setRoster` beside `mention`: the same alternation and trailing lookahead, plus the `(?<![\w-])` lookbehind the C# `Find` has (`Mentions.cs:22`; V8 and WebView2 support lookbehind), so `me@opus writes` (no TLD) is a reference for neither reader. `mentionPattern()` itself (consumed by `markdown.ts` for thread highlighting) is NOT changed, so D-e keeps history rendering exactly as it is.

3. **RED (strip).** `RecipientStrip.test.tsx` with `renderToStaticMarkup` (claim 10 — no DOM library, so the strip is a pure component over a `draft` prop, which Composer feeds from its state):
   - leading `@opus @gpt-5.6-sol go` → two `recipient-chip` items with `data-host="claude"` / `"codex"` whose text is the display names, preview text `Sends to Opus and GPT-5.6 Sol.`
   - `@claude look` → one chip with class `passive` and text `Claude · not spawned`, preview `Claude reads this from its own app; the hub spawns nothing.`
   - `@nobody @opus hi` → an `unknown` chip whose text is `@nobody · no such participant` and preview `Sends to Opus. @nobody matches nobody.`
   - `please ask @opus` → no chips, preview `Nobody is addressed. @opus is inside the text, so it is a reference.`
   - `@nob` (a leading word still being typed, nothing after it) → renders nothing; `@nob ` (trailing space) → the unknown chip.
   - `` and `hello` → renders nothing (`''`).

4. **GREEN.** `RecipientStrip.tsx`: `export default function RecipientStrip({ draft }: { draft: string })`. An unknown word that the draft ENDS with (no separator after it: `/@[A-Za-z0-9_.-]+$/.test(draft)` and that word equals the last unknown) is in progress and is dropped from `unknown` before rendering, so the owner typing `@opus` never sees `@o`, `@op`, `@opu` flagged (a `role="status"` region would announce each). Returns `null` when `recipients`, the remaining `unknown` and `references` are all empty; else `<div className="recipient-strip" role="status" aria-label="Recipients">` (a status region: live by default, and a role plus a name is what a UIA query can find — a bare `div` with a class is invisible to it) holding `<ul className="recipient-chips" role="list">` with one `<li role="listitem">` per chip whose VISIBLE TEXT is its accessible name (no `aria-label` on chips: ARIA forbids author names on generic elements and Chromium may drop them): `Opus` (`className="recipient-chip"`, `data-host={hostOf(p.id)}`), `Claude · not spawned` (`recipient-chip passive`; spawnable = `p.kind === 'model' && p.model !== null`), `@nobody · no such participant` (`recipient-chip unknown`); then `<span className="dispatch-preview">{preview}</span>`. The static-markup tests in step 3 also assert `role="status"`, `aria-label="Recipients"`, `role="list"` and the exact chip texts per state, so the UIA helper in Task 5 queries by name = visible text. Preview rules, in order: spawnable recipients → `Sends to A, B and C.` (one → `Sends to A.`); passive-only recipients → `<Names> reads this from its own app; the hub spawns nothing.` (plural `read`); unknown words appended ` @x matches nobody.` each; no recipients but references → `Nobody is addressed. @x is inside the text, so it is a reference.` (several: `@x and @y are inside the text, so they are references.`). Names via `displayName`, except the owner rows: use `p.displayName` directly (the strip names rows, it does not say "You").
   In `Composer.tsx` render `<RecipientStrip draft={draft} />` inside `composer-field`, after the reply chip and before `composer-input`. Styles in `styles.css` after `.reply-chip-*`: `.recipient-strip` (flex, wrap, gap 6px, padding 4px 8px 0, font-size 12px), `.recipient-chip` (reuse the `.mention` colour recipe: `--m` from `data-host`, `color: var(--m)`, `background: color-mix(in srgb, var(--m) 15%, transparent)`, radius 999px, padding 1px 8px, weight 600), `.recipient-chip.passive` (dashed 1px border in `--m`, transparent background), `.recipient-chip.unknown` (`--dim` colour, dashed border), `.dispatch-preview` (`color: var(--dim)`, margin-left auto). `flex-wrap: wrap` on the strip is the whole layout story for many chips; nothing in this task measures it (see "Could not verify").

5. `npx vitest run` green: `Test Files 16 passed`, `Tests 227 passed` (190 + 31 fixture cases + 6 strip tests). `npm run build` (or whatever `client/package.json` names the production build) green with no TS errors. Commit.

### Task 5 — Dry run, verify-skill helper, revert proofs (sonnet) — blocked by 2, 3, 4

**Files:** `tools/Invoke-Row43MentionCheck.ps1` (new), `.claude/skills/verify-chopitup/helpers/Invoke-ComposerCapture.ps1` (new, gitignored — exists only in the `C:\Agent Projects\ChopItUp` clone), `.claude/skills/verify-chopitup/SKILL.md` (gitignored; add the helper to its list).

1. **Script shape:** copy the skeleton of `tools/Invoke-Row42ImportCheck.ps1` (params `-HubExe`, `-DataDir` default `$env:TEMP\chopitup_row43dryrun_<guid>`, `-Port 8808`, `-TimeoutSeconds 30`; `Add-Check`/`Invoke-Api` helpers; `ChopTokenHelpers.ps1` for the scratch owner token; fail fast when the port is taken — row 40's lesson). Build the hub first (`dotnet build … -v minimal`). PATH for the child hub = a scratch `stub\` directory holding `codex.cmd` (the row 34 shape from `docs/LESSONS.md`: `@echo off`, `ping -n 600 127.0.0.1 >nul`, `exit /b 0`) plus `$env:SystemRoot\System32` only, so no real `claude.exe`/`codex.exe` is reachable (`CliResolver` takes `name.exe` anywhere on PATH before a shim and wraps a `.cmd` as `cmd.exe /d /c` — claim 17). Set `$env:PATH` only around the `Start-Process` of the hub: save it first, restore it in a `finally` that also covers every failure path, so the script never leaves the calling shell without `git`/`dotnet`. Start the hub with `--data $DataDir` (scratch only, never the deployed `data\`; `CHOPITUP_DATA` must be unset in the shell that launches it).
2. **Legs** (every wait is a barrier on a hub note or on the exchange snapshot, never a bare sleep; `GET /api/rooms/general/exchange` returns `ExchangeSnapshot` — top-level fields describe the newest open exchange, and `exchanges[]` lists every exchange the room holds with `rootMessageId`, `status` and `inFlight[]` (SpawnerService.cs:26-43) — so every "no exchange rooted here" assertion reads `exchanges[]`, never the top-level fields, because leg 2 leaves Terra's exchange open for the rest of the script):
   - `health.ok` — `/health` 200.
   - `leading.opens-exchange` — POST `@gpt-5.6-terra hello` as owner; poll until `exchanges[]` holds an entry with `rootMessageId` = that message id and `inFlight` containing `gpt-5.6-terra` (the stub holds it open).
   - `inline.no-exchange-reference-note` — POST `please ask @gpt-5.6-sol something`; wait for the hub note starting `Nobody was addressed: @gpt-5.6-sol`; assert no `exchanges[]` entry is rooted at this message and `gpt-5.6-sol` is in no entry's `inFlight`.
   - `unknown.note` — POST `@nobody hello`; wait for `No participant named @nobody.`; assert the note lists `@gpt-5.6-terra` (the roster list) and no entry is rooted here.
   - `bracket.no-exchange` — POST `[trial] @gpt-5.6-luna hi`; wait for its reference note; no entry rooted here.
   - `slash.dispatches` — import a one-line skill into the scratch data dir with `--import-skill` before the hub starts (the scratch hub only; see `tools/Invoke-M11SkillCheck.ps1` for the argument form), then POST `/<name> @gpt-6-astra go` (`gpt-6-astra` is the seed id). Wait for the `Skill /<name> is in force` note and an entry rooted here with `gpt-6-astra` in flight.
   - `stop.cleanup` — POST the room stop endpoint (it already kills spawn trees, `ProcessRunner.cs` `Kill(entireProcessTree: true)`), then end the whole process tree under the hub PID the script started (`taskkill /T /F /PID <hub pid>` — the stub's `cmd.exe`/`ping.exe` grandchildren outlive a plain `Stop-Process` and keep handles under `$DataDir`), wait for the PID to vanish, then remove the scratch data dir the script created (`New-Item` without `-Force` guarded the path at creation). Never touch a PID the script did not start. (The stub's `ping -n 600` outlives nothing: `SpawnLimits.Default.Timeout` is 5 minutes and the runner kills the tree at timeout, so an in-flight leg has a 5-minute window, ample for a 30 s script.)
3. **Negative leg (Row 42 lesson, M24):** with `Mentions.Leading` temporarily rewritten to return `Find(body)` as recipients and no unknown words, rebuild, run the script once: `inline.no-exchange-reference-note`, `bracket.no-exchange` and `unknown.note` must FAIL (an exchange opens for the first two; no unknown note ever appears for the third). Restore, rebuild, run again: all seven PASS. Record both outcomes and the three check names in the script's header comment, as `Invoke-Row42ImportCheck.ps1` does at its lines 25-35.
4. **UIA helper** (AC8): model on `.claude/skills/verify-chopitup/helpers/Invoke-ImportCapture.ps1` (launch → doctor → UIA drive → capture → `Test-CaptureSane.ps1`). Legs: focus the composer textarea, type `@opus @gpt-5.6-sol go` → find the element with `ControlType` status / name `Recipients` (Task 4's `role="status"` + `aria-label`), assert two chip children by their accessible names (`Opus`, `GPT-5.6 Sol`) and the preview text; select-all + type `please ask @opus` → the status text says nobody is addressed; type `@nobody hi` → a chip named `@nobody, no such participant`; clear → no `Recipients` element. One capture per state. `Test-CaptureSane.ps1 <png…>` on the captures (bare paths). Add the helper to the verify skill's SKILL.md list. It needs the interactive desktop: run it; if the session is headless, report `Could not verify: UIA helper needs the interactive desktop` and STOP for the orchestrator (it does not count as done).
5. Commit the script (and nothing under `.claude/`, which is gitignored — confirm with `git status --ignored .claude | head`).

## Verification (Phase B, orchestrator)

Per `references/verification-tiers.md` HIGH: every commit reviewed with the fixed lenses; `Check-BlastRadius.ps1 -Base main` (expected exit 0: no task adds a serialization, delete, migration, hook or secret line) and `Check-Slop.ps1 -Base main` (exit 0; Task 3's overlay edits are written to stay clear of its regexes) on the branch; branch patch to `.scratch/m43-intentional-recipients/diff.patch` and one `dissect-critic` interrogation with the Agent tool's `model` set to the NON-session model (`opus` when Phase B runs on Fable, `fable` when it runs on Opus — never omitted); `mattpocock-skills:code-review` (no agents); full `dotnet test` (Desktop 108, Core 286, Hub 890: the counts are measured at the gate, the plan's earlier pins were one short) + `npx vitest run` (229); `tools\Invoke-Row43MentionCheck.ps1` (7/7, no model calls); the verify-skill helper `Invoke-ComposerCapture.ps1`. The prompt-text change needs no model-judged read: the golden test is the guard. Then PR → checks → squash merge → deploy (`Deploy-ChopItUp.ps1`; stop the shell PID by image path, redeploy, start `ChopItUp.Desktop.exe`, `/health`, `Invoke-Row28SelfCheck.ps1` 3 PASS / 4 SKIP). Post-deploy live proof, one owner message in `general` starting with `@sonnet` and one with the id inline, is optional and spends one Sonnet call; the dry run already proves the dispatch path with no spend.

## Could not verify in this environment

- A real Claude or Codex spawn honouring the new prompt sentence (starting its reply with `@id`): the dry run uses a stub CLI and the service tests a fake runner. Models may still write inline hand-ons; those now conclude the exchange instead of spawning, which is the intended failure direction (no spend). Measure after deploy on the next real exchange.
- The live hub's imported `roadmap` overlay is not re-imported by this row (D-g, an owner step with the hub stopped); until then its re-dispatch template can draw one refusal-and-retry per lite-path punch list.
- The UIA helper needs the interactive desktop; a headless build session reports it unverified and the orchestrator runs it from the desk.
- Chip wrapping at the narrow breakpoint (styles.css:1493) with many recipients is set by `flex-wrap` and measured by nothing here: static markup has no layout, and the capture sanity check only proves a window rendered. A look at a many-chip draft on the phone layout belongs to row 55's narrow-layout work.
- `phase:` prefixed drafts in the composer are mirrored for parity only; the owner never types them.
- The dry run has no `phase:` leg: a stub CLI cannot post as a conductor (claim 11: only owner tokens post over `/api`), so the run flow's leading-mention dispatch rests on the twelve run tests in `SpawnerServiceTests.Runs.cs` that post `phase: build @opus …` through the real conductor path with the fake runner, plus the policy test for the refused inline shape. The first real room run after deploy is the live proof.
- Reader parity outside the fixture's alphabet: `Find` (and the client's `reference` regex) still use `\w`, which is Unicode in .NET and ASCII in V8, so `é@opus` and `@opusé` are a reference to one reader and not the other; the client's `SLASH`/`PHASE` regexes are hand copies of `SlashCommands`/`PhaseTag` (a lone-CR first line diverges); the strip names inline non-spawnable rows the hub never notes. None spawns anything. Boarded as a follow-up row.
- Suite flake under load: the ledger preflight's full-suite recheck, run while two critics were active (Hub run 17 min instead of 8), failed `SpawnerServiceTests.R35_a_conflict_keeps_the_branch_and_names_it` once; alone it passes in 3 s, and the measured baseline run was 870/870. A repeat of that one test in a loaded Phase B is load, not this row; run it alone before treating it as a defect.

## Critique dispositions

**Pass 1 (opus, 2026-09-18, 6.8, FIX-THEN-SHIP; 21 findings).** Empirical result: both readers passed all 21 original fixture cases in both engines.
- F1 token terminator allow-list (`@opus's` → nobody): FIXED — lookahead is now a Unicode negative class, `@opus's` yields `opus`; `(@opus)` and `"@opus"` stay references by decision (D-a); fixture +4 cases.
- F2 reference note fired when a non-spawnable row was addressed: FIXED — gated on `leading.Recipients.Count == 0`; test added.
- F3 note placement (double notes, reply-join, app-backed model posts): FIXED — both notes are decided after the skill switch; human authors only; reply-join and model paths untouched; the vacuous `acceptMentions:false` test replaced by a model-author test and a reply-join test.
- F4 live overlay re-dispatch template (line 33) and critique shape (line 36): FIXED in Task 3; D-g rewritten — re-import is a Class C owner item gating the next room run.
- F5 same-line wording vs cross-line code: FIXED — permissive code kept, D-a and every contract text say "at the start", fixture +2 cross-line cases.
- F6 claim 14 (7 facts → 6): FIXED, and Task 1 / ticket 01 corrected.
- F7 claim 17 (nonexistent row 34 script): FIXED — re-pointed at the LESSONS prose and `CliResolver.cs:31-42`.
- F8 overlay edits would trip Check-Slop: FIXED — edits as new lines without flagged tokens; claim 22 added; Check-Slop exit 0 is part of Task 3's DONE.
- F9 `setRoster` lookbehind: FIXED — dropped; D-e holds.
- F10 `\w`/`\s` engine divergence: FIXED — explicit ASCII classes plus `\p{L}\p{N}` lookahead in both readers; fixture +2 cases (`@opüs`, BOM).
- F11 three existing tests use inline mentions: FIXED — claim 20 names them; Task 2 rewrites the bodies.
- F12 `AppBackedTarget` semantics: FIXED — `ReferencedSpawnable` keeps the whole-body reader for target selection (D-d).
- F13 tier evidence from an unedited file: FIXED — re-run over the 11 edited files (exit 0), HIGH stated on the contract argument alone.
- F14 negative leg is three legs; AC7/Task 5 leg lists: FIXED.
- F15 process tree and `$env:PATH` restore: FIXED — `taskkill /T` on the script's own hub PID, PATH saved/restored in `finally`.
- F16 strip has no UIA role/name: FIXED — `role="status"` + `aria-label="Recipients"` + chip labels, asserted in tests.
- F17 exchange endpoint shape unverified: FIXED — snapshot shape cited (SpawnerService.cs:26-43), negatives read `exchanges[]`.
- F18 unpinned test counts: FIXED — Core 286, Hub 886/888, vitest 227.
- F19 interrogation model pin: FIXED — non-session model, stated for both session models.
- F20 overflow clause unmeasured: FIXED — dropped from AC6, moved to "Could not verify".
- F21 (a) addressable list now model ids only; (b) `@hub` sentence; (c) csproj and the OR removed; (d) `__dirname` fallback removed; (e) line-294 cite corrected; (f) dead check removed; (g) golden name kept, reason stated.

**Pass 2 (fable, 2026-09-18, 7.2, FIX-THEN-SHIP; 10 findings).** Empirical result: all fixture cases pass in both engines (.NET `[regex]`, node 22 with `/uy`); rechecks 2-18 and 20-22 exit 0; V8 accepts `.` and `-` in the classes under `u`.
- F1 fixture count 30 vs pinned 32: FIXED — one case added (31), Core 285, vitest 227 (190 + 31 + 6), Hub unchanged.
- F2 existing test with `@hub` in its body asserts no notes: FIXED — claim 20 now names four tests; Task 2 drops `@hub` from that body; a fifth failure is a STOP.
- F3 preview for `/stop` and unknown-skill drafts mirrors the reader, not the hub: DECLINED with rationale in D-a (the strip does not learn the skill store; the hub's note is the truth for slash drafts).
- F4 readers diverge on `me@opus writes`: FIXED — `referencePattern()` with the lookbehind, `mentionPattern()` untouched (D-e), fixture case added.
- F5 unknown-chip flicker while typing: FIXED — a trailing in-progress word is not flagged; two strip tests added.
- F6 addressable list names rows that never answer: FIXED — spawnable ids only (D-b).
- F7 `aria-label` on generic spans may be dropped: FIXED — chips are `role="listitem"` in a `role="list"` with visible text = accessible name; the UIA helper queries by that text.
- F8 `Msg(…, ReplyToId = 1)`: FIXED — `with { ReplyToId = 1 }`.
- F9 stub lifetime is the 5-minute spawn timeout, not ten minutes: FIXED.
- F10 raw U+FEFF in the fixture block: FIXED — stored as the six-character escape.

**Diff interrogation (opus, 2026-09-18, 7.3, FIX-THEN-SHIP; 2 MAJOR + 10 MINOR over the branch patch).**
- A1 conductor block still shows `phase: <kind>` shapes without the mention and puts `artifact:` between tag and judge: FIXED in Task 6 (shapes carry `@<id>`, artifact line below, golden regenerated, asserted).
- A2 dry run has no `phase:` leg and its negative run shows `slash.dispatches` passes under the reverted reader: DECLINED as a dry-run leg (a stub cannot post as a conductor, claim 11); the run path is bound by the twelve `SpawnerServiceTests.Runs.cs` tests posting `phase: build @opus …` through the real conductor path (all green at 889); recorded under "Could not verify".
- B1 typo'd recipient plus inline id fires two contradictory notes: FIXED in Task 6 (`referenced` also gated on no unknown word; test).
- B2 astral letters: .NET sees a surrogate as Cs and dispatches, V8 sees the code point and does not: FIXED in Task 6 (surrogate range in the .NET lookahead; fixture case).
- B3 `Find`/`reference` use `\w` (Unicode vs ASCII) and the Leading comment overclaims: DEFERRED to the follow-up row (references only; nothing spawns).
- B4 client `SLASH`/`PHASE` are hand copies (lone-CR first line diverges; kinds inlined): DEFERRED to the follow-up row.
- B5 strip names inline non-spawnable rows the hub never notes: DEFERRED to the follow-up row (the fixture's `references` column has no kind filter on the C# side; changing one reader alone breaks parity).
- B6 `@hub` fixture case is vacuous for the TS system-kind branch: FIXED in Task 6 (strip test drafts `@hub`); the roster has no kind column, so the C# side stays covered by `Row43_AC3_hub_gets_its_own_sentence`; chip wording divergence goes with row 41.
- B7 overlay build clause bare and ping sentence says "mentions" for any position: FIXED in Task 6.
- B8 six ledger rechecks invert on the branch: FIXED (paragraph under the ledger names them).
- B9 Hub count pinned one short: FIXED (counts measured at the gate).
- B10 dry run leaks `.stub` and `.skillsrc` dirs and cleans nothing on an early throw: FIXED in Task 6 (one scratch root, removed in the outer `finally`).
