# Chop It Up

A local chat room where you, Claude and GPT talk in one thread — each model joining through MCP on its own subscription. No API keys. No browser automation. Everything stays on your machine.

Status: pre-alpha, see `ROADMAP.md`.

## Run it (dev)

    dotnet run --project src/ChopItUp.Hub -- --data .data --port 8790

Tokens for each participant are generated into `.data/tokens.json` on first start and are read once at startup (restart the hub after editing the file). MCP endpoint: `http://127.0.0.1:8790/mcp` (bearer token required). One hub per data directory: a second instance on the same `--data` refuses to start (`hub.lock`). Host wiring lands in M2.

## Connecting a host

Start the hub once (it mints the tokens), then:

    dotnet run --project src/ChopItUp.Hub -- --data .data --print-config

That writes `claude-desktop.json`, `codex-config.toml` and a `README.md` into `.data/host-configs/`, each carrying that host's real token and the port the hub actually bound. Merge `claude-desktop.json` into `%APPDATA%\Claude\claude_desktop_config.json` and append `codex-config.toml` to `%USERPROFILE%\.codex\config.toml`, then restart that host. The hub never writes into those files itself — it emits the snippet, you paste it. The command prints the folder path and never a token.

Claude Code is deliberately not configured: it would have to join as the same `claude` participant Claude Desktop uses, and two hosts on one identity share one read cursor.

To revoke a token: `dotnet run --project src/ChopItUp.Hub -- --data .data --rotate-token claude` (with the hub stopped). The old token stops working at the next hub start; re-run `--print-config` and re-paste that host's file.

Recovery: if a data directory is ever in a bad state, stop the hub and delete `chopitup.db*`, `tokens.json` and `hub.lock`. Consequence: room history is gone and every host must be given its new token.

## Release

    dotnet publish src\ChopItUp.Hub\ChopItUp.Hub.csproj -c Release -o <dir>

Never add `--no-restore` to that command. `RuntimeIdentifier` is set in the `Release`
`PropertyGroup`, so the RID-specific assets it needs are not in the `Debug` restore the repo
normally runs; `--no-restore` turns that into a confusing mid-publish asset error rather than a
restore.

The output folder holds `ChopItUp.Hub.exe` — self-contained and single-file, so no .NET runtime
needs to be installed to run it — plus a `wwwroot\` folder beside it (a single-file bundle can't
serve static files from inside itself, so the web client ships alongside the exe instead) and a
`data\` folder that the exe creates on first run. That is the whole release folder: exe, `wwwroot\`,
`data\`.

### Deploying

    pwsh tools\Deploy-ChopItUp.ps1 -TargetDir "C:\Self Apps\ChopItUp"

`tools\Deploy-ChopItUp.ps1` publishes into a staging directory, sanity-checks the result (the exe
is present and at least 30 MB, `wwwroot\index.html` and a non-empty `wwwroot\assets\` exist),
copies the previous install aside as a sibling backup directory, then copies the new one in — it
replaces `wwwroot\` wholesale (after the backup, so the old copy still exists there) and copies
everything else additively, never touching `data\` or `logs\` — and replaces the exe last via a
copy-aside-and-rename so a deploy killed mid-copy never leaves a
half-written executable under the name you launch. It refuses to run at all, before touching
anything, if any running process's image path is inside the target directory.

Parameters:

- `-TargetDir` — where to deploy. Defaults to `C:\Self Apps\ChopItUp`.
- `-StagingDir` — where to publish. Defaults to a fresh temp directory; the script always prints
  the path it used.
- `-SkipPublish` — reuse an existing `-StagingDir` instead of publishing again.
- `-RestoreFrom <backupDir>` — roll back to a previous install from one of the backup directories
  this script wrote, under the same guards (process check, `data\` exclusion, atomic exe rename).

Restoring applies the same copy as a deploy: it replaces `wwwroot\` wholesale with the backup's copy
(so an asset the newer install added and the backup doesn't have is removed) and copies everything
else back over the target additively, never touching `data\` or `logs\`. A restore also backs the
current install aside first, so a rollback is itself undoable.

To check a deploy landed, `pwsh tools\Invoke-M4SelfCheck.ps1 -PublishDir <staging> -TargetDir <target>`.
It runs the published exe against a scratch copy (health, the UI, an MCP round trip, a restart) and
then compares the real target's files to staging by hash. It reads nothing inside `data\`.

The script never deletes a backup directory — only reports how many now sit beside the target.
Prune old ones (`C:\Self Apps\ChopItUp.backup-YYYYMMDD-HHmmss`) by hand once you're confident you
won't need to roll back to them.

## Spawning (M5)

A spawn starts when an owner message carries `@id` for a roster row that has a model set. A spawned
model can hand the turn on the same way, mentioning another spawnable id while turns remain.

Caps, all hard-coded: 4 turns per exchange, a 2 second debounce on repeated mentions, at least 10
seconds between two spawns of the same participant across rooms, a 5 minute wall clock per spawn,
and never two spawns of one participant in flight in the same room at once.

The hub posts its own notes as `hub` (kind `system`, badge `HU`): a spawn that times out, one that
exits without posting, a turn skipped for lack of budget, and the exchange's conclusion all land in
the room, because the room is the only durable trail this milestone keeps.

Exchange state lives in memory only. A hub restart mid-exchange drops the budget and any pending or
in-flight spawns, the owner's next message starts fresh, and a spawn that outlives the restart still
posts harmlessly when it finishes.

There's no stop button yet (that's row 16). Until then, stop an open exchange with:

    Invoke-RestMethod -Method Post http://127.0.0.1:8790/api/rooms/general/exchange/stop

A `claude` spawn runs `claude.exe -p` with the prompt on stdin and its token in a per-spawn
`mcp.json`, never `--bare`, which switches auth to an API key. A `codex` spawn runs `codex.cmd exec`
(a PATH shim, not an `.exe`) with the prompt on stdin and its token in `CHOPITUP_TOKEN`.

Rollback: the previous exe refuses a v4 database. To roll M5 back, restore the `.v3.` backup per the
host-configs README, then run the previous exe.

Checks: `pwsh tools\Invoke-M5SpawnCheck.ps1` drives one real exchange against a scratch hub with both
CLIs. `pwsh tools\Probe-SpawnCli.ps1` re-measures the two command lines on their own.

## Memory (M10)

One memory for every model, on disk under `data\memory\`: `MEMORY.md` is the core and goes into
every spawn's prompt (its first 6,000 characters); `topics\<slug>.md` hold the rest and are fetched
with the `recall(topic)` tool, which is also how Claude Desktop or the Codex app read memory at all.
Edit the files by hand whenever you like.

Models never write memory. A spawn (or any host) calls `propose_memory(room_id, topic, title, body)`;
the hub stores a pending proposal, announces it in the room as `Memory proposal #N …`, and the memory
panel above the composer shows Approve and Reject. Approve appends the entry to the topic file
(`core` appends to `MEMORY.md`) and commits it in the git repository the hub keeps inside
`data\memory\` (created on the first approval; one commit per approval, identity `ChopItUp hub`).
Reject drops it. Both post a hub note.

"Import memory" in the room header seeds the store from a vendor's own memory: point it at Claude
Code's memory folder (one file per memory) or Codex's `~\.codex\memories\` (split on headings). Every
file or section becomes a pending proposal authored as `claude` or `codex`; re-importing adds nothing.

Rollback: the previous exe refuses a v5 database. Restore the `.v4.` backup per the host-configs
README, then run the previous exe; `data\memory\` is plain markdown and needs no rollback.

Checks: `pwsh tools\Invoke-M10MemoryCheck.ps1` drives one real Sonnet spawn against a scratch hub,
proves it read the core, and approves its proposal end to end.
