# Row 24 — Memory v1.1c: the Claude Code-shape vendor export

**Goal.** Close findings-ledger item 8: a `--export-memory <dir>` verb that renders the hub's memory store into the shape Claude Code's `autoMemoryDirectory` reads, and that cannot silently destroy what is already in that directory.

**Architecture.** `MemoryExport` is a pure renderer: it turns the store's live entries into a set of in-memory files — one memory file per entry, plus the `MEMORY.md` index Claude Code loads each session — and a manifest that binds the export to *the store it came from*, by that store's root, not merely to its own output. `MemoryExportWriter` is the guarded writer: it stages the whole export in a sibling directory, classifies the target against the previous export's manifest, re-checks that classification immediately before it touches anything, and swaps the staged directory into place, so the target is never in a state where its files and its manifest disagree. The CLI verb is a non-serving `HubCommand` in the existing `HostCommands` shape. The inverse operation already exists — `MemoryImport` reads exactly this shape — which makes an export/import round trip the strongest machine-checkable acceptance this row has.

**Author model:** Opus 5. **Routing mismatch:** the workflow routes HIGH-tier planning to Fable; this session is Opus. Pass 2 was therefore mandatory and was run: pass 1 `fable` (FIX-THEN-SHIP 5.8), pass 2 `opus` (FIX-THEN-SHIP 6.7). Pass 2 found that two of pass 1's fixes had each introduced a fresh defect, which is the whole reason it exists.

**Blast radius: HIGH.** The target directory is the owner's real Claude Code memory directory, holding memories no other copy exists of. Under D1 everything the export *writes* is a reproducible copy of the hub store, so the only irreplaceable bytes in the target are the ones that got there in spite of D1 — a Claude Code session's own writes, the owner's real directory on a first run, or a previous export of a store that has since **shrunk**. That last one is pass 2's BLOCKER 3 and is why D8 is keyed the way it is. No schema change, no migration: this verb reads the memory store and the filesystem, and touches neither SQLite nor the serving path.

Written for builder-subagent execution; if something doesn't match, STOP and report rather than guess.

**Lessons consulted** (`docs/LESSONS.md`): M11 (a live check may assert only what the hub controls; sweep every copy of a literal before deploying), M10 (`Invoke-RestMethod` array unwrapping), 2026-09-07 (`-ArgumentList` quotes nothing — every path quoted, and trailing separators trimmed *before* quoting), M18 (`dotnet clean` before the `-warnaserror` build), M21 (the board's `Plan` cell is a bare path, never backticked).

---

## Lead check — corrections to the row's own claims

Re-verified against `196cb27` this session.

| Row 24 / ledger claim | Status |
|---|---|
| Ledger item 8: the memory-file template is `---` / `name:` / `description:` / `metadata:` / `  type:` / `---` / body | **Holds — and revision 1 of this plan wrongly "corrected" it.** The installed 2.1.220 binary carries **both** templates, about forty lines apart: `metadata:` / `  type: user \| feedback \| project \| reference` with `description: <one-line summary — used to decide relevance during recall>`, **and** a `metadata:` / `  pinned: { … }` variant. Revision 1's extraction stopped at the first hit and generalised from one sample. The ledger was right. |
| `metadata` is a fixed schema | **False, and this is the surviving correction.** Two variants ship in one binary, so `metadata` is an open map whose keys vary by context; `name` and `description` are the stable pair. |
| `autoMemoryDirectory` / `autoMemoryEnabled` exist at 2.1.220 | **Holds.** `autoMemoryDirectory` ×9, `autoMemoryEnabled` ×6, `autoMemory` ×15, `MEMORY.md` ×17 (UTF-16 region). |
| Index shape `- [Title](file.md) — hook`, capped at 200 lines / 25,000 | **Holds, with the units and the arithmetic both corrected (pass 2 BLOCKER 2, MAJOR 7).** The relevant code, extracted this session as **single-byte text at byte offset 241127972** — *not* UTF-16, which is why revision 2's own re-verification line reported False: `function vRt(e){let t=e.trim();return{trimmed:t,lineCount:au(t,"\n")+1,byteCount:t.length}}` and `var PS="MEMORY.md",mie=200,Lxe=25000`, consumed by `Htr` as `i=n>mie, s=o>Lxe` → `r.split("\n").slice(0,mie).join("\n")`. So: **`lineCount` is newlines + 1 over the WHOLE TRIMMED FILE** (a two-line header therefore costs two of the 200), and **`byteCount` is `t.length`, JavaScript string length — UTF-16 code units, which is exactly C#'s `string.Length`, not UTF-8 bytes.** Over either cap it **slices**, keeping the first 200 lines. |
| Row Notes: "HIGH (delete/replace path aimed at the owner's real memory dir)" | **Holds** — see the tier note above. |
| Row Notes: the findings ledger's paired delete belongs here | **Holds.** `docs/superpowers/plans/memory-v1-1-findings.md` names row 24 as its deleter; it is deleted in the commit that flips this row to DONE. |

**Extraction commands for the two `—` ledger rows** (21 and 22). The two regions are in **different encodings** — that is the trap revision 2 fell into, so both are given:

```powershell
$b = [System.IO.File]::ReadAllBytes("$env:USERPROFILE\.local\bin\claude.exe")
# Templates: UTF-16 region
$uni = [System.Text.Encoding]::Unicode.GetString($b)
$i = $uni.IndexOf('used to decide relevance during recall'); $uni.Substring($i-320, 620)   # metadata.type
$i = $uni.IndexOf('pinned: { true if');                      $uni.Substring($i-320, 620)   # metadata.pinned
# Index caps and the trimming function: SINGLE-BYTE region (Latin1), NOT Unicode
$lat = [System.Text.Encoding]::Latin1.GetString($b)
$i = $lat.IndexOf('mie=200');          $lat.Substring($i-420, 700)                          # vRt + the constants
$i = $lat.IndexOf('function Htr(');    $lat.Substring($i, 520)                               # the slicing
```

This plan asserts what the exported files **are**, never what Claude Code **does** with them. Every acceptance criterion is checkable without a Claude Code session; the one question that is not is the owner probe in Verification.

---

## Design decisions settled here

**D1 — The export owns its directory.** `autoMemoryDirectory` is pointed at a dedicated export directory, never at one Claude Code also writes into. Stated in `## Acceptance` (criterion 11), in the verb's refusal text, and in `docs/verification.md`. Rationale: the third ledger finding is real and unavoidable — Claude Code maintains its own `MEMORY.md` and writes memory files where it is pointed, so any shared directory is one whose next export must refuse. Ownership makes that refusal correct rather than a defect.

**D2 — Every override prints what it will destroy before destroying it.** Drift refuses and lists every affected path; an override prints the identical list, from the same function, and proceeds. This binds `--force` **and** `--accept-new-source` (pass 2 MAJOR 4: the wider override was the one that printed nothing).

**D3 — Stage and swap, never write in place.** The export is built in full in a sibling staging directory, manifest included, then swapped in. Rationale: it closes the second ledger finding by construction — manifest and files land in one operation, so the target can never hold files its manifest does not describe.

*Why not the non-deleting variant* (pass 2 MINOR 16): writing our own files in place and deleting only files the previous manifest names would never touch an unknown file, which would dissolve `Foreign`, the retention rule and both previous-directory names. It was rejected because it cannot deliver the property the binding spec's finding 2 actually asks for: with files written individually and the manifest updated afterwards, a crash leaves a target the next run reads as drifted — the wedge, reached by a different road. Stage-and-swap buys the land-together property, and the cost is that deletion becomes an operation the guard must make safe. D8 and T3 step 3 are that cost, paid explicitly.

**D4 — Source identity is the store's ROOT PATH, and only that.** The manifest also records a fingerprint of the store's contents, but the fingerprint is **report-only** and is never a refusal predicate. Rationale (pass 1 F1): the content fingerprint changes on every legitimate re-export, so refusing on it would refuse every normal run; and the actual finding-1 attack — exporting a scratch store whose entries happen to match — produces an *identical* fingerprint by construction. Only the root distinguishes the stores. Comparison is `OrdinalIgnoreCase` over `Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))` on each side.

**D5 — A populated directory with no manifest is `Foreign`, and `Foreign` refuses.** Only an absent or empty directory proceeds unguarded. Rationale (pass 1 F2): the owner's real memory directory on first contact has no manifest, and treating "no manifest" as "nothing to protect" makes the plan's headline hazard the default path.

**D6 — The caps are measured on the RENDERED INDEX, in the consumer's own units, and over the cap the export refuses.** (pass 2 BLOCKER 2.) The authoritative checks are `MaxIndexLines = 200` against the rendered index's own line count — newlines + 1 over the trimmed text, so the two-line header costs two — and `MaxIndexUnits = 25_000` against `index.Length`, C#'s UTF-16 count, which is the same unit `t.length` counts in the consumer. `MaxMemories = 198` exists only as a cheap early refusal and carries the header arithmetic in a comment; it is a floor, never the cap. Rationale: an entry-count proxy drifts from the header the moment anyone edits the header, and the consumer **slices** rather than refusing, so an off-by-two loses the last memories at exit 0.

**D7 — Superseded entries are not exported.** They are tombstones; `Titles()` already excludes them.

**D8 — The previous export directory is deletable only when the new export is a SUPERSET of it.** Retention is keyed on the manifests, not on the verdict: the aside directory takes the plain name `<dir>.chopitup-export-previous` only when the replaced directory was `Clean` **and** every key in its manifest's `Files` is also produced by this export. Otherwise it takes a timestamped name and is never auto-deleted. Rationale (pass 2 BLOCKER 3): keying on `Clean` alone assumes the store only ever grows. It does not — row 23's consolidation exists precisely to shrink topics, and a restored backup or a mistyped `--data` shrinks it too. A `Clean` previous export of 40 memories replaced by an export of 3 would land in the deletable slot and vanish on the run after, which is pass 1's F3 surviving F3's own fix. The superset test is the predicate D8 always meant.

**D9 — A moved data directory is resolved with `--accept-new-source`, never with `--force`.** `DifferentSource` stays non-overridable by `--force`, because overriding it *is* finding 1. But it needs a sanctioned exit, or a reinstall to a new path locks the target forever. `--accept-new-source` prints both roots **and the full drift list** (D2) and proceeds.

**D10 — The hub's core becomes memory files, and the target's `MEMORY.md` is only ever the index.** Core entries export as ordinary memory files; the target index is generated from all exported entries. Rationale: the consumer's own instruction is that content never goes in the index. Note the consequence for `metadata.type`: hub topics are not the vendor's four-value enum, so exported files legitimately carry `type: core`, `type: room-general` and so on — see the probe discriminators in T6.

**D11 — The verb refuses while a hub is running** (`HubLock.IsHeld`, exit 5, `RotateToken`'s idiom). Rationale: an approval landing mid-export produces a snapshot of a state that never existed. This does **not** stop a Claude Code session writing into the *target*, which is why T3 re-verifies before it moves anything (pass 2 MAJOR 6).

---

## Critique dispositions

Pass 1 (`fable`) **FIX-THEN-SHIP 5.8**, fifteen findings, all folded in revision 2. Pass 2 (`opus`) **FIX-THEN-SHIP 6.7**, seventeen findings, all folded here. Pass 2's brief was to hunt what pass 1 missed and to check whether pass 1's fixes introduced defects; two of them had.

### Pass 1 (folded in revision 2)

| Finding | Disposition |
|---|---|
| F1 `DifferentSource` was a content-fingerprint mismatch — refuses every legitimate re-export, blind to the real attack | **Fixed.** D4: root is the sole predicate; fingerprint demoted to report-only. |
| F2 a populated directory with no manifest proceeded | **Fixed.** D5, the `Absent`/`Foreign` split. |
| F3 one previous-export slot destroyed `--force`-rescued files two runs later | **Fixed in revision 2, then found still broken by pass 2** — see B3 below. |
| F4 trailing separator put the stage inside the target | **Fixed.** Trim at parse time; claim 20. |
| F5 `EnsureLayout` writes, so a mistyped `--data` was seeded and exported | **Fixed.** The existence fence; claim 17. Wording corrected by pass 2's MINOR 11. |
| F6 `DifferentSource` had no sanctioned exit | **Fixed.** D9. |
| F7 the lead check's "correction" was itself false | **Fixed**, and recorded rather than quietly removed. |
| F8 ledger finding 2 closed by prose | **Fixed.** The `FileShare.Read` swap-failure test; AC8's enumerated states. |
| F9 the owner's home path in a public repo | **Fixed.** `$env:USERPROFILE` throughout. |
| F10 D1 was not in Acceptance | **Fixed.** Criterion 11. |
| F11–F15 `Slugify` dilemma; drift scope; staging collision; exit code and fixture path; index escaping, empty hook, schema-evolution guard | **All fixed.** |

### Pass 2 (folded here)

| # | Finding | Disposition |
|---|---|---|
| B1 | **Blocker, and a defect introduced by F3's fix.** D8 says the unsuffixed previous is deleted by a later run; no step in T3 ever deleted it, and `Directory.Move` onto an existing destination throws — measured. The **third** consecutive export fails outright, recoverable only by a hand-delete the runbook forbids | **Fixed.** T3 gains an explicit deletion step before the aside move, and a three-consecutive-exports test — the plan's own tests all stopped at two, which is why nothing saw it. |
| B2 | **Blocker.** `MaxMemories = 200` renders a 202-line index (`# Memories` + blank + N), and the consumer's `lineCount` counts the whole trimmed file, then **slices** to 200. 199 entries loses the last memory, 200 loses two, at exit 0 | **Fixed.** D6: the caps are measured on the rendered index in the consumer's own units; `MaxMemories = 198` is a floor with the arithmetic in a comment. T1 tests the 198/199 boundary, not only 201. |
| B3 | **Blocker, and the second defect introduced by F3's fix.** D8's stated predicate ("bytes the export cannot reproduce") and T3's implemented one (`state == Clean`) coincide only if the store never shrinks. Row 23's consolidation shrinks topics by design; so does a restored backup. A `Clean` 40-memory previous replaced by a 3-memory export lands in the deletable slot and is gone one run later, with no `--force` anywhere | **Fixed.** D8 is re-keyed on a **superset** test over the two manifests, and T3 tests a shrinking store. |
| M4 | `--accept-new-source` was the widest override and the only one that printed nothing: `Verify` short-circuits at `DifferentSource`, so drift under it is never computed | **Fixed.** D2 now binds both overrides; T3 computes and prints the drift list on the accept path too, and the replaced contents land in a timestamped previous. |
| M5 | `ExportResult` was never defined, and `HostCommands.cs:151` already maps `InvalidOperationException` to exit **3** — so AC3's exit 6 had no implementer and a builder following the sibling idiom would ship the wrong code | **Fixed.** T3 defines `ExportResult` with an explicit `ExitCode`, catches the renderer's refusal and maps it to 6; T6's dry run asserts exit 6 for the over-cap case. |
| M6 | The verdict deciding both the refusal and the retention name was computed at step 2 and never revalidated before the destructive move at step 4. D11 stops the hub, not a vendor session writing into the target | **Fixed.** T3 re-verifies immediately before the aside move and aborts if the verdict changed. |
| M7 | Claim 21 was wrong in its units (UTF-16 code units, not UTF-8 bytes) **and** its advertised re-verification line returns False, because the caps live in a single-byte region while the templates live in a UTF-16 one | **Fixed.** Both encodings are in the lead check's extraction block; T1's rule is `index.Length`; the multi-byte test premise is deleted. |
| M8 | Ticket 02's acceptance said a hand-written older-shape record must read as *unreadable*; plan T2 says it must **read**. A builder following the ticket ships the wedge finding 2 exists to prevent | **Fixed.** Ticket 02 rewritten. |
| M9 | Tickets 02 and 03 still named the pre-revision mechanisms in their `Detail:` lines | **Fixed.** |
| M10 | AC10's zero-entry clause, AC6's identical-list requirement, and ticket 01's provenance-exclusion had no named test | **Fixed.** One test each; the AC6 one captures both outputs and asserts string equality. |
| M11 | "Never call `EnsureLayout`" is unachievable — `ListTopics` and `Entries` call it internally | **Fixed.** The fence is named as the actual mechanism, and the plan says plainly that the export does touch the source layout. |
| M12 | Staging directories had no disposal path ever | **Fixed.** T6's runbook names owner disposal of a *staging* directory as safe by construction — it holds only reproducible bytes. |
| M13 | A pre-created empty target is `Absent`, so retention keyed on `Clean` gave it a permanent timestamped previous holding nothing | **Fixed by B3's superset re-keying** — an empty previous is trivially a subset. |
| M14 | The step-5 double-failure report asserted "target absent" when the likeliest cause of the failure is that something re-created it | **Fixed.** The reported state comes from a fresh existence check. |
| M15 | The manifest is not excluded from its own hash enumeration; the dry run quotes a trailing separator into `-ArgumentList` before T4's trim can run | **Both fixed.** |
| M16 | D3 never argued against the non-deleting variant, though three of five guard states exist only to make deletion safe | **Answered, not adopted.** D3 now carries the rationale: the non-deleting variant cannot deliver the land-together property the binding spec's finding 2 asks for. |
| M17 | The owner probe cannot discriminate "reads the directory" from "reads only known types", and T5's fixture omits `core`, which D10 newly introduces | **Fixed.** T6 names the discriminators; T5's fixture gains a `core` entry. |

---

## Acceptance

1. WHEN `--export-memory <dir>` runs against a store with live entries and `<dir>` is absent or empty, THE SYSTEM SHALL create `<dir>` holding one memory file per live entry, each with `---` frontmatter carrying `name`, `description` and `metadata.type`, plus a `MEMORY.md` index of one `- [Title](file.md) — hook` line per exported memory and no memory content, plus a manifest.
2. WHEN the exported directory is read back by `MemoryImport.Read("claude", <dir>)`, THE SYSTEM SHALL yield one draft per exported entry with the same title, and the same topic for every entry whose topic is one of `user`, `feedback`, `project`, `reference`.
3. WHEN the rendered index would exceed 200 lines or 25,000 UTF-16 code units — the units the consumer counts, measured on the rendered index itself and not on an entry count — THE SYSTEM SHALL refuse with exit 6 naming the real figure and the cap, and SHALL create or modify nothing.
4. WHEN `<dir>` holds a previous export whose manifest names the same store root and whose files all match it, THE SYSTEM SHALL replace it.
5. WHEN `<dir>` holds a manifest naming a **different store root**, THE SYSTEM SHALL refuse naming both roots and change no file, and `--force` SHALL NOT override it; only `--accept-new-source` SHALL proceed, printing both roots **and the same drift list any other override prints**. A matching set of per-file hashes SHALL NOT be sufficient to proceed, and a store whose contents changed since its own last export SHALL NOT be refused by this criterion.
6. WHEN `<dir>` is non-empty and holds no manifest, or holds a manifest that cannot be parsed, or holds files that its manifest does not describe or describes with a different hash, THE SYSTEM SHALL refuse, list every affected path by recursive relative path, and change no file; and WHEN an override is given, THE SYSTEM SHALL print a list **byte-identical** to the refusal's and then replace the directory.
7. WHEN a run replaces a directory holding any file the new export does not itself produce, THE SYSTEM SHALL retain that directory under a timestamped name that no later export deletes, and SHALL name it in the report. Only a replaced directory whose every manifest entry the new export also produces SHALL take the reusable unsuffixed name.
8. WHEN an export is interrupted, THE SYSTEM SHALL leave the target in exactly one of: untouched; a complete export; or absent with a complete previous-export directory beside it that the next run names. It SHALL NOT leave the target holding files its manifest does not describe. A swap that fails after the target has been moved aside SHALL attempt to restore it, and SHALL report the state it actually observes afterwards rather than the state it assumed.
9. WHEN a hub is running against the data directory, THE SYSTEM SHALL refuse with exit 5 and say which hub to stop, changing no file.
10. WHEN the data directory has no memory store, THE SYSTEM SHALL refuse with exit 4 and SHALL NOT create one; and WHEN the store exists but holds no live entries, THE SYSTEM SHALL refuse unless `--force`.
11. The export owns its directory: `autoMemoryDirectory` is pointed at a directory nothing else writes to. Not machine-checkable, and deliberately so — a session's own writes are indistinguishable from any other drift, and the guard's answer to drift is to refuse rather than to guess. Stated in the verb's refusal text and in `docs/verification.md`.
12. WHEN three ordinary exports run in succession against the same target, ALL THREE SHALL exit 0, and exactly one reusable previous-export directory SHALL exist afterwards.
13. WHEN the suite runs, THE 731 tests green at `196cb27` SHALL still pass, and `dotnet clean` + `dotnet build ChopItUp.slnx -c Debug -warnaserror` SHALL report 0 warnings.

Not machine-checkable, deliberately: **whether a running Claude Code session reads an exported directory.** Nothing here can assert it — the `metadata` schema varies by context, and the consumer is a 253 MB binary we read strings out of. It is the owner probe in Verification, and the last open item in the findings ledger.

---

## Claim ledger

| # | Claim | Verified at (commit) | Recheck (pwsh, exit 0 = holds) |
|---|-------|----------------------|--------------------------------|
| 1 | Baseline: 731 tests green (211 Core + 520 Hub), clean `-warnaserror` build, 0 warnings — measured this session | 196cb27 | `$o = dotnet test ChopItUp.slnx -c Debug --nologo -v minimal 2>&1; if ($LASTEXITCODE -ne 0) { exit 1 }; if (-not ($o -match 'Total:\s+211') -or -not ($o -match 'Total:\s+520')) { exit 1 }` |
| 2 | No export verb exists: `HubCommand` is exactly `{ Serve, RotateToken, PrintConfig, ImportSkill, SetClasses }` | 196cb27 | `if (-not (Select-String -Path src/ChopItUp.Hub/Hosting/HubOptions.cs -Pattern 'enum HubCommand \{ Serve, RotateToken, PrintConfig, ImportSkill, SetClasses \}' -Quiet)) { exit 1 }` |
| 3 | `MemoryImport` is the inverse shape: `---` first line, `key: value`, nested keys keep only their own name so `metadata:`/`  type: user` yields `type`, closing `---`, body after | 196cb27 | `if (-not (Select-String -Path src/ChopItUp.Hub/Memory/MemoryImport.cs -Pattern 'internal static \(Dictionary<string, string> Fields, string Body\)\? Frontmatter' -Quiet)) { exit 1 }` |
| 4 | Import maps topic from `type` only for `user`, `feedback`, `project`, `reference`; anything else becomes `imported`, so AC2 is exact only for those four | 196cb27 | `if (-not (Select-String -Path src/ChopItUp.Hub/Memory/MemoryImport.cs -Pattern 'ClaudeTopics = \["user", "feedback", "project", "reference"\]' -Quiet)) { exit 1 }` |
| 5 | Import derives a draft's title from `description`, falling back to `name`, then the file stem | 196cb27 | `if (-not (Select-String -Path src/ChopItUp.Hub/Memory/MemoryImport.cs -Pattern 'fields.GetValueOrDefault\("description"\) \?\? fields.GetValueOrDefault\("name"\)' -Quiet)) { exit 1 }` |
| 6 | Import skips the target's `MEMORY.md` by name, so the index never round-trips as a memory | 196cb27 | `if (-not (Select-String -Path src/ChopItUp.Hub/Memory/MemoryImport.cs -Pattern 'MemoryStore.CoreFileName, StringComparison.OrdinalIgnoreCase\)\) continue' -Quiet)) { exit 1 }` |
| 7 | Import reads only top-level `*.md`, at most `MaxFiles = 300`, each at most `MaxFileBytes = 64 * 1024` | 196cb27 | `$s = Get-Content src/ChopItUp.Hub/Memory/MemoryImport.cs -Raw; foreach ($p in 'MaxFiles = 300','MaxFileBytes = 64 * 1024') { if ($s -notlike "*$p*") { exit 1 } }` |
| 8 | `MemoryStore.Entries(topic)` resolves the core through `PathOf`, so `Entries(CoreTopic)` reads `MEMORY.md` | 196cb27 | `if (-not (Select-String -Path src/ChopItUp.Core/Memory/MemoryStore.cs -Pattern 'PathOf\(string topic\) => topic == CoreTopic \? CorePath' -Quiet)) { exit 1 }` |
| 9 | `ListTopics()` enumerates `topics\*.md` only — the core is NOT in it and must be added by the caller (D10) | 196cb27 | `if (-not (Select-String -Path src/ChopItUp.Core/Memory/MemoryStore.cs -Pattern 'Directory.EnumerateFiles\(TopicsDir, "\*.md"\)' -Quiet)) { exit 1 }` |
| 10 | `MemoryEntry` is `(string Title, string Provenance, string Body, bool Superseded, int Line)` | 196cb27 | `if (-not (Select-String -Path src/ChopItUp.Core/Memory/MemoryStore.cs -Pattern 'record MemoryEntry\(string Title, string Provenance, string Body, bool Superseded, int Line\)' -Quiet)) { exit 1 }` |
| 11 | Store constants: `CoreFileName = "MEMORY.md"`, `TopicsDirName = "topics"`, `CoreTopic = "core"`, `MaxTitleChars = 120` | 196cb27 | `$s = Get-Content src/ChopItUp.Core/Memory/MemoryStore.cs -Raw; foreach ($p in 'CoreFileName = "MEMORY.md"','TopicsDirName = "topics"','CoreTopic = "core"','MaxTitleChars = 120') { if ($s -notlike "*$p*") { exit 1 } }` |
| 12 | A non-serving verb is an enum value + a `HubOptions.Parse` branch + a `HostCommands.Run` branch; a path argument is rooted with `Path.GetFullPath` **at parse time** | 196cb27 | `if (-not (Select-String -Path src/ChopItUp.Hub/Hosting/HubOptions.cs -Pattern 'importSkillPath = Path.GetFullPath\(args\[\+\+i\]\)' -Quiet)) { exit 1 }` |
| 13 | `RotateToken` is D11's precedent: `HubLock.IsHeld(options.DataDir)` → message → `return 5` | 196cb27 | `if (-not (Select-String -Path src/ChopItUp.Hub/Hosting/HostCommands.cs -Pattern 'if \(HubLock.IsHeld\(options.DataDir\)\)' -Quiet)) { exit 1 }` |
| 14 | `--force` already exists as a global flag on `HubOptions`, so D2 reuses it | 196cb27 | `if (-not (Select-String -Path src/ChopItUp.Hub/Hosting/HubOptions.cs -Pattern 'args\[i\] == "--force"' -Quiet)) { exit 1 }` |
| 15 | `--overlay`'s guard is the precedent for rejecting a flag used with the wrong verb | 196cb27 | `if (-not (Select-String -Path src/ChopItUp.Hub/Hosting/HubOptions.cs -Pattern '"--overlay is only valid with --import-skill."' -Quiet)) { exit 1 }` |
| 16 | The installed Claude Code is 2.1.x | 196cb27 | `$v = & claude --version; if ($v -notmatch '2\.1\.') { exit 1 }` |
| 17 | **`EnsureLayout()` WRITES** — it creates `topics\`, seeds `MEMORY.md` and writes `.gitignore` when absent — and `ListTopics`/`Entries` call it internally, so **any** read through the store touches the source layout; the fence, not abstinence, is the mechanism (pass 1 F5, pass 2 M11) | 196cb27 | `if (-not (Select-String -Path src/ChopItUp.Core/Memory/MemoryStore.cs -Pattern 'public void EnsureLayout' -Quiet)) { exit 1 }` |
| 18 | `ChopItUp.Hub` HAS `InternalsVisibleTo` its test project, so `MemoryExport` reuses `MemoryImport.Slugify` with no visibility change | 196cb27 | `if (-not (Select-String -Path src/ChopItUp.Hub/ChopItUp.Hub.csproj -Pattern 'InternalsVisibleTo' -Quiet)) { exit 1 }` |
| 19 | The host-command test fixture is `tests/ChopItUp.Hub.Tests/HostCommandsTests.cs`, whose exit-5 pattern T4 copies | 196cb27 | `if (-not (Test-Path tests/ChopItUp.Hub.Tests/HostCommandsTests.cs)) { exit 1 }` |
| 20 | `Path.GetFullPath` PRESERVES a trailing separator, so `<target> + ".chopitup-export-tmp"` on a tab-completed path lands INSIDE the target | 196cb27 | `if ([IO.Path]::GetFullPath("C:\a\b\") -ne "C:\a\b\") { exit 1 }` |
| 21 | **`HostCommands` already maps `InvalidOperationException` to exit 3** in its shared catch, so a renderer that throws for an over-cap index lands on 3 unless T3 catches it first and maps it to 6 (pass 2 M5) | 196cb27 | `if (-not (Select-String -Path src/ChopItUp.Hub/Hosting/HostCommands.cs -Pattern 'InvalidOperationException' -Quiet)) { exit 1 }` |
| 22 | **`Directory.Move` throws onto an existing destination**, so an aside move onto a previous-export directory that was never deleted fails (pass 2 B1) | 196cb27 | `$a = Join-Path $env:TEMP ([guid]::NewGuid()); $b = Join-Path $env:TEMP ([guid]::NewGuid()); New-Item -ItemType Directory $a, $b \| Out-Null; try { [IO.Directory]::Move($a, $b); exit 1 } catch [System.IO.IOException] { exit 0 } finally { Remove-Item $a, $b -Recurse -Force -EA SilentlyContinue }` |
| 23 | The consumer counts `lineCount` as newlines + 1 over the **whole trimmed file** and `byteCount` as `t.length` (**UTF-16 code units**, C#'s `string.Length`), caps them at 200 / 25,000, and **slices** rather than refusing — so a two-line header costs two lines and D6 measures the rendered index (pass 2 B2, M7) | 196cb27 | — (single-byte-region extraction from a 253 MB vendor binary; the command is in the lead check) |
| 24 | The binary carries BOTH frontmatter templates — `metadata.type` and `metadata.pinned` — so `metadata` is an open map and only `name`/`description` are stable (pass 1 F7) | 196cb27 | — (UTF-16-region extraction; the command is in the lead check) |

---

## Tasks

### T1 — `MemoryExport`: the pure renderer · sonnet

**New file:** `src/ChopItUp.Hub/Memory/MemoryExport.cs`, beside `MemoryImport.cs` (same assembly, namespace `ChopItUp.Hub.Memory`).

```csharp
public sealed record ExportFile(string FileName, string Text, string Title, string Hook);
public sealed record ExportPlan(IReadOnlyList<ExportFile> Files, string Index, int EntryCount);
```

- `public const int MaxIndexLines = 200;` and `public const int MaxIndexUnits = 25_000;` — the authoritative caps (claim 23, D6). `public const int MaxMemories = 198;` is a **cheap early floor only**: `MaxIndexLines` minus the two lines the header costs. Comment that arithmetic; a future header change moves the floor but not the cap.
- `public static ExportPlan Render(MemoryStore store)`:
  - topics = `MemoryStore.CoreTopic` first, then `store.ListTopics().Select(t => t.Slug)` (claim 9, D10). `MemoryStore.Search` already prepends the core the same way — match it.
  - per topic, `store.Entries(topic).Where(e => !e.Superseded)` (D7, claim 10);
  - over `MaxMemories`, throw the refusal below **before** any file is rendered;
  - file name = `MemoryImport.Slugify(topic) + "-" + MemoryImport.Slugify(title)` + `.md`, deduplicated with `-2`, `-3` … on collision. `Slugify` is `internal` in the same assembly and already test-visible (claim 18) — reuse it, never reimplement it;
  - frontmatter, exactly:
    ```
    ---
    name: <file stem>
    description: <entry title, one line>
    metadata:
      type: <topic>
    ---

    <entry body>
    ```
    This is the vendor's own template (lead check), and `description` is where our importer looks for the title first (claim 5). Hub topics are not the vendor's four-value enum, so `type: core` and `type: room-general` are expected values, not bugs (D10).
  - hook = the body's first non-empty line, trimmed, cut at `MemoryStore.MaxTitleChars` with an ellipsis. **An entry with an empty body gets no hook and no trailing separator** — the line is `- [Title](file.md)`, never `- [Title](file.md) — `.
  - **Escaping:** a title containing `[` or `]` breaks the link; escape both with a backslash in the link text.
  - index = `"# Memories\n\n"` then one line per file in render order.
  - **The authoritative cap check runs on the rendered index** (D6, pass 2 B2): count lines as the consumer does — newlines + 1 over the **trimmed** index — and units as `index.Length` (C# strings are UTF-16, the same unit `t.length` counts). Over either, throw the refusal. Do **not** count UTF-8 bytes; revision 2 said bytes and was wrong.
- The refusal is a dedicated exception type — `public sealed class ExportRefusedException(string message) : Exception(message)` — **not** `InvalidOperationException`, because `HostCommands`'s shared catch already maps that to exit 3 and AC3 requires 6 (claim 21, pass 2 M5). T3 catches this type by name.
- The entry's `Provenance` is **not** exported: it names rooms and proposal ids that mean nothing in a vendor directory, and the export is a copy for reading, not the audit trail.

**Tests** (`tests/ChopItUp.Hub.Tests/Memory/MemoryExportTests.cs`): core + two topics renders one file per live entry and none for a superseded one; rendered frontmatter parses under `MemoryImport.Frontmatter` yielding `name`, `description`, `type`; the same title in two topics gets distinct file names; the index has one line per file and no body text; **198 entries render and 199 throw** — the boundary, which is where pass 2's B2 lived, not just a 201 case; an index whose *units* exceed 25,000 throws even though its line count is fine; an empty body yields a hook-less line; a title containing `]` produces a link that still parses; **no rendered file contains an `<!-- approved` marker** (ticket 01's provenance-exclusion acceptance, pass 2 M10).

**Command:** `dotnet clean ChopItUp.slnx -c Debug -v quiet; dotnet build ChopItUp.slnx -c Debug -warnaserror -v minimal; dotnet test tests/ChopItUp.Hub.Tests -c Debug --nologo -v minimal --filter "FullyQualifiedName~MemoryExport"`

---

### T2 — `ExportManifest`: source identity · sonnet

**New file:** `src/ChopItUp.Hub/Memory/ExportManifest.cs`.

```csharp
public sealed record ExportManifest(
    int Version, string SourceRoot, string SourceFingerprint, string ExportedAt,
    IReadOnlyDictionary<string, string> Files);   // recursive relative path -> sha256

public enum TargetState { Absent, Foreign, Unreadable, DifferentSource, Drifted, Clean }
public sealed record ManifestVerdict(TargetState State, IReadOnlyList<string> Paths, string? ManifestRoot);
```

- `FileName = ".chopitup-export.json"`, inside the target. Not a `*.md` file, so `MemoryImport` never reads it as a memory (claim 7). **It is excluded from `Files` and from `Verify`'s enumeration** — it cannot hash itself, and a naive recursive diff would flag it as drift on every second run (pass 2 M15a).
- **`SourceRoot` is the identity and the ONLY `DifferentSource` predicate** (D4). Normalise both sides with `Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))`, compare `OrdinalIgnoreCase`.
- `SourceFingerprint` is **report-only**: SHA-256 hex over `topic\ntitle\nbody\n` per live entry in render order. It answers "has the store changed since this export", which the report prints. It is never a refusal — a content fingerprint cannot detect the finding-1 attack, because a scratch store with the same entries produces the same fingerprint. Say that in the comment so nobody re-promotes it to a guard.
- `Write` / `TryRead` with `System.Text.Json`. `TryRead` returns `null` for **every** failure — missing, truncated, `{}`, a **newer** `Version`, missing field — and never throws. It **must still read a hand-written v1 manifest**: that is the schema-evolution guard, and the opposite reading is the wedge (pass 2 M8).
- `Verify(ExportManifest? m, string targetDir, MemoryStore store)`:
  - target absent or empty → `Absent`;
  - non-empty, no manifest file → `Foreign` with every recursive relative path (D5);
  - manifest file present but `TryRead` null → `Unreadable`;
  - `SourceRoot` differs → `DifferentSource` with both roots, **and the drift paths computed as well** — pass 2 M4: short-circuiting here left `--accept-new-source` with nothing to print;
  - files differ from `Files` → `Drifted` with every extra, missing or mismatched **recursive relative** path;
  - else `Clean`.

**Tests:** a manifest round-trips; `TryRead` returns null and throws nothing on truncated JSON, `{}`, a **newer** `Version`, and a missing file; **a raw-literal v1 manifest fixture — hand-written JSON, never produced by this code — still READS** (the schema-evolution guard); `Fingerprint` is stable across calls, changes on an edited body and on an added entry, and does not change when only a provenance comment differs; **`Verify` returns `DifferentSource` for two stores with identical entries at different roots, asserting every file hash matched**; **`Verify` returns `Clean` when the root matches and the store changed since the export**; **`DifferentSource` carries a non-empty drift list when the target is also drifted**; `Foreign` for a populated directory with no manifest; `Drifted` naming an added, a deleted, an edited and a **file in a subdirectory**; `Clean` for an untouched export, **including one whose directory holds the manifest itself** (M15a).

---

### T3 — `MemoryExportWriter`: stage, guard, swap · sonnet

**New file:** `src/ChopItUp.Hub/Memory/MemoryExportWriter.cs`.

```csharp
public sealed record ExportResult(int ExitCode, string Target, int Exported, string? PreviousDir, IReadOnlyList<string> OtherStagingDirs);
public static ExportResult Run(MemoryStore store, string targetDir, bool force, bool acceptNewSource, TextWriter output, TextWriter error)
```

`ExitCode` is explicit and is what T4 returns (pass 2 M5). The order of operations IS the safety property — document it as an ordered list in the doc comment, the way `tools/Deploy-ChopItUp.ps1` documents its own.

1. **Render** (T1), catching `ExportRefusedException` → `ExitCode 6` with the message (AC3). Nothing has looked at the target yet.
2. **Verify** (T2). `Absent` or `Clean` proceed. `DifferentSource` refuses naming both roots — **`--force` does not override it**; only `acceptNewSource` proceeds, and it prints both roots **and the drift list** (D2, D9). `Foreign`, `Unreadable` and `Drifted` print every affected path and refuse unless `force`, which prints a list **byte-identical to the refusal's, produced by the same method** (AC6 — one method, two callers, so they cannot diverge).
3. **Clear the reusable previous slot.** If `<targetDir>.chopitup-export-previous` exists, delete it now, before anything is moved, and say so in the report. A **timestamped** previous is never a candidate. Without this step the third consecutive export dies on `Directory.Move`, which throws onto an existing destination (claim 22, pass 2 B1).
4. **Stage** into `<targetDir>.chopitup-export-tmp-<nonce>`, nonce per run. Any *other* staging directory found is **reported, never deleted** — it may be a concurrent run's half-built stage.
5. **Re-verify, then swap.** Run `Verify` again immediately before touching the target (pass 2 M6): D11 stops the hub, not a vendor session writing into the target between steps 2 and 5. If the verdict changed, abort with the new verdict and change nothing. Otherwise `Directory.Move` the target aside, then `Directory.Move` the stage onto the target.
   **Retention name (D8, pass 2 B3):** the aside directory takes the reusable `<targetDir>.chopitup-export-previous` **only when** the verdict was `Clean` **and** every key in the replaced manifest's `Files` is also produced by this export — a superset test over the two manifests. Otherwise `<targetDir>.chopitup-export-previous-<yyyyMMddTHHmmssZ>`, which no later run deletes. A shrinking store, a `--force` over drift, a `Foreign` directory and an `--accept-new-source` all land in the timestamped form.
6. **On failure of the second move**, move the aside directory back. Whether or not that restore succeeds, **derive the reported state from a fresh `Directory.Exists` check on the target** rather than from what was assumed — the likeliest cause of the failure is that something re-created the target, in which case the restore fails for the same reason and the target is present, not absent (pass 2 M14). Exit non-zero either way.
7. Print `EXPORT_RESULT: { … }` as the last line, in `Deploy-ChopItUp.ps1`'s idiom: target, count, previous directory and which form it took, other staging directories seen, and whether the store changed since the last export.

**Tests** (scratch directories under `Path.GetTempPath()` only — never a real memory directory): a first export into a missing target creates it and verifies `Clean`; **three consecutive clean exports all exit 0 and leave exactly one reusable previous** (AC12, pass 2 B1 — every earlier test list stopped at two); a populated target with no manifest refuses listing its files, and `force: true` replaces it leaving those files in a **timestamped** previous; **force-replace a drifted target, export again, and assert the rescued file still exists at a path the output printed**; **shrink the store between two clean exports and assert the previous is TIMESTAMPED and the report names the drop** (pass 2 B3); an export of a different store refuses with both roots, changes nothing, is **still refused with `force: true`**, and with `acceptNewSource: true` proceeds **after printing the drift list** (pass 2 M4); an unreadable manifest refuses naming the override and does not throw; **the refusal's list and the override's list are captured and asserted byte-identical** (AC6, pass 2 M10); **a `FileShare.Read` handle held on a file inside the target makes the swap fail** — assert the target is byte-identical and the stage is complete including its manifest; **the target is mutated between step 2 and step 5 and the run aborts** (pass 2 M6); a hand-constructed "target absent, previous present" state is named by the next run; another run's staging directory is reported and left alone; a target that is a file, not a directory, refuses cleanly.

---

### T4 — the `--export-memory` verb · sonnet

**Files:** `src/ChopItUp.Hub/Hosting/HubOptions.cs`, `src/ChopItUp.Hub/Hosting/HostCommands.cs`.

- `HubCommand` gains `ExportMemory` (claim 2 pins the current five).
- `HubOptions` gains `ExportMemoryPath` and `AcceptNewSource`; `Parse` gains the branch. Root the path at parse time (claim 12) **and** `Path.TrimEndingDirectorySeparator` it (claim 20). Refuse a drive root outright.
- Reject `--overlay` alongside it, and reject `--accept-new-source` with any other verb, in the idiom claim 15 pins.
- `HostCommands.Run` gains `HubCommand.ExportMemory => ExportMemory(options, output, error)`.
- `ExportMemory`, in order:
  1. `HubLock.IsHeld(options.DataDir)` → exit 5 naming the data directory (D11, claim 13). It does not read `tokens.json`.
  2. **Fence before constructing the store** (claim 17): if `Path.Combine(DataDir, "memory")` does not exist, exit 4 in `RotateToken`'s "check `--data`" voice. This fence, not abstinence, is the mechanism — `ListTopics` and `Entries` call `EnsureLayout` internally, so the export unavoidably touches the source layout once it starts reading (pass 2 M11). Do not go hunting for a non-mutating read path; there isn't one.
  3. Zero live entries → refuse unless `--force`.
  4. `MemoryExportWriter.Run(...)` and return its `ExitCode` **directly** — do not let `ExportRefusedException` reach the shared catch, which maps to 3 (claim 21).
- Exit codes, documented beside the siblings': 0 ok, 2 bad argument, 3 IO failure, 4 no store at `--data`, 5 hub running, 6 refused by the guard (including the over-cap refusal).

**Tests** — the fixture is `tests/ChopItUp.Hub.Tests/HostCommandsTests.cs` (claim 19); copy its exit-5 pattern rather than inventing one. `Parse` roots a relative path; `Parse` trims a trailing separator (`C:\x\`); `--export-memory` with no value throws; `--overlay` alongside throws; exit 5 under a held lock; exit 4 against a data dir with no memory directory, **asserting the directory was not created**; **a store with zero live entries exits non-zero without `--force` and 0 with it** (AC10, pass 2 M10); **an over-cap store exits 6, not 3** (pass 2 M5); a clean run against scratch dirs exits 0.

---

### T5 — the round-trip guard · sonnet

**New file:** `tests/ChopItUp.Hub.Tests/Memory/MemoryExportRoundTripTests.cs`.

Build a store with entries in `user`, `feedback`, `project`, `reference`, **`core`** (D10 newly routes it, and revision 2's fixture omitted it — pass 2 M17) and `room-general`, export to a scratch directory, then `MemoryImport.Read("claude", dir)`:

- one draft per exported entry, no more — the index must not come back as a memory (claim 6);
- titles match exactly;
- topics match exactly for the four Claude topics (claim 4);
- `core` and `room-general` come back as `imported`, asserted **explicitly as the known lossy case** with the claim-4 reasoning in a comment: a test that omitted them would let a future change break the rest silently, and one that claimed they round-trip would be false;
- bodies survive, allowing for `MemoryImport`'s demotion of `#`/`##` to `###` — read `FromFrontmatter` before asserting on a body containing headings.

---

### T6 — dry run, self-check, docs · sonnet

**New:** `tools/Invoke-M24DryRun.ps1`, `tools/Invoke-M24ExportCheck.ps1`; edits to `docs/verification.md`.

**Dry run** (fabricated corpus, scratch directories only; it **refuses** a target under a real `.claude` directory rather than trusting the caller): build a store of 12 topics; export; assert file count and index line count; **run three exports in a row and assert all three exit 0**; hand-edit an exported file and assert the refusal names it; assert `--force` replaces it and the edited file survives in a timestamped previous; export a different store into the target and assert refusal with both roots, then that `--accept-new-source` proceeds **and printed the drift list**; corrupt the manifest and assert the refusal names the override and the exit code; **build a store whose rendered index crosses the cap and assert exit 6, not 3**; run against a data dir with no memory directory and assert exit 4 **and that no directory was created**. Quote every path in `-ArgumentList`, and **trim a trailing separator before quoting** — T4's parse-time trim runs after argv is already corrupted, and `"C:\dir\"` escapes the closing quote (2026-09-07 lesson, pass 2 M15b). Assert only exit codes and the `EXPORT_RESULT` line (M11).

**Self-check**: exit codes asserted directly, `[bool]` passed to the recorder, never a string a later branch reinterprets; **observe one row RED before trusting the table** (row 23's lens-195 finding — the shipped `desk-check-template.ps1` records `'FAIL'` as PASS, so do not copy its `$shapeOk`). Report what forced the RED.

**`docs/verification.md`** gains, in the runbook's existing voice:
- the export runbook: stop the hub, run the verb, the exit-code table including 4 and 6;
- **D1 as the operating rule**: point `autoMemoryDirectory` at a dedicated export directory, never at one a session also writes into, and why the next export will otherwise correctly refuse;
- the recovery names — `<dir>.chopitup-export-previous`, its timestamped form, when each appears and which one a later run reuses — and that no recovery is ever a hand-delete of the *target*;
- **staging disposal** (pass 2 M12): a `<dir>.chopitup-export-tmp-<nonce>` left by a failed run holds only reproducible bytes, so deleting one is safe by construction and is the owner's sanctioned cleanup. Say it, or every failed run leaves a directory the tool reports forever and nobody is allowed to remove;
- `--accept-new-source`: when it is the right answer (the data directory moved) and when it is not;
- one sentence that the manifest records the absolute source data-directory path inside the export directory;
- **the owner probe**, as an owner step because no agent can run it, and **with its discriminators named** (pass 2 M17): set `autoMemoryDirectory` to an exported scratch directory in a throwaway project, start a session, and ask what it remembers. The fixture must contain (a) one memory under an out-of-enum `type` such as `room-general`, and (b) an index at 198 and again at 199 entries — otherwise the probe cannot distinguish "reads the directory" from "reads only the four known types", nor confirm the line cap. Record the answer on the board row.

---

### T7 — close-out · orchestrator

Deploy per `CLAUDE.md`, then in one commit: flip row 24 to ✅ with the merge ref and capped Notes; delete the prior ✅ row; delete this plan and `.scratch/m24-memory-v1-1c-export/`; **delete `docs/superpowers/plans/memory-v1-1-findings.md`** — this row is its named deleter and item 8 was its last open item; LESSONS entry only if it changes a future decision; board gate exit 0.

---

## Ticket graph

```
01 ──► 02 ──► 03 ──► 04 ──┬──► 05
                          └──► 06 ──► T7
```

05 and 06 are both unblocked once 04 lands and touch disjoint files (a test file; `tools/` and `docs/`), so they are a real parallel batch if one is wanted.

---

## Verification (HIGH)

- **Preflight before the first builder:** `pwsh -NoProfile -File ~\.claude\skills\roadmap\preflight\Check-PlanClaims.ps1 -PlanPath docs/superpowers/plans/m24-memory-v1-1c-export.md -RepoPath .`
- **Per commit:** orchestrator diff review with the fixed lenses. The **ordering** lens matters most: T3's step order is the safety property, and between them the two critique passes found six defects in the guard's predicates, its retention and its step list.
- **Suite:** `dotnet clean`, `-warnaserror` at 0 warnings, then 211 Core + 520 Hub plus this row's additions.
- **Round-trip guard:** T5, required.
- **Synthetic-corpus dry run:** T6, required before deploy.
- **Adversarial re-read before the PR** of the three binding ledger findings **and** of pass 2's B1, B2 and B3: for each, name the test that fails if the mechanism is reverted. A finding closed only by a comment is not closed — that standard caught two live defects on row 23, and on this plan it caught two fixes that had each introduced a new defect.
- **Branch-level `mattpocock-skills:code-review`** on Standards and Spec, instructed not to spawn agents.
- **Git flow:** branch, PR, `gh pr checks --watch`, `gh pr merge --squash --delete-branch`, `git pull`.
- **Owner probe (post-merge, not a merge gate):** T6's `autoMemoryDirectory` probe with its named discriminators. The findings ledger is deleted at T7 regardless — the probe's result is recorded on the board row, since holding a file open for a vendor behaviour we cannot test would keep it forever.

---

## Could not verify in this environment

- **Whether Claude Code reads an exported directory at all.** The settings keys, both file templates and the index-trimming function are extracted from the installed 2.1.220 binary, but no session was pointed at an exported directory. This is the owner probe, and every acceptance criterion was written to be checkable without it.
- **Which `metadata` keys the vendor honours**, and what it does with an out-of-enum `type`. Two templates ship in one binary. The export emits `metadata.type` because it is in the vendor's own template and is what our importer reads; nothing here asserts the vendor acts on it. T6's probe discriminator exists for exactly this gap.
- **Any behaviour against the owner's real memory directory.** The privacy guard denies agent access to it, and this row's whole risk is aimed there, so every test and both scripts run against fabricated scratch directories only. The first real run is the owner's.
- **The crash window between T3's two moves.** The `FileShare.Read` test makes a real `Directory.Move` fail and asserts the invariant either side of it, which is as close as this environment gets; no test kills the process mid-swap.
