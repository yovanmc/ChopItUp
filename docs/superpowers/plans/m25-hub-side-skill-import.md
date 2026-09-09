# M25 — the hub's own skill-import path: agents propose, the owner approves

**Goal.** Let a participant install a skill into a deployed hub without the owner running a CLI verb, by
routing the import through the hub itself as a proposal the owner approves in the room.

**Architecture.** Row 23 already built most of the shape: an MCP tool records a proposal, the hub
announces it in the room, and the owner acts on a card that calls a `/api` endpoint, which performs the
write in-process (`MemoryTools.propose_memory` → `MemoryApi.Approve`, D15). M25 repeats that shape for
skills over a new `skill_proposals` table (schema 10) — with one structural difference that memory does
not need and this milestone cannot ship without: **the decision endpoints are credential-gated to the
owner.** The write itself is not new code: `SkillImport.Run` is already an in-process, mutex-guarded,
rename-swap install with a nine-refusal battery, and the hub calls it directly. The new mechanisms are
(a) a side-effect-free `SkillImport.Validate`, (b) a source-tree manifest pinned at propose time and
re-checked against the *staged copy* before the swap, (c) source directories confined to the room's own
bound directory, (d) owner-token auth on approve/reject, and (e) an approval card that shows the owner
every byte of text the skill would install.

**Author model:** Opus 5. **Routing mismatch declared:** the workflow routes planning sessions to
Fable; this plan was authored by the session model, Opus 5. Critique pass 2 is therefore mandatory
(it already is at this tier). Pass 1 ran on Fable as the strongest non-author model and returned
REFRAME / 4.5; this revision is the response, and its dispositions are recorded below.

**Blast radius: HIGH.** Justification: this adds a write channel into the live skill store, and an
installed skill is executable — `RunTools.cs:108-109` resolves `scripts/<gate>.ps1` inside an installed
skill and runs it under `pwsh`. Anything that can install a skill can, one `run_gate` later, execute
code as the owner. The milestone also bumps `ChopDb.LatestSchemaVersion` 9 → 10, which lesson M11 says
breaks the gates that verify it.

*Written for builder-subagent execution; if something doesn't match, STOP and report rather than guess.*

## What this control does and does not defend against

Pass 1's central charge was that an owner-approval card is theatre: the orchestrator session holds the
`claude` MCP credential, the privacy guard permits both an MCP call to this hub and an
`Invoke-RestMethod` POST to loopback (pass 1 verified both against the real hook), so under the first
draft the same principal the guard binds could propose and approve with no human involved. That charge
was correct and the design changed. The scope of what the fixed design achieves must still be stated
plainly, because a later reader will otherwise over-trust it:

- **It binds the careless case, which is the one the guard exists for.** An agent following the rules
  cannot install a skill on its own: it can only propose, and the approve call needs a credential it is
  not given and is forbidden to go looking for. The 2026-08-31 guard ruling is about the owner staying
  in the loop on live data, not about a hostile process.
- **It does not bind the hostile case, and must not claim to.** Pass 2 sharpened this and the sharpened
  version is the one that stands: the owner token lives in `tokens.json` inside the data directory
  (`TokenStore.cs:12,33`), and ROADMAP row 13 records, measured 2026-09-07, that the abs-path deny binds
  Claude's file tools but **not** `Bash` — so both hosts reach that directory today. The approval
  credential is one file read away from every principal D1 binds. That is the current configuration, not
  an exotic attack.
- **No location on this machine fixes that, so the plan does not pretend one does.** A separate approval
  secret in the profile, in a console banner or in a sibling file is equally readable by a process with
  a shell. Making the credential unreachable is OS-level confinement, which is row 13. This milestone
  therefore ships a control that is *procedural at its weakest point*, and says so.
- **What makes the procedure real is where the rule lives.** Pass 2's decisive objection to the first
  revision was that the prohibition sat in a plan file that is deleted at close-out. Task 10 moves it
  into the repo `CLAUDE.md` safety invariants, which every builder in every future session reads, and
  which outranks this workflow on conflict. Reading `tokens.json` to approve one's own proposal, and
  invoking the deployed exe's `--import-skill` in any form, both become repo-contract prohibitions
  rather than plan trivia.
- **The board row is retitled to match.** Row 25 reads "No agent can install a skill after a deploy" —
  and after this milestone an agent still cannot install one *unaided*, but the row's implied promise of
  a hard boundary is not what ships. Task 10 retitles it to what the milestone delivers.

## Why this and not the two cheaper fixes

Both cheaper fixes were considered and are declined on the record:

- **Move the skills root out from under the guarded deploy tree** (to `%USERPROFILE%\ChopItUp\skills`,
  the D12 precedent `RoomsRootPath` already sets for room directories). This removes the blocker and
  adds no API — and it is a regression dressed as a fix, because it gives *every* agent a silent,
  unreviewed install path. Under this plan an install requires a credential the proposer does not hold;
  under that one it requires nothing at all.
- **Add ChopItUp to the harness's `selfAppsExemptApps`.** That is the guard's documented owner-only
  escape hatch with a "revert after" contract, not a fix to this repo, and it re-opens raw file access
  to the whole deployed store rather than to the one reviewed operation that needs it.

The guard blocks a *file-level* channel into the live store. Every other store mutation in this product
— every message, every memory approval — already happens through the hub's own vetted interfaces, from
agents, all day, by design. Skill import is the one mutation with no such interface, so it falls back to
the file channel, which is correctly blocked. Giving it an interface is consistent with the product, not
a route around the guard.

## Standing prohibition for every builder on this milestone

The deployed hub's data directory sits beside the exe under `C:\Self Apps\ChopItUp`. Writes into it are
guard-denied, and that deny is matched on the **text of the command**, not on the effect. Invoking the
deployed exe's `--import-skill` while omitting `--data` (the default data dir is
`AppContext.BaseDirectory\data`, `HubOptions.cs`) produces a command line the guard does not match and a
write it exists to prevent. **That form is forbidden here**, as is reading `tokens.json` to obtain an
owner credential. No builder runs `--import-skill` against the deployed install in any form. Dev work runs against the repo's gitignored `.data\`; deployment is
`tools\Deploy-ChopItUp.ps1` and nothing else.

## Acceptance

1. WHEN a participant calls `propose_skill` over `/mcp` with a source directory inside the room's own
   bound directory, THE SYSTEM SHALL record one pending proposal stamped with the participant the hub
   resolved from the credential, post a room note naming the skill, and write nothing under the skills
   root.
2. WHEN the source of a `propose_skill` call fails any import refusal, names a directory outside the
   room's bound directory, resolves through a link or junction at any path segment, or contains a file
   whose extension is outside the reviewable-text allowlist, THE SYSTEM SHALL return that refusal's own
   message, record no proposal, and leave the skills root untouched.
3. WHEN a second `propose_skill` names a skill that already has an undecided proposal with the same tree
   hash, THE SYSTEM SHALL return the existing proposal rather than minting a duplicate card.
4. WHEN the owner lists skill proposals, THE SYSTEM SHALL return for each undecided one: the skill name,
   whether it replaces an installed skill, the file count and byte total, every relative path, every
   declared gate, the full text of every file in the tree, and a `sourceChanged` flag comparing the
   source's current hash to the one recorded at propose time — and SHALL suppress the file contents when
   that flag is set. Any text the listing cannot show in full SHALL be marked, and a marked proposal
   SHALL NOT be approvable.
5. WHEN a request to approve or reject a skill proposal arrives with no bearer credential or an
   unresolvable one, THE SYSTEM SHALL refuse it with 401; WHEN it arrives with a credential resolving to
   a participant other than `owner` or `owner-remote`, THE SYSTEM SHALL refuse it with 403. Either way
   it SHALL change nothing.
6. WHEN the owner approves a pending skill proposal, and the source hash in the request body equals the
   hash recorded at propose time, and the installed-or-not state still matches what the listing showed,
   and the staged copy hashes to the recorded manifest before the swap, THE SYSTEM SHALL install the
   skill, and `GET /api/skills` SHALL list it without a hub restart.
7. WHEN any of those hashes disagree, or the installed-or-not state has changed since the listing, or
   any spawn is in flight, THE SYSTEM SHALL refuse the approval with a message naming the reason,
   install nothing, leave any previously installed skill of that name exactly as it was, and leave the
   proposal decidable.
8. WHEN an approval is interrupted after the proposal was marked approved but before the install was
   recorded, THE SYSTEM SHALL still count that proposal as undecided for listing purposes, and a repeat
   approval SHALL finish it — including when the install had in fact already completed, which the system
   SHALL detect by comparing the installed tree to the recorded manifest rather than by re-running the
   install.

## Decisions

- **D1 — the propose channel is MCP; the decision channel is owner-credentialed.**
  `BearerTokenMiddleware` guards `/mcp` alone; the rest of `/api` is unauthenticated by design (loopback
  is the boundary) and row 13 still carries "no-auth `/api`" as an open residual. This milestone extends
  the middleware to also guard `POST /api/skills/proposals/{id}/approve|reject`, and those handlers
  additionally require the resolved participant to be `owner` or `owner-remote`. `GET` stays
  unauthenticated like the rest of `/api`: reading a proposal discloses only what the proposer already
  put there. Nothing else on `/api` changes — widening auth to the whole surface is row 13's work.
- **D2 — the SPA holds the owner token from a one-time paste**, kept in `localStorage`, sent as
  `Authorization: Bearer …` on the two decision calls. This is also the CSRF fix pass 1 asked for: an
  `Authorization` header makes the request non-simple, so a cross-origin page cannot forge it and no
  `Origin` check is needed. A missing or stale token renders the card read-only with a "paste the owner
  token to decide" prompt rather than failing on click.
- **D3 — approval is refused while any spawn is in flight** (`SpawnerService.AnySpawnInFlight` → 409),
  the same defence `MemoryApi.Approve` takes (F3: a spawn lives inside the loopback boundary with a
  shell). Note the limit pass 1 identified: `AnySpawnInFlight` counts hub-spawned CLIs only and is blind
  to every other local process. It is kept as defence in depth, not relied on; D1 is the control.
- **D4 — the proposal carries a path, confined to the room's bound directory.** The agent writes the
  tree into the room's own directory and proposes it. Pass 2 killed the "free audit record" rationale
  the first revision claimed for this: `CommitAllAsync` is called only from `SpawnerService.cs:782,793`,
  so a proposal arriving over MCP with no spawn is never committed until some unrelated spawn later runs
  in that room, whose `git add -A` then sweeps the tree into *that* spawn's commit and, via
  `GitTrail.cs:182`, attributes it to the wrong participant. The confinement is kept for the reason that
  survives — bounding what the hub will read — and the audit claim is withdrawn. Pass 2 did verify that
  the confinement does not break the workflow: hub-created room dirs default to
  `%USERPROFILE%\ChopItUp\rooms` (`HubOptions.cs:38-39`), which the privacy guard's personal-directory
  deny list does not cover, so a proposing agent can write there. A path anywhere else is refused, which
  also stops the hub from
  becoming an existence oracle for directories the guard denies the agent from enumerating (grill D10:
  no reads outside the room). Refusal 1 ("source does not exist") and refusal 3 ("no SKILL.md") return
  different messages, so an unconfined `source_dir` would answer "does path X exist" for any X.
- **D5 — the pin is on the staged copy, not on a second read of the source.** Pass 1's finding stands:
  hashing the source at approve time and then letting `CopyTree` read it again is two reads with a
  window between them. `SkillImport.Run` gains an optional `expectedTree` manifest; after
  `CopyTree(source, staging)` it hashes **staging** and refuses with a rollback on any mismatch, before
  either `MoveWithRetry`. The bytes that install are therefore the bytes that were pinned, and the
  window is closed rather than narrowed. The request body carries the hash the owner's card displayed,
  so owner, database and staged copy must all three agree.
- **D6 — v1 proposals carry no overlay, and may not replace a skill that has one.** An overlay is
  hub-side by definition (`SkillImport` refusal 7b) and travels only through `--overlay`; refusal 8b
  already refuses a forced re-import that would drop one. Both are refused at propose time so the owner
  never sees a card that cannot be approved.
- **D7 — a skill proposed this way may contain only reviewable text, and only text the card can show in
  full.** `Validate` refuses any file whose extension is outside `.md .ps1 .psm1 .psd1 .txt .json .yml
  .yaml` — matched case-insensitively, with an extensionless file refused — and, per pass 2, **also
  refuses any file over `SkillStore.MaxSkillChars` (32,000) at propose time**. The first revision capped
  display at 8,000 and made a capped proposal un-approvable, which minted cards that could only ever be
  rejected: a skill's own `SKILL.md` may legitimately run to 32,000 characters. The cap now refuses
  early, with its own message, instead of failing after the room note.
  **What D7 guarantees, stated honestly (pass 2):** not "the owner is safe", but "the owner is shown
  every byte". `.ps1` must be on the allowlist because `RunTools` executes `scripts/<gate>.ps1` under
  `pwsh`, so the card asks the owner to audit arbitrary PowerShell — a visible base64-decode-and-`iex`
  line discloses nothing useful. Disclosure is the control; it is not a sandbox. Skills needing a binary
  stay on the CLI path, where the owner is already at the keyboard.
- **D8 — a new `skill_proposals` table, not a `kind` on `memory_proposals`.** The columns barely overlap
  and the memory panel would have to filter a row type it knows nothing about.
- **D9 — NTFS alternate data streams are an accepted, recorded residual.** `File.Copy` is believed to
  preserve ADS (**unverified this session**), no hash reads them, and detecting them needs P/Invoke.
  Under D4 the source lives in a git-tracked room directory and under D7 it is all reviewable text, so
  the vehicle is narrow. Declined for v1 and recorded here rather than silently omitted; it belongs with
  row 13's residuals.

## Tasks

### Task 1 — `SkillImport.Validate`: the refusal battery with no side effects
`src/ChopItUp.Hub/Skills/SkillImport.cs`. Extract refusals 1–9 out of `RunCore` into
`SkillImport.Validate(sourceDir, skillsRoot, force, overlayDir)` returning a `SkillImportResult`
extended with `Files`, `Bytes` and `ReplacesInstalled` (task 5 records all three; the current record
carries only `Files`). `RunCore` calls it. Two things currently run *before* some refusals and must stay
in `RunCore`: `Directory.CreateDirectory(skillsRoot)` and the `.replaced` restore. `Validate` must
therefore not assume the skills root exists, and — pass 1's finding — must treat a present
`<name>.replaced` with an absent `<name>` as *installed*, or it will disagree with `RunCore`'s refusal 8
after a torn import. Add two refusals: D7's extension allowlist, and a root/ancestor link check using
the existing `RoomPaths.ResolveLinks`/`LinkTarget` seam (`RoomPaths.cs:84-118`) — today's
`FindReparsePoint` (`SkillImport.cs:372-390`) attribute-checks children only, so a junction *named*
`<slug>` passes. Also expose `SkillImport.HashSourceTree(root)` (the private `HashTree`, generalised to
apply `CopyTree`'s root-`.git` skip) and `SkillImport.ManifestDigest(map)` — an ordinal sort of
`path\n<sha>\n` folded to one SHA-256, so the `tree_sha256` column and the request body have one defined
meaning.

**Locking contract (pass 2).** `Run` wraps `RunCore` in the `Global\ChopItUp.Skills.<root>` mutex that
`SkillStore.Read`/`List` also take (`SkillImport.cs:63-64`). `Validate` is that battery lifted out from
under it, so `Validate` **takes the same mutex on its own**, with the same 10-second timeout; on timeout
it returns `IoFailure` with a message the MCP caller can act on ("the skill store is busy; try again"),
never a partial answer computed against a store mid-swap. `RunCore` calls the inner, already-locked
form so the mutex is not re-entered.

RED first: a test calling `Validate` against a source that fails refusal 8 (target exists, no force),
asserting that no `<name>.importing` directory and no `skills` row appeared — and that this holds when
the skills root did not exist beforehand.

### Task 2 — `SkillImport.Run` pins the staged copy (D5)
Same file. `Run`/`RunCore` gain an optional `IReadOnlyDictionary<string,string>? expectedTree`. After
`CopyTree(sourceDir, staging)` (and after the overlay copy), hash staging with `HashTree` and, when
`expectedTree` is non-null and disagrees, `RollBack` and return `BadArgument` naming the first differing
path. This runs before either `MoveWithRetry`, so a mismatch never touches the installed skill. The CLI
path passes null and is unchanged.

RED first, per lesson M24 — do not race the real path: call the comparison helper directly with a
manifest that differs in one path and assert the refusal; then keep a real-path test that pre-seeds a
staging mismatch, so the helper is proven wired in.

### Task 3 — schema 10: the `skill_proposals` table
`src/ChopItUp.Core/Storage/ChopDb.cs`. Bump `LatestSchemaVersion` to 10; add a v10 migration step in the
existing transactional style (DDL + `PRAGMA user_version = 10` in one transaction, `IF NOT EXISTS`).
Columns: `id` INTEGER PK AUTOINCREMENT, `room_id` TEXT NOT NULL REFERENCES rooms(id), `author_id` TEXT
NOT NULL REFERENCES participants(id), `name` TEXT NOT NULL, `source_dir` TEXT NOT NULL, `tree_sha256`
TEXT NOT NULL, `replaces_installed` INTEGER NOT NULL, `force` INTEGER NOT NULL, `files` INTEGER NOT
NULL, `bytes` INTEGER NOT NULL, `status` TEXT NOT NULL DEFAULT 'pending', `created_at` TEXT NOT NULL,
`decided_at` TEXT, `installed_at` TEXT. Index on `(status, room_id, id)`, mirroring
`ix_memory_proposals_status`. Add a v9-fixture case to the existing schema-migration tests.

`force` is a column because of pass 2's second blocker: `SkillImport.Run` takes a `force` flag, the
first revision never said what approve passes, and **both** derivations are wrong — deriving it from
`replaces_installed` lets a stale value decide a destructive replace, and hard-coding it true lets a
card the owner read as "new" silently overwrite a skill installed since. The decision is persisted at
propose time from the tool argument, and approve re-checks the installed state against it (task 7).

**Lesson M11 binds and is ledger claim 12:** pass 2 found the first revision's sweep list short by one.
Ten scripts match the literal `schema -eq 9` (`Invoke-M2DryRun`, `M4SelfCheck`, `M5`, `M9`, `M10`,
`M11`, `M18`, `M19`, `M20`, `M23DryRun`), but `tools/Invoke-M23MemoryCheck.ps1:32` asserts it as a
parameter default, `[int]$ExpectedSchema = 9`, consumed at line 95 — a form the first recheck's pattern
missed entirely, so the gate would have gone green over an unswept script. **Eleven scripts**, and the
sweep is form-insensitive: every `schema`-adjacent 9 and every `ExpectedSchema` default, plus any
roster/`tokenKeys` count the same scripts assert.

### Task 4 — `SkillProposalStore`
`src/ChopItUp.Core/Storage/SkillProposalStore.cs` (new — beside `MemoryProposalStore.cs`, which lives in
`Storage/`, not `Memory/`), modelled on it: `Add`, `Get`,
`List(room, status)`, `FindPending(name, treeSha)` (AC3's dedup — `MemoryProposalStore.cs:159` has the
analogue), `MarkApproved`, `MarkRejected`, `MarkInstalled`. Same `Undecided`/`Pending`/`Approved`/
`Rejected` vocabulary as memory so the two panels read alike — **including memory's definition of
`Undecided` as pending ∪ approved-with-nothing-written** (`MemoryProposalStore.cs:80-84`), which here
means pending ∪ approved-with-`installed_at`-NULL. Pass 2's first blocker was that the first revision
added a Retry arm to task 7 while leaving `Undecided` implicitly pending-only, so the row the Retry arm
exists to finish would never have been listed and the arm was unreachable.

Also cap undecided proposals per room. Dedup is keyed on `(name, treeSha)`, so an agent can otherwise
mint unbounded distinct proposals, each of which the unauthenticated `GET` re-hashes on every request
(pass 2, finding 10).

### Task 5 — the `propose_skill` MCP tool
`src/ChopItUp.Hub/Mcp/SkillTools.cs` (new), registered where the other `[McpServerToolType]` classes
are. `propose_skill(room_id, source_dir, force = false)`, `ReadOnly = false`, `Destructive = false`,
`Idempotent = false`. It resolves `source_dir`, refuses anything outside the room's bound directory
(D4, reusing `RoomPaths`' own refusals), calls `Validate`, and on any non-`Ok` outcome returns that
message as the tool error with nothing recorded. On `Ok` it dedups via `FindPending`, records the
proposal with `ManifestDigest(HashSourceTree(source))`, and posts a room note naming the skill, the file
count and the proposer — best-effort, the row is the arbiter (the `MemoryTools` stance). The overlay
refusals of D6 come back from `Validate`, so this task adds no new refusal logic beyond the path
confinement.

### Task 6 — owner auth on the decision endpoints (D1, D2)
`src/ChopItUp.Hub/Security/BearerTokenMiddleware.cs`: widen the guarded prefix set from `/mcp` alone to
`/mcp` plus `POST /api/skills/proposals/*/approve|reject`, keeping the existing 401 shape for a missing
or unresolvable credential. The handlers then check the resolved participant is `owner` or
`owner-remote` and return **403** otherwise — 401 and 403 are different answers to different questions,
and pass 2 found the first revision's AC, task and ticket each giving a different one. AC5 now states
both; ticket 06 matches. Every existing `/api` route must remain unauthenticated — a test asserts
`/api/memory/proposals/{id}/approve` still works with no header, so this change cannot silently widen.

### Task 7 — `/api/skills/proposals`: list, approve, reject
`src/ChopItUp.Hub/Web/SkillsApi.cs`. Add `MapGet("/skills/proposals")` and the two POSTs. `GET` reads
the source tree at request time, computes its current digest, and returns `sourceChanged` when it
differs from the recorded one — **with file contents suppressed in that case** (pass 1's swap-back
attack: what the card renders must be what is pinned). Otherwise it returns relative paths, declared
gates (via `SkillStore.StripFrontmatter`, the same parser import uses), and the full text of every file.
No display cap is needed: D7 refuses an over-`MaxSkillChars` file at propose time, so nothing that
reaches a card is un-showable, and the first revision's truncation-blocks-approval dead end is gone. A
vanished source directory is an explicit `sourceMissing` state. Cache the computed digest against the
source's newest write time so a card refetching on every hub note does not re-hash the tree each time.

`approve` takes a JSON body `{ treeSha256 }`, the one-at-a-time `SemaphoreSlim` `MemoryApi` uses, and
refuses in this order: `AnySpawnInFlight` (D3); body ≠ recorded hash; `ReplacesInstalled` re-checked
against the store now and disagreeing with what the listing showed (the `force` blocker — a card the
owner read as "new" must never silently overwrite a skill installed since). Then — **order corrected per
pass 1**, which found the first draft misquoted `MemoryApi.cs:161-162` — it marks approved with
`installed_at` NULL, calls `SkillImport.Run` with the persisted `force` and the recorded manifest as
`expectedTree`, then `MarkInstalled`, then the note.

**The Retry arm, reworked per pass 2's first blocker.** `MoveWithRetry`'s own doc comment says the swap
fails in the *ordinary* case under a live reader (`SkillImport.cs:318-323`), so approved-but-uninstalled
is a routine outcome of approving during an exchange, not a rare crash. A repeat approval on such a row
therefore must not simply re-enter `Run`: if the previous attempt's install actually completed, refusal
8 (`SkillImport.cs:202-204`, target exists without force) would refuse it forever, stranding installed
code under a row that can never be finished — the exact state pass 1's fix claimed to remove. So the
Retry arm first hashes the **installed** tree and compares it to the recorded manifest; on a match it
goes straight to `MarkInstalled`, and only on a mismatch does it re-run `Run`.

### Task 8 — the approval card (owner-visible; build with `opus`)
`src/ChopItUp.Hub/client/src/` — a `SkillProposalCard`, the `SkillProposal` type in `types.ts`, and the
fetch/decide calls in `api.ts`. The card leads with the skill name and a replace-or-new badge, then the
file list, then — expanded by default, not behind a disclosure — the declared gates and the full text of
**every** file (D7). `SKILL.md` goes through `renderBody`'s existing sanitiser; every other file renders
as `<pre>` text, never as markdown. `sourceChanged` and `sourceMissing` each render as a banner that
disables approve. An approved-but-uninstalled row renders as its own state with a **Retry** button, the
way `MemoryPanel.tsx:182` already does for a memory write — pass 2 found the first revision enumerated
four card states and left this one out, which is what made the Retry arm unreachable from the UI.
Without an owner token the card is read-only with a paste prompt (D2).
The card refreshes on the hub note the same way `App.tsx:198-210` refreshes memory proposals — that
wiring is the trigger, and lesson M9 says it is what a JSDOM test cannot see.

### Task 9 — the gates: dry run, self-check, live check, deploy
Row 24 shipped this tier with a migration dry run, two self-checks and a per-mechanism mutation test;
pass 1 was right that one live check is thinner than the precedent.
- `tools/Invoke-M25DryRun.ps1`: v9 → v10 migration over a fabricated real-shape database, in the style
  of `Invoke-M2DryRun.ps1`.
- `tools/Invoke-M25SkillProposalCheck.ps1`: scratch hub, fabricated source inside a room directory,
  `propose_skill` over MCP, list, assert the payload carries each file's text, assert 401 without the
  owner token, approve with it, assert `GET /api/skills` lists the skill; then the refusal legs —
  source mutated between propose and approve, `sourceChanged` suppression, and a staged-copy mismatch.
  Pass 2 found the first revision's legs did not cover the acceptance set; these are added and are not
  optional: **AC3** (a repeat offer of the same tree returns the first proposal, no second card),
  **AC5's 403 half** (a valid non-owner credential), **AC2's two new refusals** (a file type off the
  allowlist, and a source reached through a junction), and **AC8** (approve, kill the hub between the
  mark and the install record, restart, re-approve, and assert it finishes without re-installing).
  The live-hub swap failure is itself a leg: approve while an exchange holds a reader open, and assert
  the row lands approved-but-uninstalled rather than erroring.
  **Lesson M11's converse binds:** assert only what the hub controls — note text, payloads, exit codes —
  never a model's wording. **Lesson M10:** `Invoke-RestMethod` wraps a top-level JSON array. **Lesson
  2026-09-07:** `Start-Process -ArgumentList` joins with spaces and quotes nothing. Like the other
  M-check scripts, it leaves its data dir and log in place.
- The M24 step: revert each of this milestone's named mechanisms one at a time and record which test
  fails; a mechanism with no failing test gets one written.
- `tools\Invoke-M4SelfCheck.ps1` against the staging publish, then `tools\Deploy-ChopItUp.ps1`, with the
  schema-bump rollback that `docs/verification.md` makes mandatory recorded on the board row.

### Task 10 — move the prohibition into the contract, and retitle the row
Pass 2's third finding: the control's weakest point is procedural, and a procedure that lives only in a
plan file dies when the plan is deleted at close-out. Two edits, in the merge commit:
- `CLAUDE.md`, under **Safety invariants**, one line: the deployed hub's data directory is never written
  by any agent — not by `--import-skill` in any argument form, and its `tokens.json` is never read to
  obtain a credential; skills reach a deployed hub only through propose-and-approve. `CLAUDE.md` is
  budgeted at 4 KB and the roadmap gate ratchets its size, so this must land as one sentence, and
  something else in that file gives way if it does not fit.
(The row-25 retitle pass 2 also asked for was applied at plan time, not deferred to the merge: the old
title promised a hard boundary this milestone does not build, and leaving it standing through the build
would have misled every session that read the board in between.)

## Could not verify in this environment

- **Verified by pass 1, no longer open:** the guard denies a copy into the deployed skills directory,
  and permits both an MCP call to this hub and a loopback `Invoke-RestMethod` POST. Ledger claim 11
  carries the reproducible probe.
- That `--import-skill` without `--data` would in fact install into the deployed store: read from
  `HubOptions.Parse`'s default (`AppContext.BaseDirectory\data`) and deliberately **not** executed.
- Whether current Chrome blocks a cross-origin POST to loopback under private-network-access
  enforcement: **unverified**. D2's `Authorization` header makes the question moot for the two endpoints
  this milestone adds; it stays open for the rest of `/api` and belongs to row 13.
- Whether `File.Copy` preserves NTFS alternate data streams: **unverified** (D9).
- Whether the room UI's approval click can be verified with a real Browser-pane click: lesson M23 says
  the pane's injected clicks silently dispatch nothing when hidden. The `elementFromPoint` + `.click()`
  + server-side-effect substitute is what task 8's gate must use.

## Claim ledger

| # | Claim | Verified at (commit) | Recheck (pwsh, exit 0 = holds) |
|---|-------|----------------------|--------------------------------|
| 1 | Baseline: 787 tests green (211 Core + 576 Hub), measured this session | a11d23f | `dotnet test ChopItUp.slnx -c Debug --nologo -v minimal` |
| 2 | Pre-milestone baseline: `SkillsApi` mapped `GET /skills` and nothing else. Task 7 falsifies this by design | a11d23f | — (baseline; a live recheck would hard-fail `Check-PlanClaims` mid-build — pass 2 finding 6) |
| 3 | Pre-milestone baseline: `BearerTokenMiddleware` guarded `/mcp` only, so all of `/api` was unauthenticated. Task 6 falsifies this by design | a11d23f | — (baseline; see claim 2) |
| 4 | An installed skill's `scripts/<gate>.ps1` is executed under `pwsh` by `RunTools` | a11d23f | `pwsh -c "exit ([int](-not (Select-String -Path src/ChopItUp.Hub/Mcp/RunTools.cs -Pattern 'cliLocator\(\"pwsh\"\)' -Quiet)))"` |
| 5 | Pre-milestone baseline: `ChopDb.LatestSchemaVersion` was 9. Task 3 falsifies this by design | a11d23f | — (baseline; see claim 2) |
| 6 | `SkillImport.Run` is the whole install seam and is a public static entry point callable in-process (pinned to the entry point, not the parameter list, which task 2 extends — pass 2 finding 6) | a11d23f | `pwsh -c "exit ([int](-not (Select-String -Path src/ChopItUp.Hub/Skills/SkillImport.cs -Pattern 'public static SkillImportResult Run\(string sourceDir, string skillsRoot' -Quiet)))"` |
| 7 | Default data dir is `AppContext.BaseDirectory\data` when `--data` is absent | a11d23f | `pwsh -c "exit ([int](-not (Select-String -Path src/ChopItUp.Hub/Hosting/HubOptions.cs -Pattern 'Path.Combine\(AppContext.BaseDirectory, \"data\"\)' -Quiet)))"` |
| 8 | `MemoryApi.Approve` refuses while a spawn is in flight — the pattern D3 copies | a11d23f | `pwsh -c "exit ([int](-not (Select-String -Path src/ChopItUp.Hub/Web/MemoryApi.cs -Pattern 'spawner.AnySpawnInFlight' -Quiet)))"` |
| 9 | `SkillStore` caps: `MaxFiles` 200, `MaxBytes` 2 MiB, `MaxSkillChars` 32,000, `MaxOverlayChars` 8,000 | a11d23f | `pwsh -c "exit ([int](-not (Select-String -Path src/ChopItUp.Hub/Skills/SkillStore.cs -Pattern 'MaxFiles = 200' -Quiet)))"` |
| 10 | `memory_proposals` is the table shape task 3 mirrors, with `ix_memory_proposals_status` | a11d23f | `pwsh -c "exit ([int](-not (Select-String -Path src/ChopItUp.Core/Storage/ChopDb.cs -Pattern 'ix_memory_proposals_status' -Quiet)))"` |
| 11 | The privacy guard denies a file write into the deployed skills directory, and permits both an MCP call to this hub and a loopback POST (verified by pass 1 against the real hook) | pass 1, 2026-09-09 | — (two-step by construction, so not a single recheck: write a synthetic PreToolUse JSON for the write to a scratch file, pipe it to the guard hook, assert the reply carries a deny decision. The one-liner form cannot be used here — its own command text names the guarded path, so the guard denies the probe.) |
| 12 | Eleven scripts under `tools/` pin schema 9 and must all be swept — ten as the literal `schema -eq 9`, plus `Invoke-M23MemoryCheck.ps1:32`'s `[int]$ExpectedSchema = 9` (pass 2 finding 5) | a11d23f | `pwsh -c "exit ([int]((Select-String -Path tools/*.ps1 -Pattern 'schema -eq 9','ExpectedSchema\s*=\s*9' \| Select-Object -ExpandProperty Path -Unique).Count -ne 11))"` |
| 13 | `RoomPaths` already exposes a per-segment link resolver task 1 reuses | a11d23f | `pwsh -c "exit ([int](-not (Select-String -Path src/ChopItUp.Hub/Rooms/RoomPaths.cs -Pattern 'ResolveLinks' -Quiet)))"` |
| 14 | `MemoryProposalStore` exposes the pending-lookup analogue task 4's `FindPending` mirrors, at `Storage/MemoryProposalStore.cs:159` | a11d23f | `pwsh -c "exit ([int](-not (Select-String -Path src/ChopItUp.Core/Storage/MemoryProposalStore.cs -Pattern 'public MemoryProposal\? FindPending' -Quiet)))"` |

## Critique pass 1 dispositions (fable, REFRAME / 4.5)

| # | Finding | Disposition |
|---|---------|-------------|
| 1 | The guard-bound principal can propose and self-approve; the card is theatre | **Fixed** — D1/D2 credential-gate the decision endpoints; task 6. The residual against a hostile shell is stated in "What this control does and does not defend against" rather than papered over. |
| 2 | The recorded hash never binds the bytes that install | **Fixed** — D5, task 2: the pin is checked against the staged copy before the swap. |
| 3 | Refusal 6 never checks the source root or its ancestors for a reparse point | **Fixed** — task 1 reuses `RoomPaths.ResolveLinks`. |
| 4 | What the card shows is not what approve installs (swap-back) | **Fixed** — task 7 returns `sourceChanged` and suppresses contents; approve takes the hash in the body. |
| 5 | The bodiless approve POST is browser-CSRF-able | **Fixed** — D2's `Authorization` header makes the request non-simple. The rest of `/api` shares the shape; recorded for row 13. |
| 6 | The card shows the entry point, not the code | **Fixed** — D7's text allowlist; the card renders every file; truncation blocks approval. |
| 7 | An unconfined `source_dir` violates grill D10 and makes the hub an existence oracle | **Fixed** — D4 confines the source to the room's bound directory. |
| 8 | The approve order misquoted `MemoryApi` and left an unrecoverable crash state | **Fixed** — task 7 marks before writing and adds the Retry arm. |
| 9 | Gate list thinner than the same-tier precedent; no deploy task | **Fixed** — task 9. |
| 10 | The M11 sweep was called a ledger claim but had no ledger row | **Fixed** — claim 12. |
| 11 | Missing interface contracts; no dedup of repeat proposals | **Fixed** — task 1 extends `SkillImportResult` and aligns `Validate` with the `.replaced` case; task 4 adds `FindPending`; AC3. |
| 12 | The card's refresh trigger and render path were unspecified | **Fixed** — task 8 names the note trigger and the sanitiser/`<pre>` split. |
| 13 | Claim 11 now verified; the "only thing standing between a spawn and a gate script" sentence was inferred | **Fixed** — claim 11 carries the probe; that sentence is gone, replaced by the scoped statement of what this control binds. |
| 14 | Lessons M10, M9, M1 and the 2026-09-07 `Start-Process` lesson omitted; ticket 07's cleanup contradicted the M11 style | **Fixed** — cited in tasks 3, 8 and 9; ticket 07 corrected. |
| 15 | NTFS alternate data streams are copied and never hashed | **Declined for v1, recorded** — D9. Detection needs P/Invoke; D4 and D7 narrow the vehicle; it belongs with row 13's residuals. |

## Critique pass 2 dispositions (opus, FIX-THEN-SHIP / 6.0)

| # | Finding | Disposition |
|---|---------|-------------|
| 1 | BLOCKER: the Retry arm is unreachable and its main branch unrecoverable — approved-but-uninstalled is the *ordinary* outcome under a live reader, was never in `Undecided`, had no card state, and a retry hit refusal 8 forever | **Fixed** — task 4 defines `Undecided` as memory does; task 7's Retry arm hashes the installed tree and short-circuits to `MarkInstalled` on a match; task 8 adds the state and the Retry button; AC8; task 9 adds the interrupt leg and the live-reader leg. |
| 2 | BLOCKER: `force` is neither persisted nor specified, so approve has no defined behaviour against an installed skill of the same name | **Fixed** — task 3 adds a `force` column set at propose time; task 7 re-checks `ReplacesInstalled` at decision time and refuses on disagreement rather than replacing. |
| 3 | MAJOR: the control's credential sits in `tokens.json` in the data dir, which row 13 measured as Bash-reachable; the row title becomes false | **Fixed as far as it can be, and the residual is stated** — no location on this box is unreadable to a shell, so the plan stops claiming one and says the weakest point is procedural. Task 10 moves the prohibition into `CLAUDE.md`'s safety invariants (surviving the plan's deletion) and retitles row 25. Making the credential unreachable is row 13. |
| 4 | MAJOR: the 8,000-char display cap mints proposals that can never be approved (`MaxSkillChars` is 32,000), and D7's disclosure guarantee was overstated | **Fixed** — D7 refuses over-`MaxSkillChars` files at propose time and drops the display cap and the truncation dead end; the guarantee is restated as "the owner is shown every byte", explicitly not a sandbox; case-insensitive matching and extensionless files specified. |
| 5 | MAJOR: claim 12's recheck passes while certifying the wrong sweep list — `Invoke-M23MemoryCheck.ps1:32`'s `$ExpectedSchema = 9` is missed | **Fixed and re-measured this session: eleven files, not ten.** Claim 12 rewritten form-insensitively and executed (exit 0 at 11). |
| 6 | MAJOR: claims 2, 5 and 6 are falsified by the milestone's own tasks, so `Check-PlanClaims` hard-fails mid-build | **Fixed** — 2, 3 and 5 restated as pre-milestone baselines with `—` rechecks; 6 re-pinned to the entry point rather than the parameter list task 2 extends. |
| 7 | MAJOR: this is two milestones; split at tasks 1-2 versus 3-9 | **Declined, with rationale.** Tasks 1 and 2 are hard prerequisites of tasks 5 and 7 — propose cannot refuse without `Validate`, approve cannot pin without `expectedTree` — so splitting buys a separate rollback story for a one-file, no-schema change whose two defects are reachable today only by an owner running the CLI import over a hostile source. Reviewability is already served by one commit per task. Recorded here so the call is visible rather than silent. |
| 8 | MAJOR: task 9's gates miss AC3, AC5's 403 half, AC4's truncation half and AC2's new refusals; ticket 06's `Blocked by: —` is false; 401 vs 403 is stated three ways | **Fixed** — task 9 lists the missing legs (AC4's truncation half is gone with finding 4); ticket 06 now `Blocked by: 07`; AC5, task 6 and ticket 06 all say 401-for-absent, 403-for-non-owner. |
| 9 | MINOR: D4's "free audit record" is unearned, has no cleanup, and misattributes the tree to a later spawn | **Fixed** — the audit claim is withdrawn in D4 with the mechanism named; cleanup on decide is in task 7's scope. Pass 2's own verification that the confinement does not break the workflow is folded into D4. |
| 10 | MINOR: unauthenticated `GET` re-hashes every pending tree per request, with no cap on pending proposals | **Fixed** — task 4 caps undecided proposals per room; task 7 caches the digest against the source's newest write time. |
| 11 | MINOR: `Validate` is the refusal battery lifted out from under `PathMutex` with no locking contract | **Fixed** — task 1 states the contract: `Validate` takes the same mutex with the same timeout and returns `IoFailure` on contention; `RunCore` calls the inner already-locked form. |

## Lessons consulted

`M11` (schema-literal sweep on a version bump; a live check asserts only what the hub controls), `M1`
(DDL, seeds and the `user_version` stamp in one transaction), `M23` ui-gate (hidden-pane clicks dispatch
nothing — use `elementFromPoint` + `.click()` + server-side effect), `M23` subagents (single-shot
critics run synchronously with the anti-wait clause), `M24` (a guard test whose RED depends on timing
binds nothing — test the deciding function directly and keep the real-path test as well; revert each
mechanism to find out which tests bind), `M18` (`dotnet clean` before the `-warnaserror` gate; an
incremental 0-warning build is not evidence), `M21` (the board's `Plan` cell is a literal path, never
backticked), `M10` (`Invoke-RestMethod` wraps a top-level JSON array), `M9` (a card's refresh trigger is
hub-note wiring a JSDOM test cannot see), the 2026-09-07 lesson (`Start-Process` joins `-ArgumentList`
with spaces and quotes nothing), and `M20` (a long server-side MCP call needs a 30 s progress cadence —
task 5's `propose_skill` hashes a tree of at most 2 MiB, nowhere near the 300 s cut, so no cadence is
added; recorded so the reasoning can be attacked rather than the omission).
