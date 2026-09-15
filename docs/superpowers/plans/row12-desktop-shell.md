# Row 12: desktop shell (WebView2 window that starts the hub, hosts the room UI, sits in the tray)

**Goal:** One double-clickable `ChopItUp.Desktop.exe` that starts the hub as a child process, shows the room UI in a chromeless WebView2 window whose title bar is drawn by the page (Curio's look), hides to the tray on close, and stops the hub on Quit.

**Architecture:** A new WPF project `src/ChopItUp.Desktop` whose window has `WindowStyle=None`, `CaptionHeight=0` and a single child, the WebView2. Dragging and double-click-to-maximize come from WebView2's own non-client-region support (`IsNonClientRegionSupportEnabled` + CSS `app-region: drag`), so the React client gains a hosted-only chrome row (wordmark, hub chip, three 46×32 glyph buttons) and a tiny page↔host bridge over `window.chrome.webview.postMessage`. The shell starts `ChopItUp.Hub.exe` in a kill-on-close Job Object, drains its output to `data\logs\hub.log`, polls `/health` until ready, and hands the page an owner credential minted for this launch: a random token passed to the child in `CHOPITUP_SHELL_TOKEN`, which the hub accepts as an in-memory owner bearer and scrubs from its own environment before any spawn. If a hub already owns the data dir (the owner started the exe by hand), the shell attaches to it instead: same window, no child, no token, Quit closes only the window. A WinForms `NotifyIcon` (WPF and WinForms coexist in one project, probed this session) gives the tray icon and menu.

**Author model:** Fable 5.1 (session model; HIGH routes to Fable, no mismatch).

**Blast radius:** HIGH. A new cross-process contract (shell ↔ hub lifetime, environment-borne credential), a second executable in the deploy dir with deploy-script and self-check changes, and a credential path that must never reach a spawn or `tokens.json`.

*Written for builder-subagent execution; if something doesn't match, STOP and report rather than guess.*

## Rulings this plan implements

Owner, 2026-09-13 (`.scratch/decisions/rows-32-12-14-2026-09-13.md`, row 12 section, absorbed here): hub is a child process the shell starts and Quit stops; close hides to tray; no autostart at login; stack is a .NET host on system WebView2.

Owner, 2026-09-15 (this session's design round, four wireframes): **borderless window with in-page chrome, the look `Curio.Shell` has**; no elements folded in from the other options (no native status strip, no log drawer, no Copy-MCP-config item).

Orchestrator defaults (Class B, reversible, logged here; the owner can overturn any in a new row):
- **B1 WPF, not WinForms.** The 09-13 note defaulted to WinForms for its built-in `NotifyIcon`. The chosen look is exactly `Curio.Shell`'s proven WPF shape (WindowChrome + `Web.Margin` resize trick + `WM_GETMINMAXINFO` clamp), and `<UseWPF>` + `<UseWindowsForms>` in one project builds clean (probe, this session) so the `NotifyIcon` comes along anyway. Reverting = a rewrite, so this is the one default worth reading twice.
- **B2 Credential handoff is environment-borne and process-scoped.** The owner's host-file token is hashed at rest (`TokenStore`, row 28), so the shell cannot read it and must not try. The shell mints 32 random bytes per launch, passes them as `CHOPITUP_SHELL_TOKEN` to the child, and the hub holds them in memory only, never in `tokens.json`. The hub deletes the variable from its own environment before `ProcessRunner` can inherit it into a spawn. The page receives it in memory only: `AddScriptToExecuteOnDocumentCreatedAsync` sets `window.__chopitupShellToken` on every document of the hub origin, and the client's `readOwnerToken()` consults that global before `localStorage`. Nothing is written to the WebView2 profile, so no stale copy can outlive Quit and no removal step exists for attach mode (pass 2, finding 7, overturning the pass 1 fold that wrote `localStorage`: a LevelDB copy is readable by any same-user process, including a spawn's scheduled-task escape that row 29's job check cannot see). The paste flow (`writeOwnerToken`) keeps working as the fallback and is what attach mode shows.
- **B3 Attach mode.** When `<data>\hub.lock` is held and `/health` answers on the port in `<data>\hub.port` (polled within the same 20 s budget as a start: a hand-started hub takes the lock before it binds and writes the port file, and Task 1 makes the hub delete `hub.port` right after taking the lock so a readable port always belongs to the lock holder; pass 2, finding 5), the shell attaches (no child, no token, the page shows today's paste field). Quit then closes the window and leaves that hub alone. The tray chip says `attached`. The port in `hub.port` may differ from `--port`: the window navigates, the bridge trusts and the tray labels by the origin the shell actually resolved (`HubChild.ResolvedOrigin`), never by `ShellArgs.Port` (critique pass 1, finding 1).
- **B4 Quit kills, it does not ask.** `Process.Kill(entireProcessTree: true)` plus the Job Object's kill-on-close. SQLite runs in WAL (M2) and `HubLock` is a file handle released by process exit. One thing does prefer a graceful stop: `SpawnerService.StopAsync` waits up to 10 s for a row 35 worktree merge, and Kill can land mid-merge; that is recoverable by design (`ExchangeWorktrees.AbortStaleExchangeMergeAsync` runs at the next hub start and posts "The last hub's exchange merge was aborted."), which is why Kill is acceptable. Quit does not wait for in-flight exchanges; the owner stops them first if they care. Sending Ctrl+C to a windowless child needs console attachment tricks; declined.
- **B5 Navigation is locked to the hub origin.** Any other origin (links in messages) opens in the default browser; `NewWindowRequested` does the same. The bridge only answers messages whose source is the hub origin.
- **B6 Second launch shows the first.** A named mutex per data dir plus two named events: `--show` (default for a bare second launch) activates the primary's window; `--quit` asks the primary to quit. These are also what the self-check harness drives.
- **B7 Window bounds are not persisted across Quit** (default 1280×800, min 640×400). Hide/show keeps them within a run. A later row if wanted.
- **B8 The boot page is HTML in the WebView2, not native UI.** While the hub starts (or if it fails) the WebView2 shows a dark page in the client's palette with its own drag strip and a Close/Quit pair, so the window can be moved or dismissed before React loads. Failure shows the last 40 log lines. The boot page's messages carry a per-launch nonce; the bridge trusts a non-hub-origin source only with that nonce (pass 1, finding 12).
- **B11 The WebView2 profile lives under `%LOCALAPPDATA%\ChopItUp\webview2\<hash16(dataDir)>`**, not inside `data\`: it is regenerable, hundreds of MB, and must not sit beside `chopitup.db` and `tokens.json` in the directory every backup and safety rule protects (pass 1, finding 20).
- **B9 The tray menu is three items:** Open, a disabled status line (`Hub running on :8790` / `Attached to :8790` / `Hub failed`), Quit. Double-click = Open. Owner chose "None" for folded-in elements.
- **B10 Dev launch takes `--hub <path>`**; the default is `ChopItUp.Hub.exe` beside the shell exe. No ProjectReference from Desktop to Hub (an exe-to-exe reference drags `wwwroot` and the client build into the wrong output).

## Acceptance
1. WHEN `ChopItUp.Desktop.exe --data D --port P --hub H` starts and no hub holds `D` THE SYSTEM SHALL start `H --data D --port P` as a child in a kill-on-close Job Object with `CHOPITUP_SHELL_TOKEN` set to a fresh 43-char token, show the boot page (logging `BOOT SHOWN`), and navigate to `http://127.0.0.1:P/` once `GET /health` returns `ok: true`; WHEN the child exits before `/health` answers, or 20 s pass, or starting throws (exe present but not launchable, lock file unreadable, job assignment fails) THE SYSTEM SHALL show the failure page with the reason and the log tail and keep the tray icon, never staying on "Starting" past the budget.
2. WHEN a hub started with `CHOPITUP_SHELL_TOKEN=T` receives `Authorization: Bearer T` on a guarded `/api` write THE SYSTEM SHALL treat it as `owner`; `tokens.json` SHALL be byte-identical before and after `TokenStore.Load`; `Environment.GetEnvironmentVariable("CHOPITUP_SHELL_TOKEN")` SHALL be null inside the hub after `HubHost.Build`, so a spawn's inherited environment never carries it; and the harness SHALL prove the whole path end to end: a message typed into the hosted page's composer lands with `authorId: owner`, both with no spawn live and while a stub-CLI spawn is in flight (the row 29 peer check then runs against a WebView2 network process).
3. WHEN the page loads inside the shell (`window.chrome.webview` present) THE UI SHALL render a 36 px chrome row above the rail/room grid with the wordmark, a hub chip and Minimize / Maximize-or-Restore / Close buttons, the row marked `app-region: drag` and the buttons `no-drag`; WHEN the page loads in a plain browser THE UI SHALL render no chrome row and every existing client test SHALL pass unchanged (so hosted detection never touches `window` at module load; vitest runs under node).
4. WHEN Close is pressed (chrome row, boot page, or Alt+F4) THE SYSTEM SHALL hide the window and keep the hub and tray icon running; WHEN Open (tray menu, tray double-click, or a second launch / `--show`) is invoked THE SYSTEM SHALL show and activate the same window with its bounds unchanged.
5. WHEN Quit is chosen (tray menu or `--quit`) THE SYSTEM SHALL kill the hub it started, release `D\hub.lock` within 5 s, remove the tray icon and exit 0; WHEN the shell attached to a hub it did not start THE SYSTEM SHALL exit without touching that hub; WHEN that hub's `hub.port` differs from `--port` THE SYSTEM SHALL have navigated to, trusted and labelled the attached port, with no launch token injected (the page shows today's paste flow); WHEN the lock is held but `hub.port` is absent for the first seconds THE SYSTEM SHALL keep probing within the 20 s budget and attach once it appears.
6. WHEN `tools\Deploy-ChopItUp.ps1` runs THE SYSTEM SHALL publish both projects into staging, refuse staging output where either exe is missing or below its floor (hub 30 MB, desktop 100 MB), copy both exes in via copy-aside-and-rename, and `Invoke-M4SelfCheck.ps1`'s `$allowedTopLevel` SHALL be exactly `ChopItUp.Hub.exe`, `ChopItUp.Desktop.exe`, `ChopItUp.Hub.staticwebassets.endpoints.json`, `web.config`, `wwwroot` (the hub's publish emits the middle two today, ledger 19; the check runs on a publish dir, which never holds `data\` or `logs\`); every existing `DeployScriptTests` case SHALL pass with the fixture extended and the suite's wall time SHALL not grow by more than 10 % (floors are parameterised, tests use KB-scale floors).
7. WHEN the window is maximized THE SYSTEM SHALL fit the monitor work area (not overhang the taskbar) and the WebView2 margin SHALL be 0; WHEN not maximized the margin SHALL be 6 px so the resize edges respond.

## Lessons consulted
- **M4 release shape** (`ChopItUp.Hub.csproj` comments): single-file + `IncludeNativeLibrariesForSelfExtract`, never `IncludeAllContentForSelfExtract`; `DebugType=embedded` is repo-wide. The Desktop csproj mirrors the Release block and adds a target dropping the WebView2 package's loose `*.xml` docs (probe: without it three XML files land beside the exe and `c1.loose-file-set-exact` fails).
- **M3** (task-level, not target-level, conditions): the XML-drop target is unconditional.
- **M18** (incremental builds hide errors): every task's gate is `dotnet clean` then `-warnaserror`; while the deployed hub runs, `--artifacts-path`.
- **M23 / Row 34** (UI gate in a hidden pane): the hosted chrome is verified on the real shell window by UIA (`Invoke-Row12ShellCheck.ps1`), not in the browser pane; the plain-browser branch by `renderToStaticMarkup` tests.
- **M5 / Row 29** (job objects, credentials in spawns): the shell reuses the row 29 `JobObjectInterop` shape; the scrub in Task 1 exists because `ProcessRunner` inherits the parent environment (`psi.Environment[k] = v` only adds).
- **M28** (name the call sites of a changed signature): `TokenStore.Load` gains an optional trailing parameter; its call sites (`HubHost.Build`, `HostCommands` rotate, `TokenStoreTests`) compile unchanged.
- **Row 35** (worktrees): not touched.
- Memory `reference_singlefile_wpf_traps`: WindowsAppSDK/MsixTooling full extraction moves BaseDirectory into TEMP; this project references neither.
- Curio `verify/web/proofs/142.spikes.md` spike (i) (read this session via digest): route A moves the window; the resize edge over the WebView2 child does NOT respond without the `Web.Margin` trick; `IsNonClientRegionSupportEnabled` must be set before the first `Navigate`; Chromium's `app-region` hit test is geometry-driven, so anything stacked over the strip must declare `no-drag` explicitly.

## Could not verify in this environment
- A real mouse drag of the strip and a real double-click maximize. Curio's spike proved route A on this machine's WebView2 family (runtime 152.0.4191.66 today); the harness asserts the setting is on and the strip's computed `app-region`, not the gesture.
- The tray icon's visibility in the notification area (Windows may hide it in the overflow). The harness drives `--show`/`--quit` through the single-instance events instead and reads `NotifyIcon.Visible` through the shell log.
- CI (`windows-latest`) never creates a WebView2; `ChopItUp.Desktop.Tests` are pure. The shell's `EnsureCoreWebView2Async` path is exercised only by the local harness.
- Publish size: the probe exe was 174 MB (WPF + WinForms self-contained). The floor is 100 MB; a future runtime could shrink it below that and trip the sanity check, which is the intended failure mode.
- DPI other than this desktop's; the clamp math is Curio's, copied.

## Claim ledger
| # | Claim | Verified at (commit) | Recheck (pwsh, exit 0 = holds) |
|---|-------|----------------------|--------------------------------|
| 1 | Baseline: 1027 .NET tests green (229 Core + 798 Hub, Hub run 9 m 9 s) and 112 client tests, measured this session at d5d4f2d4; `src/`, `tests/`, `tools/` unchanged since | d5d4f2d4 | `git diff --quiet d5d4f2d4 HEAD -- src tests tools` |
| 2 | `HubOptions` is a positional record ending `bool OwnerPeerCheck = true)`; `Parse(string[] args, Func<string, string?> getEnv)` reads env at lines 142-147 (`CHOPITUP_DATA`, `CHOPITUP_PORT`, `CHOPITUP_ROOMS`, `CHOPITUP_OWNER_PEER_CHECK`); `DefaultPort = 8790` | d5d4f2d4 | `if ((Select-String -LiteralPath src/ChopItUp.Hub/Hosting/HubOptions.cs -SimpleMatch 'bool OwnerPeerCheck = true)' -Quiet) -and (Select-String -LiteralPath src/ChopItUp.Hub/Hosting/HubOptions.cs -SimpleMatch 'ownerPeerCheck ??= getEnv("CHOPITUP_OWNER_PEER_CHECK");' -Quiet) -and (Select-String -LiteralPath src/ChopItUp.Hub/Hosting/HubOptions.cs -SimpleMatch 'public const int DefaultPort = 8790;' -Quiet)) { exit 0 } else { exit 1 }` |
| 3 | `TokenStore` has a private 5-arg ctor `(string dataDir, string[] allIds, Dictionary<string,string> hashed, Dictionary<string,string> ephemeral, Dictionary<string,string> justMinted)`, `Load(string dataDir, IReadOnlyList<Participant> participants)`, and `TryResolve` falls through hashed then ephemeral | d5d4f2d4 | `if ((Select-String -LiteralPath src/ChopItUp.Hub/Security/TokenStore.cs -SimpleMatch 'private TokenStore(string dataDir, string[] allIds, Dictionary<string, string> hashed, Dictionary<string, string> ephemeral, Dictionary<string, string> justMinted)' -Quiet) -and (Select-String -LiteralPath src/ChopItUp.Hub/Security/TokenStore.cs -SimpleMatch 'public static TokenStore Load(string dataDir, IReadOnlyList<Participant> participants)' -Quiet) -and (Select-String -LiteralPath src/ChopItUp.Hub/Security/TokenStore.cs -SimpleMatch 'public bool TryResolve(string presented, out string participantId)' -Quiet)) { exit 0 } else { exit 1 }` |
| 4 | `TokenStore.Load(` has exactly two call sites in src: `HubHost.Build` (`var tokens = TokenStore.Load(options.DataDir, roster);`) and `HostCommands` (rotate, line 93); `TokenStoreTests.cs` exists | d5d4f2d4 | `if ((@(Get-ChildItem -Path src -Recurse -Filter *.cs \| Select-String -SimpleMatch 'TokenStore.Load(').Count -eq 2) -and (Select-String -LiteralPath src/ChopItUp.Hub/Hosting/HubHost.cs -SimpleMatch 'var tokens = TokenStore.Load(options.DataDir, roster);' -Quiet) -and (Test-Path tests/ChopItUp.Hub.Tests/TokenStoreTests.cs)) { exit 0 } else { exit 1 }` |
| 5 | `ProcessRunner` adds spec env onto the inherited environment: `foreach (var (k, v) in spec.Environment) psi.Environment[k] = v;` | d5d4f2d4 | `if (Select-String -LiteralPath src/ChopItUp.Hub/Spawning/ProcessRunner.cs -SimpleMatch 'foreach (var (k, v) in spec.Environment) psi.Environment[k] = v;' -Quiet) { exit 0 } else { exit 1 }` |
| 6 | `HubLock.FileName = "hub.lock"` opened `FileShare.None`; `HubPortFile.FileName = "hub.port"` holds a bare int | d5d4f2d4 | `if ((Select-String -LiteralPath src/ChopItUp.Hub/Hosting/HubLock.cs -SimpleMatch 'public const string FileName = "hub.lock";' -Quiet) -and (Select-String -LiteralPath src/ChopItUp.Hub/Hosting/HubPortFile.cs -SimpleMatch 'public const string FileName = "hub.port";' -Quiet)) { exit 0 } else { exit 1 }` |
| 7 | `/health` returns `{ ok = true, schema, key_usage }` | d5d4f2d4 | `if (Select-String -LiteralPath src/ChopItUp.Hub/Hosting/HubHost.cs -SimpleMatch 'app.MapGet("/health", (ChopDb d, MessageStore s) => Results.Json(new' -Quiet) { exit 0 } else { exit 1 }` |
| 8 | The client stores the owner token under `localStorage` key `chopitup.ownerToken` and reads it on load | d5d4f2d4 | `if ((Select-String -LiteralPath src/ChopItUp.Hub/client/src/ownerToken.ts -SimpleMatch "const KEY = 'chopitup.ownerToken';" -Quiet) -and (Select-String -LiteralPath src/ChopItUp.Hub/client/src/App.tsx -SimpleMatch 'readOwnerToken' -Quiet)) { exit 0 } else { exit 1 }` |
| 9 | `App.tsx` renders `<div className="app">` with `<RoomRail` first and `<main className="room">` second; `.app` is a two-column grid `248px minmax(0, 1fr)`; `.wordmark` and `.rail-head` exist in `styles.css` | d5d4f2d4 | `if ((Select-String -LiteralPath src/ChopItUp.Hub/client/src/App.tsx -SimpleMatch '<div className="app">' -Quiet) -and (Select-String -LiteralPath src/ChopItUp.Hub/client/src/styles.css -SimpleMatch 'grid-template-columns: 248px minmax(0, 1fr);' -Quiet) -and (Select-String -LiteralPath src/ChopItUp.Hub/client/src/styles.css -SimpleMatch '.wordmark {' -Quiet)) { exit 0 } else { exit 1 }` |
| 10 | Client component tests use `renderToStaticMarkup` (no jsdom); `vitest run` is the test script | d5d4f2d4 | `if ((Select-String -LiteralPath src/ChopItUp.Hub/client/src/App.test.tsx -SimpleMatch 'renderToStaticMarkup' -Quiet) -and (Select-String -LiteralPath src/ChopItUp.Hub/client/package.json -SimpleMatch '"test": "vitest run"' -Quiet)) { exit 0 } else { exit 1 }` |
| 11 | `JobObjectInterop` (row 29) exposes `CreateKillOnCloseJob()`, `Assign(SafeFileHandle job, nint processHandle)`, `IsInJob(int pid, SafeFileHandle job)` | d5d4f2d4 | `if ((Select-String -LiteralPath src/ChopItUp.Hub/Spawning/JobObjectInterop.cs -SimpleMatch 'public static SafeFileHandle CreateKillOnCloseJob()' -Quiet) -and (Select-String -LiteralPath src/ChopItUp.Hub/Spawning/JobObjectInterop.cs -SimpleMatch 'public static void Assign(SafeFileHandle job, nint processHandle)' -Quiet)) { exit 0 } else { exit 1 }` |
| 12 | Deploy script: publishes only the hub (`& dotnet publish $hubProj -c Release -o $source`), sanity floor `$floorBytes = 30MB` on `ChopItUp.Hub.exe`, robocopy `/XF ChopItUp.Hub.exe`, copy-aside `ChopItUp.Hub.exe.new`; `DeployScriptTests` fixture writes `ChopItUp.Hub.exe` from `StagingExeBytes` | d5d4f2d4 | `if ((Select-String -LiteralPath tools/Deploy-ChopItUp.ps1 -SimpleMatch '& dotnet publish $hubProj -c Release -o $source' -Quiet) -and (Select-String -LiteralPath tools/Deploy-ChopItUp.ps1 -SimpleMatch '/XF ChopItUp.Hub.exe' -Quiet) -and (Select-String -LiteralPath tests/ChopItUp.Hub.Tests/DeployScriptTests.cs -SimpleMatch 'File.WriteAllBytes(Path.Combine(StagingDir, "ChopItUp.Hub.exe"), StagingExeBytes);' -Quiet)) { exit 0 } else { exit 1 }` |
| 13 | `Invoke-M4SelfCheck.ps1` has checks `c1.exe-exists`, `c1.exe-size-floor-30mb`, `c1.loose-file-set-exact`, `c1.zero-loose-dll/pdb/deps-json`, `c1.wwwroot-index-html-exists` | d5d4f2d4 | `if (@(Select-String -LiteralPath tools/Invoke-M4SelfCheck.ps1 -Pattern "c1\.(exe-exists\|exe-size-floor-30mb\|loose-file-set-exact\|zero-loose-dll\|zero-loose-pdb\|zero-loose-deps-json\|wwwroot-index-html-exists)").Count -ge 7) { exit 0 } else { exit 1 }` |
| 14 | `tests/Directory.Build.props` supplies xunit 2.9.3 + Test SDK to every project under `tests/`; `Directory.Build.props` sets `DebugType=embedded` for Release repo-wide | d5d4f2d4 | `if ((Select-String -LiteralPath tests/Directory.Build.props -SimpleMatch '<PackageReference Include="xunit" Version="2.9.3" />' -Quiet) -and (Select-String -LiteralPath Directory.Build.props -SimpleMatch '<DebugType>embedded</DebugType>' -Quiet)) { exit 0 } else { exit 1 }` |
| 15 | `ChopItUp.slnx` lists exactly 5 projects (Core, Hub, two test projects, Corpus) — true at HEAD only; Task 2 makes it 7, so this row is expected to fail any recheck run after Task 2 | d5d4f2d4 | `if (@(Select-String -LiteralPath ChopItUp.slnx -SimpleMatch '<Project Path=').Count -eq 5) { exit 0 } else { exit 1 }` |
| 16 | Machine: WebView2 Evergreen runtime 152.0.4191.66 installed; newest `Microsoft.Web.WebView2` on nuget.org is 1.0.4191.47; SDK 10.0.401 | — (measured 2026-09-15) | `if ((Test-Path 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}')) { exit 0 } else { exit 1 }` |
| 17 | Probe (scratch, this session): a `net10.0-windows` project with `UseWPF` + `UseWindowsForms` + WebView2 1.0.4191.47 builds `-warnaserror` clean once `App.xaml.cs` writes `System.Windows.Application` (CS0104 ambiguity otherwise); Release publish with the Hub's single-file block, `DebugType=embedded` and an XML-drop target yields exactly one file, 174,114,012 bytes | — | — (the project does not exist at HEAD, so no recheck can run before Task 2; Task 2's gate re-measures it: one file, ≥ 100 MB) |
| 19 | `Invoke-M4SelfCheck.ps1:258` is `$allowedTopLevel = @('ChopItUp.Hub.exe', 'ChopItUp.Hub.staticwebassets.endpoints.json', 'web.config', 'wwwroot')` | d5d4f2d4 | `if (Select-String -LiteralPath tools/Invoke-M4SelfCheck.ps1 -SimpleMatch "@('ChopItUp.Hub.exe', 'ChopItUp.Hub.staticwebassets.endpoints.json', 'web.config', 'wwwroot')" -Quiet) { exit 0 } else { exit 1 }` |
| 20 | `tests/ChopItUp.Hub.Tests/AssemblyInfo.cs` carries `[assembly: CollectionBehavior(DisableTestParallelization = true)]` (per-assembly; the new test project must repeat it) | d5d4f2d4 | `if (Select-String -LiteralPath tests/ChopItUp.Hub.Tests/AssemblyInfo.cs -SimpleMatch 'DisableTestParallelization = true' -Quiet) { exit 0 } else { exit 1 }` |
| 18 | `CoreWebView2Settings.IsNonClientRegionSupportEnabled` enables CSS `app-region: drag/no-drag`, takes effect on the next navigation (Microsoft Learn, read this session) | — | — |

## Tasks

Branch: `row12-desktop-shell` from `main` at d5d4f2d4. Repo root `C:\Agent Projects\ChopItUp`.

Baseline (measured this session at d5d4f2d4): **1027 .NET tests green (229 Core + 798 Hub) and 112 client tests green.** No task edits an existing test except `DeployScriptTests` (Task 8 extends its fixture) and `SpaServingTests`/`App.test.tsx` are untouched.

Commands used by every task:
```powershell
dotnet clean ChopItUp.slnx -c Debug -v minimal
dotnet build ChopItUp.slnx -c Debug -warnaserror -v minimal
dotnet test tests/ChopItUp.Hub.Tests -c Debug --nologo -v minimal --filter "FullyQualifiedName~TokenStoreTests|FullyQualifiedName~HubHostTests|FullyQualifiedName~HubOptionsTests"
dotnet test tests/ChopItUp.Desktop.Tests -c Debug --nologo -v minimal
dotnet test ChopItUp.slnx -c Debug --nologo -v minimal
npm --prefix src/ChopItUp.Hub/client test
```
Never build while a Debug hub runs (exe lock); while the deployed hub is running, pass `--artifacts-path` to build. Each task ends with the full `dotnet test ChopItUp.slnx` green (Task 1 onward) and one commit. Task classes: 1, 2, 3, 5, 8 = `sonnet`; 4, 6, 7 = `opus` (the owner sees them). Task 9 is the orchestrator's.

### Task 1: the hub accepts a launch-scoped owner bearer from `CHOPITUP_SHELL_TOKEN` and scrubs it (sonnet)

Files: `src/ChopItUp.Hub/Hosting/HubOptions.cs`, `src/ChopItUp.Hub/Hosting/HubHost.cs`, `src/ChopItUp.Hub/Security/TokenStore.cs`, `tests/ChopItUp.Hub.Tests/TokenStoreTests.cs`, `tests/ChopItUp.Hub.Tests/HubHostTests.cs` (the existing `HubOptions.Parse` tests live here at lines 133-150; the new Parse test goes beside them).

Also in this task (pass 2, finding 5): `HubHost.Build` deletes `<data>\hub.port` immediately after `HubLock.Acquire` (line 28; `File.Delete` inside try, ignore `IOException`), so a port file that exists while the lock is held always belongs to the current hub, never to the previous run. `ApplicationStarted` writes it as today. Test in `HubHostTests`: write a bogus `hub.port` = `1` into a fresh dir, start `HubTestHost`, assert `HubPortFile.Read(dir)` equals the bound port (not 1) after start, and that during the window between `Build` and `Run` the file is absent (assert right after `HubHost.Build` returns, before the host runs, using the existing `Build` seam if `HubTestHost` exposes one; otherwise assert only the post-start value and note it).

**1a. RED.** Tests, in this order:

`TokenStoreTests`:
```csharp
    [Fact]
    public void Load_with_shell_owner_token_resolves_it_to_owner_and_writes_nothing()
    {
        using var dir = TestDirs.Temp();   // use the file's existing temp-dir helper; if it is named differently, use that one
        var roster = ChopDb.SeedRoster;    // the participant list the other tests in this file use; mirror them
        var first = TokenStore.Load(dir.Path, roster);            // mints and writes tokens.json
        var before = File.ReadAllBytes(Path.Combine(dir.Path, TokenStore.FileName));

        var store = TokenStore.Load(dir.Path, roster, shellOwnerToken: "shell-launch-token");
        var after = File.ReadAllBytes(Path.Combine(dir.Path, TokenStore.FileName));

        Assert.True(store.TryResolve("shell-launch-token", out var who));
        Assert.Equal(ChopDb.OwnerParticipantId, who);
        Assert.Equal(before, after);
        Assert.Equal(first.Count, store.Count);          // the shell token is not a participant credential
        Assert.False(first.TryResolve("shell-launch-token", out _));
    }

    [Fact]
    public void Load_without_shell_owner_token_refuses_it()
    {
        using var dir = TestDirs.Temp();
        var store = TokenStore.Load(dir.Path, ChopDb.SeedRoster, shellOwnerToken: null);
        Assert.False(store.TryResolve("shell-launch-token", out _));
    }
```
(Adapt the temp-dir and roster helpers to whatever `TokenStoreTests.cs` already uses; do not invent new helpers.)

`HubHostTests` (beside the existing Parse tests):
```csharp
    [Fact]
    public void Parse_reads_the_shell_token_from_the_environment_only()
    {
        var env = new Dictionary<string, string?> { ["CHOPITUP_SHELL_TOKEN"] = "abc" };
        var o = HubOptions.Parse(["--data", "x"], k => env.GetValueOrDefault(k));
        Assert.Equal("abc", o.ShellToken);
        Assert.Null(HubOptions.Parse(["--data", "x"], _ => null).ShellToken);
    }
```

`HubHostTests`:
```csharp
    [Fact]
    public async Task Build_scrubs_the_shell_token_from_the_process_environment()
    {
        Environment.SetEnvironmentVariable(HubOptions.ShellTokenEnvVar, "scrub-me");
        try
        {
            await using var host = await HubTestHost.StartAsync(TestDirs.New());   // mirror the file's existing StartAsync usage
            Assert.Null(Environment.GetEnvironmentVariable(HubOptions.ShellTokenEnvVar));
        }
        finally { Environment.SetEnvironmentVariable(HubOptions.ShellTokenEnvVar, null); }
    }

    [Fact]
    public async Task A_shell_token_authenticates_an_owner_write()
    {
        var dir = TestDirs.New();
        await using var host = await HubTestHost.StartAsync(dir, shellToken: "launch-token");
        var client = host.Client;   // the fixture's HttpClient; mirror ChatApiTests' owner post shape
        client.DefaultRequestHeaders.Authorization = new("Bearer", "launch-token");
        var res = await client.PostAsJsonAsync("/api/rooms/general/messages", new { body = "hello from the shell" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ChopDb.OwnerParticipantId, body.GetProperty("authorId").GetString());
    }
```
Read `ChatApiTests.cs` for the exact owner-post route and JSON property casing before writing the second test; if the route differs, use the file's. Run the filters: all six new tests (2 TokenStore, 1 Parse, 2 HubHost token, 1 hub.port) fail to compile or fail (`ShellToken`, `shellOwnerToken`, `shellToken` do not exist; the port file is not deleted).

**1b. GREEN.**

`HubOptions.cs`: add `string? ShellToken = null` as the LAST positional parameter of the record (after `bool OwnerPeerCheck = true`), add
```csharp
    /// <summary>Row 12: a launch-scoped owner bearer the desktop shell hands its child hub. Read from
    /// the environment only (never an argument, so it is not in any process listing), held in memory
    /// by <see cref="Security.TokenStore"/>, never written, and deleted from this process's
    /// environment by <c>HubHost.Build</c> before anything can inherit it.</summary>
    public const string ShellTokenEnvVar = "CHOPITUP_SHELL_TOKEN";
```
and in `Parse`, next to the other env reads, `var shellToken = getEnv(ShellTokenEnvVar);` passed as the last constructor argument (empty string → null: `string.IsNullOrEmpty(shellToken) ? null : shellToken`).

`TokenStore.cs`: add a sixth private field `private readonly string? _shellOwner;` and ctor parameter `string? shellOwner`; `Load` gains `string? shellOwnerToken = null` and passes it through; `TryResolve` gains a third fall-through after the ephemeral loop:
```csharp
        if (match is null && _shellOwner is not null)
        {
            var presentedBytes = Encoding.UTF8.GetBytes(presented);
            var bytes = Encoding.UTF8.GetBytes(_shellOwner);
            if (bytes.Length == presentedBytes.Length && CryptographicOperations.FixedTimeEquals(bytes, presentedBytes))
                match = ChopDb.OwnerParticipantId;
        }
```
`Count` is unchanged. The doc comment on the class gets one sentence: "Row 12 adds a third class: one launch-scoped owner bearer supplied by the desktop shell, memory only."

`HubHost.cs` line 64 becomes:
```csharp
            var tokens = TokenStore.Load(options.DataDir, roster, options.ShellToken);
            // Row 12: ProcessRunner inherits this process's environment into every spawn; the shell's
            // owner bearer must not ride along. Scrubbed here, before any spawn can exist.
            Environment.SetEnvironmentVariable(HubOptions.ShellTokenEnvVar, null);
```

`HubTestHost.StartAsync`: add `string? shellToken = null` at the end of the parameter list and pass `ShellToken: shellToken` into the `HubOptions` it constructs (line 63).

**1c. Gate.** Clean build `-warnaserror` 0 warnings; the filters green; full `dotnet test ChopItUp.slnx` green (1027 + 6). Commit: `Row 12 T1: hub accepts a launch-scoped owner bearer from CHOPITUP_SHELL_TOKEN and scrubs it`.

### Task 2: the Desktop project skeleton, args, and its test project (sonnet)

Files: `src/ChopItUp.Desktop/ChopItUp.Desktop.csproj`, `src/ChopItUp.Desktop/App.xaml`, `src/ChopItUp.Desktop/App.xaml.cs`, `src/ChopItUp.Desktop/ShellArgs.cs`, `src/ChopItUp.Desktop/ShellLog.cs`, `src/ChopItUp.Desktop/AssemblyInfo.cs`, `tests/ChopItUp.Desktop.Tests/ChopItUp.Desktop.Tests.csproj`, `tests/ChopItUp.Desktop.Tests/ShellArgsTests.cs`, `ChopItUp.slnx`.

**2a. RED.** `ShellArgsTests`:
```csharp
public class ShellArgsTests
{
    [Fact]
    public void Defaults_put_data_and_hub_beside_the_exe()
    {
        var a = ShellArgs.Parse([], baseDir: @"C:\x\");
        Assert.Equal(@"C:\x\data", a.DataDir);
        Assert.Equal(8790, a.Port);
        Assert.Equal(@"C:\x\ChopItUp.Hub.exe", a.HubExe);
        Assert.Equal(ShellCommand.Run, a.Command);
    }

    [Fact]
    public void Explicit_values_win_and_are_made_absolute()
    {
        var a = ShellArgs.Parse(["--data", ".data", "--port", "8795", "--hub", @"src\Hub.exe"], baseDir: @"C:\x\", cwd: @"C:\repo\");
        Assert.Equal(@"C:\repo\.data", a.DataDir);
        Assert.Equal(8795, a.Port);
        Assert.Equal(@"C:\repo\src\Hub.exe", a.HubExe);
    }

    [Theory]
    [InlineData("--show", ShellCommand.Show)]
    [InlineData("--quit", ShellCommand.Quit)]
    public void Single_instance_verbs_parse(string flag, ShellCommand expected) =>
        Assert.Equal(expected, ShellArgs.Parse([flag], baseDir: @"C:\x\").Command);

    [Theory]
    [InlineData("--port")]
    [InlineData("--port", "nope")]
    [InlineData("--data")]
    [InlineData("--bogus")]
    public void Bad_input_throws_with_the_flag_named(params string[] args)
    {
        var ex = Assert.Throws<ArgumentException>(() => ShellArgs.Parse(args, baseDir: @"C:\x\"));
        Assert.Contains(args[0], ex.Message);
    }
}
```

**2b. GREEN.**

`src/ChopItUp.Desktop/ChopItUp.Desktop.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <RootNamespace>ChopItUp.Desktop</RootNamespace>
    <UseWPF>true</UseWPF>
    <!-- Row 12 B1: WinForms only for NotifyIcon (the tray). Both UI stacks in one exe is supported by
         the SDK and was probed clean; the price is that `Application`, `MessageBox` and friends are
         ambiguous, so this project always writes System.Windows.* in full. -->
    <UseWindowsForms>true</UseWindowsForms>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
  <ItemGroup>
    <!-- UseWindowsForms adds implicit usings for System.Windows.Forms and System.Drawing, which make
         Application, MessageBox, Cursors and HorizontalAlignment ambiguous in every WPF file. Removed
         here; the two WinForms types this project uses (NotifyIcon, ContextMenuStrip) and the Drawing
         Icon are written fully qualified. -->
    <Using Remove="System.Windows.Forms" />
    <Using Remove="System.Drawing" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Web.WebView2" Version="1.0.4191.47" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="ChopItUp.Desktop.Tests" />
  </ItemGroup>

  <!-- Release shape mirrors ChopItUp.Hub.csproj (M4): self-contained single-file, native libraries
       (WebView2Loader.dll) self-extracted, NO IncludeAllContentForSelfExtract (it would move
       BaseDirectory, and the hub exe is found beside this one). DebugType=embedded comes from the
       repo-wide Directory.Build.props. -->
  <PropertyGroup Condition="'$(Configuration)' == 'Release'">
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
  </PropertyGroup>

  <!-- The WebView2 package ships XML doc files as lib content; publish would drop three of them
       beside the exe and tools\Invoke-M4SelfCheck.ps1's exact loose-file set would fail. Measured on
       a probe this milestone. Unconditional on purpose (LESSONS M3). -->
  <Target Name="DropPackageXmlDocs" AfterTargets="ComputeFilesToPublish">
    <ItemGroup>
      <ResolvedFileToPublish Remove="@(ResolvedFileToPublish)" Condition="'%(Extension)' == '.xml'" />
    </ItemGroup>
  </Target>
</Project>
```
Task 6 adds `<ApplicationIcon>` and the `<Resource Include="chopitup.ico" />` line; this task ships neither. `app.manifest`: the SDK's default WPF manifest with `<dpiAware>true/PM</dpiAware>` and `<dpiAwareness>PerMonitorV2</dpiAwareness>` (`dotnet new wpf` does not emit one; write the standard 20-line manifest with only the DPI section and the `supportedOS` line for Windows 10/11 GUID `{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}`). Unverified (pass 2, finding 14): the WinForms analyzer `WFAC010` may fire on a manifest carrying DPI settings and becomes an error under `-warnaserror`; if it does, add `<NoWarn>$(NoWarn);WFAC010</NoWarn>` with a comment saying the manifest is the right mechanism for a WPF host and report it. Do not remove the manifest to silence it.

`ShellArgs.cs`:
```csharp
namespace ChopItUp.Desktop;

public enum ShellCommand { Run, Show, Quit }

/// <summary>Row 12: the shell's whole command line. Everything resolves to an absolute path so the
/// child hub, the log and the WebView2 profile agree on one data dir regardless of the launcher's cwd.
/// `--hub` exists for development (B10): the deployed layout has the hub beside this exe.</summary>
public sealed record ShellArgs(string DataDir, int Port, string HubExe, ShellCommand Command)
{
    public const int DefaultPort = 8790;
    public const string HubExeName = "ChopItUp.Hub.exe";

    public string LogDir => Path.Combine(DataDir, "logs");
    /// <summary>B11: outside data\. Keyed by the data dir so two data dirs never share a profile.</summary>
    public string WebViewProfileDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChopItUp", "webview2", SingleInstance.Hash16(DataDir));
    /// <summary>The origin implied by --port. Only the START path may use it; after StartOrAttach every consumer reads HubChild.ResolvedOrigin (B3).</summary>
    public Uri RequestedOrigin => new($"http://127.0.0.1:{Port}/");

    public static ShellArgs Parse(string[] args, string baseDir, string? cwd = null)
    {
        cwd ??= Environment.CurrentDirectory;
        string? data = null, port = null, hub = null;
        var command = ShellCommand.Run;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data": data = Next(args, ref i); break;
                case "--port": port = Next(args, ref i); break;
                case "--hub": hub = Next(args, ref i); break;
                case "--show": command = ShellCommand.Show; break;
                case "--quit": command = ShellCommand.Quit; break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }
        var p = port is null ? DefaultPort
            : int.TryParse(port, out var parsed) && parsed is > 0 and <= 65535 ? parsed
            : throw new ArgumentException($"--port needs a number between 1 and 65535, got '{port}'.");
        return new ShellArgs(
            Path.GetFullPath(data is null ? Path.Combine(baseDir, "data") : Path.Combine(cwd, data)),
            p,
            Path.GetFullPath(hub is null ? Path.Combine(baseDir, HubExeName) : Path.Combine(cwd, hub)),
            command);
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} requires a value.");
        return args[++i];
    }
}
```
(`Path.Combine(cwd, absolute)` returns the absolute path, so explicit absolute values pass through.)

`ShellLog.cs`: an append-only text log at `<LogDir>\desktop.log`, `Append(string line)` writing `yyyy-MM-ddTHH:mm:ss.fffZ <line>` under a lock, creating the directory lazily, swallowing IO errors (the log must never take the shell down). Also `Tail` is NOT here; the hub tail lives in Task 3.

`App.xaml`: no `StartupUri` (the window is built after the hub decision, Task 5); merges nothing yet.
`App.xaml.cs` for this task: `public partial class App : System.Windows.Application` with an empty `OnStartup` override that parses `ShellArgs` from `Environment.GetCommandLineArgs()[1..]` with `AppContext.BaseDirectory`, shows a `System.Windows.MessageBox` and `Shutdown(2)` on `ArgumentException`. Task 5 replaces its body.

`tests/ChopItUp.Desktop.Tests/ChopItUp.Desktop.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\ChopItUp.Desktop\ChopItUp.Desktop.csproj" />
  </ItemGroup>
</Project>
```
`tests/ChopItUp.Desktop.Tests/AssemblyInfo.cs`: `[assembly: CollectionBehavior(DisableTestParallelization = true)]` (ledger 20; `SingleInstanceTests` in Task 5 use real named mutexes). `SingleInstance.Hash16(string)` (first 16 hex chars of SHA-256 over the lower-invariant full path) lands in this task as a one-method static class `SingleInstance` that Task 5 extends.

`ChopItUp.slnx`: add `<Project Path="src/ChopItUp.Desktop/ChopItUp.Desktop.csproj" />` under `/src/` and the test project under `/tests/`.

`CLAUDE.md` Layout line: append ` · src/ChopItUp.Desktop (WPF + WebView2 shell, row 12; dev: dotnet run --project src/ChopItUp.Desktop -- --data .data --hub src/ChopItUp.Hub/bin/Debug/net10.0/ChopItUp.Hub.exe)` — keep the file under 4 KB (it is 3.8 KB; trim the Deploy paragraph's "(a single-file bundle can't serve static files from inside itself)" if needed).

**2c. Gate.** Clean build; `dotnet test tests/ChopItUp.Desktop.Tests` green (8 cases: 2 facts, 2 + 4 inline data); full suite green; **and the Release publish shape, measured now rather than in Task 8** (pass 1, finding 9): `dotnet publish src/ChopItUp.Desktop -c Release -o <scratch> -v minimal` then assert the scratch dir holds exactly one file, `ChopItUp.Desktop.exe`, ≥ 100 MB (report the byte count; a loose `.dll`, `.pdb`, `.xml` or `.json` is a STOP). CI: `ci.yml` builds the slnx, so the new projects build there unchanged (windows-latest). Commit: `Row 12 T2: ChopItUp.Desktop project, ShellArgs, test project`.

### Task 3: `HubChild` starts, watches, and stops the hub; attach detection (sonnet)

Files: `src/ChopItUp.Desktop/Hub/JobObjectInterop.cs` (copy of `src/ChopItUp.Hub/Spawning/JobObjectInterop.cs`, namespace `ChopItUp.Desktop.Hub`, header comment "copied from the hub's row 29 file; the Desktop project has no reference to the Hub exe (B10)"), `src/ChopItUp.Desktop/Hub/IHubProcess.cs`, `src/ChopItUp.Desktop/Hub/HubChild.cs`, `src/ChopItUp.Desktop/Hub/HubProbe.cs`, `src/ChopItUp.Desktop/Hub/LogTail.cs`, `tests/ChopItUp.Desktop.Tests/HubChildTests.cs`, `tests/ChopItUp.Desktop.Tests/LogTailTests.cs`, `tests/ChopItUp.Desktop.Tests/HubProbeTests.cs`.

Design: `HubChild` owns the state machine; the process and the HTTP probe are seams.

```csharp
namespace ChopItUp.Desktop.Hub;

/// <summary>What HubChild needs from a process: start it, know when it exits, kill it. Seamed so the
/// state machine is tested without a real hub.</summary>
public interface IHubProcess : IDisposable
{
    int Pid { get; }
    bool HasExited { get; }
    event Action<string> OutputLine;     // stdout and stderr, merged, one line at a time
    event Action Exited;
    /// <summary>Starts the output pumps. Called by HubChild AFTER it has subscribed OutputLine and
    /// Exited, so a hub that dies in its first milliseconds (port in use is the common one) still
    /// lands its lines in the tail (pass 2, finding 1).</summary>
    void BeginReading();
    void Kill();
}

public interface IHubProcessFactory
{
    IHubProcess Start(string exe, string dataDir, int port, string shellToken);
}

public interface IHealthProbe
{
    /// <summary>True when GET /health answered 200 with ok:true.</summary>
    Task<bool> IsHealthyAsync(Uri origin, CancellationToken ct);
}

public enum HubState { Starting, Ready, Failed, Attached, Stopped }

/// <summary>Port is the one the hub is actually on: --port when the shell started it, the value of
/// hub.port when it attached. Every consumer (navigate, trusted source, tray, chip) reads it from here.</summary>
public sealed record HubStatus(HubState State, int? Pid, int Port, string? Reason);
```

`HubChild`:
```csharp
public sealed class HubChild : IDisposable
{
    public static readonly TimeSpan ReadyBudget = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly ShellArgs _args;
    private readonly IHubProcessFactory _factory;
    private readonly IHealthProbe _probe;
    private readonly TimeProvider _clock;
    private readonly Action<string> _log;
    private IHubProcess? _proc;
    public LogTail Tail { get; } = new(40);
    public string? ShellToken { get; private set; }
    public HubStatus Status { get; private set; }
    /// <summary>Where the hub really is. Valid once Status is Ready or Attached; before that it is the
    /// requested origin (the boot page needs none).</summary>
    public Uri ResolvedOrigin => new($"http://127.0.0.1:{Status.Port}/");
    public event Action<HubStatus>? StatusChanged;   // raised on whatever thread set the status; App marshals (Task 5)

    // Lifetimes (pass 1, finding 21): HubChild owns the factory (which owns the kill-on-close job
    // handle) and App owns HubChild for the process lifetime, so the job never finalizes early.
    public HubChild(ShellArgs args, IHubProcessFactory factory, IHealthProbe probe, TimeProvider clock, Action<string> log)
    { ...; Status = new(HubState.Starting, null, args.Port, null); }

    /// <summary>B3: a hub already owns the data dir → attach. Otherwise start ours and wait for
    /// /health. Never throws: the whole body is guarded and every exception becomes Failed.</summary>
    public async Task StartOrAttachAsync(CancellationToken ct)
    {
        try { await StartOrAttachCoreAsync(ct); }
        catch (Exception ex) { Set(new(HubState.Failed, _proc?.Pid, Status.Port, $"{ex.GetType().Name}: {ex.Message}")); }
    }

    private async Task StartOrAttachCoreAsync(CancellationToken ct)
    {
        var deadline = _clock.GetUtcNow() + ReadyBudget;
        if (HubProbe.LockIsHeld(_args.DataDir))
        {
            // A hand-started hub takes the lock before it binds; hub.port appears after (Task 1 makes
            // the hub delete it right after locking). Poll, never probe once.
            int? port = null;
            while (_clock.GetUtcNow() < deadline)
            {
                port = HubProbe.ReadPort(_args.DataDir);
                if (port is int p && await _probe.IsHealthyAsync(new Uri($"http://127.0.0.1:{p}/"), ct))
                {
                    Set(new(HubState.Attached, null, p, $"attached to :{p}"));
                    return;
                }
                await Task.Delay(PollInterval, _clock, ct);
            }
            Set(new(HubState.Failed, null, port ?? _args.Port, $"another hub holds {_args.DataDir} but {(port is null ? "wrote no hub.port" : $":{port}/health does not answer")} within {ReadyBudget.TotalSeconds:0} s"));
            return;
        }
        if (!File.Exists(_args.HubExe)) { Set(new(HubState.Failed, null, _args.Port, $"hub exe not found: {_args.HubExe}")); return; }

        ShellToken = NewToken();
        _proc = _factory.Start(_args.HubExe, _args.DataDir, _args.Port, ShellToken);
        _proc.OutputLine += line => { Tail.Add(line); _log("HUB " + line); };
        // Any non-terminal state at exit time is a failure (Starting, or Ready if the probe answered
        // and the hub died right after; pass 2, finding 13).
        _proc.Exited += () => { if (Status.State is HubState.Starting or HubState.Ready) Set(new(HubState.Failed, _proc.Pid, _args.Port, @"the hub exited (see data\logs\hub.log)")); };
        _proc.BeginReading();
        _log($"HUB START pid={_proc.Pid}");

        while (_clock.GetUtcNow() < deadline)
        {
            if (_proc.HasExited) { Set(new(HubState.Failed, _proc.Pid, _args.Port, "the hub exited before it was ready")); return; }
            if (await _probe.IsHealthyAsync(_args.RequestedOrigin, ct))
            {
                Set(new(HubState.Ready, _proc.Pid, _args.Port, null));
                if (_proc.HasExited) Set(new(HubState.Failed, _proc.Pid, _args.Port, @"the hub exited (see data\logs\hub.log)"));
                return;
            }
            await Task.Delay(PollInterval, _clock, ct);
        }
        Set(new(HubState.Failed, _proc.Pid, _args.Port, $"/health did not answer within {ReadyBudget.TotalSeconds:0} s"));
    }

    /// <summary>B4. Idempotent. Attached → nothing to stop.</summary>
    public void Stop()
    {
        if (_proc is null || _proc.HasExited) { if (Status.State != HubState.Attached) Set(Status with { State = HubState.Stopped }); return; }
        _log($"HUB KILL pid={_proc.Pid}");
        _proc.Kill();
        Set(new(HubState.Stopped, _proc.Pid, Status.Port, null));
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');   // 43 chars, URL-safe
    private void Set(HubStatus s) { Status = s; _log($"HUB STATE {s.State} {s.Reason}"); StatusChanged?.Invoke(s); }
    public void Dispose() { Stop(); _proc?.Dispose(); }
}
```
`HubProbe` (static): `LockIsHeld(dataDir)` = open `hub.lock` with `FileShare.None` inside try, return `false` when it opens or does not exist, `true` on `IOException` (mirror of `HubLock.IsHeld`, ledger 6); an `UnauthorizedAccessException` propagates and is turned into `Failed` by the outer guard (its message names the path); `ReadPort(dataDir)` = parse `hub.port` as int in 1..65535 else null. `HttpHealthProbe : IHealthProbe` = one shared `HttpClient` with a 2 s timeout, `GET {origin}health`, true iff 200 and JSON `ok` is `true`; any exception → false.

`ProcessHubFactory : IHubProcessFactory`: `ProcessStartInfo(exe)` with `ArgumentList` `--data`, dataDir, `--port`, port; `UseShellExecute=false`, `CreateNoWindow=true`, redirect stdout+stderr (NOT stdin); `Environment[HubOptions.ShellTokenEnvVar] = shellToken` — the literal `"CHOPITUP_SHELL_TOKEN"` is repeated here with a comment naming `HubOptions.ShellTokenEnvVar` (no project reference, B10); `EnableRaisingEvents=true`; after `Start()`, `JobObjectInterop.Assign(job, proc.Handle)` with a job from `CreateKillOnCloseJob()` held for the factory's lifetime, wrapped exactly as `SpawnJobs.Track` does it (`src/ChopItUp.Hub/Spawning/SpawnJobs.cs:57-61`): `catch (Win32Exception) when (proc.HasExited)` is not an error, the exit reason is (pass 2, finding 1); `BeginReading()` = `BeginOutputReadLine()` + `BeginErrorReadLine()` both feeding `OutputLine` (the drain is what keeps the child from blocking on a full pipe), called by `HubChild` after it has subscribed, never inside `Start`. `Kill()` = `proc.Kill(entireProcessTree: true)` inside try. Also writes every line to `<LogDir>\hub.log`; at factory construction, if `hub.log` exceeds 5 MB it is renamed to `hub.1.log` (replacing any previous one), so the pair is bounded at ~10 MB; `ShellLog` applies the same rule to `desktop.log`. The deploy script already excludes `logs\`. Grandchild handle inheritance (a spawn outliving the hub keeping the stdout pipe open) is not a concern here: the shell never waits for EOF, it reacts to `Exited`, and the kill-on-close job takes the whole tree down with the shell.

`LogTail(int capacity)`: ring buffer of the last N lines, `Add`, `Snapshot() → IReadOnlyList<string>`; thread-safe (lock).

**3a. RED.** `LogTailTests` (keeps last N, order preserved, empty snapshot). `HubProbeTests` (lock file absent → false; a file held open with `FileShare.None` from the test → true; released → false; `hub.port` "8795" → 8795, "x" → null, missing → null). `HubChildTests` with a `FakeHubProcess`/`FakeFactory`/`FakeProbe` and `FakeTimeProvider` (add `Microsoft.Extensions.TimeProvider.Testing` 10.0.0 to the test csproj, as `Hub.Tests` does). Fixture (pass 2, finding 11): each test gets a temp dir with an empty file named `ChopItUp.Hub.exe` (the start path checks `File.Exists`) and, for attach cases, a `FileStream` on `hub.lock` opened `FileShare.None` and disposed at the end; `ShellArgs` is built with `--data <tempdir> --hub <that file>`. Cases:
- starts with `--data`/`--port` and a 43-char token in the factory call; probe false×3 then true → `Ready` with the pid and `Port == args.Port`; StatusChanged fired `Starting→Ready`; `ResolvedOrigin` is `http://127.0.0.1:<port>/`.
- factory `Start` throws `Win32Exception("blocked")` → `Failed` with a reason containing `Win32Exception` and `blocked`; nothing hangs, no unobserved exception (`TaskScheduler.UnobservedTaskException` is not needed: the method returns normally).
- lock held, `hub.port` says 8811 while `--port` is 8790, probe true only for 8811 → `Attached` with `Port == 8811` and `ResolvedOrigin` on 8811.
- lock held, no `hub.port` for the first 2 s of fake time, then the file appears with 8811 and the probe answers → `Attached` on 8811 (the attach loop polls); lock held and no port ever → `Failed` naming "wrote no hub.port".
- fake process raises two output lines synchronously inside `BeginReading()` → both in `Tail` (proves the subscribe-then-read order).
- probe true, then the process exits before the next poll → `Failed` with the log hint (not `Ready`).
- process exits before healthy → `Failed` with "exited before it was ready"; Stop() afterwards does not call Kill again.
- probe never true, advance the fake clock past 20 s → `Failed` naming the budget.
- lock held + probe true on the port from `hub.port` → `Attached`, factory never called, `ShellToken` null; `Stop()` leaves `Attached` and calls nothing.
- Ready then process exits → `Failed` with the log hint.
- output lines land in `Tail` (capacity 40 → the 41st pushes the first out).

**3b. GREEN.** As above. **3c. Gate.** Desktop tests green; full suite green. Commit: `Row 12 T3: HubChild starts, probes, attaches and kills the hub`.

### Task 4: the chromeless window, boot page, origin lock, token injection (opus)

Files: `src/ChopItUp.Desktop/MainWindow.xaml`, `src/ChopItUp.Desktop/MainWindow.xaml.cs`, `src/ChopItUp.Desktop/BootPage.cs`, `src/ChopItUp.Desktop/Bridge/IHostActions.cs` (the interface, defined HERE so this task compiles alone; Task 5 only dispatches onto it), `src/ChopItUp.Desktop/Bridge/OwnerTokenScript.cs`, `src/ChopItUp.Desktop/App.xaml.cs` (adds only `public static bool Quitting { get; set; }` and `public static string LaunchNonce { get; } = <16 random hex>`; the rest of App is Task 5), `tests/ChopItUp.Desktop.Tests/BootPageTests.cs`, `tests/ChopItUp.Desktop.Tests/OwnerTokenScriptTests.cs`.

`IHostActions` (Task 5's `HostBridge` consumes it unchanged):
```csharp
public interface IHostActions
{
    bool IsMaximized { get; }
    HubStatus Hub { get; }
    void Minimize();
    void Maximize();
    void Restore();
    void Close();   // hide to tray
    void Quit();
}
```

`MainWindow.xaml` (the Curio.Shell shape, verbatim in spirit):
```xml
<Window x:Class="ChopItUp.Desktop.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:wv2="clr-namespace:Microsoft.Web.WebView2.Wpf;assembly=Microsoft.Web.WebView2.Wpf"
        Title="Chop It Up"
        WindowStyle="None" ResizeMode="CanResize" AllowsTransparency="False"
        Width="1280" Height="800" MinWidth="640" MinHeight="400"
        Background="#0E1013">
    <!-- Row 12: the page draws the chrome. CaptionHeight=0 because WebView2's non-client-region
         support owns dragging (app-region: drag on the page's chrome row). ResizeBorderThickness
         alone does not reach through the WebView2 HWND child (Curio spike (i), measured Δwidth=0),
         hence Web.Margin 6/0 in code-behind. AllowsTransparency must stay False: the WebView2 is an
         HWND child and a layered window breaks its airspace. -->
    <WindowChrome.WindowChrome>
        <WindowChrome CaptionHeight="0" GlassFrameThickness="0" ResizeBorderThickness="6"
                      UseAeroCaptionButtons="False" CornerRadius="0"/>
    </WindowChrome.WindowChrome>
    <Grid>
        <wv2:WebView2 x:Name="Web"/>
    </Grid>
</Window>
```

`MainWindow.xaml.cs` responsibilities (write it fully; the Curio digest's `ChromeHook`, `OnStateChanged`, `OnSourceInitialized` are copied verbatim with `Web` as the child):
- ctor `(ShellArgs args, HubChild hub, ShellLog log)`: `Web.CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = args.WebViewProfileDir }`; `Web.Margin = new Thickness(6)`; `Loaded += OnLoaded`.
- `OnLoaded` (`async void`, so its WHOLE body is one try/catch: any exception → log `WEBVIEW FAILED <type> <msg>`, `System.Windows.MessageBox` naming the profile dir and the log path, `App.Quitting = true`, `System.Windows.Application.Current.Shutdown(4)`; and an `_initialized` latch so a re-fired `Loaded` returns at once): `await Web.EnsureCoreWebView2Async()`; then, BEFORE any navigation: `Settings.IsNonClientRegionSupportEnabled = true` in its own try/catch (log `NONCLIENT unsupported <msg>` on failure and keep going: the page's buttons still work, only drag is lost); `Settings.AreDefaultContextMenusEnabled = false`; `Settings.IsStatusBarEnabled = false`; the token script (`OwnerTokenScript.For(hub.ResolvedOrigin, hub.ShellToken)`) is registered with `await CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(...)` inside the `Ready` branch below, before `Navigate` (Ready implies the shell started the hub, so the token exists and the origin is known); the `Attached` branch registers nothing (B2: the page falls back to its paste flow); `NavigationStarting` → if `new Uri(e.Uri).GetLeftPart(UriPartial.Authority)` ≠ `hub.ResolvedOrigin` authority and the scheme is http/https → `e.Cancel = true; OpenExternal(e.Uri)`; `about:blank` allowed (the boot page is `NavigateToString`, which reports `about:blank`); every other scheme (`data:`, `file:`, …) cancelled and logged; `NavigationCompleted` → log `NAV <IsSuccess> <Source>`; `NewWindowRequested` → `e.Handled = true; OpenExternal(e.Uri)`; `WebMessageReceived` → Task 5's bridge; then `ShowBoot()`; then `OnHubStatus(hub.Status)` once (the hub can be Ready before WebView2 initialises) — App's subscriber (Task 5) calls the same `OnHubStatus` for later changes via `Dispatcher.BeginInvoke`. `OnHubStatus(HubStatus s)` is `async void` with the SAME whole-body try/catch and `WEBVIEW FAILED` log line as `OnLoaded` (pass 2, finding 8: a `COMException` from a crashed WebView2 process here is otherwise a process kill), returns at once if `CoreWebView2` is null (not initialised yet; `OnLoaded` will call it) or if a `_navigated` latch is already set for a terminal state, and does: `Ready` → `await` the token script registration, set `_navigated`, `CoreWebView2.Navigate(hub.ResolvedOrigin.ToString())`; `Attached` → set `_navigated`, `Navigate` (no script); `Failed` → `NavigateToString(BootPage.Failed(s.Reason, hub.Tail.Snapshot(), App.LaunchNonce))` (clears `_navigated`, so a later Ready after a retry would navigate again).
- `ShowBoot()` = `NavigateToString(BootPage.Starting(App.LaunchNonce))`; log `BOOT SHOWN`.
- `OnClosing(CancelEventArgs e)`: `e.Cancel = true; Hide();` unless `App.Quitting` is true (Task 5 sets it). Log `HIDE`.
- `ShowAndActivate()`: `Show(); if (WindowState == Minimized) WindowState = Normal; Activate(); Web.Focus();` (an activated WPF window does not hand keyboard focus to the WebView2 HWND by itself).
- `OpenExternal(string url)`: `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })` in try; log.
- `IHostActions` implementation lives here: Minimize/Maximize/Restore via `SystemCommands`, `Close` = `Hide()`, `Quit` = `App.Quitting = true` then `((App)System.Windows.Application.Current).QuitAsync()` (Task 5 writes that method; until then `Quit` calls `System.Windows.Application.Current.Shutdown(0)` and Task 5 replaces the one line), `IsMaximized`, `Hub => hub.Status`. `Close` and `Quit` go through `Dispatcher.BeginInvoke` so the reply reaches the page first.

`BootPage` (pure, tested): `Starting(string nonce)` and `Failed(string reason, IReadOnlyList<string> tail, string nonce)` return complete HTML strings; every `postMessage` in them carries `nonce:'<nonce>'` (B8): `<meta name="color-scheme" content="dark">`, body `background:#0e1013;color:#e7eaf0;font:14px ui-sans-serif,system-ui,"Segoe UI"`, a 36 px top strip `style="app-region:drag;-webkit-app-region:drag"` with the wordmark `CHOP IT UP` (13px, 650, letter-spacing .04em, `#e7eaf0`) and, right-aligned, two 46×32 buttons `Close` (`×`, hides) and `Quit` styled `app-region:no-drag`, each `onclick="chrome.webview.postMessage({cmd:'close',nonce:'<nonce>'})"` / `{cmd:'quit',nonce:'<nonce>'}`; a centred block: title `Starting the hub…` with a 2-dot pulsing indicator (CSS animation, no JS) for `Starting`, or `The hub did not start` + the reason + a `<pre>` of the tail (HTML-encoded, `#8d95a5`, mono 12px, max-height 50vh, overflow auto) for `Failed`. Tests: both pages contain the drag strip and both buttons; both buttons' messages carry the nonce; `Failed` HTML-encodes `<script>` in a tail line; `Starting` contains no `<pre>`; a nonce containing `'` is rejected by the constructor (`ArgumentException`), since it is interpolated into JS.

`OwnerTokenScript.For(Uri origin, string token)` returns
```js
(function(){try{if(location.origin!=='<originAuthorityWithoutTrailingSlash>')return;Object.defineProperty(window,'__chopitupShellToken',{value:'<token>',writable:false,configurable:false,enumerable:false});}catch(e){}})();
```
with the token JSON-string-escaped (`JsonEncodedText.Encode`) — memory only, per document, re-run by WebView2 on every navigation (B2). Tests: origin literal is `http://127.0.0.1:8795` (no trailing slash, `Uri.GetLeftPart(UriPartial.Authority)`), a token containing `'` and `\` is escaped, the script contains `__chopitupShellToken` and no `localStorage`. Task 7 teaches the client to read the global.

**4a. RED:** `BootPageTests` (7) and `OwnerTokenScriptTests` (3) fail to compile. **4b. GREEN.** **4c. Gate.** Clean build; Desktop tests green; full suite green. This task cannot run the window in tests; the harness in Task 9 does. Commit: `Row 12 T4: chromeless WebView2 window, boot page, origin lock, token injection`.

### Task 5: host bridge, tray, single instance, startup order (sonnet)

Files: `src/ChopItUp.Desktop/Bridge/HostBridge.cs`, `src/ChopItUp.Desktop/TrayIcon.cs`, `src/ChopItUp.Desktop/SingleInstance.cs`, `src/ChopItUp.Desktop/App.xaml.cs`, `tests/ChopItUp.Desktop.Tests/HostBridgeTests.cs`, `tests/ChopItUp.Desktop.Tests/SingleInstanceTests.cs`. `IHostActions` already exists (Task 4).

`HostBridge` (pure, tested): `Handle(string json, IHostActions host) → string`. Wire grammar (Curio's): request `{id?, cmd, nonce?}`; reply `{id, ok:true, result}` or `{id, ok:false, error}`; event `StateEvent(bool maximized, HubStatus hub)` = `{event:"state", maximized, hub:{state:"ready"|"attached"|"starting"|"failed"|"stopped", port, reason}}` (port from `HubStatus.Port`). Commands: `minimize`, `maximize`, `restore`, `toggleMaximize`, `close`, `quit`, `getState` (returns the same object as the state event's payload). `IsTrusted(string? source, Uri hubOrigin, string? messageNonce, string launchNonce)`: a source whose `GetLeftPart(UriPartial.Authority)` equals the hub origin's (ordinal-ignore-case) is trusted with or without a nonce; any other source (including `about:blank`, which is what a `NavigateToString` boot page reports) is trusted ONLY when `messageNonce` equals `launchNonce` (B8, pass 1 finding 12: Curio trusts authorities only; the boot page is our one widening and the nonce bounds it); null source → false. Unknown cmd → `ok:false, error:"unknown command 'x'"`; malformed JSON → `error:"malformed"`.

Tests (`HostBridgeTests`, with a recording fake): each command calls exactly its method; `toggleMaximize` picks by `IsMaximized`; `getState` shape (serialize with `JsonSerializerDefaults.Web`, enum lower-cased by a custom converter or by `.ToString().ToLowerInvariant()`) including `port`; id round-trips; malformed; unknown; trust matrix (`http://127.0.0.1:8795/x` no nonce ✓, `http://127.0.0.1:8796/` with the right nonce ✗, `https://example.com` ✗, `about:blank` with the right nonce ✓, `about:blank` with a wrong or missing nonce ✗, null ✗).

`TrayIcon` (thin, untested beyond construction): before it is created, `App` calls `System.Windows.Forms.Application.EnableVisualStyles()` once (pass 1, finding 16; DPI comes from the manifest's PerMonitorV2, which is process-wide). `System.Windows.Forms.NotifyIcon` with `Icon` from `chopitup.ico` (until Task 6 lands, `System.Drawing.SystemIcons.Application`), `Text = "Chop It Up"`, `ContextMenuStrip` with `Open`, a disabled status item, a separator, `Quit`; `DoubleClick` → Open; `Update(HubStatus s)` sets the status text: `Ready` → `Hub running on :{s.Port}`, `Attached` → `Attached to :{s.Port}`, `Failed` → `Hub failed`, else `Starting…`. `Update` is called ONLY from `App`'s `hub.StatusChanged` subscriber, which hops to the UI thread with `Dispatcher.BeginInvoke` before touching the window or the tray (pass 1, finding 19: `NotifyIcon` and `ToolStripMenuItem` are not thread-safe and `StatusChanged` fires on a pool thread). `Dispose` sets `Visible=false` first (a NotifyIcon disposed while visible leaves a ghost until the mouse passes).

`SingleInstance`: `Key(dataDir)` = `"ChopItUp.Desktop." + Hash16(dataDir)` (Task 2's helper); `TryBecomePrimary(key)` = `new Mutex(true, "Local\\" + key, out createdNew)`; events `"Local\\" + key + ".show"` and `".quit"` (`EventWaitHandle`, `AutoReset`); `Signal(key, ShellCommand)` sets the matching event (returns false when the event does not exist, i.e. no primary); the primary runs a background thread `WaitAny` over both events, marshalling `OnShow`/`OnQuit` callbacks to the dispatcher. Tests: two `TryBecomePrimary` on one key → second false; `Signal` before any primary → false; a primary's `OnShow` fires after `Signal(Show)` (use a `ManualResetEventSlim` with a 5 s timeout); dispose releases the mutex so a later `TryBecomePrimary` succeeds.

`App.xaml.cs` (`System.Windows.Application`), `OnStartup`, in order (Curio's, minus Companion):
1. `ShellArgs.Parse`; on `ArgumentException` → `System.Windows.MessageBox.Show(msg, "Chop It Up")`, `Shutdown(2)`.
2. `Command == Show/Quit` → `SingleInstance.Signal(...)`; `Shutdown(0)` (exit 1 if no primary, message to stderr only).
3. `WebView2 runtime`: `CoreWebView2Environment.GetAvailableBrowserVersionString()` in try; `WebView2RuntimeNotFoundException` → MessageBox naming `https://developer.microsoft.com/microsoft-edge/webview2/`, `Shutdown(3)`.
4. `TryBecomePrimary`; false → `Signal(Show)`, `Shutdown(0)`.
5. `ShellLog` init (`SESSION START args=…` without the token), `HubChild` construct (App holds it for the process lifetime), one `hub.StatusChanged` subscriber that `Dispatcher.BeginInvoke`s to `window.OnHubStatus(s)` and `tray.Update(s)`, `MainWindow` construct + `Show()`, `System.Windows.Forms.Application.EnableVisualStyles()`, `TrayIcon` construct (`Visible = true`), single-instance listener wired (`OnShow → window.ShowAndActivate()`, `OnQuit → QuitAsync()`).
6. `_ = hub.StartOrAttachAsync(_startCts.Token)` — safe to discard because the method never throws (Task 3 guards its whole body; an `OperationCanceledException` from the token is caught there too and logged as `HUB STATE Stopped`); the window reacts to StatusChanged.
`QuitAsync()`: `Quitting = true`; `_startCts.Cancel()` (a `--quit` during Starting must not keep the poll loop logging after `EXIT`); `tray.Dispose()`; `hub.Stop()`; `window.Close()`; `Shutdown(0)`. `OnExit`: `hub.Dispose()` (idempotent), `log("EXIT")`, `singleInstance.Dispose()`. `ShutdownMode = OnExplicitShutdown` in `App.xaml` so hiding the last window does not exit.

**5a. RED** (`HostBridgeTests` ≈ 12, `SingleInstanceTests` 4). **5b. GREEN.** **5c. Gate:** clean build, Desktop tests green, full suite green; ALSO a manual smoke by the builder: `dotnet run --project src/ChopItUp.Desktop -- --data <scratch> --hub src/ChopItUp.Hub/bin/Debug/net10.0/ChopItUp.Hub.exe --port 8797`, confirm `<scratch>\logs\desktop.log` shows `HUB STATE Ready`, then `dotnet run ... -- --data <scratch> --quit` and confirm the hub PID is gone and `desktop.log` ends with `EXIT`. Report the log lines (not the token). Commit: `Row 12 T5: host bridge, tray icon, single instance, startup and quit order`.

### Task 6: the icon (opus)

Files: `tools/New-DesktopIcon.ps1`, `src/ChopItUp.Desktop/chopitup.ico` (committed output), `src/ChopItUp.Desktop/ChopItUp.Desktop.csproj` (add `<ApplicationIcon>chopitup.ico</ApplicationIcon>` and `<Resource Include="chopitup.ico" />`), `src/ChopItUp.Desktop/TrayIcon.cs` (load the resource: `new System.Drawing.Icon(System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/chopitup.ico")).Stream)`), `src/ChopItUp.Desktop/MainWindow.xaml` (`Icon="chopitup.ico"`).

`New-DesktopIcon.ps1` (pwsh, `System.Drawing`): renders 256, 48, 32, 16 px layers: a rounded square `#0e1013` with a 1 px `#242a34` edge, and a bold speech-bubble "C" mark in the owner accent `#6fb2ff` (the client palette, `styles.css`); writes a valid ICO with PNG-encoded entries (ICONDIR header, one ICONDIRENTRY per layer with `bColorCount=0`, `wPlanes=1`, `wBitCount=32`, PNG bytes appended). Idempotent: running it twice yields identical bytes (no timestamps). Verify by loading the result with `new System.Drawing.Icon(path)` in the script and printing its sizes. Commit both the script and the ico. Gate: clean build; the exe's shell icon shows in Explorer (state in the report; the screenshot judge in Task 9 confirms the tray/taskbar glyph). Commit: `Row 12 T6: app icon`.

### Task 7: the hosted chrome row in the React client (opus)

Files: `src/ChopItUp.Hub/client/src/shell/hostBridge.ts`, `src/ChopItUp.Hub/client/src/shell/ChromeBar.tsx`, `src/ChopItUp.Hub/client/src/shell/hostBridge.test.ts`, `src/ChopItUp.Hub/client/src/shell/ChromeBar.test.tsx`, `src/ChopItUp.Hub/client/src/App.tsx` (three lines), `src/ChopItUp.Hub/client/src/ownerToken.ts` (three lines + doc), `src/ChopItUp.Hub/client/src/styles.css` (one new section).

`ownerToken.ts` (B2): `readOwnerToken()` first returns `window.__chopitupShellToken` when `typeof window !== 'undefined'` and that property is a non-empty string, else falls through to today's `localStorage` read; the file's doc comment gains one paragraph: the desktop shell sets that global per document, in memory only, so nothing here ever writes it. `writeOwnerToken` is unchanged. Test (in the existing `api.test.ts` or a new `ownerToken.test.ts`, whichever already tests this module; if none does, create `ownerToken.test.ts`): with a fake `globalThis.window = { __chopitupShellToken: 'launch', localStorage: <throwing stub> }`, `readOwnerToken()` returns `launch`; without the global it returns null; restore `globalThis.window` afterwards.

`hostBridge.ts` (Curio's shape, trimmed):
```ts
interface WebViewLike { postMessage(m: unknown): void; addEventListener(t: 'message', h: (e: { data: unknown }) => void): void }
function webview(): WebViewLike | null {
  if (typeof window === 'undefined') return null;   // vitest runs under node (ownerToken.ts guards the same way); pass 1, finding 3
  const w = (window as unknown as { chrome?: { webview?: WebViewLike } }).chrome?.webview;
  return w && typeof w.postMessage === 'function' ? w : null;
}
export function isHosted(): boolean { return webview() !== null; }
export type HubState = 'starting' | 'ready' | 'attached' | 'failed' | 'stopped';
export interface ShellState { maximized: boolean; hub: { state: HubState; port: number; reason: string | null } }
export function call<T = unknown>(cmd: string, timeoutMs = 10_000): Promise<T | undefined> { … }   // id, pending map, one 'message' listener, replies {id, ok, result|error}
export function onState(handler: (s: ShellState) => void): () => void { … }   // events {event:'state', ...}
export const host = { minimize: () => settled(call('minimize')), toggleMaximize: () => settled(call('toggleMaximize')), close: () => settled(call('close')), getState: () => call<ShellState>('getState') };
```
`settled` swallows rejections (a timed-out bridge call must never surface as an unhandled rejection). `main.tsx` is NOT touched (an earlier draft set a `data-hosted` attribute nothing reads; pass 1, finding 23).

`ChromeBar.tsx`: renders `null` unless `isHosted()` (prop `hosted` for tests, default `isHosted()`); otherwise
```tsx
<div className="chrome" data-testid="chrome">
  <span className="wordmark chrome-wordmark">Chop It Up</span>
  <span className="chrome-hub" data-state={hub.state}>{hubLabel(hub)}</span>   // `hub · 8790`, `hub · attached`, `hub · starting…`, `hub · failed`
  <div className="chrome-cluster">
    <button type="button" className="chrome-btn" aria-label="Minimize" title="Minimize" onClick={() => void host.minimize()}>{MinimizeGlyph}</button>
    <button type="button" className="chrome-btn" aria-label={maximized ? 'Restore' : 'Maximize'} title=… onClick={() => void host.toggleMaximize()}>{maximized ? RestoreGlyph : MaximizeGlyph}</button>
    <button type="button" className="chrome-btn chrome-close" aria-label="Close" title="Close" onClick={() => void host.close()}>{CloseGlyph}</button>
  </div>
</div>
```
Glyphs are inline 10×10 SVGs with `stroke="currentColor"` (a line, a square, two overlapping squares, an ×), Segoe MDL2's shapes redrawn; no icon font. State comes from `onState` + an initial `host.getState()` in a `useEffect` (guarded for hosted only).

`App.tsx`: import `ChromeBar` and `isHosted`; INSIDE the `App` component body (never at module scope: `App.test.tsx` imports this module under node), `const hosted = isHosted();`; the root becomes `<div className={hosted ? 'app hosted' : 'app'}>` with `<ChromeBar hosted={hosted} />` as its first child. Nothing else in App changes.

`styles.css`, new section after `.app`:
```css
/* ---------- desktop shell chrome (row 12) — rendered only inside ChopItUp.Desktop ---------- */
.app.hosted { grid-template-rows: 36px minmax(0, 1fr); }
.chrome { grid-column: 1 / -1; display: flex; align-items: center; gap: 12px; height: 36px; padding-left: 16px; background: var(--rail); border-bottom: 1px solid var(--line-soft); user-select: none; app-region: drag; -webkit-app-region: drag; }
.chrome-wordmark { flex: 0 0 auto; }
.chrome-hub { font-size: 11px; color: var(--dim); font-family: var(--mono); }
.chrome-hub[data-state='failed'] { color: var(--danger-ink); }
.chrome-cluster { margin-left: auto; display: flex; height: 36px; app-region: no-drag; -webkit-app-region: no-drag; }
.chrome-btn { width: 46px; height: 36px; border: 0; padding: 0; background: transparent; color: var(--dim); display: grid; place-items: center; cursor: default; app-region: no-drag; -webkit-app-region: no-drag; }
.chrome-btn:hover { background: rgba(255, 255, 255, 0.10); color: var(--text); }
.chrome-close:hover { background: #c42b1c; color: #fff; }   /* the Windows 11 close-hover red, Curio's precedent */
.rail, .room { min-height: 0; }
@media (prefers-color-scheme: light) { .chrome-btn:hover { background: rgba(0, 0, 0, 0.08); } }
```
`.app.hosted` puts the chrome on row 1 spanning both columns and the rail/room on row 2 (grid auto-placement: `.chrome` is the first child with `grid-column: 1 / -1`, the rail and room fill row 2). Verify with a quick `npm run build` that the two-row grid does not collapse the room's flex column (the `.room` already has `min-height: 0`).

**7a. RED.** `hostBridge.test.ts`: `isHosted()` false in node; with a fake `window.chrome.webview` (define `globalThis.window` minimally as the other client tests do, or skip that case if `window` is unavailable — check how `ownerToken.ts` tests handle `window`), `call('x')` posts `{id, cmd:'x'}` and resolves on a matching reply; a non-matching id is ignored; `onState` invokes on `{event:'state'}`. `ChromeBar.test.tsx` (`renderToStaticMarkup`): `hosted={false}` → empty string; `hosted={true}` with a state prop → three buttons with the aria-labels, wordmark text, `hub · 8790`; `maximized` → `Restore` label. `App.test.tsx` is untouched and must still pass (it imports the module under node and renders `TokenGate`, never `App`, so the only hazard is module-load time).

**7b. GREEN.** **7c. Gate.** `npm --prefix src/ChopItUp.Hub/client test` green (112 + new); `npm run build` clean; `dotnet build` (the client build runs inside it) clean; full suite green. Commit: `Row 12 T7: hosted chrome row and page-to-host bridge in the client`.

### Task 8: deploy both exes; self-check learns the second one (sonnet)

Files: `tools/Deploy-ChopItUp.ps1`, `tools/Invoke-M4SelfCheck.ps1`, `tests/ChopItUp.Hub.Tests/DeployScriptTests.cs`, `CLAUDE.md` (Deploy paragraph: "Release = two single-file exes, `ChopItUp.Hub.exe` and `ChopItUp.Desktop.exe`, in `C:\Self Apps\ChopItUp\` with `wwwroot\` and `data\` beside them").

`Deploy-ChopItUp.ps1`:
- Header comment: step 2 publishes both projects; step 3 checks both exes; step 5 swaps both.
- `$desktopProj = Join-Path $repoRoot 'src\ChopItUp.Desktop\ChopItUp.Desktop.csproj'` next to `$hubProj`; after the hub publish line (ledger 12): `& dotnet publish $desktopProj -c Release -o $source` with the same exit check.
- A new parameter `[hashtable]$ExeFloors = @{ 'ChopItUp.Hub.exe' = 30MB; 'ChopItUp.Desktop.exe' = 100MB }`; `Test-StagingOutput` loops it; each missing/undersized exe returns the message with its own name and floor. The tests pass KB-scale floors (`@{ 'ChopItUp.Hub.exe' = 4KB; 'ChopItUp.Desktop.exe' = 4KB }`) so the fixture stays small (pass 1, finding 8: a real 100 MB fixture times ~11 script runs times three copies is gigabytes of I/O per suite run), and one test asserts the default table's two literals by `Select-String` on the script.
- `Invoke-GuardedCopyIn`: `/XF ChopItUp.Hub.exe ChopItUp.Desktop.exe`; the copy-aside-and-rename loop runs per exe name, hub first, desktop second.
- The `DEPLOY_RESULT` gains `desktop_exe_size_bytes`.
- `Assert-ProcessGuardClear` already matches any process image under the target, so a running shell blocks the deploy as intended; update its comment to say so.
- Restore mode (pass 2, finding 3): every backup made before this row is hub-only, so `-RestoreFrom` checks only the exes PRESENT in the restore source against their floors (a restore source with no `ChopItUp.Desktop.exe` is valid) and, when the source lacks an exe the target has, deletes that exe from the target after the swap (an old hub beside a new shell would 401 the shell's token). The header documents this; `DeployScriptTests` gains: restore from a hub-only backup succeeds and leaves no `ChopItUp.Desktop.exe` in the target.

`Invoke-M4SelfCheck.ps1`: `c1.exe-exists` and `c1.exe-size-floor-30mb` gain desktop twins `c1.desktop-exe-exists`, `c1.desktop-exe-size-floor-100mb`; the exact loose-file set adds `ChopItUp.Desktop.exe`; the check count in the script's header/summary is updated. Read the whole script's check list before editing: any check that launches `ChopItUp.Hub.exe` from staging stays hub-only.

`DeployScriptTests`: the fixture writes `ChopItUp.Desktop.exe` as `KnownBytes(8192, seed: 11)` and every `RunDeploy` passes `-ExeFloors` with the KB-scale floors (the existing 30 MB hub fixture bytes may shrink to match; keep one case at the real default floors that stages a 30 MB hub and expects the *desktop* floor to fail, proving the defaults bind), and two new cases: staging missing the desktop exe fails sanity with the target untouched; both exes are replaced and the `.new` files are gone after a deploy. Every existing case passes unchanged.

Gate: `dotnet test tests/ChopItUp.Hub.Tests --filter "FullyQualifiedName~DeployScriptTests"` green and its wall time within 10 % of the baseline run (measure both; report); a real `dotnet publish` of both projects into a scratch `-StagingDir` followed by `Invoke-M4SelfCheck.ps1 -PublishDir <staging> -TargetDir <scratch target>` all green (report counts); `(Get-Item CLAUDE.md).Length -lt 4096` (Task 2 and this task both append to it; pass 1, finding 15); full suite green. Commit: `Row 12 T8: deploy and self-check both executables`.

### Task 9: verification harness (orchestrator, after the builders)

`tools/Invoke-Row12ShellCheck.ps1` (from `~\.claude\skills\roadmap\references\desk-check-template.ps1`; every leg records an explicit `$passed` boolean, never a string, and the run refuses to start unless the session is interactive: `[Environment]::UserInteractive` and a non-zero `SystemInformation.VirtualScreen`): a fresh scratch data dir per run (`D = $env:TEMP\chopitup_row12_<guid>`, seeded by `tools/ChopItUp.Corpus` with `--schema-version 11 --messages 200` so no migration chain runs inside the readiness budget; pass 2, finding 12), a free port `P` picked per run (`TcpListener` on 0), the Debug hub exe `H`; evidence under `.scratch/m12-desktop-shell/evidence/<yyyyMMdd-HHmmss>/` (gitignored, one dir per run). Legs:
1. `start`: launch `ChopItUp.Desktop.exe --data D --hub H --port P`; wait ≤ 25 s for `D\logs\desktop.log` to contain `HUB STATE Ready`, then ≤ 15 s more for `NAV True http://127.0.0.1:P/` (the SPA has navigated; UIA before this sees an empty tree); assert a child hub PID whose command line holds `--port P`; assert `GET :P/health` ok; record the `BOOT SHOWN` timestamp minus `SESSION START` (reported, not asserted).
2. `chrome`: UIA (`System.Windows.Automation`): find the window named `Chop It Up` of that PID; poll ≤ 10 s for buttons named `Minimize`, `Maximize`, `Close` (WebView2 exposes the page's accessibility tree); assert the shell log has no `NONCLIENT unsupported`; assert the margin by P/Invoke: `GetWindowRect` of the top-level HWND and `GetWindowRect` of the WebView2 child HWND (class `Chrome_WidgetWin_0`, found by `EnumChildWindows`) differ by 6 physical-pixels-times-DPI-scale on every edge while not maximized.
3. `owner-token`: the real path, end to end (pass 2, finding 6): UIA-focus the composer (`document` role / the textbox named per `Composer.tsx`), type `shell check <guid>`, press Enter; within 5 s `GET :P/api/rooms/general/messages` contains that body with `authorId: owner`; and `tokens.json` in `D` is byte-identical to its state right after leg 1.
4. `hide`: UIA Invoke `Close`; assert the window is not visible within 2 s (`IsOffscreen` or no window of that PID), the hub PID is still alive, `/health` still ok.
5. `show`: second launch with `--data D --show`; assert the window is visible again within 5 s (same PID) and its rect equals the rect recorded in leg 2.
6. `maximize`: UIA Invoke `Maximize`; assert the window rect equals the monitor work area (`Screen.FromHandle(...).WorkingArea`, physical pixels) and the child/window rect difference is 0; Invoke `Restore`; assert the rect is 1280×800 scaled by the monitor's DPI (`GetDpiForWindow / 96`), ± 2 px.
7. `spawn-peer`: mandatory, using the repo's stub-CLI spawn tooling (read `tools/Invoke-Row29PeerCheck.ps1` and `Probe-SpawnCli.ps1` for how a stub spawn is held open): with one stub spawn in flight (`LiveCount > 0`, so the row 29 peer check runs), repeat leg 3's typed post and assert 201-equivalent (the message appears with `authorId: owner`); this is the only gate that sees whether `OwnerPeerCheck` resolves a WebView2 network process as Allowed (inferred yes; unit tests cannot see `OpenProcess` on a Chromium utility process). A 403 here is a STOP for the orchestrator, not a harness bug.
8. `quit`: second launch with `--data D --quit`; assert the shell exits 0 within 10 s, the hub PID is gone, `D\hub.lock` opens with `FileShare.None`; notification-area listing logged, not asserted.
9. `attach`: start the Debug hub by hand on a second free port `P2` with `--data D`; launch the shell with `--port P` (deliberately mismatched); assert `HUB STATE Attached` with `:P2` in the log, `NAV True http://127.0.0.1:P2/`, no child; the chrome chip reads `hub · attached` (UIA text); `--quit`; assert the hand-started hub still answers; stop it by PID.
Screenshot judge: one capture of the shell window (`Graphics.CopyFromScreen` of its rect) taken after leg 2, run through `~\.claude\skills\roadmap\helpers\Test-CaptureSane.ps1` (a uniform or wallpaper-sized capture fails here, never reaches a judge; memory `reference_headless_screenshot_grab`), then judged by a pinned `sonnet` subagent for: dark chrome row with the wordmark left, three glyph buttons right, room UI below, no native title bar. Counts reported, content never quoted.

## Verification (orchestrator, Phase B)
1. `Check-PlanClaims.ps1` exit 0 before the first builder.
2. Per commit: fixed lenses (persisted-format compat: `tokens.json` byte-identical; ordering: token scrub before any spawn; concurrency: `HubChild` status marshalling, output drain threads; cross-file invariants: the `CHOPITUP_SHELL_TOKEN` literal in two projects, the `chopitup.ownerToken` key in two places, the two exe names in three scripts; resource lifetimes: job handle, NotifyIcon, mutex, WebView2 profile dir).
3. Full suite + client tests; `Invoke-Row12ShellCheck.ps1` all legs; screenshot judge PASS.
4. `mattpocock-skills:code-review` on the branch (Standards + Spec, no agents).
5. Deploy: the live hub (running today from the install dir, and the endpoint this session's own `chopitup` MCP tools use) goes away for the swap and the MCP hosts lose it until the shell is up; say so in the ping. Stop it by PID (image path under the install dir), `tools\Deploy-ChopItUp.ps1`, `Invoke-M4SelfCheck.ps1`, then start `ChopItUp.Desktop.exe` from the install dir and LEAVE IT RUNNING in the tray with the hub on 8790 and `/health` schema unchanged (11): "as the owner had it" means hub up (pass 2, finding 2). One approval prompt per write under `C:\Self Apps\`.
6. Board flip (the row's Notes drop "(WinForms)" and name B1), plan + tickets deleted, lessons entry only if a new trap surfaced, gate exit 0.

## Critique dispositions
Pass 1 (Opus, 2026-09-15, score 6.8, FIX-THEN-SHIP). Every finding and what happened to it:
1. Attach-mode port unreachable — **fixed**: `HubStatus.Port`, `HubChild.ResolvedOrigin`, every consumer reads it; AC5 and harness leg 9 attach with a mismatched `--port`.
2. Task 4 cannot compile (`IHostActions`, `App.Quitting` defined in Task 5) — **fixed**: both moved into Task 4.
3. Module-scope `window` breaks the client suite under node — **fixed**: guard in `webview()`, `hosted` computed inside the component, AC3 says so.
4. AC6 loose-file set contradicted HEAD — **fixed**: AC6 lists the real five entries; ledger 19 pins the literal.
5. Stale token survives into attach mode; "never reaches the disk" was false — **fixed twice**: pass 1's fold (`localStorage` write plus a `Remove` in attach mode) was superseded by pass 2's finding 7: the token now never touches storage at all, so no stale copy can exist.
6. Unguarded `async void OnLoaded` — **fixed**: whole-body guard, MessageBox naming the profile dir, exit 4, `_initialized` latch; profile moved out of `data\` (B11) which also removes the two-shells-one-profile collision.
7. `StartOrAttachAsync` could throw outside the loop — **fixed**: outer try/catch maps to Failed; throwing-factory test.
8. 100 MB fixture I/O — **fixed**: `-ExeFloors` parameter, KB-scale floors in tests, one default-floor case.
9. Publish shape validated late — **fixed**: Task 2's gate publishes and asserts one file ≥ 100 MB; ledger 17 explains why no HEAD recheck exists.
10. Harness step 2 unfinished, 1→2 race, DPI units — **fixed**: legs rewritten (NAV wait, child-HWND rect math, DPI scaling, per-run dir and port, explicit booleans).
11. Screenshot judge unguarded — **fixed**: interactive-session refusal + `Test-CaptureSane.ps1` before any judge.
12. `about:blank` trust unbounded — **fixed**: per-launch nonce on boot-page messages; `data:` navigations cancelled.
13. csproj block self-contradicting — **fixed**.
14. "within 1 s" unmeasured — **fixed**: dropped; `BOOT SHOWN` is logged and reported.
15. CLAUDE.md cap contended — **fixed**: Task 8 gate asserts < 4096 bytes.
16. `EnableVisualStyles` — **fixed**: called before the tray is built.
17. Test parallelization — **fixed**: `AssemblyInfo.cs` in Task 2, ledger 20.
18. Unbounded logs / handle inheritance — **fixed** (5 MB rotation for both logs); handle inheritance **declined**: the shell never waits for EOF and the kill-on-close job takes the tree down; rationale in Task 3.
19. `TrayIcon.Update` thread hop unnamed — **fixed**: one `App` subscriber, `Dispatcher.BeginInvoke`, named in Tasks 3 and 5.
20. Profile under `data\` — **fixed**: B11, `%LOCALAPPDATA%\ChopItUp\webview2\<hash16>`.
21. Job handle owner unnamed — **fixed**: `HubChild` owns the factory, `App` owns `HubChild`, in Task 3's code comment.
22. Board row still says WinForms — **fixed** at the Phase-A flip (this session) and restated in Verification 6.
23. Dead `data-hosted` attribute — **fixed**: `main.tsx` untouched.
24. Ledger 15 falsified by Task 2 — **fixed**: the row says so.
Size (66.6 KB WARN): **declined** to split, per the critic: the HIGH exemplar that shipped clean is 88 KB and the ticket `Detail:` pointers bind to these sections.

Pass 2 (Fable, 2026-09-15, score 6.7, FIX-THEN-SHIP):
1. Early hub output lost before the tail subscribes; job-assign on an exited process reported instead of the exit — **fixed**: `IHubProcess.BeginReading()` after subscribe, `SpawnJobs.Track`'s exited-guard, two tests.
2. Verification 5 would leave the live hub down — **fixed**: the shell is left running in the tray; the MCP outage during the swap is named in the ping.
3. Restore refuses hub-only backups — **fixed**: restore checks only present exes and removes an orphan desktop exe; test.
4. Ticket 06 not parallel-safe — **fixed**: blocked by 05.
5. Stale `hub.port` during a hand-started hub's first second — **fixed**: hub deletes the file right after locking (Task 1), shell polls the attach branch within the budget; tests.
6. No end-to-end gate for the credential path, none with a live spawn — **fixed**: harness leg 3 types a real post, leg 7 repeats it with a stub spawn in flight and is mandatory; AC2 says so.
7. Token on disk in LevelDB — **Class B decision: adopted the in-memory alternative**. `window.__chopitupShellToken` per document, `readOwnerToken()` consults it first; no `localStorage` write, no `Remove` step; B2 rewritten. Reverting = re-instate pass 1's #5 fold.
8. Second unguarded `async void` created by the pass 1 fold — **fixed**: `OnHubStatus` guarded, `_navigated` latch, null-`CoreWebView2` early return.
9. B4's "nothing needs a graceful stop" unmeasured — **fixed**: B4 cites `AbortStaleExchangeMergeAsync` as the recovery; Quit does not wait.
10. Test counts and the HubOptions test location — **fixed**.
11. `HubChildTests` fixture unstated — **fixed**: temp dir with a dummy hub exe, lock holder for attach cases.
12. Corpus seed migration inside the budget — **fixed**: `--schema-version 11 --messages 200`.
13. Exit between a true probe and Ready — **fixed**: post-Ready `HasExited` recheck; the exit handler fails any non-terminal state.
14. WFAC010 under `-warnaserror` — **unverified, fallback named** in Task 2.
15. NITs — **fixed**: ticket 05 matrix, `<Using Remove>` for the WinForms implicit usings, `Web.Focus()` on show, `App.test.tsx` statement, `--quit` during Starting cancels the poll. Backup-aside growth (~250 MB per deploy, never pruned) — **declined**: pruning is a separate row; the count is reported.
