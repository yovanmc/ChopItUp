# Chop It Up

A local chat room where you, Claude and GPT talk in one thread — each model joining through MCP on its own subscription. No API keys. No browser automation. Everything stays on your machine.

Status: pre-alpha, see `ROADMAP.md`.

Developer checks: [focused verification, test profiling and coverage decisions](docs/test-efficiency.md).

## Run it (dev)

    dotnet run --project src/ChopItUp.Hub -- --data .data --port 8790

Tokens for each participant are generated on first start. `.data/tokens.json` keeps only their hashes and is read once at startup (restart the hub after editing the file). A model row the hub spawns gets a fresh in-memory token on every start instead. MCP endpoint: `http://127.0.0.1:8790/mcp` (bearer token required). One hub per data directory: a second instance on the same `--data` refuses to start (`hub.lock`).

## Connecting a host

Start the hub once (it mints the tokens), then:

    dotnet run --project src/ChopItUp.Hub -- --data .data --print-config

That writes `claude-desktop.json`, `codex-config.toml`, `claude-code-owner-remote.json` and a `README.md` into `.data/host-configs/`, each carrying the port the hub actually bound and a `{{TOKEN}}` placeholder where the token goes. Get a host's token with `--rotate-token <id>` (below) and paste it over the placeholder. Merge `claude-desktop.json` into `%APPDATA%\Claude\claude_desktop_config.json` and append `codex-config.toml` to `%USERPROFILE%\.codex\config.toml`, then restart that host. The hub never writes into those files itself — it emits the snippet, you paste it. The command prints the folder path and never a token.

Claude Code is deliberately not configured as a model participant (`claude-code-owner-remote.json` is for driving the hub as its human owner, see `docs/verification.md`): it would have to join as the same `claude` participant Claude Desktop uses, and two hosts on one identity share one read cursor.

To revoke a token: `dotnet run --project src/ChopItUp.Hub -- --data .data --rotate-token claude` (with the hub stopped). It prints the new token once and writes it to no file. The old token stops working at the next hub start; paste the new one into that host's config in place of the old.

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
`data\` folder that the exe creates on first run. A deploy (below) also publishes the desktop shell,
so the installed folder holds `ChopItUp.Hub.exe`, `ChopItUp.Desktop.exe`, `wwwroot\` and `data\`.

### Deploying

    pwsh tools\Deploy-ChopItUp.ps1 -TargetDir "C:\Self Apps\ChopItUp"

`tools\Deploy-ChopItUp.ps1` publishes the hub and the desktop shell into a staging directory,
sanity-checks the result (the hub exe at least 30 MB, the desktop exe at least 100 MB,
`wwwroot\index.html` and a non-empty `wwwroot\assets\` exist),
copies the previous install aside as a sibling backup directory, then copies the new one in — it
replaces `wwwroot\` wholesale (after the backup, so the old copy still exists there) and copies
everything else additively, never touching `data\` or `logs\` — and replaces the exes last (hub,
then desktop) via a copy-aside-and-rename so a deploy killed mid-copy never leaves a
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

Pin the room's governing work with `/objective <text>` and update its latest correction with
`/correction <text>`. These explicit owner commands survive the rolling transcript and hub restarts.
They spawn nobody. Bare `/objective` clears both values, and bare `/correction` clears only the
correction. Each value accepts up to 6,000 characters. See [governing context](docs/governing-context.md)
for provenance, supersession and omission accounting.

A spawn starts when an owner message begins with `@id` for a roster row that has a model set (several
ids may follow each other at the start; after a `/skill` token they still count). An `@id` anywhere
later in the text is a reference and spawns nothing; the hub says so in a note when a message
addresses nobody but names someone inline, and when a leading `@word` matches no participant. A
spawned model hands the turn on the same way, starting its reply with another spawnable id while
turns remain.

Caps, all hard-coded: 8 turns per exchange by default (a `turns: N` token among the leading mentions
sets 1 to 16), a 2 second debounce on repeated mentions, at least 10 seconds between two spawns of
the same participant across rooms, a 5 minute wall clock per spawn in a plain room (30 minutes in a
directory room or a run), and never two spawns of one
participant in flight in the same room at once.

The hub posts its own notes as `hub` (kind `system`, badge `HU`): a spawn that times out, one that
exits without posting, a turn skipped for lack of budget, and the exchange's conclusion all land in
the room, because the room is the only durable trail this milestone keeps.

Exchange state lives in memory only. A hub restart mid-exchange drops the budget and any pending or
in-flight spawns, the owner's next message starts fresh, and a spawn that outlives the restart still
posts harmlessly when it finishes.

The Stop exchange button on an exchange's strip stops that exchange. From a shell, with an
owner-class token (every `/api` write needs one), stop an open exchange with:

    Invoke-RestMethod -Method Post http://127.0.0.1:8790/api/rooms/general/exchange/stop -Headers @{ Authorization = 'Bearer <token>' }

That stop ends every exchange in the room. To stop one exchange and leave the others in the room
running, POST to `/api/rooms/<room>/exchanges/<root message id>/stop` (the id is in the `exchanges`
list of `GET /api/rooms/<room>/exchange`).

Reply to a message in the web UI (the Reply button on any message that is not a hub note) and your
post joins that message's exchange instead of starting a new one. Its mentions spend the exchange's
remaining turns, the skill it started with stays in force, and a concluded or stopped exchange opens
again. A reply that invokes a skill, or a reply with a mention to a hub note, a run's message, or an
exchange from before a hub restart, is handled as a new prompt, and the hub posts a note saying so.
While a run is going, a reply is handled like any other post in the run. The hub remembers the last
50 exchanges per room for this.

The participant the owner addressed first gets a synthesis turn when another model posted last (a
free turn if one is left, else one more). `/continue` (typed, or the strip's Continue button, which
posts it as a reply to the exchange's root) reopens a concluded or stopped exchange with 8 more turns
(`/continue turns: N` for another number), re-running the hand-offs the budget refused, or the
mentions the `/continue` message carries, or the addressee. A hub restart forgets exchanges, so
`/continue` after one says so.

A `claude` spawn runs `claude.exe -p` with the prompt on stdin and its token in a per-spawn
`mcp.json`, never `--bare`, which switches auth to an API key. A `codex` spawn runs `codex.cmd exec`
(a PATH shim, not an `.exe`) with the prompt on stdin and its token in `CHOPITUP_TOKEN`.

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

`propose_rewrite(room_id, topic, body)` is the other way in, and it replaces a whole topic rather than
adding one entry: the body is the entire file, folded and deduplicated. The panel shows it as a diff
against the current file, naming every entry it would remove and how many would lose their approval
record, and nothing is written until the owner approves that. The previous file stays on disk at
`<file>.rewrite-<id>.bak`, which no later write reuses.

"Import memory" in the room header seeds the store from a vendor's own memory: point it at Claude
Code's memory folder (one file per memory) or Codex's `~\.codex\memories\` (split on headings). Every
file or section becomes a pending proposal authored as `claude` or `codex`; re-importing adds nothing.

Rollback: `data\memory\` is plain markdown and needs no rollback.

Checks: `pwsh tools\Invoke-M10MemoryCheck.ps1` drives one real Sonnet spawn against a scratch hub,
proves it read the core, and approves its proposal end to end.

## Rooms (M9)

Every room is one conversation with its own directory: a git repository the hub owns. A room made
after M9 gets one at creation (a blank directory field means `<rooms root>\<room id>`; the rooms
root is `--rooms-root` / `CHOPITUP_ROOMS` / `%USERPROFILE%\ChopItUp\rooms`). A room from before M9
has no directory until the owner binds one, once — after that it cannot be re-bound.

Refused directories: drive roots, the user profile folder itself, anything under `C:\Self Apps`, the
hub's own data and install folders, the credential folders (`.claude .codex .ssh .gnupg .aws .azure
.kube .docker`), Windows and Program Files folders, network and device paths (`\\server\share`,
`\\?\...`), a folder inside another git repository that is not its root, and a folder that overlaps
another room's directory. A junction or symbolic link is resolved and both the link and its target
must pass.

Inside a directory room a spawned participant can read, create, edit, search and run shell commands
with network access, cwd set to the room; git is read-only for it by rule. Only one spawn runs at a
time within one exchange, so two models never edit the same tree at once.

Outside a run, each of a directory room's exchanges works in its own git worktree at
`<room dir>.worktrees\x<root>`, on its own branch `chopitup/x<root>` forked from the room directory's
HEAD, so two exchanges edit the room's repository side by side without racing. When an exchange
concludes with nothing left in flight, its worktree is removed and its branch is merged into the room
directory's checked-out branch with a `--no-ff` merge commit, and a hub note names the merge. A
conflict aborts the merge and keeps the branch, naming it and the conflicting paths; a stop, an
interrupted spawn (cancelled or timed out), or a run owning the room also keeps the branch unmerged,
each with its own note. A run's own spawns still work in the room directory itself, one at a time
across the whole room, exactly as before.

The trail: before a spawn, if the tree is dirty, the hub commits any owner edits; when the spawn ends
the hub always commits the tree for that participant, with the shell commands it ran listed in the
commit body. Every room commit carries the repository's own configured git identity as author and
committer (the hub's `ChopItUp hub <hub@chopitup.local>` only when none is configured), the participant
is named in the subject, and a turn that changed something is credited with
`Co-authored-by: Codex <noreply@openai.com>` or `Co-authored-by: Claude <noreply@anthropic.com>` as its
last paragraph; an exchange merge carries the trailers of the commits it merges, and the hub's own
bookkeeping commits (`Room trail start`, leftovers swept at a close or a restart) carry the same
repository identity and no trailer. During a run a spawn works in the room directory itself, so an edit
you make while its turn is running is swept into that turn's commit and shares its trailer. A hub note
`Committed <hash> for <id>: …`
(or `Not committed for <id>: …`) lands in the room, and the Trail button in the header lists the last 20
commits. Nothing is ever pushed.

Confinement is asymmetric and stated plainly rather than assumed: Codex runs under its own sandbox
(workspace-write, network on); Claude Code runs as the owner's own Windows user, confined only by a
deny list and the prompt, because Claude Code 2.1.220 has no read fence this hub can switch on — a
read outside the room is a rule the model is told to follow, not a wall it cannot cross.

The crash window: if the hub itself dies between the model's CLI exiting and the after-spawn commit,
the model's edits are left uncommitted, and the *next* pre-spawn commit sweeps them in authored as the
owner. The hub logs a warning at startup for every directory room that is already dirty, so the owner
can look before the next spawn runs.

Archive hides a room from the rail and from `list_rooms`; nothing on disk changes, and unarchive
restores it. The seeded `general` room can never be archived.

Checks: `pwsh tools\Invoke-M9RoomCheck.ps1` starts a scratch hub against a scratch rooms root, spends
one real Sonnet call (`-IncludeCodex` adds one Codex call), and proves room creation and refusal,
the owner-then-model commit order, the shell log in the commit body, the trail endpoint, unread and
mark-read, and archive/unarchive — leaving its log and the room folder behind under `%TEMP%`.
