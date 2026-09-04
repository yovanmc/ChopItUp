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
copies the previous install aside as a sibling backup directory, then copies the new one in —
additively, never touching `data\` or `logs\`, never deleting anything (`/MIR` is never used) — and
replaces the exe last via a copy-aside-and-rename so a deploy killed mid-copy never leaves a
half-written executable under the name you launch. It refuses to run at all, before touching
anything, if any running process's image path is inside the target directory.

Parameters:

- `-TargetDir` — where to deploy. Defaults to `C:\Self Apps\ChopItUp`.
- `-StagingDir` — where to publish. Defaults to a fresh temp directory; the script always prints
  the path it used.
- `-SkipPublish` — reuse an existing `-StagingDir` instead of publishing again.
- `-RestoreFrom <backupDir>` — roll back to a previous install from one of the backup directories
  this script wrote, under the same guards (process check, `data\` exclusion, atomic exe rename).

Restoring is additive, like the deploy: it copies the backup's files back over the target but never
deletes files the newer install added and the backup does not contain. That is deliberate — the
alternative is a mirror-style copy that deletes to be tidy, standing next to your only copy of the
room history — but it means a restore rolls the program back, not the whole directory. A restore
also backs the current install aside first, so a rollback is itself undoable.

To check a deploy landed, `pwsh tools\Invoke-M4SelfCheck.ps1 -PublishDir <staging> -TargetDir <target>`.
It runs the published exe against a scratch copy (health, the UI, an MCP round trip, a restart) and
then compares the real target's files to staging by hash. It reads nothing inside `data\`.

The script never deletes a backup directory — only reports how many now sit beside the target.
Prune old ones (`C:\Self Apps\ChopItUp.backup-YYYYMMDD-HHmmss`) by hand once you're confident you
won't need to roll back to them.
