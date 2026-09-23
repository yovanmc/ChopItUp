# Lessons

Pull-based. Entries are `### [keywords] <the trap>` plus one short paragraph about the current code. Grep headings before planning work on a surface.

### [schema, migrations, check-scripts, tests] A schema bump breaks the scripts that verify it
Version literals live outside `ChopDb.LatestSchemaVersion`: the dry runs and live checks under `tools/` assert stamped versions (for example `Invoke-M25DryRun.ps1` checks the migrated stamp), and tests pin the roster size. Before a bump deploys, grep `tools/` and `tests/` for the old integer and the old roster count. A migration test asserts the columns its version adds by name. A bare `pragma_table_info` count breaks on every later migration.

### [sqlite, wal, testing, backup] Disposing a WAL writer checkpoints away the state under test
A WAL-mode connection checkpoints and deletes its `-wal` file on a plain `Dispose()`. A test or tool that means to prove something about writes still sitting in the WAL (a backup captured them, a torn shutdown survives) loses that state when its writer closes normally, and the assertion then passes against a checkpointed file. `tools/ChopItUp.Corpus` holds its writer open and leaves through `Environment.Exit` for this reason. Prove the on-disk state is present (the `-wal` file exists, or rows the main file cannot hold yet) before asserting on it.

### [process, async-io, tests, harness] A timed WaitForExit returns before output is drained
`Process.WaitForExit(ms)` returns when the child exits without waiting for `BeginOutputReadLine` handlers to deliver buffered lines, so a final sentinel line can still be in flight. It only shows under load, so it passes locally and fails on a busy CI runner. Any code that reads a child's output asynchronously calls the parameterless `WaitForExit()` after a timed one succeeds and before reading the buffers, as `ProcessRunner` does.

### [xunit, assertions, control-chars] String DoesNotContain ignores control characters
xUnit's `Assert.DoesNotContain(string, string)` is culture-sensitive and treats U+001B as ignorable, so an assertion that output carries no escape character passes whatever the output holds. Pass `StringComparison.Ordinal` on any assertion about control characters.

### [powershell, check-scripts, live-check] Check scripts: array wrapper, argument quoting, model wording
PowerShell 7 `Invoke-RestMethod` returns a top-level JSON array as one nested `Object[]`, so `Where-Object prop -eq x` matches nothing. Pipe the response through `ForEach-Object { $_ }` before filtering. `Start-Process -ArgumentList` joins its items with spaces and quotes nothing, so a path under `C:\Agent Projects\` splits into two arguments. Quote every path inside its argument string, even when the default path has no space. A live check asserts only what the hub controls (its note text, exchange status, exit codes), never the wording a model chose. One failed tool leg in a live check is re-run once before it counts as a defect.

### [claude-code, mcp, timeouts, run_gate, progress] A silent MCP tool call is cut at 300 s
A hub-spawned Claude CLI cuts a silent MCP tool call at 300 s whatever `MCP_TOOL_TIMEOUT`, the per-server `timeout` or `CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT` say. The cut sits in the runtime's HTTP client and only bytes on the wire reset it. `run_gate` reports an MCP progress notification every 30 s for this reason. Any new server-side call that can run long needs the same progress cadence, not a bigger timeout knob.

### [spawns, sandbox, codex, claude-code, credentials] No spawn CLI can fence reads
Neither spawn CLI confines what a spawn can read. Claude's `Read()` deny rules bind the tool, not the shell, so `Bash` bypasses them. Codex on Windows confines writes only: `--sandbox read-only` (used by panel spawns in `PanelExecution`) and `sandbox_permissions=[]` still read files outside the workspace, and `--sandbox-state-readable-root` is unreachable from `codex exec`. A design that protects a credential file cannot rely on hiding it from a spawn. It has to limit what the credential can do. Probe a sandbox claim with `codex sandbox -- cmd /c type <path>`, which costs no model call.

### [regex, mentions, dotnet, v8, parity] .NET and V8 regex twins diverge outside the fixture alphabet
The mention reader in `Mentions.cs` and its client twin agree only on the characters `tests/mention-cases.json` covers. `\w` is Unicode in .NET and ASCII in JavaScript. .NET matches UTF-16 code units, so an astral letter after `@opus` is a surrogate (`Cs`) that passes a `\p{L}` lookahead in .NET and fails it in V8 under `u`, which means the .NET class has to spell `\uD800-\uDBFF` out. A twin that keeps `\w` on either side is not a twin. A shape a prompt shows, such as `phase: <kind>`, is a literal that tests assert on, so a wording change sweeps those tests along with the golden file.
