# Lessons

Pull-based. Entries are `### [keywords] M<row> (<date>, <hash>)` + one paragraph, written only when a shipped milestone changes a future decision on the same surface. Grep headings before planning.

### [schema-literals, check-scripts, live-check, verification] M11 (2026-09-07, 4607779)
A schema bump breaks the gates that verify it. Nine v6 literals were spread across the suite and `tools\Invoke-M*.ps1`, and four sat inside the very checks row 11's own verification section runs, so the dry run and three live checks would each assert the old version until swept — `owner-remote` also moved `tokenKeys` 13 to 14. Row 9's plan carried that sweep as a ledger claim, row 11's first draft dropped it, and only critique pass 2 caught it: bumping `LatestSchemaVersion` means grepping `tools/` and `tests/` for the old integer AND the roster size before anything deploys. The same row taught the converse about live checks: `Invoke-M11SkillCheck.ps1` failed twice on its first run while the product was correct, because it asserted on the *model's* reply — one specific arrow codepoint that Opus did not use, and a reply landing before a timeout on an exchange another leg had already stopped. A live check may assert only what the hub itself controls: its own note text, exchange status, exit codes. Anything the model chose how to word is not a gate, and a check that cries wolf on a working product is worse than no check.

### [sqlite, schema, migrations] M1 (2026-09-04, a80ba0c)
A create-only schema still needs its `PRAGMA user_version` stamp inside the same transaction as the DDL and the seeds. Critique pass 1 declined a migration guard here on the reasoning that v1 has nothing to migrate; pass 2 reproduced what that reasoning misses — a first start interrupted between the DDL commit and the stamp leaves the tables present at version 0, so the next start's `if (version < 1)` re-runs the creates against existing tables and bricks the data directory permanently. "Nothing to migrate yet" is never a reason to skip transactional versioning. Make the DDL `IF NOT EXISTS`, the seeds `OR IGNORE`, and the stamp the last statement inside the transaction, so a torn start repairs itself instead of crashing.

### [sqlite, wal, testing, migrations] M2 (2026-09-04, 5e84675)
A WAL-mode SQLite connection checkpoints and deletes its `-wal` file on a plain `Dispose()`. Any test or tool that means to prove something about writes *still sitting in the WAL* — that a backup captured them, that a torn shutdown is survivable — silently loses the state it was built to exercise the moment its writer connection closes normally, and the assertion then passes against a fully-checkpointed database while claiming to have tested the opposite. The M2 corpus builder had to hold its writer open and leave the process via `Environment.Exit` to reproduce the shape a killed process leaves. The general rule: when a test's premise is an on-disk state that only exists between operations, prove the state is actually present (check for the `-wal` file, count rows the main file cannot yet have) before asserting anything about it, or the test is green for the wrong reason.

### [msbuild, node, csproj] M3 (2026-09-04, a4bc007)
MSBuild evaluates a `Target`'s `Condition` *before* running its `DependsOnTargets`, so a target gated on a property that an earlier dependency was supposed to set is skipped silently — dependencies and all — with no mention in even a `-v diag` log. Wiring the web client's npm build as `<Target Name="ClientBuild" Condition="'$(HasNode)'=='true'" DependsOnTargets="ProbeNode">` therefore never ran and never explained itself. Put every such gate on the *task*, not the target, and leave a comment saying why so nobody tidies it back. The same shape will bite any future conditional build step that has to probe for a tool first.

### [process, async-io, ci-flake] M4 (2026-09-04, deb52c4)
A timed `Process.WaitForExit(ms)` returns the instant the child exits; it does not wait for `BeginOutputReadLine`/`BeginErrorReadLine` handlers to finish delivering already-buffered data, so the last line the child wrote — often the one line a test parses, like a `RESULT:` sentinel — can still be in flight when the read completes. Only the parameterless `WaitForExit()` guarantees the async streams are drained. This only surfaces under load (a busy CI runner), so a harness with headroom passes locally every time and fails green-locally on shared build hosts. Any harness that spawns a child, reads its output asynchronously, and parses a sentinel line must call the parameterless `WaitForExit()` after a timed one succeeds, before touching the captured buffers.

### [windows, spawn, cli-shims] M5 (2026-09-05, ebd8c20)
Node-ecosystem and Rust-packaged CLIs install on Windows as `.cmd` shims with no `.exe` beside them, and a host that spawns them by bare name with a direct process create finds nothing to execute. This has now cost this project twice: `npx` in the generated Claude Desktop config (fixed in `6bcb0a4`), and `codex`, which exists only as `~\.localin\codex.cmd`. Both fail silently - no error surfaces, the server simply never appears - because the failure is in the parent's `CreateProcess`, before the child can report anything. Any code here that launches an external CLI resolves it through one helper that checks for a real `.exe` and otherwise goes through a shell; two call sites getting this right independently is how it happened the second time.

### [codex, headless, mcp, approvals] M5 (2026-09-05, ebd8c20)
`codex exec` runs with `approval: never`, and under that policy an MCP tool call is **denied outright** - `MCP tool call requires approval, but approval policy is never`. The server attaches (`mcp: <name>/<tool> started`) and the call then fails, so a headless Codex reaches the hub and cannot post. `--approve-for-me` is the only route that works; `-c approval_policy='on-request'` is accepted and **silently ignored**, with the run still reporting `approval: never`. `--approve-for-me` is also mutually exclusive with `--sandbox` (hard error), so a Codex that can post necessarily runs workspace-write rather than read-only - point `-C` at a dedicated empty directory and treat that as the containment, not the sandbox flag.

### [claude-code, headless, tool-surface] M5 (2026-09-05, ebd8c20)
`claude -p --tools ""` strips every built-in tool and leaves MCP tools untouched, which is what makes a single-purpose spawned agent possible (verified: with `--tools ""` the only tool present was the MCP one; with `--tools Read` it was `Read` plus the MCP one, proving the flag bound rather than being dropped as an empty argument). The reason to use it is not only least-privilege: with the full built-in set loaded, MCP tools were **deferred behind `ToolSearch`** and not directly callable, so an agent would have to spend a turn searching for the tool it was spawned to use. Subscription auth survives a spawn from a detached, console-less parent in the same user session; a session-0 Windows service is untested and is a different question.

### [browser-pane, launch-json, verification] M8 (2026-09-05, c92d578)
The Browser pane's `preview_start` resolves `.claude/launch.json` against the directory the session was opened in, not the repo a plan names, so a plan that tells a builder to create `<repo>\.claude\launch.json` yields a file the tool never reads; the M8 builder found this mid-task and added its entry to the session cwd's launch.json instead. A plan whose browser check needs a dev server must name the launch.json under the session's working directory (the orchestrator knows it when the plan is written) and pin a port the deployed install does not hold (8790 is the live hub's).

### [ci, path, seams, tests] M5 (2026-09-05, 0f32ae7)
Every spawn-expecting Hub test passed on the workstation and all 11 failed on the CI runner: `SpawnerService` resolved `claude`/`codex` from PATH inside the launch, the runner has neither, and the resolver's exception became a "could not be started" note before the fake process runner was ever reached. Anything the hub looks up from the machine (PATH tools, installed programs, user profile files) goes behind a DI seam with a fake default in `HubTestHost`, and a plan whose tests boot the real service names that seam in the task table. Two smaller traps from the same day: a relative `--data` handed children relative config paths (root every path at option parsing), and xunit's culture-sensitive `Assert.DoesNotContain(string, string)` treats U+001B as ignorable, so assertions on control characters need `StringComparison.Ordinal`.

### [browser-pane, input-events, headless-capture, verification] M16 (2026-09-05, m16-live-exchange-ui)
The Browser pane drops typed text and clicks while the pane is hidden (`document.visibilityState === 'hidden'`, which it stays even after `tabs_select`): the tool reports the click dispatched, the app never sees it, and `find` keeps returning a stale accessibility ref. The gate for row 16 worked by posting the trigger over the API with curl, checking state with `find`/`read_page` plus a `javascript_tool` DOM query, and firing the button through `element.click()` — which still runs the React handler and the real POST, so the server log is the proof. Headless Edge (`--headless=new --screenshot`) captures an SPA before React paints unless `--virtual-time-budget=8000` is passed (both captures came back near-uniform and `Test-CaptureSane.ps1` caught it). A UI gate against a live spawn must also expect the model to finish faster than the click: a one-line reply concluded in ~10 s, so the open-state check needs a long task (120 numbered lines held Sonnet ~30 s+).

### [powershell, invoke-restmethod, check-scripts, live-check] M10 (2026-09-06, e838645)
PowerShell 7.6 `Invoke-RestMethod` hands a top-level JSON array back as ONE nested `Object[]`: `@(Invoke-RestMethod …) | Where-Object prop -eq x` reads the property off the wrapper and matches nothing, while `Where-Object { $_.prop -eq x }` matches through member enumeration. Every check script pipes an array response through `ForEach-Object { $_ }` before filtering. The same live check's first run showed Sonnet narrating a tool call as markdown ("**Tool Call: post_message** … Status: Completed") and exiting 0 without calling it; a two-form probe proved the comma-joined `--allowedTools` value binds all three tools with zero permission denials, so a failed tool leg in a live check is one re-run first and a defect only on repeat.

### [signalr, cross-room-ui, verification] M9 (2026-09-06, cf237e8)
The M9 rail shows every room's unread count and orders rooms by recency, but `MessagePosted` is broadcast with `Clients.Group(roomId)` and the client joined only the room it had open, so a message in any other room reached the browser only on the next full refresh: the badge never rose, the count went stale and the order froze. Unit tests and the type-checker were both silent because the bug is in which group the client subscribes to, not in the handler, whose non-open branch was simply unreachable. A UI that renders state for rooms it is not showing must subscribe to all of them, and a plan that adds cross-room state to a per-room fan-out has to say so in the task that touches the client. Only the live interactive gate caught it, which is the argument for driving the real hub rather than trusting a green suite.

### [2026-09-07] Start-Process joins -ArgumentList with spaces and quotes nothing

`Invoke-M19RunCheck.ps1`'s first real run died in setup with `'Agent' is not a valid skill name`: its
default `-SkillSource` is this repo's own `tools\skills\toy-run`, the repo lives under `C:\Agent
Projects\`, and `Start-Process -ArgumentList @('--import-skill', $SkillSource)` handed the exe
`--import-skill C:\Agent` plus a stray positional. Four sibling check scripts carry the identical
shape and had never tripped it only because their default paths sit under `%USERPROFILE%`. So: any
path passed through `-ArgumentList` is quoted inside the argument string, always, not only when the
current default happens to contain a space — quoting a space-free path is a no-op, and the next
caller who passes a spaced path is not the person who should discover this.

### [claude-code, mcp, timeouts, run_gate, progress, live-check] M20 (2026-09-08, 2f787c8)

A silent MCP tool call from a hub-spawned Claude CLI (2.1.220, Bun-compiled) is cut at 300 s even with `MCP_TOOL_TIMEOUT`, the per-server `timeout` field and `CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT` all at 25 min: the CLI's own idle timer is `min(max(idle env, per-server timeout, 1 s), hard timeout)` polled every 30 s (so a 5 s idle knob cuts at the first tick, about 31 s), and the 300-s cut sits below it in the runtime's HTTP client, where only bytes on the wire reset it. `run_gate` therefore reports an MCP progress notification every 30 s while a script runs; the probe's rescue leg (a 400-s gate surviving, run ended by ping) is the proof, and a leg-4 FAIL with legs 1-3 passing means the progress is not reaching the client. Any future long server-side call over this transport needs the same cadence, not a bigger knob.

### [roadmap, board-gate, rooms] M21 (2026-09-08, room run)

The board gate resolves the `Plan` cell as a literal path, so a cell written the markdown way —
`` `.scratch/m21-…/brief.md` `` in backticks — FAILs with "Plan path does not resolve" while the bare
path passes. Every other cell on the board is prose where backticks are house style; this one is not.

### [build-gate, warnaserror, incremental-build] M18 (2026-09-08, ddfb053)

`dotnet build ChopItUp.slnx -c Debug -warnaserror -v minimal` reports "0 Warning(s), 0 Error(s)"
without recompiling a project whose inputs it thinks are unchanged, so a real error can sit in a file
that no green build ever saw. Two consecutive builders reported the gate clean while
`MemoryTools.cs:94` held two CS8604s (null-checking a non-nullable parameter narrows its flow state to
maybe-null for the rest of the method, which the later `Create(…)` call then trips); CI would have
caught it, the local gate did not. The build gate is `dotnet clean` first, then the `-warnaserror`
build — an incremental 0-warning build is not evidence. When the deploy target's exe is locked by a
running hub, `--artifacts-path <fresh dir>` gives the same cold compile without touching it.

### [ui-gate, browser-pane, click-verification] M23 (2026-09-09, 034167a)

The interactive gate for a web card could not use a real click: the Browser pane was hidden, and
injected input into a hidden pane silently dispatches nothing — listeners on the button, the document
and `pointerdown`/`mousedown`/`click` recorded zero events across three attempts, with no error from
the tool, which reported the click as delivered at the right coordinate. Claude in Chrome was not
connected either. Row 18 hit the same wall and shipped "handler proven programmatically", which
leaves the hit-testing half unverified. The substitute that does cover it: assert
`document.elementFromPoint(centre)` returns the button itself (that is the overlay and mis-position
check, and it is better evidence than a screenshot), assert the button's box is non-zero and inside
`innerHeight`, then dispatch `.click()` on the real rendered DOM and verify the server-side effect.
Set a viewport first — a hidden pane reports `innerHeight` 0, which silently collapsed a
`max-height: min(30vh, 220px)` to `0px` and made the first layout measurement meaningless.

### [subagents, critique-gate, dispatch, background-tasks] M23 (2026-09-08, 98e88a3)

A `dissect-critic` dispatched with `run_in_background: true` spent its entire budget — 239k tokens,
28 tool uses — and returned no verdict, ending its turn on "waiting on the completion notification".
A subagent never receives task notifications, so any turn it ends in order to wait is a turn it never
resumes; the parent sees a completed agent whose result is a status line. The documented recovery
(continue it with `SendMessage`) did not exist either: `SendMessage` is disabled in some sessions,
including that one, and the failure only surfaces at the call. Re-running the identical critique with
`run_in_background: false` and a prompt that forbids the Agent tool, forbids backgrounding or polling
anything, and states that the final message MUST be the verdict, returned a full verdict on the first
try — and the stalled agent later returned one too, so the two passes corroborated rather than
duplicated. So: a judge, critic or any other single-shot subagent whose output the current turn is
blocked on is dispatched SYNCHRONOUSLY, with the anti-wait clause written into the prompt, and with
an instruction to label unobtainable evidence "unverified" and move on rather than chase it — an
unverified finding is useful, a missing verdict is worthless. Background dispatch stays correct for
work whose result the turn does not need, such as the parallel code-comprehension digests that fed
this same plan.
