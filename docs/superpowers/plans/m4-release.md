# M4 — Release: a published exe deployed to `C:\Self Apps\ChopItUp\`

**Goal:** turn the repo into something the owner runs like an app — one published executable in a stable directory, with its room database beside it, deployed by a script that cannot destroy the data it is standing next to.

**Architecture:** no new product code. A publish profile makes `src/ChopItUp.Hub` a self-contained single-file win-x64 executable with the SQLite native library bundled for self-extraction, so `AppContext.BaseDirectory` — which `HubOptions` already uses to resolve `data\` — keeps pointing at the folder holding the exe rather than a temp extraction directory. `tools/Deploy-ChopItUp.ps1` publishes, refuses to run over a live install, copies the previous install aside, and copies in additively while never touching `data\`. `tools/Invoke-M4SelfCheck.ps1` then starts the *published artifact* against a scratch data directory and proves it actually serves.

**Author model:** Opus 5 (session orchestrator). HIGH-tier routing prefers Fable for planning. **Mismatch declared: critique pass 2 is mandatory** regardless of pass 1's score.

**Blast radius: HIGH.** A deploy path that writes into a directory holding the owner's only copy of their room history, with replace semantics and a backup step; plus the single-file publish layout, which is a cross-process contract in the sense that gets people: the runtime decides where `BaseDirectory` points, and getting it wrong silently relocates the database.

*Written for builder-subagent execution; if something doesn't match, STOP and report rather than guess.*

---

## Acceptance

- **C1** — WHEN the release publish command runs, THE SYSTEM SHALL produce a self-contained `ChopItUp.Hub.exe` for `win-x64` with the SQLite native library bundled into it, a `wwwroot` folder beside it, and no loose managed assemblies in the publish directory.
- **C2** — WHEN the published exe is started with no `--data` argument, THE SYSTEM SHALL create and use `data\` in the directory that contains the exe, **not** in a temp extraction directory.
- **C3** — WHEN the published exe is running, THE SYSTEM SHALL answer `/health` with schema 2, serve the web client at `/`, and accept an authenticated MCP `post_message` that reads back — proven against the published artifact, with the repo's build output not on the path.
- **C4** — WHEN the deploy script runs against an existing install, THE SYSTEM SHALL copy the previous install aside before writing anything, SHALL NOT write into or delete the existing `data\`, and SHALL abort having changed nothing if any running process's executable path is inside the target directory.
- **C5** — WHEN the deploy completes, THE SYSTEM SHALL leave a runnable exe at `C:\Self Apps\ChopItUp\ChopItUp.Hub.exe`, and the self-check harness SHALL (a) confirm C2 and C3 against a copy of the deployed artifact and (b) confirm **against the real target directory itself** that the exe and every `wwwroot` file match staging by SHA-256 and that no managed assembly is loose in the target root — reading nothing under `data\` — writing one PASS/FAIL line per check to a log and exiting non-zero on any FAIL.

---

## Claim ledger

| # | Claim | Verified at (commit) | Recheck (pwsh, exit 0 = holds) |
|---|-------|----------------------|--------------------------------|
| 1 | Baseline: 101 tests green (33 Core + 68 Hub), 0 warnings | 91f23cc | `dotnet test ChopItUp.slnx -c Debug --nologo -v minimal` |
| 2 | `HubOptions` resolves the default data dir as `Path.Combine(AppContext.BaseDirectory, "data")` — this is the line C2 turns on | 91f23cc | `if (Select-String -Quiet -Path src\ChopItUp.Hub\Hosting\HubOptions.cs -Pattern 'AppContext\.BaseDirectory, "data"') { exit 0 } else { exit 1 }` |
| 3 | `SpaFiles.ResolveWebRoot` also resolves beside the exe, so the published `wwwroot` must sit next to it | 91f23cc | `if (Select-String -Quiet -Path src\ChopItUp.Hub\Web\SpaFiles.cs -Pattern 'AppContext\.BaseDirectory, "wwwroot"') { exit 0 } else { exit 1 }` |
| 4 | `IncludeClientInPublish` exists in the csproj and pushes `wwwroot` into `ResolvedFileToPublish` — written in M3, **never executed** | 91f23cc | `if (Select-String -Quiet -Path src\ChopItUp.Hub\ChopItUp.Hub.csproj -Pattern 'IncludeClientInPublish') { exit 0 } else { exit 1 }` |
| 5 | `Microsoft.Data.Sqlite` 10.0.11 is referenced by Core and brings `SQLitePCLRaw.bundle_e_sqlite3`, whose native `e_sqlite3.dll` is what single-file must carry | 91f23cc | `if (Select-String -Quiet -Path src\ChopItUp.Core\obj\project.assets.json -Pattern 'SQLitePCLRaw.lib.e_sqlite3') { exit 0 } else { exit 1 }` |
| 6 | `CLAUDE.md` names the deploy target as `C:\Self Apps\ChopItUp\` with `data\` beside the exe | 91f23cc | `if (Select-String -Quiet -Path CLAUDE.md -Pattern 'Self Apps.ChopItUp') { exit 0 } else { exit 1 }` |
| 7 | The Node probe in the csproj is task-level, so publish on a Node-less machine degrades to "no UI" rather than failing — relevant because publish must not become the one place that hard-fails | 91f23cc | `if (Select-String -Quiet -Path src\ChopItUp.Hub\ChopItUp.Hub.csproj -Pattern 'Target Name="ProbeNode"') { exit 0 } else { exit 1 }` |
| 8 | With `PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract` (and **not** `IncludeAllContentForSelfExtract`), `AppContext.BaseDirectory` is the exe's own directory; full-content extraction is what moves it to TEMP. The runtime half is proven by C2. The **precondition** is what this recheck pins: no single-file property in the tree today sets full-content extraction, so nothing pre-existing already relocates `BaseDirectory` — and the same command still passes after Task 1 writes the safe pair, which is why it is the half that is automatable | owner memory `reference_singlefile_wpf_traps`, 2026-09-04 (MS single-file docs **not fetched this session — unverified as a citation**) | `if (Select-String -Quiet -Path src\ChopItUp.Hub\ChopItUp.Hub.csproj -Pattern '<IncludeAllContentForSelfExtract') { exit 1 } else { exit 0 }` — **anchored on the opening tag on purpose:** Task 1 writes the bare word into the comment that documents the trap, and a bare-word pattern would go red on a correct tree, whose cheapest fix is deleting that comment |
| 9 | The privacy guard denies reads under `C:\Self Apps\<app>\data\` in every session, with no exemption declared for this run | probed this session, 2026-09-04 | — (guard behaviour; see "Could not verify") |

**Lessons consulted:** `[sqlite, schema, migrations] M1` (nothing migrates here, but the deployed exe runs the ladder on first start against a fresh `data\`, so C3 exercises it); `[sqlite, wal, testing, migrations] M2` (the self-check must not assume a `-wal` survives a clean stop); `[msbuild, node, csproj] M3` (every Node gate is task-level — do not add a Target-level `Condition` to the publish path).

---

## Task 1 — Release publish profile

**Files:** `src/ChopItUp.Hub/ChopItUp.Hub.csproj`, `Directory.Build.props`, `README.md`.

**Amendment (Task 1 build, 2026-09-04).** `DebugType` must go in `Directory.Build.props` under a
`Configuration == 'Release'` condition, **not** in the Hub csproj alone. Scoped to the Hub, Core
keeps the SDK's default portable symbols and `ChopItUp.Core.pdb` lands loose in the publish root --
measured, not predicted -- which fails Task 3's exact-loose-file assertion. The Hub's own
`DebugType` line then becomes redundant and comes out. Publish size measured at this fix: about
103 MB; the plan's 30 MB floor stands.

Add a `Release`-flavoured publish configuration. Do **not** put `PublishSingleFile` in an unconditional `PropertyGroup` — it must not affect `dotnet build`, the test run, or CI.

```xml
  <!-- Release shape (M4). Self-contained so C:\Self Apps\ChopItUp keeps working across SDK changes,
       single-file so the folder holds an exe rather than a pile of DLLs, and
       IncludeNativeLibrariesForSelfExtract so SQLite's e_sqlite3.dll travels inside the exe.

       IncludeAllContentForSelfExtract is deliberately NOT set: it moves AppContext.BaseDirectory to
       the temp extraction directory, and both the data folder (HubOptions) and the web root
       (SpaFiles) are resolved from BaseDirectory — so setting it would silently relocate the
       owner's database into a folder Windows may clean up. That is the whole trap this milestone
       exists to not fall into.

       PublishTrimmed is NOT set either: the MCP SDK, SignalR and System.Text.Json all resolve types
       reflectively, and a trimmed build fails at runtime in ways no build gate catches. -->
  <PropertyGroup Condition="'$(Configuration)' == 'Release'">
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
    <DebugType>embedded</DebugType>
  </PropertyGroup>
```

**`EnableCompressionInSingleFile` is deliberately absent.** It trades cold-start time for file size,
and this plan has no baseline, no threshold and no measurement procedure for "measurably slows
startup" — an instruction to check it would be an invitation to guess, which this plan forbids. Size
on a local disk is not a problem the owner has. If a future row wants it, that row brings a
measurement.

**This `PropertyGroup` is conditioned on `Configuration`, not on `dotnet publish`.** So
`dotnet build -c Release` and `dotnet test -c Release` of the solution also see `RuntimeIdentifier`
and `SelfContained`, and Release output moves to `bin\Release\net10.0\win-x64\`. That is accepted:
the repo's documented build and test commands are `-c Debug` (`CLAUDE.md`), and CI runs Debug. The
requirement "must not affect `dotnet build`, the test run, or CI" is met for the paths this repo
actually uses — do not read it as a claim that Release builds are untouched.

The publish command, which the deploy script uses and the README documents:

```powershell
dotnet publish src\ChopItUp.Hub\ChopItUp.Hub.csproj -c Release -o <dir>
```

**Never add `--no-restore` to that command, and say so in the README.** `RuntimeIdentifier` is set in
the `Release` `PropertyGroup`, so the RID-specific assets it needs are not in the `Debug` restore the
repo normally runs; `--no-restore` turns that into a confusing mid-publish asset error rather than a
restore. It is the obvious "speed it up" edit and it is wrong here.

**`wwwroot` beside the exe is intended, not a leak.** A single-file bundle cannot serve static files
through a `PhysicalFileProvider`, and `SpaFiles` resolves the web root from `AppContext.BaseDirectory`
(ledger claim 3). So the release folder is: `ChopItUp.Hub.exe`, `wwwroot\`, and `data\` created on
first run. `CLAUDE.md` says "single-file exe … with `data\` beside it" and this satisfies it; say so
in the README rather than leaving the owner to wonder why there is a folder next to their exe.

**README:** add a short "Release" section — the publish command, what the folder contains, and that
the exe is self-contained so no .NET runtime is needed.

**Expected:** `dotnet publish -c Release` succeeds; `dotnet build ChopItUp.slnx -c Debug -warnaserror` and `dotnet test` are unchanged at 101; **and `dotnet build ChopItUp.slnx -c Release -warnaserror` still succeeds** — check it, because that is the path the new condition actually changes, and a Debug-only check would not notice a Release build it broke.

---

## Task 2 — `tools/Deploy-ChopItUp.ps1`

**Files:** `tools/Deploy-ChopItUp.ps1` (new), `README.md`.

**Blocked by:** Task 1.

The script's whole job is to be unable to destroy the thing it is standing next to.

Required behaviours, in order — this ordering *is* the safety property:

1. **Refuse to run over a live install.** Enumerate running processes; if any process's executable
   path is inside the target directory, abort with a non-zero exit **before touching anything**,
   naming the PID and the path. Match by **PID and image path**. Never `Stop-Process -Name`, and
   never stop anything at all — the owner stops their own app.
2. Publish into `-StagingDir` (never publish straight into the target), unless `-SkipPublish`.
3. **Sanity-check the staging output before it is allowed near the target:** the exe exists, it is
   at least 30 MB, `wwwroot\index.html` exists, **and `wwwroot\assets\` contains at least one file**.
   A failed check aborts with the target untouched.

   The size floor is not arbitrary and the assets check is not belt-and-braces. A non-self-contained
   Release build of this project is 162,304 bytes (measured in the tree this session), so a floor in
   the tens of MB separates "the runtime came along" from "a stub did". And `index.html` alone is not
   the client: `SpaFiles` serves it from `MapFallback` for every unreserved path, so a missing
   `wwwroot\assets\` produces a 200 with HTML for the very script the page needs — see Task 3's C3.
4. **Back up the existing install aside**, if there is one (a first deploy has nothing to back up —
   say so and continue). Copy the target's contents — **excluding `data`** — to a **sibling of the
   target**, derived from the parameter:

   ```powershell
   $backupDir = "$($TargetDir.TrimEnd('\')).backup-$stamp"
   ```

   **Never a literal `C:\Self Apps\ChopItUp.backup-…`.** With a hardcoded path, Task 4's tests — which
   drive `-TargetDir` at a scratch directory — would write four backups into the owner's real
   `C:\Self Apps` on every test run, each one an ask-tier prompt inside `dotnet test`, and would fail
   outright on CI where that folder does not exist. Copy, never move.
5. **Re-run the process check from step 1**, then copy in additively:

   ```powershell
   robocopy $staging $target /E /XD data logs /XF ChopItUp.Hub.exe /R:2 /W:2 /NP /NFL /NDL
   if ($LASTEXITCODE -ge 8) { throw "robocopy failed with $LASTEXITCODE" }
   Copy-Item "$staging\ChopItUp.Hub.exe" "$target\ChopItUp.Hub.exe.new" -Force
   Move-Item  "$target\ChopItUp.Hub.exe.new" "$target\ChopItUp.Hub.exe" -Force
   ```

   **`/XF ChopItUp.Hub.exe` is what makes the atomic rename below real.** Without it robocopy writes
   the 52 MB exe under its final name and the `.exe.new` dance is dead code sitting next to a
   contradiction — a kill mid-copy still leaves a half-written executable under the name the owner
   double-clicks. Excluded from the bulk copy, written aside, renamed last.

   `$target` is `$TargetDir.TrimEnd('\')` — pass robocopy a normalized path, since a trailing
   backslash immediately before the closing quote of a quoted argument is the classic Windows
   argument-parsing trap.

   **`/E` is load-bearing:** robocopy copies only top-level files by default, so `/XD data logs`
   alone would leave `wwwroot\` behind entirely. The app would still start, `/health` would still be
   green, and the owner would open the UI to a plain-text "No web client is built" — because
   `SpaFiles` treats a missing web root as non-fatal by design. Green checks, no UI.

   **Robocopy exit codes 0–7 are success**; only `>= 8` is failure. `/R:2 /W:2` matters too: the
   default is a million retries thirty seconds apart, so a sharing violation on a running exe reads
   as a hang rather than an error.

   **`/MIR` is forbidden** — it deletes everything not in the source, which on this target means the
   owner's database. The guard denies it, and the guard is right.
6. **Report, in a machine-readable last line**: the target path, **the staging path**, whether a
   backup was written and where, the exe's size, and how many backup directories now sit beside the
   target (report only — the script never deletes a backup; the README tells the owner to prune, and
   an auto-prune under `C:\Self Apps` would be both an ask-tier prompt storm and a delete of the one
   thing recovery depends on).

   **Step 6 does not verify file contents.** That verification is Task 3's post-deploy stage, and
   calling it from here would make Task 2 depend on Task 3, which is `Blocked by:` Task 2 — a cycle.
   Task 2 *reports* the staging path; **Task 5 runs the verification**, passing that path as
   `-PublishDir` and the target as `-TargetDir`.

**Parameters:**

- `-TargetDir`, defaulting to `C:\Self Apps\ChopItUp`, so tests can drive it somewhere harmless.
- **`-StagingDir`** — where to publish. Defaults to a fresh GUID-named directory under `$env:TEMP`.
  Always printed, because Task 5 and the self-check both need it and a path the script invents and
  keeps to itself is unusable.
- **`-SkipPublish`** — use the contents of `-StagingDir` as-is instead of publishing. This is the
  seam Task 4 needs: without it every deploy-script test either invents a parameter (a guess, in a
  plan that forbids guessing) or pays for its own Release publish. `-SkipPublish` without an
  explicit `-StagingDir` is an error, and step 3's sanity check still runs on whatever it is handed.
- **`-RestoreFrom <backupDir>`** — copy a backup back over the target under the same process guard,
  the same `data` exclusion and the same `/XF` + rename treatment of the exe. Rollback must be a mode
  of this script, not a README paragraph telling the owner to hand-copy files — that paragraph is the
  moment someone reaches for `/MIR`.

**Crash safety.** Dying between steps 4 and 5 is safe: the target is untouched and an extra backup
exists. Dying *during* step 5 leaves a torn target, which is why `-RestoreFrom` exists. Copy the exe
as `ChopItUp.Hub.exe.new` and `Move-Item -Force` it into place last, so the rename is atomic on the
same volume and a torn copy never leaves a half-written executable under the real name.

**Process-guard matching.** Compare against `$TargetDir.TrimEnd('\') + '\'` — a bare prefix match on
`C:\Self Apps\ChopItUp` also matches `C:\Self Apps\ChopItUp.backup-…\ChopItUp.Hub.exe`, so running a
backup copy would block a deploy. And `Get-Process` returns a null `.Path` for a large share of
processes (155 of 360 in one measurement); those are invisible to the check, so **log the count of
processes whose path could not be read** on both the abort and the proceed line. A blind spot that is
reported is a caveat; one that is silent is a lie.

**Expect one harness approval prompt per write under `C:\Self Apps`.** That is the ask tier working
as designed, not a failure — if a write is refused, report it, do not route around it.

---

## Task 3 — `tools/Invoke-M4SelfCheck.ps1` (the HIGH-tier gate)

**Files:** `tools/Invoke-M4SelfCheck.ps1` (new).

**Blocked by:** Task 2.

Proves C2 and C3 **against the published artifact**, not the build output. This is the milestone's
self-check harness and its synthetic-corpus dry run in one: the "corpus" is a fresh database the
published exe creates itself, which is exactly the composition M4 changes.

Take a `-PublishDir` parameter. Copy that directory to a scratch location under `$env:TEMP` with a
GUID nonce — **with `robocopy /E /XD data`** — and run every check there.

**Two rules about that copy, both load-bearing.** First, the `data` exclusion is not tidiness: if
this is ever pointed at a deployed install rather than at staging, a plain recursive copy would drag
the owner's real `chopitup.db` and `tokens.json` into `%TEMP%`, which is a real-database copy and is
banned outright. Second, **refuse to start if `<scratch>\data` exists before the first launch** —
C2's whole claim is that the database is *created* beside the exe by this run, and a pre-existing
folder makes the check pass without proving anything.

**Do not run the launch checks against `C:\Self Apps\ChopItUp\` itself.** Reading under a Self Apps
`data\` folder is denied by policy in every session (ledger claim 9), so a check that inspects the
deployed database cannot run there. A byte-identical copy proves the same property inside the
perimeter. Say this in the script's header comment so nobody "fixes" it later by repointing it.

Checks, each a PASS/FAIL line with the measured value:

- **C1** — the exe exists and is at least **30 MB**. Put the justification in a comment, not a
  measured release size the plan cannot vouch for: a non-self-contained Release build of this project
  is 162,304 bytes, so the floor separates "the runtime is inside" from "a stub". The loose file set
  in the publish root is **exactly**: `ChopItUp.Hub.exe`,
  `ChopItUp.Hub.staticwebassets.endpoints.json`, `web.config`, and the `wwwroot` directory. Assert
  **zero** `*.dll`, `*.pdb` and `*.deps.json`, and **STOP on anything else in the set** — do not
  measure the emitted list and assert that it equals itself, which would let a loose `e_sqlite3.dll`
  (i.e. the single-file property silently not applied) become the expected answer. That zero-`*.dll`
  assertion is also the whole proof that native self-extraction is on; do not add a second, weaker
  check on the runtime's extraction cache to try to prove it again. `wwwroot\index.html` exists
  **and `wwwroot\assets\` holds at least one file**.
- **C2** — start the exe **with no `--data` and `--port 0`**, from a working directory that is *not*
  the exe's directory, with `CHOPITUP_DATA` and `CHOPITUP_PORT` cleared from the child environment
  (`HubOptions.Parse` falls back to both, and an inherited value would silently decide the answer).
  `--port 0` is not optional: the default 8790 collides with the owner's own running hub, after which
  this harness fails for a reason that has nothing to do with the release.

  Assert: `data\chopitup.db` appears **beside the exe**, and no database was created in the working
  directory.

  **Do not assert anything about `%TEMP%\.net\ChopItUp.Hub\*`.** An earlier draft did, to prove the
  bundle really self-extracted. It cannot: the runtime *caches* that directory across runs and this
  script explicitly does not delete it, so the second run of an unchanged bundle finds it present and
  not refreshed — the check either flakes or gets relaxed to "exists", which then passes for a stale
  reason. C1's zero-loose-`*.dll` assertion already proves the same property from the artifact
  itself, deterministically.

  **Correct the reasoning, not just the check:** `AppContext.BaseDirectory` is never the working
  directory, so a same-directory launch is not "passing either way" for that reason. The different
  working directory is still required, for a different one — ASP.NET Core's ContentRoot *is* the
  working directory, so any future drift from `BaseDirectory` to a ContentRoot-relative path breaks
  only when the two differ, and a same-directory launch would never notice.
**Port discovery — the contract every launch in this script obeys.** With `--port 0` the bound port
is only knowable from `<data>\hub.port`, which `HubHost` writes on `ApplicationStarted` from the
*bound* address (`HubHost.cs:67-73` — port 0 is genuinely supported, not assumed). But
`HubPortFile` never deletes that file: its own summary says it holds the port of "the running **or
last-running** hub". So **delete `<data>\hub.port` immediately before every launch and poll for its
recreation** (M2 precedent: `tools/Invoke-M2DryRun.ps1:105-117`). Skip this and the restart check
reads the *previous* run's port and either connects to nothing or, worse, to something else. Read
the host token from `<data>\tokens.json` the same way, after the same wait.

- **C3** — `/health` reports `schema: 2`; an MCP `post_message` with a fresh `client_key` lands and
  reads back, and a repeat with the same key returns `deduplicated`. Reuse
  `tools/ChopItUp.Corpus --mcp-check` rather than hand-rolling the transport.

  **The UI check must fetch the script, not grep for its tag.** `GET /` returning HTML with a
  `<script src="/assets/index-<hash>.js">` proves nothing: `SpaFiles` serves `index.html` from
  `MapFallback` for every path that is not `/api`, `/hub`, `/mcp` or `/health`, so if `wwwroot\assets\`
  never shipped, that script URL returns **200 with the HTML shell** and a tag-grep passes while the
  owner gets a blank page. So: parse the `src` out of the shell, `GET` it, and assert the response is
  `text/javascript` (not `text/html`) and its bytes are not the shell's. This is the same class of
  defect as the missing-`wwwroot` one, one directory deeper, and it is the reason C1 also counts
  files under `wwwroot\assets\`.
- A **restart** check: stop the exe by PID, **`WaitForExit`** (the hub lock is `FileShare.None`, so a
  restart that races the previous process's shutdown fails for the wrong reason), start it again on
  the same scratch dir, assert the message posted in C3 is still there — the release's first
  migration-ladder run plus a real restart, which is where a single-file layout mistake surfaces.

### Post-deploy stage (`-TargetDir`, file-level only)

Everything above runs on a scratch copy, which means **nothing in it can see what actually landed in
`C:\Self Apps\ChopItUp\`** — a partial copy, a sharing violation, a torn write, or the missing
`wwwroot` from the `/E` bug would leave every check honestly green. The tier requires a deploy-time
self-check of the real target, so add a second stage, taking `-TargetDir`, that makes **file-level
assertions only and never reads under `data\`**:

- SHA-256 of `<target>\ChopItUp.Hub.exe` equals the staging exe's.
- The `wwwroot` file list and per-file hashes equal staging's, **enumerated recursively** — the
  interesting failure lives one level down in `wwwroot\assets\`, so a top-level comparison would
  reproduce the `/E` bug in the verifier that exists to catch it.
- Zero `*.dll` in the target root.
- `Test-Path <target>\data` — reported as present/absent, and **nothing inside it is opened, listed
  or hashed**.

Stop the exe by the PID the script started, after confirming its image path matches. Never
`Stop-Process -Name`. Clear `CHOPITUP_MCP_TOKEN` in a `finally` so a failure cannot leave a live
token in the environment. Build `tools/ChopItUp.Corpus` if it is not already built. Delete the
scratch directory unless `-KeepEvidence` — note in the log that the runtime's own
`%TEMP%\.net\ChopItUp.Hub\<id>` extraction directory is left behind by design and is not the
script's to remove. Write `m4-selfcheck.log`, print the path and the PASS/FAIL counts, exit non-zero
on any FAIL.

The orchestrator runs this and reads the log; log contents are never quoted into commits, the board,
or the ping.

---

## Task 4 — Deploy-script tests

**Files:** `tests/ChopItUp.Hub.Tests/DeployScriptTests.cs` (new).

**Blocked by:** Task 2 (the script under test) and Task 1 (the staging publish these tests consume).
**Not** Task 3 — nothing here touches the self-check harness, and a false edge would have made the
graph lie about what a re-dispatch of Task 3 invalidates. Tasks 3 and 4 are therefore both unblocked
once Task 2 lands; **dispatch them sequentially anyway** (3 then 4) — a linear order is a valid
topological order, and the parallel path would require worktree isolation for no real wall-clock win
on two tasks.

The deploy script's safety properties must be regression-tested, not just exercised once by hand.
Drive `Deploy-ChopItUp.ps1` with `-TargetDir` at a scratch directory and **`-StagingDir <pre-staged>
-SkipPublish`** (Task 2's seam) — publish once in a class fixture, not once per test. Every test in
this list runs against that one staging copy or a modified copy of it; none of them publishes.

- `Deploy_never_touches_an_existing_data_directory` — seed the target with `data\chopitup.db`
  containing known bytes plus a `data\tokens.json`; deploy; assert both files are byte-identical
  afterwards.
- `Deploy_copies_the_previous_install_aside_before_writing` — seed the target with a marker file;
  deploy; assert a backup directory exists containing the marker, and that the marker is *not* in
  the backup's `data` (because `data` is excluded from the backup too).
- `Deploy_aborts_and_changes_nothing_when_a_process_is_running_from_the_target` — start any long-lived
  process whose image lives inside the target directory (copy the published exe there and start it
  with a scratch `--data` and `--port 0`), run the deploy, assert a non-zero exit, an error naming
  the PID, and that the target's contents are unchanged. Stop that process **by the id this test
  started** afterwards. If you redirect the child's stdout/stderr, you must also drain them — an
  unread pipe fills and the child blocks mid-startup, which reads as a hang in an unrelated test.
  Not redirecting at all is the simpler correct answer here.
- `Deploy_refuses_a_staging_output_that_is_missing_the_client` — delete `wwwroot\index.html` from a
  copy of the staging dir; assert the deploy aborts with the target untouched.
- `Deploy_lands_the_client_in_the_target` — deploy to an empty scratch target; assert
  `wwwroot\index.html` exists **in the target** and its bytes match staging's. This is the regression
  test for the `/E` bug: without it, every other test on this list still passes while the owner gets
  an app with no UI.
- `Deploy_writes_the_backup_as_a_sibling_of_the_target` — deploy twice to a scratch target under a
  temp root; assert the backup directory's parent is the target's parent and its name starts with the
  target's leaf name, and assert **nothing was created under `C:\Self Apps`** by the test run. A
  hardcoded backup path is the failure this pins, and it is the one that escapes the test sandbox.
- `Deploy_replaces_the_exe_by_rename_not_in_place` — assert no `ChopItUp.Hub.exe.new` survives a
  successful deploy, and that a staging exe with known bytes lands byte-identical. Thin, but it is
  the only thing standing between `/XF` + rename and someone "simplifying" it back into the bulk
  robocopy, which silently deletes the crash-safety property.
- `Restore_puts_the_backup_back_and_leaves_data_untouched` — deploy v1, seed `data\` with known
  bytes, deploy v2 (which backs v1 aside), then run `-RestoreFrom <that backup>`; assert the exe is
  v1's bytes again and `data\` is untouched. **`-RestoreFrom` is the entire recovery story for the
  torn target this plan predicts**, and without this test its first execution ever would be on the
  owner's real install, under pressure, at the worst possible moment.

If invoking PowerShell from xUnit proves awkward, the acceptable alternative is a
`tools/Test-DeployScript.ps1` that the orchestrator runs, with `DeployScriptTests` reduced to a
single test asserting the script file exists and declares `-TargetDir` — **but say which you did and
why**, because the weaker option loses CI coverage of the safety properties.

---

## Task 5 — Deploy, verify, document

**Files:** `README.md`, `CLAUDE.md`.

**Blocked by:** Task 4. **This task is the orchestrator's, not a builder's** — it writes under
`C:\Self Apps`, where the harness asks per write.

1. Run `tools/Deploy-ChopItUp.ps1` for real against `C:\Self Apps\ChopItUp`. **Capture the staging
   path it prints** (Task 2 step 6) — the next step needs it and the script chooses it.
2. Run `tools/Invoke-M4SelfCheck.ps1 -PublishDir <that staging path> -TargetDir "C:\Self Apps\ChopItUp"`
   and read the log. **Both parameters, every time.** `-PublishDir` alone runs only the scratch-copy
   stage and skips the post-deploy stage entirely — which is to say it skips the half of C5 that
   looks at what the owner actually got, which was the whole point of adding that stage.
3. README: a "Release" section — publish, deploy, what the folder holds, how to roll back to a
   backup directory, and that rotating a token needs the app stopped.
4. `CLAUDE.md`: keep the Deploy paragraph accurate; it must stay under 4 KB (the budget gate checks
   it, and it is at 3.2 KB — add a clause, not a paragraph).

**The repository does not become public in this milestone.** That is board row 4b, an `OWNER:` row,
and it needs a `confidentiality-review` pass first. Nothing in this plan touches repository
visibility.

**The grill notes are NOT deleted at this flip.** Board row 4's Notes say "Deletes grill notes" and
row 4b says the ledger "goes with" the public flip; those contradict, and row 4's reading is the
wrong one. `docs/superpowers/plans/grill-notes-chop-it-up.md` holds R7 — the record of why the repo
stays private and what `confidentiality-review` has to clear — which is the only durable evidence
guarding row 4b. Deleting it at M4 destroys the input to the decision it is waiting on. So: at the
board flip, **correct row 4's Notes** to say the grill notes go with 4b, and M4's paired deletes are
this plan file and `.scratch/m4-release/` only. The grill notes' delete point is row 4b's completion,
which is where row 4b already records it.

---

## Critique dispositions (pass 1 — `fable`, FIX-THEN-SHIP, 6.4)

| Sev | Finding | Disposition |
|-----|---------|-------------|
| MAJOR | `robocopy /XD data logs` without `/S` or `/E` copies top-level files only, so `wwwroot\` never lands; the exe runs, `/health` is green, and the owner opens a text page | **Fixed** — Task 2 step 5 now specifies `/E`, with the failure mode written out beside it; Task 4 gains `Deploy_lands_the_client_in_the_target` so it cannot regress silently |
| MAJOR | C5 is measured on the wrong artifact: the self-check runs against staging, not against what landed in the target, so a torn or partial copy passes every check | **Fixed** — C5 rewritten; Task 3 gains a `-TargetDir` post-deploy stage making file-level assertions only (SHA-256 of exe and every `wwwroot` file vs staging, zero loose `*.dll`), reading nothing under `data\` |
| MAJOR | Backup written to a hardcoded `C:\Self Apps\ChopItUp.backup-…`, so Task 4's scratch-target tests would write into the owner's real Self Apps on every run and fail outright on CI | **Fixed** — `$backupDir` derived from `-TargetDir`; Task 4 gains `Deploy_writes_the_backup_as_a_sibling_of_the_target`, which also asserts nothing appears under `C:\Self Apps` |
| MAJOR | Task 3's scratch copy was a plain recursive copy and could drag a real `chopitup.db` into `%TEMP%`; and a pre-existing `data\` would let C2 pass without proving creation | **Fixed** — `robocopy /E /XD data` for the copy, refuse-to-start if `<scratch>\data` exists, and the header comment says why it is never repointed at the deployed install |
| MINOR | The C2 rationale was wrong: it justified the different working directory by claiming a same-directory launch "passes either way" via `BaseDirectory` | **Fixed** — corrected in place. `BaseDirectory` is never the cwd; the real reason is ContentRoot, which *is* the cwd, so future drift to a ContentRoot-relative path is invisible unless the two differ |
| MINOR | A publish that emitted loose native libraries would also put the database beside the exe and pass C2 | **Fixed** — C2 additionally asserts `%TEMP%\.net\ChopItUp.Hub\*` was created or refreshed, honouring `DOTNET_BUNDLE_EXTRACT_BASE_DIR` |
| MINOR | The loose-file assertion measured the emitted set and compared it to itself | **Fixed** — the allowed set is now enumerated literally with a STOP on anything outside it |
| MINOR | Default port 8790 collides with the owner's own running hub, failing the harness for a reason unrelated to the release; inherited `CHOPITUP_DATA`/`CHOPITUP_PORT` could decide C2's answer | **Fixed** — `--port 0` and both variables cleared from the child environment |
| MINOR | The restart check races the previous process's `FileShare.None` hub lock | **Fixed** — `WaitForExit` before restart |
| MINOR | Crash during the copy leaves a half-written executable under the real name; and rollback existed only as a README paragraph | **Fixed** — `.exe.new` + `Move-Item -Force` last, and `-RestoreFrom` is a mode of the script |
| MINOR | Ticket 04 declared `Blocked by: 03`, an edge that does not exist | **Fixed** — corrected to `02` in the ticket and in Task 4, with the dispatch order left sequential deliberately |
| MINOR | Ledger row 8 was marked unautomatable in full, when half of it is a file fact | **Fixed** — recheck pins the precondition (no `IncludeAllContentForSelfExtract` anywhere in the csproj), which holds both before and after Task 1; the runtime half stays C2's job |
| NIT | Process guard: bare prefix match also matches `…ChopItUp.backup-…`, and `Get-Process` returns a null `.Path` for a large share of processes | **Fixed** — match on `TrimEnd('\') + '\'`, and the unreadable-path count is logged on both the abort and the proceed line |
| NIT | The process check ran once, well before the copy | **Fixed** — re-run immediately before step 5 |
| NIT | `--no-restore` is the obvious speed-up and breaks a RID-specific publish | **Fixed** — Task 1 forbids it and the README says why |
| NIT | The scratch cleanup would appear to leak the runtime's own extraction directory | **Declined as a defect, documented instead** — `%TEMP%\.net\ChopItUp.Hub\<id>` is the runtime's, not the script's; the log says so rather than the script deleting it |
| REFRAME (rejected) | Board rows 4 and 4b disagree about when the grill notes are deleted | **Resolved against row 4** — the notes hold R7, the only durable record guarding 4b, so they survive M4 and go with the public flip; row 4's Notes are corrected at the board flip (Task 5) |

## Critique dispositions (pass 2 — `opus`, FIX-THEN-SHIP, 6.2)

Pass 2 upheld HIGH. Every finding below is new; none re-reports pass 1.

| Sev | Finding | Disposition |
|-----|---------|-------------|
| MAJOR | Ledger claim 8's recheck greps the bare word `IncludeAllContentForSelfExtract`, which Task 1 writes **into the comment documenting the trap** — so the recheck goes red on a correct tree, and the cheapest fix a builder finds is deleting that comment. Reproduced by the critic | **Fixed** — anchored on `<IncludeAllContentForSelfExtract`, with the reason written into the ledger cell so nobody "simplifies" the pattern back |
| MAJOR | Task 2 step 6 called Task 3's post-deploy stage, but Task 3 is `Blocked by:` Task 2 — a cycle; and Task 5 invoked the self-check with `-PublishDir` only, so C5(b), pass 1's own fix, never ran. The staging path was also unresolvable: the script invented it and never printed it | **Fixed** — step 6 reports, never verifies; the staging path is in its machine-readable output line; Task 5 passes `-PublishDir` **and** `-TargetDir`, with the consequence of omitting the latter spelled out |
| MAJOR | Task 4's tests need a pre-staged publish, but Task 2 declared only `-TargetDir` and `-RestoreFrom` — no seam. A builder would invent a parameter or pay for six Release publishes | **Fixed** — Task 2 gains `-StagingDir` and `-SkipPublish` as part of its contract; Task 4 now names them |
| MAJOR | The `.exe.new` + rename was dead code: the robocopy line beside it copies the exe under its real name with nothing excluding it, so a kill mid-copy still leaves a half-written exe under the name the owner double-clicks | **Fixed** — `/XF ChopItUp.Hub.exe` in the bulk copy, then copy-aside and `Move-Item -Force` last; `Deploy_replaces_the_exe_by_rename_not_in_place` pins it |
| MAJOR | C3's UI check was a grep for the script tag. `SpaFiles` serves `index.html` from `MapFallback` for every unreserved path, so a missing `wwwroot\assets\` returns 200 + HTML for the script URL and every check stays green while the owner gets a blank page | **Fixed** — C3 now fetches the `src` and asserts `text/javascript` and non-shell bytes; C1 and the deploy sanity check both require a non-empty `wwwroot\assets\`; the post-deploy hash comparison is explicitly recursive |
| MAJOR | `-RestoreFrom` is the entire recovery story for the torn target the plan itself predicts, and nothing tested or exercised it — first run would be on the owner's real install under pressure | **Fixed** — `Restore_puts_the_backup_back_and_leaves_data_untouched` added to Task 4 |
| MINOR | The `%TEMP%\.net\ChopItUp.Hub\*` assertion is unobservable and self-contradictory: the same task says that directory is cached and deliberately not deleted, so run 2 finds it stale | **Fixed by deletion** — removed, with the reasoning kept in place so it is not re-added. C1's zero-loose-`*.dll` STOP proves the same property deterministically from the artifact |
| MINOR | No port/token discovery contract for `--port 0`: `hub.port` holds "the running **or last-running**" port and is never deleted, so the restart check reads the previous run's port | **Fixed** — delete `hub.port` before every launch and poll for recreation, M2's precedent cited by file and line; token read the same way. Confirmed against `HubHost.cs:67-73` that port 0 writes the *bound* port |
| MINOR | Two builder-guess points: "if compression measurably slows startup, drop it" with no baseline or threshold, and an unattributed 52,253,014-byte release size | **Fixed** — `EnableCompressionInSingleFile` dropped outright with the reason stated; the size floor is justified by the 162,304-byte non-self-contained build measured in the tree, not by a number the plan cannot vouch for |
| MINOR | Task 1 claims the Release condition does not affect build or test, but it applies to any `-c Release` build and moves output under `win-x64\`; Expected checked Debug only | **Fixed** — the real scope is stated rather than the claim softened, and Expected now includes a Release build |
| NIT | `robocopy $TargetDir` not normalized for a trailing backslash; backups accumulate unbounded, one ask-tier prompt each | **Partly fixed** — normalized to `$TargetDir.TrimEnd('\')`. Backup count is **reported, not pruned**: an auto-prune under `C:\Self Apps` would delete the one thing recovery depends on, so the README tells the owner to prune |
| NIT | Task 4's child hub may be started with redirected handles that nothing drains | **Fixed** — Task 4 says drain or do not redirect, and names not-redirecting as the simpler correct answer |

## Could not verify in this environment

- **Anything inside `C:\Self Apps\ChopItUp\data\`.** The privacy guard denies reads there in every
  session and this run declared no exemption, so the deployed database is written and never
  inspected. Every data-shaped assertion is made against a byte-identical copy in a scratch
  directory instead (Task 3). The deploy itself is unaffected — it never reads `data\` either.
- **A machine without the .NET 10 SDK.** Self-contained publish is meant to make the exe run without
  one; this machine has the SDK, so "runs on a clean machine" is argued from the publish shape, not
  observed.
- **Startup cost on cold disk.** `EnableCompressionInSingleFile` trades startup for size; measured
  warm here at best.
- **The owner's real usage of the deployed app** — that it is *pleasant* to run from
  `C:\Self Apps\ChopItUp\` is a judgement only the owner makes.
