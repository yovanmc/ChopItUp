# Row 40 — Memory editing page

**Goal:** the owner edits `MEMORY.md` and any topic body from the hub UI, and every save lands through the row 23 rewrite trail (a `rewrite` proposal row, a `.rewrite-<id>.bak`, one git commit, a hub note), so an edit made from the hub UI is a commit and never a bypass of the 6,000-character core cap or the 409-while-a-spawn-is-in-flight rule. The owner's own text editor on the files stays the ungated door it has always been (`MemoryStore.cs:18` names it as the expected workflow); this row adds a gated one and, for the phone, the only one.

**Architecture:** three new routes on the existing `/api/memory` group — `GET /topics` (the files and their caps), `GET /topics/{slug}` (the whole file, uncut, with a content hash), `PUT /topics/{slug}` (the save). The save files a `rewrite` proposal authored by the bearer's own participant (`owner` or `owner-remote`, set by `BearerTokenMiddleware`) with `source = "editor"`, then runs the same approval sequence the panel's Approve runs, extracted into one shared method so the two doors cannot drift. Every refusal (spawn in flight, stale hash, no `## ` heading, over cap) happens before the row is created, so a refused save leaves nothing behind. The client gets a `Memory` header button opening a dialog in the Roles-dialog idiom: file picker, textarea, live character count, Save.

**Author model:** Fable 5.1 (session model; planning routes to Fable, so no mismatch).

**Blast radius: HIGH.** The row writes a persisted store (`<data>\memory\*.md` plus a `memory_proposals` row per save) through a delete/replace path (`MemoryStore.Rewrite` copies a `.bak` and moves a temp file over the topic). Evidence, `Check-BlastRadius.ps1 -Files` over the planned list, 2026-09-17:

```
TIER-EVIDENCE HIGH src/ChopItUp.Core/Memory/MemoryStore.cs:158 delete-replace: File.Copy(path, path + ".bak", overwrite: true);
TIER-EVIDENCE HIGH src/ChopItUp.Hub/client/src/App.tsx:639 secrets: const stored = writeOwnerToken(token);
TIER-EVIDENCE HIGH tests/ChopItUp.Hub.Tests/MemoryApiTests.cs:410 delete-replace: File.Delete(Path.Combine(Memory.TopicsDir, "user.md"));
TIER-EVIDENCE: HIGH triggers in 3 of 14 files (delete-replace, secrets)   exit 3
```

Reading the evidence (critique pass 1, finding 8): the load-bearing path is `MemoryStore.Rewrite` at `MemoryStore.cs:196-201` (`File.Copy(path, bak, overwrite: false)` then the temp-file move), which the scanner does not list because it reports one hit per file per category and `:158` (`Supersede`'s shared `.bak` slot, same class, not this row's path) came first. The `MemoryApiTests.cs:410` hit is a test's own `File.Delete`; the `App.tsx:639` hit is token storage this row does not touch. Both are noise; the tier stands on the `Rewrite` path alone. No schema change: `LatestSchemaVersion` stays 12, and `source = "editor"` is a new value in an existing nullable column.

*Written for builder-subagent execution; if something doesn't match, STOP and report rather than guess.*

Lessons consulted (`docs/LESSONS.md`): M10 (Invoke-RestMethod arrays; a narrated tool call is one re-run), M11 (a live check asserts only hub-controlled text), M18 (`dotnet clean` before the `-warnaserror` gate), M23 (hidden-pane clicks; `elementFromPoint` + `.click()` on the real DOM), M24 (a guard test must bind: revert the mechanism and watch it fail), M25 (a recovery arm must be reachable from every state; a server-side rule that gates a button is state, not presentation), Row 14 (branch review maps ACs to code, not to test names; HIGH rows get an implemented-but-wrong pass on main before deploy), Row 26 (`claude auth status` before any spawn; this row spawns nothing), Row 31 (credential-optional check scripts).

## Acceptance

1. WHEN the owner opens the Memory dialog in a room THE SYSTEM SHALL list `MEMORY.md` first and every topic file after it, each with its current character count and its cap (6,000 for the core, 24,000 for a topic), and SHALL load the whole file into the editor uncut, even when the file is already over its cap, with every read LF-normalised (a CRLF file on disk loads with no `\r`, so the hash and the dirty check see the same text the textarea holds).
2. WHEN the owner saves an edit THE SYSTEM SHALL write the file through `MemoryStore.Rewrite` and nothing else: a `memory_proposals` row of kind `rewrite`, status `approved`, `source = "editor"`, authored by the bearer's own participant id, titled `Edit <slug>`; a `<file>.rewrite-<id>.bak` holding the pre-edit text; one commit in `<data>\memory` whose hash the row records; a hub note in the room reading `Memory proposal #<id> approved: edited memory/<path>[, removing '<title>'…] (commit <hash>).`; and the response SHALL carry the text as written, its new hash and the backup's path. A surviving entry (same `## ` heading before and after) keeps its approval-record line even when the owner's text omitted it, so the written text can differ from the submitted text.
3. WHEN a save would produce a file over its cap, or arrives while any spawn is in flight, or names a file whose content changed since it was loaded (hash mismatch), or has an empty body, no `## ` heading or a duplicate heading THE SYSTEM SHALL refuse with 409 (cap, spawn, stale) or 400 (body rules) carrying the hub's sentence, deciding the cap on the composed size at every size above the cap (never on `ValidateRewrite`'s floor), SHALL leave the file, the `.bak` set and `memory_proposals` exactly as they were, and SHALL create no row.
4. WHEN a `PUT` arrives without an owner-class bearer THE SYSTEM SHALL answer 401 (none) or 403 (a non-owner participant) before any handler code runs, exactly as every other guarded `/api` write.
5. WHEN a model's `rewrite` proposal is approved from the panel THE SYSTEM SHALL post the same note it posts today (`… approved: consolidated memory/…`), so the wording branch for editor saves changes nothing for consolidations.
6. WHEN the room has a spawn in flight THE SYSTEM SHALL disable Save in the dialog and show exactly `A spawn is running; save when the exchange has finished.`, so the 409 is explained before it can happen.
7. WHEN the dialog asks for a count THE SYSTEM SHALL answer with the composed size the cap is enforced on (`POST /api/memory/topics/{slug}/preview`), and the dialog's count line and Save gate SHALL use that number whenever the call succeeds, falling back to the raw length only while it has not.

## Rulings (made on the owner's behalf; each reversible)

- R1 **Save is one action.** The dialog's Save files the proposal and approves it in one server call under the same `Decisions` semaphore the panel uses. The owner does not review their own diff on a card. Revert: drop the `ApproveCore` call from `PutTopic` and let the row stay pending for the panel.
- R2 **A refused save creates no row.** The cap pre-check runs with a 12-digit placeholder proposal id in the provenance string (never shorter than a real id, and the stamp is fixed-width), so a text that passes it also passes the re-check inside `ApproveCore`. The false-refusal band this buys is at most 11 characters wide; leave it.
- R3 **Stale-edit guard.** `GET` returns a SHA-256 of the file text; `PUT` must echo it, else 409 `The file changed since you opened it. Reload it and apply your edit again.` This is what stops an editor save from silently clobbering an approval that landed in between.
- R4 **No new topics from the editor** (a topic is born by an approved `propose_memory` append or a hand-dropped file). Revert: a `POST /api/memory/topics` that seeds `# <slug>\n` and one heading.
- R5 **No hub `Proposed` note and no review flags** for an editor save: the row is never pending for anyone, and the author is the reviewer. The `Approved` note is the trail.
- R6 **The trail's own rules stand:** a body needs at least one `## ` heading (`ValidateRewrite`), so a core rewritten as pure prose is refused; the dialog says so in its hint. Not relaxed, because `MemoryProposalStore.Create` enforces the same rule for every rewrite row (`MemoryProposalStore.cs:35-39`). A fresh install's seed core has no `## ` entry, so the first save from the editor must add one (typing `## Standing rules` above the prose is enough). Say "relax" and the plan adds an opt-out parameter to `ValidateRewrite` instead. What IS fixed in Core: the floor inside `ValidateRewrite` charged an H1 and a marker line the body already carried, so it could refuse (400) a text whose composed size the cap check (409) had accepted; task 1 makes the floor honest and orders the cap check first.
- R7 **An owner edit while a model's consolidation of the same topic is pending is allowed.** The pending card's diff is recomputed against the current file at list time (`MapForList`), so the owner sees their own lines among what the consolidation would remove. That is a display, not a guard: approving the older consolidation replaces the edited file wholesale, with the `.bak` and the commit as the way back. A base hash on model rewrites is declined for this row (it changes `propose_rewrite`'s contract).
- R8 **The two `GET` routes stay unauthenticated** like every `/api` GET (row 28 AC2): the same text is already readable through `recall` by any bearer; the only extra exposure is the tail of a file past its cap. Revert: a route-scoped bearer check in the handlers.
- R9 **The dialog opens from the room header** (next to `Roles`); the proposal row and the note land in that room. The button reads `Edit memory`: an eighth header button reading `Memory` beside `Import memory` is ambiguous.
- R10 **Rollback is by file and by git, not by button.** The save response and the dialog's status line name the `.bak`; `docs/verification.md` gains the restore recipe (stop the hub, copy `<file>.rewrite-<id>.bak` over the file, restart; or `git -C <data>\memory revert <hash>` with the hub stopped). `.bak` files are kept (gitignored, one per save, never reused); pruning is a delete path and a row of its own.
- R11 **One hub note per save** is the trail (D15: the note is what the thread shows). Twenty edits are twenty notes; accepted.
- R12 **Reads are LF-normalised on the hub** (`GET`, `preview`, `PUT`): the store writes LF, browsers hand textareas LF, and a CRLF file dropped in by hand would otherwise read as dirty from the first render.

## Could not verify in this environment

- The UIA interactive gate and the capture helper need the interactive desktop; the orchestrator runs them in Phase B and stamps the result. If the session is headless they become a `LEAD:` line in the row's Notes, never a claim.
- The deployed hub is never touched by any task here; the deploy step (Phase B step 6) stops the Desktop shell PID, redeploys, restarts the Desktop exe (memory: row 12).
- Real Claude/Codex spawns are not exercised (no leg of this row needs a model); the spawn-in-flight refusal is proven with `FakeProcessRunner` in the unit test only.
- The .NET baseline ran green once (1,208) and, re-run by the ledger checker while two critics loaded the machine (15 min instead of 11), failed exactly one test, `SpawnerServiceTests.R36_a_reopened_exchange_in_a_directory_room_leases_its_worktree_again_after_the_merge`, which then passed alone in 6 s. Treat it as the row 36 flake class (LESSONS: rerun before diagnosing), not a regression; a builder that sees it red re-runs the one test before anything else.
- The dialog's `locked` wiring (`(exchange?.inFlight.length ?? 0) > 0`) is asserted as a prop in vitest and never produced live; the identical wiring already ships for `MemoryPanel` and `SkillPanel` (`App.tsx:747,753`). Accepted.
- UIA typing into the WebView2-hosted React textarea is proven only by the API content check the capture helper runs after Save; the keystroke idiom is cloned from `tools\Invoke-Row12ShellCheck.ps1:514-574` (real `SendKeys`, because `ValuePattern.SetValue` does not fire React's `onChange`).

## Claim ledger

| # | Claim | Verified at (commit) | Recheck (pwsh, exit 0 = holds) |
|---|-------|----------------------|--------------------------------|
| 1 | Baseline: 1,208 .NET tests green (108 + 250 + 850, measured this session) | da85a25 | `pwsh -NoProfile -Command "dotnet test 'C:\Agent Projects\ChopItUp\ChopItUp.slnx' -c Debug --nologo -v minimal; exit $LASTEXITCODE"` |
| 2 | Baseline: vitest 162 tests in 12 files green (baseline-only: the count rises at tasks 2 and 3) | da85a25 | `pwsh -NoProfile -Command "Set-Location 'C:\Agent Projects\ChopItUp\src\ChopItUp.Hub\client'; npm test 2>&1 \| Select-String 'Tests\s+162 passed' \| Measure-Object \| ForEach-Object { exit [int](1 - $_.Count) }"` |
| 3 | `MapMemoryApi` maps exactly five routes today (`MemoryApi.cs:28-33`; baseline-only: nine after task 1) | da85a25 | `pwsh -NoProfile -Command "$n = (Select-String -Path 'C:\Agent Projects\ChopItUp\src\ChopItUp.Hub\Web\MemoryApi.cs' -Pattern '^\s+api\.Map(Get\|Post\|Delete\|Put)\(' \| Measure-Object).Count; $n; exit [int]($n -ne 5)"` |
| 4 | `BearerTokenMiddleware.ParticipantKey = "chopitup.participant"` is set in `HttpContext.Items` on every guarded write | da85a25 | `pwsh -NoProfile -Command "$n = (Select-String -Path 'C:\Agent Projects\ChopItUp\src\ChopItUp.Hub\Security\BearerTokenMiddleware.cs' -Pattern 'ParticipantKey' \| Measure-Object).Count; $n; exit [int]($n -lt 2)"` |
| 5 | `MemoryStore.ReadTopic(string topic, int max)` exists; `ReadTopic("core")` is uncut | da85a25 | `pwsh -NoProfile -Command "$n = (Select-String -Path 'C:\Agent Projects\ChopItUp\src\ChopItUp.Core\Memory\MemoryStore.cs' -Pattern 'public MemoryText\? ReadTopic\(string topic, int max\)' \| Measure-Object).Count; $n; exit [int]($n -ne 1)"` |
| 6 | `MemoryStore.ProjectedRewriteChars(topic, body, provenance)` and `ValidateRewrite(topic, body)` are public | da85a25 | `pwsh -NoProfile -Command "$n = (Select-String -Path 'C:\Agent Projects\ChopItUp\src\ChopItUp.Core\Memory\MemoryStore.cs' -Pattern 'public (int ProjectedRewriteChars\|static void ValidateRewrite)\(' \| Measure-Object).Count; $n; exit [int]($n -ne 2)"` |
| 7 | `MemoryProposalStore.Create(roomId, authorId, topic, title, body, source, replaces, flags, kind)` with `KindRewrite = "rewrite"` | da85a25 | `pwsh -NoProfile -Command "$n = (Select-String -Path 'C:\Agent Projects\ChopItUp\src\ChopItUp.Core\Storage\MemoryProposalStore.cs' -Pattern 'public MemoryProposal Create\(string roomId, string authorId, string topic, string title, string body, string\? source, string\? replaces = null, string\? flags = null, string kind = KindAppend\)' \| Measure-Object).Count; $n; exit [int]($n -ne 1)"` |
| 8 | `HubNotes.Approved(MemoryProposal, IReadOnlyList<string>?)` writes the literal `consolidated memory/` for rewrites (`HubNotes.cs:49-58`; baseline-only: task 1b turns it into `{verb} memory/`) | da85a25 | `pwsh -NoProfile -Command "$n = (Select-String -Path 'C:\Agent Projects\ChopItUp\src\ChopItUp.Hub\Memory\HubNotes.cs' -Pattern 'consolidated memory/' \| Measure-Object).Count; $n; exit [int]($n -ne 1)"` |
| 9 | `HubTestHost.StartAsync(dir, processRunner:, limits:)`, `AuthorizeAs`, `TokenFor("opus")` (403 idiom in `SkillsApiAuthTests.cs:104-112`) | da85a25 | `pwsh -NoProfile -Command "$n = (Select-String -Path 'C:\Agent Projects\ChopItUp\tests\ChopItUp.Hub.Tests\SkillsApiAuthTests.cs' -Pattern 'TokenFor\(\"opus\"\)' \| Measure-Object).Count; $n; exit [int]($n -lt 1)"` |
| 10 | In-flight idiom: `FakeProcessRunner` + `runner.NextSpecAsync(Wait)` + `SpawnLimits Fast` (`MemoryApiGuardTests.cs:16-17,40-53`) | da85a25 | `pwsh -NoProfile -Command "$n = (Select-String -Path 'C:\Agent Projects\ChopItUp\tests\ChopItUp.Hub.Tests\MemoryApiGuardTests.cs' -Pattern 'NextSpecAsync\(Wait\)' \| Measure-Object).Count; $n; exit [int]($n -lt 1)"` |
| 11 | `api.ts` `write()` attaches the stored owner token to every non-GET (`api.ts:75-89`) | da85a25 | `pwsh -NoProfile -Command "$n = (Select-String -Path 'C:\Agent Projects\ChopItUp\src\ChopItUp.Hub\client\src\api.ts' -Pattern '^function write\(url: string, init: RequestInit\)' \| Measure-Object).Count; $n; exit [int]($n -ne 1)"` |
| 12 | `RoomHeader` takes `onRoles` and renders a `Roles` button (`RoomHeader.tsx:15,55-62`); `App.tsx:804` mounts `RolesDialog` on `rolesOpen` | da85a25 | `pwsh -NoProfile -Command "$n = (Select-String -Path 'C:\Agent Projects\ChopItUp\src\ChopItUp.Hub\client\src\App.tsx' -Pattern 'rolesOpen && activeRoom && <RolesDialog' \| Measure-Object).Count; $n; exit [int]($n -ne 1)"` |
| 13 | `MemoryPanel.tsx:85` renders `imported from {p.source}` for any non-null source | da85a25 | `pwsh -NoProfile -Command "$n = (Select-String -Path 'C:\Agent Projects\ChopItUp\src\ChopItUp.Hub\client\src\MemoryPanel.tsx' -Pattern 'imported from \{p\.source\}' \| Measure-Object).Count; $n; exit [int]($n -ne 1)"` |
| 14 | The `AnySpawnInFlight` 409 appears in four handlers today (Approve/Reject/Import/Discard; baseline-only: five after task 1, `PutTopic`'s; `ApproveCore` keeps none because `Approve` checks above the extraction point) | da85a25 | `pwsh -NoProfile -Command "$n = (Select-String -Path 'C:\Agent Projects\ChopItUp\src\ChopItUp.Hub\Web\MemoryApi.cs' -Pattern 'AnySpawnInFlight\) return Results\.Conflict' \| Measure-Object).Count; $n; exit [int]($n -ne 4)"` |
| 15 | Client tests use `renderToStaticMarkup` + `vi.stubGlobal('fetch', …)`; no jsdom | da85a25 | `pwsh -NoProfile -Command "$n = (Select-String -Path 'C:\Agent Projects\ChopItUp\src\ChopItUp.Hub\client\src\RolesDialog.test.tsx' -Pattern 'renderToStaticMarkup' \| Measure-Object).Count; $n; exit [int]($n -lt 1)"` |
| 16 | `ChopDb.LatestSchemaVersion = 12`; this row adds no migration | da85a25 | `pwsh -NoProfile -Command "$n = (Select-String -Path 'C:\Agent Projects\ChopItUp\src\ChopItUp.Core\Storage\ChopDb.cs' -Pattern 'LatestSchemaVersion = 12;' \| Measure-Object).Count; $n; exit [int]($n -ne 1)"` |
| 17 | `Timestamps.Stamp` is the round-trip `"o"` format (fixed width), so a placeholder provenance differs from the real one only in the id digits | da85a25 | `pwsh -NoProfile -Command "$n = (Select-String -Path 'C:\Agent Projects\ChopItUp\src\ChopItUp.Core\Storage\Timestamps.cs' -Pattern 'ToString\(\"o\"' \| Measure-Object).Count; $n; exit [int]($n -ne 1)"` |
| 18 | `tools\ChopTokenHelpers.ps1` exports `Initialize-ChopScratchTokens -DataDir -ParticipantIds` and `New-ChopBearerHeaders -Token` | da85a25 | `pwsh -NoProfile -Command "$n = (Select-String -Path 'C:\Agent Projects\ChopItUp\tools\ChopTokenHelpers.ps1' -Pattern '^function (Initialize-ChopScratchTokens\|New-ChopBearerHeaders)' \| Measure-Object).Count; $n; exit [int]($n -ne 2)"` |
| 19 | Verify skill exists at `.claude\skills\verify-chopitup\` with `helpers\Invoke-RolesCapture.ps1` to clone | da85a25 | `pwsh -NoProfile -Command "exit [int](-not (Test-Path 'C:\Agent Projects\ChopItUp\.claude\skills\verify-chopitup\helpers\Invoke-RolesCapture.ps1'))"` |
| 20 | `Convert.ToHexString` + `SHA256.HashData(byte[])` compile on net10.0 (in use at `TokenStore.cs:220`, `ExportManifest.cs:110,130`; verified by critique pass 1) | da85a25 | `pwsh -NoProfile -Command "$n = (Select-String -Path 'C:\Agent Projects\ChopItUp\src\ChopItUp.Hub\Security\TokenStore.cs' -Pattern 'Convert\.ToHexString\(SHA256\.HashData' \| Measure-Object).Count; $n; exit [int]($n -lt 1)"` |
| 21 | The textarea DOM value is LF-normalised by the browser; the hub therefore LF-normalises every read and hash (R12), so hash, dirty check and textarea agree (critique pass 1, finding 6) | — | — |
| 22 | `MemoryStore.MaxBodyChars = 4_000` bounds `Append`, so over-cap fixtures are written to disk directly, never through `Append` | da85a25 | `pwsh -NoProfile -Command "$n = (Select-String -Path 'C:\Agent Projects\ChopItUp\src\ChopItUp.Core\Memory\MemoryStore.cs' -Pattern 'MaxBodyChars = 4_000;' \| Measure-Object).Count; $n; exit [int]($n -ne 1)"` |
| 23 | `ValidateRewrite`'s floor (`MemoryStore.cs:253-255`) adds an H1 and a marker allowance to the raw length whether or not the body already carries them (baseline-only: task 1a replaces the line) | da85a25 | `pwsh -NoProfile -Command "$n = (Select-String -Path 'C:\Agent Projects\ChopItUp\src\ChopItUp.Core\Memory\MemoryStore.cs' -Pattern 'var floor = normalized\.Trim\(\)\.Length \+ topic!\.Length \+ 3' \| Measure-Object).Count; $n; exit [int]($n -ne 1)"` |
| 24 | `tools\Invoke-Row12ShellCheck.ps1:514-574` types into the controlled React textarea with `SendKeys::SendWait` after focusing it by UIA | da85a25 | `pwsh -NoProfile -Command "$n = (Select-String -Path 'C:\Agent Projects\ChopItUp\tools\Invoke-Row12ShellCheck.ps1' -Pattern 'SendKeys\]::SendWait' \| Measure-Object).Count; $n; exit [int]($n -lt 3)"` |
| 25 | `MemoryApiTests.cs:327` is the only test asserting `consolidated memory/`, and its proposal has `source: null` (the wording branch cannot break it) | da85a25 | `pwsh -NoProfile -Command "$n = (Get-ChildItem 'C:\Agent Projects\ChopItUp\tests' -Recurse -Filter *.cs \| Select-String -Pattern 'consolidated memory/' \| Measure-Object).Count; $n; exit [int]($n -ne 1)"` |

Baseline count line (filled by the orchestrator from this session's run): **.NET: 1,208 passed (Desktop 108, Core 250, Hub 850; Hub suite ≈ 11 min)** · **vitest: 162 passed / 12 files**.

## Tasks

Chain: `01 → 02 → 03 → 05`, with `04` needing only `01`. Dispatched one at a time in the order 1, 2, 3, 4, 5 (no worktree parallelism from this cwd). Each task is one commit on branch `row40-memory-editor`. Builders: tasks 1, 2, 4, 5 → `sonnet`; task 3 → `opus` (owner-visible). TDD: RED executed and quoted in the builder report before GREEN.

### Task 1 — Hub: list, read, preview and save routes; honest floor; shared approval core (sonnet)

Files: `src/ChopItUp.Core/Memory/MemoryStore.cs`, `src/ChopItUp.Core/Storage/MemoryProposalStore.cs`, `tests/ChopItUp.Core.Tests/Memory/MemoryStoreTests.cs`, `src/ChopItUp.Hub/Memory/HubNotes.cs`, `src/ChopItUp.Hub/Web/MemoryApi.cs`, new `tests/ChopItUp.Hub.Tests/MemoryEditApiTests.cs`.

**1a. Core.** In `MemoryProposalStore`, after `KindRewrite`:

```csharp
    /// <summary>Row 40: the <see cref="MemoryProposal.Source"/> of a rewrite the owner saved from the
    /// editor. Imports use <c>&lt;vendor&gt;:&lt;path&gt;</c>; room proposals have none.</summary>
    public const string SourceEditor = "editor";
```

In `MemoryStore.ValidateRewrite`, replace the two floor lines (`var cap = …; var floor = …;`) with:

```csharp
        var cap = topic == CoreTopic ? CoreChars : TopicChars;
        // An honest floor (row 40): a body that already carries its H1, or the marker line ComposeRewrite
        // drops and re-inserts, is not charged for them a second time. Without this the floor could
        // exceed the composed size and refuse a text the authoritative cap check had accepted.
        var lines = normalized.Trim('\n').Split('\n');
        var hasH1 = lines.Length > 0 && lines[0].StartsWith("# ", StringComparison.Ordinal);
        var marker = lines.Length > 1 && lines[1].StartsWith(RewrittenPrefix, StringComparison.Ordinal) ? lines[1].Length + 1 : 0;
        var floor = normalized.Trim().Length - marker + (hasH1 ? 0 : topic!.Length + 3) + RewrittenPrefix.Length + CommentClose.Length + 1;
```

Core test, appended to `MemoryStoreTests.cs` (RED at HEAD: the old floor throws):

```csharp
    [Fact]
    public void R40_ValidateRewrite_floor_does_not_charge_an_H1_or_a_marker_the_body_already_carries()
    {
        var marker = "<!-- rewritten: approved 2026-01-01T00:00:00.0000000+00:00 proposal 1 by owner in room general -->";
        var n = 5_990 - (7 + marker.Length + 2 + 9 + 1);
        var body = "# core\n" + marker + "\n\n## Rules\n" + new string('r', n) + "\n";
        Assert.Equal(5_990, body.Length);
        MemoryStore.ValidateRewrite("core", body);   // composed ≈ 5,990: under the cap, so no throw
        Assert.Throws<ArgumentException>(() => MemoryStore.ValidateRewrite("core", "# core\n\n## Rules\n" + new string('r', 6_000) + "\n"));
    }
```

**1b. Note wording.** In `HubNotes.Approved`, inside the `KindRewrite` branch, replace the return with:

```csharp
            var verb = p.Source == MemoryProposalStore.SourceEditor ? "edited" : "consolidated";
            return $"{ProposalPrefix}{p.Id} approved: {verb} memory/{p.WrittenTo}{removed}{commit}";
```

and in `HubNotes.Refused`'s `KindRewrite` branch make the closing sentence `Trim it and save again.` when `p.Source == MemoryProposalStore.SourceEditor` (the Retry path of an editor row must not say "propose"), keeping `Trim it and propose the rewrite again.` otherwise.

**1c. MemoryApi.** Add `using System.Security.Cryptography;`, `using System.Text;`, `using ChopItUp.Hub.Security;`. Map four routes after the existing five:

```csharp
        api.MapGet("/topics", ListTopics);
        api.MapGet("/topics/{slug}", GetTopic);
        api.MapPost("/topics/{slug}/preview", PreviewTopic);
        api.MapPut("/topics/{slug}", PutTopic);
```

Constants and helpers (next to `SpawnRunning`):

```csharp
    public const string StaleEdit = "The file changed since you opened it. Reload it and apply your edit again.";
    private const string BadSlug = "topic must be a slug: lowercase letters, digits and hyphens.";

    private static string PathOf(string slug) => slug == MemoryStore.CoreTopic ? MemoryStore.CoreFileName : $"{MemoryStore.TopicsDirName}/{slug}.md";
    private static int CapOf(string slug) => slug == MemoryStore.CoreTopic ? MemoryStore.CoreChars : MemoryStore.TopicChars;
    /// <summary>CRLF and lone CR both become LF: the store writes LF and a browser textarea holds LF.</summary>
    private static string Lf(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    /// <summary>Row 40: the stale-edit token, over LF-normalised text so a CRLF file on disk, the hub's
    /// reply and the browser's textarea all hash alike. Never over what a browser echoed back.</summary>
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Lf(text)))).ToLowerInvariant();
    /// <summary>The provenance <see cref="ApproveCore"/> will compose, with the one unknown — the row id —
    /// as twelve nines: never fewer digits than a real id, so a size that passes on this string passes
    /// on the real one. <c>MemoryTools.ProposeRewrite</c> asks the same question with <c>proposal 0</c>,
    /// which is looser and relies on the approval re-check; this one must be strict because a refused
    /// save must leave no row.</summary>
    private static string Provisional(string author, string roomId) =>
        $"approved {Timestamps.Stamp(DateTimeOffset.UtcNow)} proposal {new string('9', 12)} by {author} in room {roomId}";
    private static object FileRow(MemoryStore memory, string slug) =>
        new { slug, path = PathOf(slug), chars = memory.ReadTopic(slug, int.MaxValue) is { } t ? Lf(t.Text).Length : 0, cap = CapOf(slug) };
    private static object FileBody(string slug, string text)
    {
        var lf = Lf(text);
        return new { slug, path = PathOf(slug), text = lf, chars = lf.Length, cap = CapOf(slug), hash = Hash(lf) };
    }
```

Handlers:

```csharp
    /// <summary>Row 40: the editor's file list — the core first, then every topic in slug order, each
    /// with its live character count and the cap a save must stay under.</summary>
    private static IResult ListTopics(MemoryStore memory)
    {
        var rows = new List<object> { FileRow(memory, MemoryStore.CoreTopic) };
        rows.AddRange(memory.ListTopics().Select(t => FileRow(memory, t.Slug)));
        return Results.Json(rows);
    }

    /// <summary>Row 40: the whole file, uncut — the editor is the one reader that must see past a cap,
    /// because shrinking an over-cap topic is the only thing propose_rewrite cannot do (it refuses a
    /// truncated read).</summary>
    private static IResult GetTopic(string slug, MemoryStore memory)
    {
        if (!MemoryStore.TopicSlug.IsMatch(slug)) return Results.BadRequest(new { error = BadSlug });
        var current = memory.ReadTopic(slug, int.MaxValue);
        return current is null ? Results.NotFound(new { error = $"No topic '{slug}'." }) : Results.Json(FileBody(slug, current.Text));
    }

    /// <summary>Row 40: the size the cap is enforced on — the composed file, marker line and carried
    /// provenance included — so the dialog's count is the hub's count, not the textarea's. Reads
    /// nothing but the topic file; writes nothing.</summary>
    private static IResult PreviewTopic(string slug, PreviewBody body, HttpContext http, MemoryStore memory)
    {
        if (!MemoryStore.TopicSlug.IsMatch(slug)) return Results.BadRequest(new { error = BadSlug });
        var author = http.Items[BearerTokenMiddleware.ParticipantKey] as string ?? ChopDb.OwnerParticipantId;
        var chars = memory.ProjectedRewriteChars(slug, Lf(body.Text ?? "").Trim(), Provisional(author, body.RoomId ?? ""));
        var cap = CapOf(slug);
        return Results.Json(new { slug, chars, cap, over = chars > cap });
    }

    /// <summary>Row 40: a hand edit, saved as an approved <c>rewrite</c> authored by the bearer's own
    /// participant (the middleware set it) with <see cref="MemoryProposalStore.SourceEditor"/>. Every
    /// refusal runs BEFORE the row is created — spawn in flight, stale hash, empty body, cap, body rules —
    /// so a refused save leaves no row; the cap is decided on the composed size before ValidateRewrite
    /// runs, so an over-cap text is always a 409 and never the floor's 400. Then <see cref="ApproveCore"/>
    /// does exactly what the panel's Approve does. One action, one commit, under the same semaphore.</summary>
    private static async Task<IResult> PutTopic(string slug, EditBody body, HttpContext http, MemoryProposalStore proposals, MemoryStore memory, MemoryGit git, MessageStore store, MessageSignal signal, SpawnerService spawner)
    {
        if (!MemoryStore.TopicSlug.IsMatch(slug)) return Results.BadRequest(new { error = BadSlug });
        if (string.IsNullOrWhiteSpace(body.RoomId) || !store.RoomExists(body.RoomId)) return Results.NotFound(new { error = $"Unknown room '{body.RoomId}'." });
        // The middleware sets this on every guarded write; the check stays so a route mapped outside the
        // guard could never author a row as nobody.
        if (http.Items[BearerTokenMiddleware.ParticipantKey] is not string author) return Results.Unauthorized();
        // Exactly the string the row will hold: Create stores body.Trim(), and ApproveCore re-checks the
        // stored text, so every check here runs on the trimmed text or a leading-space heading could pass
        // the pre-checks unrecognised and then be refused after the INSERT.
        var text = Lf(body.Text ?? "").Trim();
        await Decisions.WaitAsync();
        try
        {
            if (spawner.AnySpawnInFlight) return Results.Conflict(new { error = SpawnRunning });
            var current = memory.ReadTopic(slug, int.MaxValue);
            if (current is null) return Results.NotFound(new { error = $"No topic '{slug}' to edit." });
            var currentHash = Hash(current.Text);
            if (!string.Equals(currentHash, body.BaseHash?.Trim(), StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { error = StaleEdit, hash = currentHash });
            if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest(new { error = "body is empty." });
            var cap = CapOf(slug);
            var projected = memory.ProjectedRewriteChars(slug, text, Provisional(author, body.RoomId));
            if (projected > cap)
            {
                var where = slug == MemoryStore.CoreTopic ? "the core" : $"topic '{slug}'";
                return Results.Conflict(new { error = $"The edit of {where} would be {projected} characters, over the {cap} cap. Trim it and save again.", chars = projected, current = Lf(current.Text).Length, cap });
            }
            try { MemoryStore.ValidateRewrite(slug, text); }
            catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
            MemoryProposal proposal;
            try { proposal = proposals.Create(body.RoomId, author, slug, $"Edit {slug}", text, MemoryProposalStore.SourceEditor, null, null, MemoryProposalStore.KindRewrite); }
            catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
            var (refusal, decided) = await ApproveCore(proposal, proposals, memory, git, store, signal);
            if (refusal is not null) return refusal;
            var written = memory.ReadTopic(slug, int.MaxValue)!.Text;
            return Results.Json(new
            {
                proposal = Map(decided!),
                slug, path = PathOf(slug), text = Lf(written), chars = Lf(written).Length, cap, hash = Hash(written),
                backup = $"{PathOf(slug)}.rewrite-{decided!.Id}.bak",
            });
        }
        finally { Decisions.Release(); }
    }

    internal sealed record EditBody(string? RoomId, string? Text, string? BaseHash);
    internal sealed record PreviewBody(string? RoomId, string? Text);
```

**1d. Extract `ApproveCore`.** In `Approve`, keep everything through the `already {p.Status}` 409, then replace the rest of the `try` body with:

```csharp
            var (refusal, decided) = await ApproveCore(p, proposals, memory, git, store, signal);
            return refusal ?? Results.Json(Map(decided!));
```

and move the moved code into:

```csharp
    /// <summary>Everything an approval does for a row that is pending or approved-but-unwritten —
    /// pre-write checks, mark, write, commit, record, note — shared by the panel's Approve and the
    /// editor's save (row 40, which hands it the row it just created) so the two doors cannot drift.
    /// Runs under <see cref="Decisions"/>, which the caller holds. Returns the refusal, or null and the
    /// decided row.</summary>
    private static async Task<(IResult? Refusal, MemoryProposal? Decided)> ApproveCore(MemoryProposal p, MemoryProposalStore proposals, MemoryStore memory, MemoryGit git, MessageStore store, MessageSignal signal)
    {
        var provenance = $"approved {Timestamps.Stamp(DateTimeOffset.UtcNow)} proposal {p.Id} by {p.AuthorId} in room {p.RoomId}";
        // … the existing block from the row-23 rewrite checks through the note, unchanged except:
        //   every `return Results.Conflict(…)` becomes `return (Results.Conflict(…), null);`
        //   `proposals.Decide(id, …)` / `proposals.RecordWrite(id, …)` use `p.Id`
        //   the final line is `return (null, decided);`
    }
```

Comments inside the moved block stay as they are (they describe the code, not this session).

**1e. Tests — `tests/ChopItUp.Hub.Tests/MemoryEditApiTests.cs`** (RED first: the routes 404/405 until 1c lands):

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChopItUp.Core.Memory;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Spawning;
using ChopItUp.Hub.Tests.Spawning;
using ChopItUp.Hub.Web;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests;

/// <summary>Row 40: the editor's routes. Every save goes through the rewrite trail; every refusal leaves
/// no row; the cap is a 409 at every size above it.</summary>
public sealed class MemoryEditApiTests : IAsyncLifetime
{
    private static readonly SpawnLimits Fast = new(Budget: 4, Debounce: TimeSpan.FromMilliseconds(150), MinSpacing: TimeSpan.Zero, Timeout: TimeSpan.FromSeconds(30), TranscriptMessages: 60, TranscriptChars: 24_000);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private const string SeedProvenance = "approved 2026-01-01T00:00:00.0000000+00:00 proposal 0 by opus in room general";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_memedit_" + Guid.NewGuid().ToString("N"));
    private HubTestHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await HubTestHost.StartAsync(_dir);
        _host.AuthorizeAs(ChopDb.OwnerParticipantId);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private MemoryStore Memory => _host.Services.GetRequiredService<MemoryStore>();
    private MemoryProposalStore Proposals => _host.Services.GetRequiredService<MemoryProposalStore>();

    private static string Sha(string text) => MemoryApi.Hash(text);

    private async Task<JsonElement> GetJson(string path)
    {
        var r = await _host.Client.GetAsync(path);
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private Task<HttpResponseMessage> Put(string slug, string text, string baseHash, string room = "general") =>
        _host.Client.PutAsJsonAsync($"api/memory/topics/{slug}", new { roomId = room, text, baseHash });

    /// <summary>One appended entry through the store's own door: an H1, a heading, a provenance line.</summary>
    private string Seed(string topic, string body)
    {
        Memory.Append(topic, "Seed", body, SeedProvenance);
        return Memory.ReadTopic(topic, int.MaxValue)!.Text;
    }

    /// <summary>A file written straight to disk: the way past Append's 4,000-character body rule, and the
    /// way to fabricate CRLF.</summary>
    private string Drop(string topic, string text)
    {
        Memory.EnsureLayout();
        File.WriteAllText(Path.Combine(Memory.TopicsDir, topic + ".md"), text);
        return text;
    }

    private IEnumerable<string> Backups() =>
        Directory.GetFiles(Memory.Root, "*.bak").Concat(Directory.GetFiles(Memory.TopicsDir, "*.bak"));

    private async Task<List<(string Author, string Body)>> Messages()
    {
        using var doc = JsonDocument.Parse(await _host.Client.GetStringAsync("api/rooms/general/messages?afterId=0&limit=200"));
        return doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => (m.GetProperty("authorId").GetString()!, m.GetProperty("body").GetString()!)).ToList();
    }

    [Fact]
    public async Task AC1_list_puts_the_core_first_with_counts_and_caps_and_get_returns_the_whole_file_uncut()
    {
        Seed("user", "Likes tests.");
        var big = Drop("big", "# big\n\n## Seed\n" + new string('x', 30_000) + "\n");   // over the 24,000 topic cap on purpose

        var list = (await GetJson("api/memory/topics")).EnumerateArray().ToList();
        Assert.Equal(["core", "big", "user"], list.Select(r => r.GetProperty("slug").GetString()).ToList());
        Assert.Equal(("MEMORY.md", 6000), (list[0].GetProperty("path").GetString(), list[0].GetProperty("cap").GetInt32()));
        Assert.Equal(("topics/big.md", 24000, big.Length), (list[1].GetProperty("path").GetString(), list[1].GetProperty("cap").GetInt32(), list[1].GetProperty("chars").GetInt32()));

        var file = await GetJson("api/memory/topics/big");
        Assert.Equal(big, file.GetProperty("text").GetString());
        Assert.Equal(Sha(big), file.GetProperty("hash").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Client.GetAsync("api/memory/topics/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.Client.GetAsync("api/memory/topics/Not-A-Slug")).StatusCode);
    }

    [Fact]
    public async Task AC1_a_crlf_file_reads_as_lf_and_its_hash_saves()
    {
        var crlf = Drop("notes", "# notes\r\n\r\n## Seed\r\nTyped in Notepad.\r\n");
        var file = await GetJson("api/memory/topics/notes");
        var text = file.GetProperty("text").GetString()!;
        Assert.DoesNotContain('\r', text);
        Assert.Equal(crlf.Replace("\r\n", "\n"), text);

        var r = await Put("notes", text + "\n## More\nStill LF.\n", file.GetProperty("hash").GetString()!);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.DoesNotContain('\r', File.ReadAllText(Path.Combine(Memory.TopicsDir, "notes.md")));
    }

    [Fact]
    public async Task AC2_save_writes_through_the_rewrite_trail_and_carries_a_surviving_entrys_record_forward()
    {
        var before = Seed("user", "Likes tests.");
        // "Seed" survives (same heading) with its approval line deleted by the owner; one entry is added.
        var edited = "# user\n\n## Seed\nLikes tests, RED before GREEN.\n\n## Drinks tea\nEvery morning.\n";

        var r = await Put("user", edited, Sha(before));
        var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.True(r.IsSuccessStatusCode, body.ToString());

        var written = File.ReadAllText(Path.Combine(Memory.TopicsDir, "user.md"));
        Assert.Equal(written, body.GetProperty("text").GetString());
        Assert.Equal(Sha(written), body.GetProperty("hash").GetString());
        Assert.Equal("topics/user.md.rewrite-1.bak", body.GetProperty("backup").GetString());
        Assert.StartsWith("# user\n<!-- rewritten: approved ", written);
        Assert.Contains(" proposal 1 by owner in room general -->\n", written);
        Assert.Contains("## Seed\n<!-- " + SeedProvenance + " -->\nLikes tests, RED before GREEN.\n", written);   // carried forward
        Assert.Contains("## Drinks tea\nEvery morning.\n", written);
        Assert.NotEqual(edited, written);   // what lands is composed, not echoed
        Assert.Equal(before, File.ReadAllText(Path.Combine(Memory.TopicsDir, "user.md.rewrite-1.bak")));

        var row = Proposals.Get(1)!;
        Assert.Equal(("rewrite", "approved", "editor", "owner", "Edit user", "topics/user.md"), (row.Kind, row.Status, row.Source, row.AuthorId, row.Title, row.WrittenTo));
        Assert.Null(row.Flags);
        Assert.Matches("^[0-9a-f]{7,}$", row.CommitHash);
        Assert.Equal(row.CommitHash, body.GetProperty("proposal").GetProperty("commitHash").GetString());

        var (author, note) = (await Messages()).Last();
        Assert.Equal(ChopDb.HubParticipantId, author);
        Assert.Matches(@"^Memory proposal #1 approved: edited memory/topics/user\.md \(commit [0-9a-f]{7,}\)\.$", note);
        Assert.DoesNotContain(await Messages(), m => m.Body.StartsWith("Memory proposal #1 by owner", StringComparison.Ordinal));   // no Proposed note

        Assert.Empty((await GetJson("api/memory/proposals?room=general")).EnumerateArray());   // nothing left to decide
    }

    [Fact]
    public async Task AC2_a_removed_entry_is_named_in_the_note_and_the_bearer_names_the_author()
    {
        _host.AuthorizeAs(ChopDb.OwnerRemoteParticipantId);
        var before = Seed("user", "Likes tests.");

        var r = await Put("user", "# user\n\n## Only this\nStays.\n", Sha(before));
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());

        Assert.Equal(ChopDb.OwnerRemoteParticipantId, Proposals.Get(1)!.AuthorId);
        Assert.Matches(@"^Memory proposal #1 approved: edited memory/topics/user\.md, removing 'Seed' \(commit [0-9a-f]{7,}\)\.$", (await Messages()).Last().Body);
    }

    [Fact]
    public async Task AC2_the_editor_can_shrink_a_topic_already_over_its_cap()
    {
        var before = Drop("big", "# big\n\n## Seed\n" + new string('x', 30_000) + "\n");
        var r = await Put("big", "# big\n\n## Seed\nShort now.\n", Sha(before));
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.True(Memory.ReadTopic("big", int.MaxValue)!.FullChars < 200);
    }

    [Fact]
    public async Task AC3_refusals_leave_no_row_no_backup_and_no_write()
    {
        var before = Seed("user", "Likes tests.");
        var core = Memory.ReadTopic(MemoryStore.CoreTopic)!.Text;
        var path = Path.Combine(Memory.TopicsDir, "user.md");

        var stale = await Put("user", "# user\n\n## Seed\nChanged.\n", "0000");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var staleBody = JsonDocument.Parse(await stale.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal((MemoryApi.StaleEdit, Sha(before)), (staleBody.GetProperty("error").GetString(), staleBody.GetProperty("hash").GetString()));

        Assert.Equal(HttpStatusCode.BadRequest, (await Put("user", "   \n", Sha(before))).StatusCode);
        var noHeading = await Put("user", "just prose\n", Sha(before));
        Assert.Equal(HttpStatusCode.BadRequest, noHeading.StatusCode);
        Assert.Contains("at least one '## ' heading", await noHeading.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await Put("user", "# user\n## Same\na\n## Same\nb\n", Sha(before))).StatusCode);

        // The cap is a 409 just above it AND far above it: the composed-size check runs before the floor.
        foreach (var pad in new[] { 6_010, 60_000 })
        {
            var overCap = await Put("core", "# Memory\n\n## Big\n" + new string('y', pad) + "\n", Sha(core));
            Assert.Equal(HttpStatusCode.Conflict, overCap.StatusCode);
            var overBody = JsonDocument.Parse(await overCap.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(6000, overBody.GetProperty("cap").GetInt32());
            Assert.Contains("over the 6000 cap", overBody.GetProperty("error").GetString());
            Assert.True(overBody.GetProperty("chars").GetInt32() > pad);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await Put("nope", "# nope\n\n## A\nb\n", "0000")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Put("user", "# user\n\n## A\nb\n", Sha(before), room: "no-such-room")).StatusCode);

        // A leading space hides `## Seed` from an untrimmed check but not from the trimmed text the row
        // stores; the carried-forward provenance then tips the composed size over the cap. The refusal must
        // come from the pre-check (409, no row), never from ApproveCore after the INSERT.
        Memory.Append(MemoryStore.CoreTopic, "Seed", "Core seed.", SeedProvenance);
        var coreSeeded = Memory.ReadTopic(MemoryStore.CoreTopic)!.Text;
        var sneaky = await Put("core", " ## Seed\n" + new string('y', 5_850) + "\n## Other\nz\n", Sha(coreSeeded));
        Assert.Equal(HttpStatusCode.Conflict, sneaky.StatusCode);
        Assert.Contains("over the 6000 cap", await sneaky.Content.ReadAsStringAsync());
        Assert.Equal(coreSeeded, Memory.ReadTopic(MemoryStore.CoreTopic)!.Text);

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Empty(Backups());
        Assert.Empty(Proposals.List(null, null));
    }

    [Fact]
    public async Task AC3_a_save_is_refused_while_a_spawn_is_in_flight_and_lands_after_it_ends()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_memedit_spawn_" + Guid.NewGuid().ToString("N"));
        var runner = new FakeProcessRunner();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Handler = async (_, _, ct) => { await release.Task.WaitAsync(ct); return FakeProcessRunner.Ok("""{"result":"done"}"""); };
        await using var host = await HubTestHost.StartAsync(dir, processRunner: runner, limits: Fast);
        host.AuthorizeAs(ChopDb.OwnerParticipantId);
        var memory = host.Services.GetRequiredService<MemoryStore>();
        memory.Append("user", "Seed", "Likes tests.", SeedProvenance);
        var before = memory.ReadTopic("user", int.MaxValue)!.Text;
        var edit = new { roomId = "general", text = "# user\n\n## Seed\nChanged.\n", baseHash = MemoryApi.Hash(before) };

        Assert.Equal(HttpStatusCode.Created, (await host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "@opus hi" })).StatusCode);
        await runner.NextSpecAsync(Wait);
        var spawner = host.Services.GetRequiredService<SpawnerService>();
        Assert.True(spawner.AnySpawnInFlight);

        var refused = await host.Client.PutAsJsonAsync("api/memory/topics/user", edit);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains(MemoryApi.SpawnRunning, await refused.Content.ReadAsStringAsync());
        Assert.Empty(host.Services.GetRequiredService<MemoryProposalStore>().List(null, null));

        release.SetResult();
        var deadline = DateTime.UtcNow + Wait;
        while (spawner.AnySpawnInFlight && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.False(spawner.AnySpawnInFlight);

        var ok = await host.Client.PutAsJsonAsync("api/memory/topics/user", edit);
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AC4_a_put_without_an_owner_class_bearer_is_401_or_403_and_writes_nothing()
    {
        var before = Seed("user", "Likes tests.");
        var edit = new { roomId = "general", text = "# user\n\n## Seed\nChanged.\n", baseHash = Sha(before) };

        _host.Client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Client.PutAsJsonAsync("api/memory/topics/user", edit)).StatusCode);
        _host.AuthorizeAs("opus");
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.Client.PutAsJsonAsync("api/memory/topics/user", edit)).StatusCode);

        Assert.Equal(before, Memory.ReadTopic("user", int.MaxValue)!.Text);
        Assert.Empty(Proposals.List(null, null));
    }

    [Fact]
    public async Task AC5_a_model_consolidation_approved_from_the_panel_still_says_consolidated()
    {
        Seed("user", "Likes tests.");
        Proposals.Create("general", "opus", "user", "Consolidate user", "# user\n\n## Seed\nFolded.\n", null, null, null, MemoryProposalStore.KindRewrite);
        var r = await _host.Client.PostAsync("api/memory/proposals/1/approve", null);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.Matches(@"^Memory proposal #1 approved: consolidated memory/topics/user\.md \(commit [0-9a-f]{7,}\)\.$", (await Messages()).Last().Body);
    }

    [Fact]
    public async Task AC7_preview_returns_the_composed_size_the_cap_is_enforced_on()
    {
        Seed("user", "Likes tests.");
        var typed = "# user\n\n## Seed\nNo approval line typed here.\n";
        var r = await _host.Client.PostAsJsonAsync("api/memory/topics/user/preview", new { roomId = "general", text = typed });
        var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.True(r.IsSuccessStatusCode, body.ToString());
        Assert.True(body.GetProperty("chars").GetInt32() > typed.Length + SeedProvenance.Length);   // marker line + carried provenance
        Assert.Equal((24000, false), (body.GetProperty("cap").GetInt32(), body.GetProperty("over").GetBoolean()));

        var over = await _host.Client.PostAsJsonAsync("api/memory/topics/core/preview", new { roomId = "general", text = "# Memory\n\n## Big\n" + new string('y', 6_100) + "\n" });
        Assert.True((JsonDocument.Parse(await over.Content.ReadAsStringAsync()).RootElement).GetProperty("over").GetBoolean());
        Assert.Empty(Proposals.List(null, null));
        Assert.Empty(Backups());
    }
}
```

Notes for the builder: `MemoryStore.Append(topic, title, body, provenance)` is public and creates the file with an H1, but its body is capped at `MaxBodyChars` (4,000), which is why over-cap fixtures use `Drop`. `HubTestHost.AuthorizeAs(string)` accepts any roster id the fixture minted; `"opus"` is a spawnable row with a bearer from `TokenFor` (ledger 9). `MemoryApi.Hash` is reachable through the Hub's `InternalsVisibleTo`.

Gate: `dotnet clean ChopItUp.slnx -c Debug -v minimal`, then `dotnet build ChopItUp.slnx -c Debug -warnaserror -v minimal` (0 warnings), then `dotnet test ChopItUp.slnx -c Debug --nologo -v minimal` (baseline 1,208 + 11 new, all green; no existing test changes; the AC3 refusal test's core assertions run before its own `Append` to the core). Expected RED before the source edits: the Core floor test throws at `ValidateRewrite`; every `MemoryEditApiTests` case fails on the route.

### Task 2 — Client API + types (sonnet)

Files: `src/ChopItUp.Hub/client/src/types.ts`, `src/ChopItUp.Hub/client/src/api.ts`, `src/ChopItUp.Hub/client/src/api.test.ts`.

`types.ts` (after `MemoryImportResult`):

```ts
/** Row 40: one row of `GET /api/memory/topics` — the core first, then topics in slug order. */
export interface MemoryFile {
  slug: string;
  /** `MEMORY.md` for the core, `topics/<slug>.md` otherwise. */
  path: string;
  chars: number;
  cap: number;
}

/** Row 40: `GET /api/memory/topics/{slug}` — the whole file, uncut and LF-normalised, and the hash a
 *  save must echo. */
export interface MemoryFileText extends MemoryFile {
  text: string;
  hash: string;
}

/** Row 40: `POST /api/memory/topics/{slug}/preview` — the size the hub would write, which is what the
 *  cap is enforced on. */
export interface MemoryPreview {
  slug: string;
  chars: number;
  cap: number;
  over: boolean;
}

/** Row 40: what a save returns — the approved editor row, the file as the hub wrote it, and where the
 *  pre-edit copy went. */
export interface MemoryEditResult extends MemoryFileText {
  proposal: MemoryProposal;
  backup: string;
}
```

`api.ts` (after `importMemory`; import the four types at the top):

```ts
/** Row 40: the editor's file list and reads need no credential — every `GET` on `/api` stays open
 *  (row 28 AC2) and `recall` already hands any bearer the same text. */
export async function listMemoryFiles(signal?: AbortSignal): Promise<MemoryFile[]> {
  return unwrap<MemoryFile[]>(await fetch('/api/memory/topics', { signal }));
}

export async function readMemoryFile(slug: string, signal?: AbortSignal): Promise<MemoryFileText> {
  return unwrap<MemoryFileText>(await fetch(`/api/memory/topics/${encodeURIComponent(slug)}`, { signal }));
}

/** Row 40: the hub's count for the text as typed. A POST, so it carries the owner token like every
 *  write; it writes nothing. */
export async function previewMemoryFile(slug: string, roomId: string, text: string, signal?: AbortSignal): Promise<MemoryPreview> {
  return unwrap<MemoryPreview>(
    await write(`/api/memory/topics/${encodeURIComponent(slug)}/preview`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ roomId, text }),
      signal,
    }),
  );
}

/** Row 40: one save = one approved rewrite. `baseHash` is the hash the read returned; the hub answers
 *  409 with its own sentence when the file moved on since, when a spawn is in flight, or when the
 *  result would pass the cap — all through `unwrap` as an `ApiError`, like every other refusal. */
export async function saveMemoryFile(slug: string, roomId: string, text: string, baseHash: string, signal?: AbortSignal): Promise<MemoryEditResult> {
  return unwrap<MemoryEditResult>(
    await write(`/api/memory/topics/${encodeURIComponent(slug)}`, {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ roomId, text, baseHash }),
      signal,
    }),
  );
}
```

`api.test.ts`: four cases in the file's existing stubbed-fetch idiom — `listMemoryFiles` hits `/api/memory/topics` with no `authorization` header; `previewMemoryFile` sends `POST` with `{ roomId, text }` and the token; `saveMemoryFile` sends `PUT`, a JSON body `{ roomId, text, baseHash }`, and the stored owner token as `Bearer …` (seed it the way the file's other write cases do); a 409 `{ error }` reply surfaces as an `ApiError` whose message is the hub's sentence. RED: the functions do not exist. Gate: `npm test`, `npm run build` (tsc clean).

### Task 3 — The dialog, the header button, App wiring (opus)

Files: new `src/ChopItUp.Hub/client/src/MemoryEditorDialog.tsx`, new `src/ChopItUp.Hub/client/src/MemoryEditorDialog.test.tsx`, `RoomHeader.tsx`, `App.tsx`, `MemoryPanel.tsx`, `styles.css`.

Contract (the builder writes the component and the prose; this is what it must do):

- `RoomHeader` gains `onMemory: () => void` and an `Edit memory` button (class `quiet`, `title="Read and edit the shared memory files"`) right after `Roles`.
- `App` gains `const [memoryOpen, setMemoryOpen] = useState(false);`, passes `onMemory={() => setMemoryOpen(true)}`, and mounts `{memoryOpen && activeRoom && <MemoryEditorDialog room={activeRoom} locked={(exchange?.inFlight.length ?? 0) > 0} onClose={() => setMemoryOpen(false)} />}` beside `RolesDialog`.
- `MemoryEditorDialog` props: `{ room: Room; locked: boolean; onClose: () => void }`. Structure, in the `.dialog` idiom (`styles.css:1315`, `.roles-dialog:2170`; new classes prefixed `memory-editor-`): `role="dialog"` `aria-labelledby="memory-editor-title"`; a close control `aria-label="Close"`; a `<select aria-label="Memory file">` whose options read `MEMORY.md (core) · N of 6,000` / `topics/<slug>.md · N of 24,000`, core first, disabled while the text is dirty; a `<textarea aria-label="Memory text">` holding the file; a count line driven by the hub's preview (`previewMemoryFile`, debounced 300 ms after each change, the latest reply wins, a failed call leaves the last good number; when `readOwnerToken()` is null the preview is not called at all, the hint shows the owner-token sentence and Save is disabled, because every `POST` under `/api` needs the owner bearer and each accepted bearer costs a peer-process check): `N of CAP characters as the hub would write it`, becoming `over the cap by X` when `over`; until the first preview answers, the raw length with the words `(typed; the hub adds its bookkeeping lines)`; buttons `Reload` (quiet) and `Save` (send; disabled when clean, when `locked`, when the latest count is `over`, or while a save is in flight, labelled `Saving…` then); a hint line: when `locked`, exactly `A spawn is running; save when the exchange has finished.`; otherwise one sentence saying a save files an approved rewrite in this room (backup, commit, note) and that the file needs at least one `## ` entry heading.
- After a save: the textarea and hash take the response (`text`, `hash`), the picker's count updates, the status line says `Saved as proposal #<id> (commit <hash>); the previous text is at <backup>.` or `Saved as proposal #<id> (not committed: git unavailable; see the hub log); the previous text is at <backup>.` when `commitHash` is null.
- Refusals: `api.isCredentialRefusal` → the same owner-token sentence `RolesDialog` shows for 401/403 (prefixed `That edit was not saved.`); any other `ApiError` → the hub's sentence verbatim; on a stale 409 the `Reload` button is the recovery arm and stays enabled (M25: reachable from every state).
- Effectful halves live outside the component so they run without a DOM (the `saveStanding`/`SaveHooks` shape in `RolesDialog.tsx`): export `saveEdit(slug, roomId, text, baseHash, hooks)`, `countLine(preview: MemoryPreview | null, rawLength: number)`, and `isDirty(loaded: string, text: string)` (plain inequality; the hub already normalised newlines, R12).
- `MemoryPanel.tsx:85`: `{p.source && <p className="memory-source">{p.source === 'editor' ? 'edited by hand; Retry writes it' : `imported from ${p.source}`}</p>}` — the only way an editor row reaches the panel is the approved-but-unwritten crash window.

Tests (`renderToStaticMarkup` + `vi.stubGlobal('fetch')`, no jsdom): the dialog renders the picker with the core first and the raw count line from a stubbed list/read; `Save` is disabled when `locked` and the hint is exactly `A spawn is running; save when the exchange has finished.`; with no stored token the hint is the owner-token sentence, Save is disabled and no preview request is made; `countLine` reads over-the-cap from a preview with `over: true` and raw with `null`; `isDirty` is false for two equal LF strings; `saveEdit` PUTs `{ roomId, text, baseHash }` through the token-attaching `write` and reports `done` with the response including `backup`; a 409 reply reports `fail` with the hub's sentence; a 401 reports the credential sentence; `RoomHeader` renders an `Edit memory` button. RED: the module does not exist. Gate: `npm test`, `npm run build`.

### Task 4 — Synthetic-corpus dry run + verification doc (sonnet)

Files: new `tools/Invoke-Row40MemoryEditCheck.ps1`, `docs/verification.md`.

Mirror `tools/Invoke-M23DryRun.ps1`'s frame (param block with `-HubExe`, `-DataDir` under `$env:TEMP`, `-Port 8806`, `Add-Check`, hub started by PID and stopped in `finally`, `Results: n/m PASS`, exit 0 only when all pass; `ChopTokenHelpers.ps1` seeds `owner` before the first start; `Invoke-RestMethod` arrays through `ForEach-Object { $_ }`). Fabricate on disk before the hub starts: a core with five `## ` entries near 5,000 chars; topics `alpha` (six entries, each with a provenance line), `beta` (two entries), `huge` (one entry padded past 24,000 chars), `crlf` (two entries written with `\r\n`). Never touch `C:\Self Apps` or any real data directory. Legs (assert only hub-controlled text, LESSONS M11):

1. `list.order-and-caps` — `GET /api/memory/topics`: slugs `core, alpha, beta, crlf, huge`; caps 6000 then 24000; `huge.chars -gt 24000`.
2. `get.uncut` — `GET /api/memory/topics/huge`: `text.Length -eq chars` and `-gt 24000`; `hash` is 64 hex chars.
3. `get.crlf-as-lf` — `GET /api/memory/topics/crlf`: `text` contains no `\r`.
4. `put.unauthenticated-401` — PUT without a bearer → 401.
5. `preview.counts-bookkeeping` — `POST /api/memory/topics/alpha/preview` with alpha's entries minus every provenance line → `chars` greater than the sent length; `over -eq $false`.
6. `put.alpha-200` — owner bearer, drop one entry, keep the rest with their provenance lines deleted: 200; `proposal.kind -eq 'rewrite'`, `status approved`, `source editor`, `authorId owner`, `commitHash` matches `^[0-9a-f]{7,}$`; `backup -eq 'topics/alpha.md.rewrite-<id>.bak'`.
7. `put.alpha-file-bak-and-carry` — `topics\alpha.md` equals the response `text`; the `.bak` named by leg 6 equals the pre-edit text; the written file contains every surviving entry's original `<!-- approved … -->` line.
8. `put.alpha-note` — the last `general` message is by `hub` and matches `^Memory proposal #<id> approved: edited memory/topics/alpha\.md, removing '.+' \(commit [0-9a-f]{7,}\)\.$`.
9. `put.alpha-git` — `git -C <data>\memory log --oneline` lists exactly one commit (the repository is created lazily by the first approval, so there is no "before"; the M23 dry run's `approve.one-commit` idiom).
10. `put.stale-409` — re-send leg 6's body with leg 6's ORIGINAL hash → 409 with the `StaleEdit` sentence; file unchanged.
11. `put.core-over-cap-409` — core text padded to 6,100 and again to 60,000 → 409 both times, `cap -eq 6000`; `MEMORY.md` unchanged; no new `.bak` anywhere under `memory\`; `GET /api/memory/proposals?room=general&status=all` count unchanged since leg 10.
12. `put.no-heading-400` — `beta` with prose only → 400.
13. `put.huge-shrink-200` — `huge` replaced by a two-line entry → 200 and `chars -lt 200`.
14. `put.crlf-200` — `crlf` saved with leg 3's hash and one appended entry → 200; the file on disk has no `\r`.
15. `restore.bak-round-trip` — stop the hub inline (by the PID the script started; the guarded stop stays in `finally` as well, idempotent, so an earlier failure never leaks the process), then copy leg 6's `.bak` over `topics\alpha.md`: the file equals the pre-edit text captured in leg 7, `git -C <data>\memory status --porcelain` is non-empty (the restore is an uncommitted change the next approval's `add -A` would otherwise sweep into a model's commit), then `git add -A && git commit -m "restore topics/alpha.md from <bak>"` leaves it clean with two commits. This is the R10 recipe, executed.
16. `proposals.no-undecided` — `GET /api/memory/proposals?room=general` was empty before the stop.

`docs/verification.md`: after the row 23 dry-run line — `Row 40 editor dry run (no model calls, scratch hub, fabricated corpus, drives the editor routes, the trail they leave and the .bak restore): pwsh tools\Invoke-Row40MemoryEditCheck.ps1`; and extend the "Restoring a rewrite" paragraph: an editor save is a rewrite proposal too, so the same recipe applies, and the save's status line names the `.bak`. With git present prefer `git -C <data>\memory revert <hash>` (hub stopped; every save is listed by `log --oneline` as `Approve memory proposal #<id> (<topic>): Edit <topic>`); when copying the `.bak` instead, commit the restore before restarting (`git -C <data>\memory add -A` then `commit -m "restore <file> from <bak>"`), because the next approval's `add -A` would otherwise record the restore as part of a model's proposal. Gate: the script runs 17/17 (resave leg added at pass 3) against the Debug hub exe built in task 1 (the orchestrator re-runs it before deploy).

### Task 5 — Verify skill feature + capture helper (sonnet; look judged by opus afterwards)

Files (gitignored, clone-only): `.claude/skills/verify-chopitup/features/memory-editor.md`, `.claude/skills/verify-chopitup/helpers/Invoke-MemoryEditorCapture.ps1`, `.claude/skills/verify-chopitup/SKILL.md` (add the `Edit memory` header button and the dialog's `aria-labelledby="memory-editor-title"` to the Drive list).

Helper: clone `Invoke-RolesCapture.ps1`'s launch → doctor → drive → capture → `Test-CaptureSane` shape; for text entry clone the typing leg of `tools\Invoke-Row12ShellCheck.ps1:514-574` (focus by UIA, then `[System.Windows.Forms.SendKeys]::SendWait`, because a controlled React textarea does not fire `onChange` on `ValuePattern.SetValue`; typed text contains no SendKeys special characters). Drive = click `Edit memory`, wait for the dialog by `aria-labelledby`, focus `Memory text`, press `^{END}` (end of document, not end of line) then type `{ENTER}{ENTER}## Verify helper{ENTER}Edited by the verify helper.`, wait for the count line to change (the preview answered), click `Save`, wait for the status line to start with `Saved as proposal #`, then prove the write through `GET /api/memory/topics/core` (no credential) — `text` contains `Edited by the verify helper.` — capture, sanity-check, close, kill only the PIDs it started. The scratch data dir seeds `MEMORY.md` with one `## ` entry before launch so a save is legal (R6). No token step: inside the Desktop shell the credential is the shell token the shell mints and injects (`HubChild.cs:88-89`, `ProcessHubFactory.cs:23,42`, read by `ownerToken.ts:35-37` before `localStorage`), which is how `Invoke-RolesCapture.ps1` already performs a credentialed persona save with no token handling of its own. Feature file: how to reach the dialog, the selectors, the count line's two forms, the three refusal states a harness can stage (stale: save from a second client first; over cap: paste 6,100 characters into the core; locked: needs a spawn, not stageable without a model), and that an empty room still shows the button (unlike the panel). Gate: the helper runs end to end on the interactive desktop; the orchestrator dispatches an `opus` look at the capture (text verdict) and runs the UIA interactive gate before "verified".

## Ticket graph

`01 → 02 → 03 → 05` and `01 → 04`. Acyclic; consistent with the dispatch order 1, 2, 3, 4, 5. Ticket 04 says `Blocked by: 01` and is dispatched after 03 only because this cwd cannot run worktree-isolated builders in parallel.

## Critique findings and dispositions

Pass 1 (opus, 2026-09-17, score 6.8, FIX-THEN-SHIP):

1. MAJOR over-cap returned 400 via `ValidateRewrite`'s floor — **fixed**: cap check on the composed size runs first (task 1c), the floor made honest in Core (task 1a, with its own RED test), AC3 tests at 6,010 and 60,000.
2. MAJOR AC2 note regex contradicted the body — **fixed**: the AC2 body keeps `## Seed`; the removal clause lives in the sibling test.
3. MAJOR fixture `Append` throws over 4,000 chars — **fixed**: `Drop` writes fixtures to disk; ledger row 22.
4. MAJOR composed-vs-typed divergence untested and misstated — **fixed**: AC2 asserts carry-forward and `written != edited`; AC7 preview route feeds the dialog's count and Save gate; the raw-count fallback says so.
5. MAJOR no rollback story — **fixed**: R10, the `backup` field in the response and the status line, the verification-doc recipe, dry-run leg 15 executes the restore. `.bak` pruning declined (a delete path is its own row).
6. MAJOR CRLF makes the textarea dirty forever — **fixed**: R12, LF normalisation on every hub read and hash, `AC1_a_crlf_file_reads_as_lf_and_its_hash_saves`, dry-run legs 3 and 14, `isDirty` tested.
7. MINOR three sentences for AC6 — **fixed**: one literal in AC6 and the test.
8. MINOR tier evidence misread — **fixed**: the header now cites `MemoryStore.cs:196-201` and labels the other two hits noise.
9. MINOR hash guard one-directional — **fixed** in R7's wording; a base hash on model rewrites declined (changes `propose_rewrite`'s contract).
10. MINOR four ledger rows go red by design — **fixed**: rows 2, 3, 8, 14, 23 marked baseline-only; row 14 reworded to what a count can see.
11. MINOR no typing precedent named — **fixed**: task 5 clones the row 12 `SendKeys` leg (ledger 24); the API content check stays the gate.
12. MINOR `locked` wiring proven only as a prop — **accepted**, stated under "Could not verify".
13. MINOR ticket graph mismatch — **fixed**: `01 → 04` in the plan; sequential dispatch explained.
14. NIT `Seed` async without await — **fixed** (synchronous).
15. NIT `.bak` sweep missed `Memory.Root` — **fixed** (`Backups()` covers both).
16. NIT R2 band width unstated — **fixed** (≤ 11 chars, "leave it").
17. NIT `Memory` label ambiguous — **fixed** (`Edit memory`, R9).
18. NIT Goal overstated; seed core has no entry — **fixed** (Goal narrowed; R6 names the first-save step).

Pass 2 (fable, 2026-09-17, score 7.3, FIX-THEN-SHIP; new findings only):

1. MAJOR pre-checks ran on the untrimmed text while `Create` stores `body.Trim()`, so a leading-space heading could be refused after the INSERT and leave a pending editor row — **fixed**: `PutTopic` and `PreviewTopic` check the trimmed text, `Create` is wrapped in the same 400 catch, `HubNotes.Refused` gets the editor verb, AC3 gains the leading-space case asserting 409 and no row.
2. MINOR dry-run leg 9 measured `git log` before a repository existed — **fixed**: exactly one commit after leg 6.
3. MINOR moving the stop out of `finally` leaked the hub on an earlier failure — **fixed**: inline stop plus the guarded `finally` stop.
4. MINOR task 5 described a token paste `Invoke-RolesCapture.ps1` never performs — **fixed**: the shell token is the credential; sentence removed.
5. MINOR a copied-back `.bak` is swept into the next model commit — **fixed**: R10 and the doc prefer `git revert`, else commit the restore; leg 15 asserts the dirty tree and commits it.
6. MINOR no-token path unbound and costly — **fixed**: no preview without a token, hint + Save disabled, vitest case.
7. NIT ledger 14 count — **fixed** (five).
8. NIT two placeholder-provenance conventions — **accepted** with a cross-reference comment; a shared Core helper is out of this row's scope.
9. NIT `{END}` is end-of-line — **fixed** (`^{END}`).
10. NIT ticket drift — **fixed** (tickets 03 and 04 reworded).
11. NIT lone `\r` — **fixed** (`Lf` folds it too).

Pass 3 (opus diff interrogation over `.scratch/m40-memory-editor/diff.patch`, 2026-09-17, score 7.4, FIX-THEN-SHIP) plus the branch code-review axes (Standards S1-S8, Spec X1-X3):

1. P3-1 MAJOR `PutTopic` returns the panel's `SpawnRunning` sentence ("decide memory proposals") and the client `locked` is per-room while `AnySpawnInFlight` is process-wide — **fixed**: new `MemoryApi.SpawnRunningEdit` = `A spawn is running; save when the exchange has finished.` (the dialog's `LOCKED_HINT` verbatim) returned by `PutTopic`, asserted by the AC3 spawn test; a cross-room spawn stays refused-but-explained (the 409 sentence is the hint), the per-room `locked` pre-empts the common case. A global in-flight signal on the client is declined for this row.
2. P3-2 MAJOR no gate performs a second save with the response hash — **fixed**: AC2 test saves twice (second `.bak` holds the first save's output, two commits), dry-run leg `put.alpha-resave-200` (17 legs), the capture helper types-saves-types-saves.
3. P3-3 MINOR `room-*` topics listed with the 24,000 cap while spawns read 2,000 — **declined here, boarded** as row 41 (a spawn-prompt budget surfaced in the editor is its own small row).
4. P3-4 MINOR "every refusal before the row" over-claimed; `MemoryPanel` copy assumes approved-but-unwritten — **fixed**: docstring narrowed to pre-checks; panel copy status-aware (`Approve writes it` when pending). The uncaught `Rewrite` IOException 500 is the same window every approval has today; accepted under "Could not verify".
5. P3-5 MINOR overlay click, Escape and Reload drop dirty text silently; Close enabled mid-save — **fixed**: overlay mousedown and Escape are ignored while dirty (the Close button stays the explicit exit); Close and Reload disabled while saving.
6. P3-6 MINOR `white-space: pre` disables soft wrap — **fixed**: `pre-wrap` + `overflow-wrap: anywhere`.
7. P3-7 MINOR dry run: readiness never checks `$hub.HasExited` or a taken port; `& git … 2>&1` under Stop can crash leg 9; `$total` never asserted — **fixed**: fail fast on exit/port, git calls guarded by `$LASTEXITCODE` in try/catch, exit asserts `$total -eq 17`.
8. P3-8 NIT plan-narrating comments in the script — **fixed** (statements about the script).
9. P3-9 / S3 NIT `$hubStopped` dead — **fixed** (removed).
10. P3-10 NIT `saveMemoryFile` `signal?` never passed — **fixed** (parameter dropped).
11. P3-11 NIT trailing-backslash `-DataDir` — **fixed** (`TrimEnd('\','/')` before quoting).
12. P3-12 NIT pasted provenance matching the next rowid dedups the write — **declined** (needs the next rowid; self-healing; recorded).
13. S1 stale `ValidateRewrite` XML doc — **fixed**. S2 false `.memory-editor-dialog` width comment — **fixed**. S6 `CapOf` not used in `ApproveCore` — **fixed** (`ApproveCore` uses `CapOf(p.Topic)`). S4 AC-prefixed test names, S5 two cap sentences (one is a no-row pre-check, the other needs a row), S7 floor re-deriving `ComposeRewrite`'s shape, S8 `{slug,path,chars,cap}` clump — **declined** (judgement calls; S5's split is deliberate).
14. X1 Spec AC7 the Save gate did not fall back to the raw length before the first preview — **fixed**: `over` = `preview ? preview.over : text.length > loaded.cap`, vitest case. X2 helper waits on the heading text, not `aria-labelledby` — **accepted** (the WebView2 UIA bridge exposes no attribute; SKILL.md names it). X3 two moved comments reworded for the slop gate — **accepted**.
