# M9 — Rooms as chats with a directory each

**Goal:** Every room is a per-task conversation bound to a git working tree the models can read, edit and shell inside, and the hub — never a model — commits what happened after every spawn, authored as the model, with the shell commands it ran in the message.

**Architecture:** Schema v6 gives `rooms` two nullable columns, `directory` and `archived_at`. A new `/api/rooms` surface creates a room (validating the typed path against the refused-roots rules in `RoomPaths`, creating the folder when its parent exists, `git init`-ing it when it is not a repository), archives and unarchives it, binds a directory to a legacy room once, advances the owner's read cursor, and reads the commit trail. The M10 memory trail's git plumbing is generalised into `GitTrail` (author and committer split, `--allow-empty`, log, toplevel, dirty) and `MemoryGit` becomes a thin subclass; `RoomTrails` hands one `GitTrail` per room directory to the spawner. In a directory room the spawner runs one spawn at a time, commits the owner's edits before a spawn when the tree is dirty, launches Claude with cwd = the room directory under `--permission-mode dontAsk` with six built-in tools and a per-spawn `--settings` file whose deny list is every git write verb plus the credential folders (measured to hold on 2.1.220), launches Codex with `-C <room>` and `--json`, parses each CLI's structured output for the shell commands it ran (`SpawnOutput`), and commits the tree afterwards as the model with those commands in the message, posting a hub note that names the commit. The web client's rail becomes a chat list ordered by recency with unread badges, a New-room dialog, Archive, and a Trail dialog.

**Author model:** Claude Fable 5.1 (session model; tier routing satisfied — HIGH plans on Fable).

**Blast radius:** HIGH. A schema migration on the owner's live database (v6 adds two columns); the hub creates folders and git repositories at paths the owner types, and commits into repositories the owner already owns (a room bound to a real project); spawned models get file, shell and network access on the owner's machine for the first time, so the permission flags are a security boundary; the cross-process spawn contract changes for both CLIs; two new command-line outputs are parsed. `references/verification-tiers.md`: schema-evolution guard test, synthetic dry run (`Invoke-M2DryRun.ps1` plus the M9 live check against a scratch hub and a scratch room folder), screenshot + UIA gate on the rail and dialogs, two critic passes.

Written for builder-subagent execution; if something doesn't match, STOP and report rather than guess.

Size: over the 60 KB WARN line for the reason M5 and M10 were: every production file and every test is written in full. The milestone is one seam — a room has a tree, the models work in it, the hub records it — and a split (rooms + directories first, spawn access + trail second) would ship a row where the owner binds folders nobody can use, and pay a second Phase A, two more critique passes and a second deploy for the half that makes the first half worth having. The size is accepted, not ignored; the owner can veto it in the decision digest.

Binding definition: `docs/superpowers/plans/grill-notes-m5-autonomy.md` — D3, D10, D11, D12, D13 and F3, F5, F10, plus two measurements this plan adds (claims 23, 24) that the grill did not have: Codex's workspace-write sandbox did **not** stop a `git commit` inside the workspace under `--approve-for-me` on native Windows, and Claude Code 2.1.220 has no read fence that this hub can switch on. Lessons consulted: M1 `[sqlite, schema, migrations]` (stamp last, same transaction, probe before ALTER — the v3 shape); M2 `[sqlite, wal, testing, migrations]` (raw-SQL fixtures for old shapes); M5 `[windows, spawn, cli-shims]` (git is a real `.exe`; the codex shim needs `cmd /d /c`); M5 `[codex, headless, mcp, approvals]` (`--approve-for-me` is the only Codex policy that can post — and this plan measures that it also commits); M5 `[claude-code, headless, tool-surface]` (a restricted `--tools` list keeps MCP tools directly callable; F2); M5 `[ci, path, seams, tests]` (machine lookups behind a seam; git is the one exception, argued in M10 decision 4 and kept here); M4 `[process, async-io, ci-flake]` (`ProcessRunner` drains; reused unchanged); M16 `[browser-pane, input-events, headless-capture, verification]` (pane drops clicks; `element.click()` + server log; `--virtual-time-budget`); M8 `[browser-pane, launch-json, verification]` (the launch.json under the session cwd, port 8795); M10 `[powershell, invoke-restmethod, check-scripts, live-check]` (`ForEach-Object { $_ }` before filtering; one re-run before a tool leg is a defect). Omitted deliberately: M3 `[msbuild, node, csproj]` (no build-target change).

Measured this session (2026-09-06, HEAD `4965d3ad`): `dotnet test` = 76 Core + 169 Hub green in 1 m 22 s; `claude --help` (2.1.220) has `--settings <file-or-json>`, `--permission-mode` with `dontAsk`, `--tools`, `--allowedTools`, `--disallowedTools`, `--output-format stream-json`, `--verbose`, and has **no** `--restricted` and **no** `--permission-prompts`; `codex exec --help` (0.153.3) has `--json`, `-C`, `--add-dir`, `--approve-for-me` ("Route approval requests through automatic review using the workspace-write sandbox"), `--skip-git-repo-check`, `--ephemeral`, `-c key=value`; `git version 2.45.2.windows.1`; `CLAUDE.md` is 3,775 bytes; two Haiku probes and one gpt-5.4-mini probe in throwaway repositories under `%TEMP%` (claims 23–25); the .NET path facts of claim 26.

## Decisions taken here (B-class: reversible rulings, logged not asked)

1. **Every new room has a directory; rooms made before M9 keep `NULL` and behave exactly as today until the owner binds one.** A migration that created folders on disk for `general` would write outside the data directory during a startup the owner did not watch. `POST /api/rooms/{id}/directory` binds a directory to a `NULL` room once; it never re-binds. A `NULL` room's spawns keep today's scratch cwd, tool list and command lines byte for byte (claim 14). Revert: a one-off backfill that binds `general` to `<RoomsRoot>\general`.
2. **Hub-created directories live under one rooms root, one folder per room id.** `--rooms-root <dir>` / `CHOPITUP_ROOMS` / default `%USERPROFILE%\ChopItUp\rooms`. The install directory is under `C:\Self Apps\`, which D12 refuses, and the data directory holds the database and every token, so neither can hold room trees; a folder inside the profile is what D12 allows. Revert: pass `--rooms-root`.
3. **The refused set is D12's list plus what D12 implies.** Refused: a relative, UNC or `\\?\` path; a drive root; the profile folder itself; anything under `C:\Self Apps\`; anything under the hub's data folder or its install folder; the credential folders under the profile (`.claude`, `.codex`, `.ssh`, `.gnupg`, `.aws`, `.azure`, `.kube`, `.docker` — the first two hold the very credentials the spawns run on); anything under `SystemRoot`, `ProgramFiles`, `ProgramFiles(x86)`, `ProgramData`; a folder that is inside another git repository but is not its root (the hub would be committing into the owner's other project); a folder equal to, inside, or containing another room's directory (two trails over one tree). A junction or symbolic link is resolved and both the link and its target must pass. A typed folder that does not exist is created when its parent exists; a blank field means `<RoomsRoot>\<room id>`. Comparisons are `OrdinalIgnoreCase` on normalised full paths with no trailing separator. Revert: shrink `RoomPathRules.ForHub`.
4. **A room's repository is created when the room is created, not lazily, and a machine without `git.exe` cannot create a directory room (400).** D11 says the room *is* a git repo; a room with file access and no trail would silently lose the record the whole row exists to keep. Memory's lazy trail stays as it was. Revert: make `RoomDirectories.PrepareAsync` tolerate an unavailable git and log once.
5. **A directory room runs one spawn at a time.** The tree is one mutable thing; two models editing it at once would interleave edits, and the owner-edits commit taken before the second launch would sweep the first model's half-finished work into a commit authored as the owner. `ExchangePolicy.Due`/`NextWake` take an `exclusive` flag; a `NULL` room keeps today's parallel launches (D7: "parallel across rooms" is untouched). Turn numbers, budget and mention order are unchanged — the second pending participant simply launches when the first finishes. Revert: pass `exclusive: false`.
6. **Commit protocol.** Before a launch, if `git status --porcelain` is non-empty, everything is committed as `<human DisplayName> <humanId@chopitup.local>` with subject `owner: edits before the next spawn in room <room>`; a clean tree gets no commit. After the spawn ends — however it ends: posted, timed out, stopped, crashed — everything is committed with `--allow-empty` as `<DisplayName> <id@chopitup.local>`, committer `ChopItUp hub <hub@chopitup.local>`, subject `<id>: turn n/b in room <room>`, body `Shell commands run (k):` with one numbered line per command (first 50, each flattened to one line and cut at 400 characters, the hub token scrubbed, a permission-denied one marked `[denied]`). The message goes to git on stdin (`commit -F -`), never on the command line (a Windows command line is capped at 32,767 characters). The hub then posts `Committed <hash> as <id>: f file(s) changed, k shell command(s).`, or `Not committed for <id>: <reason>.`; when HEAD after the spawn differs from HEAD after the owner commit, the note adds `HEAD moved during the spawn: <id> committed on its own.` and the commit still lands on top (measured, claim 35: an absolute-path `git.exe commit` is exactly this case, and it was caught this way). Git log is the trail and the notes are its room-side index; there is no new table. An empty commit per spawn is D11 read literally ("commits after every spawn"): the shell log is the record even when nothing changed. Crash window, stated: if the hub dies after the CLI exits and before the after-spawn commit (shutdown waits 10 s on the spawn task), the model's edits stay uncommitted and the next pre-spawn commit sweeps them in as the owner; the startup sweep logs a warning for every directory room that is dirty at boot, so the owner can look before the next spawn. Revert: skip the commit when the tree is clean and no command ran.
7. **Git is read-only for the models by different mechanisms on the two hosts, both measured, and the plan says so where D13 assumed more.** Claude: a per-spawn `--settings` file whose `permissions.deny` names every git write verb as `Bash(git <verb> *)` and `Bash(git <verb>)`, plus `Bash(git -*)` (any invocation that opens with an option — `-c`, `-C`, `--git-dir`, `--work-tree` — which is how `git -c user.name=x commit` was denied in the probe), plus `Bash(git.exe *)` and `Bash(git.exe)` (claim 35: `git.exe commit` is denied by them), plus `Edit(.git/**)` and `Write(.git/**)`; measured on 2.1.220 (claim 23): plain commands and `git log` ran, both commit forms were denied and HEAD did not move; deny rules apply in every mode. Codex: the docs promise `.git` is read-only inside workspace-write, and the probe (claim 24) shows `git commit --allow-empty` **succeeding** under `--approve-for-me` on native Windows 0.153.3 — so for Codex the mechanism is the prompt rule plus the trail (HEAD-moved detection and the shell log), which is the stance D13 already takes for Claude. Row 13's Notes get this measurement. Residual, stated and measured (claim 35): deny rules are prefix rules — a leading-wildcard rule (`Bash(*git.exe *)`) did not match on 2.1.220, so `"C:/Program Files/Git/mingw64/bin/git.exe" commit` ran and moved HEAD in the probe, and the same holds for a git write hidden inside another program (`pwsh -c "git commit"`, a script, `node -e`). For those the mechanism is the one Codex already relies on: the after-spawn commit lands on top, the shell log names the command, and the note says HEAD moved. Revert: none — a measurement is not a choice; the upgrade path is row 13.
8. **"No reads outside the room" is a rule in the prompt, not a fence, on both hosts — and Claude's file tools are additionally denied the credential folders.** Measured (claim 23): `permissions.blockReadsOutsideWorkingDirectories: true` in `--settings` had no effect on 2.1.220 (a `Read` of `C:\Windows\win.ini` succeeded) and `Read(//C:/Windows/**)` did not match, while `Read(~/<folder>/**)` did (`File is in a directory that is denied by your permission settings.`); Codex documents that workspace-write restricts writes, not reads. The deny list therefore carries `Read`, `Write` and `Edit` rules in the `~/<folder>/**` form for `.claude`, `.codex`, `.ssh`, `.gnupg`, `.aws`, `.azure`, `.kube`, `.docker` — the folders that hold credentials, including the two the spawns themselves run on; `Write(~/…/**)` was measured to bind like `Read` (claim 35: a Write into `~/m9probe-decoy` was refused while a Write into the cwd succeeded), so a spawn cannot plant a hook in `~/.claude/settings.json` or a memory in `~/.codex` through its file tools. The fence rules travel as `--append-system-prompt` (F10; the flag is in 2.1.220's `--help`) — the system channel, which the room transcript on stdin cannot write into — and are repeated in the stdin prompt for both hosts (`SpawnPrompt.DirectoryRules`, one string). Residuals, stated because D13 accepted them and row 13 is their fix, and repeated in the README in the same words: (a) Bash runs as the owner, so `type` reads any file, including `<data>\tokens.json`, and a spawn can therefore post as any participant through the MCP endpoint; (b) `/api` has no auth and the spawn has network (D10), so `curl` can post or import as the owner (`POST /api/rooms/{id}/messages`, `/import`) and stop an exchange — and a post as the owner triggers spawns; those routes are not 409-guarded because the owner posts and stops during spawns by design, and any header or port the browser can use the spawn can use too; (c) absolute-path git and interpreters (decision 7). Revert: drop `SpawnCommands.CredentialFolders`.
9. **Spawn command lines.** Claude in a directory room: `-p --permission-mode dontAsk --tools Read,Edit,Write,Glob,Grep,Bash --strict-mcp-config --mcp-config <scratch>\mcp.json --allowedTools Read,Edit,Write,Glob,Grep,Bash,mcp__chopitup__post_message,mcp__chopitup__recall,mcp__chopitup__propose_memory --settings <scratch>\settings.json --append-system-prompt <rules> --no-session-persistence --model <m> --output-format stream-json --verbose --disable-slash-commands --setting-sources ""`, cwd = the room directory; `<rules>` is `SpawnPrompt.DirectoryRules(directory)`. `dontAsk` plus an allow list is the measured shape (claim 23); `bypassPermissions` is not used because under it protected-path writes (`.git`) are auto-approved and only the deny list would stand, whereas under `dontAsk` an unlisted request is auto-denied as well. WebFetch/WebSearch stay off the list: network is for the shell (D10), and a model that wants a page runs `curl`. Codex in a directory room: `exec --ephemeral --ignore-user-config --json -c mcp_servers.chopitup.* (four, as today) -c sandbox_workspace_write.network_access=true --approve-for-me -C <room> -m <m> --color never -o <scratch>\last.txt -`; `--skip-git-repo-check` is dropped because the room is a repository (a `NULL` room keeps it). `mcp.json`, `settings.json` and `last.txt` live in the per-spawn scratch folder under `<data>\spawns\`, never in the room tree — the token must not be committed. Revert: per flag, in `SpawnCommands`.
10. **Owner-visible surfaces are one `opus` task.** The rail lists rooms newest activity first with an unread badge; a New-room button opens a dialog (title, directory with a blank-means-hub-created hint, error line); the header shows the directory (or a Bind-directory control on a legacy room), an Archive/Unarchive button and a Trail button that opens a dialog listing the last 20 commits; archived rooms appear only behind a "Show archived" toggle. Unread is the owner's own cursor (the same `read_cursors` row every MCP participant has); the browser advances it with `POST /api/rooms/{id}/read` when the room is open and a message lands. No client test runner is added (claim 21): the client is verified by `tsc`, the screenshot judge and the UIA gate, as in M10 and M16.
11. **Room create, bind, archive and unarchive are refused with 409 while any spawn is in flight** (one hub-wide check, `SpawnerService.AnySpawnInFlight`). Create and bind: the M10 argument (decision 13 there) — `/api` has no auth and a spawn now has a shell and `curl`, so it could bind a room to a folder it wants the next spawn to read; a spawn process exists only inside the in-flight window. Archive widens nothing, but the room-local in-flight set lives on the spawner's loop thread and is not safe to read from a request thread, while `AnySpawnInFlight` is an `Interlocked` counter; spawns are short and the owner is one person, so the coarser guard costs nothing. Revert: drop the guard when owner auth lands.
12. **The trail endpoint reads `git log` on demand, last 20, no cache.** Revert: none needed.
13. **Room ids are slugs of the title, capped at 40 characters, with `-2`, `-3` on collision, never a Windows reserved device name.** The id is the MCP `room_id`, the URL segment and — for a hub-created directory — the folder name. Revert: switch `RoomIds.Unique` to a random suffix.
14. **Archive hides, never blocks.** `GET /api/rooms` and `list_rooms` omit archived rooms; `?archived=true` includes them; posting into an archived room still works (nothing is deleted, nothing is refused). Revert: 409 on post.
15. **`RoomTrails` goes behind the same optional factory seam `MemoryGit` uses (`HubHost.Build(..., Func<string, GitTrail>? roomGit)`), and hub tests commit for real** (M10 decision 4, unchanged: git is on every developer machine and on `windows-latest`, and a fake would test the degraded path). Revert: register behind `CliLocator`.
16. **The `general` room cannot be archived** (400). It is the seeded room, the spawner's tests and the check scripts are hard-wired to it, and the client always has a visible room to open. Every other room, directory or not, can be archived and unarchived freely. Revert: drop the check and give the client an empty state.

## Acceptance

- A1 WHEN a v5 database starts THE SYSTEM SHALL back it up (name contains `.v5.`), add nullable `rooms.directory` and `rooms.archived_at` (both `NULL` for every existing row), stamp `user_version = 6` in the same transaction, and change nothing else; a torn v6 (both columns present, stamp still 5) SHALL be repaired, not crashed; a second `EnsureDatabase()` SHALL make no backup.
- A2 WHEN the owner posts `{name, directory}` to `POST /api/rooms` THE SYSTEM SHALL: refuse an empty name or one over 80 characters (400); refuse with 409 while any spawn is in flight; refuse with 400 and a sentence naming the reason, creating no row and no folder, a directory that is relative, UNC or `\\?\`, a drive root, the user profile folder itself, under `C:\Self Apps\`, under the hub's data or install folder, under a credential folder, under a Windows system folder, inside another git repository but not its root, or overlapping another room's directory; otherwise create the folder when it does not exist and its parent does, `git init` it when it is not a repository, insert the room with id `RoomIds.Unique(name)` and return 201 with `directory` set to the normalised path; a blank directory SHALL mean `<RoomsRoot>\<id>`; WHEN `git.exe` is not on PATH THE SYSTEM SHALL refuse with 400.
- A3 WHEN `GET /api/rooms` is called THE SYSTEM SHALL return rooms newest activity first (the newest message's time, else the room's creation), each with `directory`, `archivedAt`, `unread` (messages past the owner's cursor) and `lastActivityAt`, omitting archived rooms unless `?archived=true`; `list_rooms` SHALL omit archived rooms and carry `directory`, and its existing fields SHALL be unchanged.
- A4 WHEN `POST /api/rooms/{id}/archive` is called THE SYSTEM SHALL set `archived_at` and hide the room, WHEN `unarchive` SHALL clear it; both SHALL answer 404 for an unknown room and 409 while the room has a spawn in flight; nothing on disk SHALL change.
- A5 WHEN `POST /api/rooms/{id}/read` is called THE SYSTEM SHALL move the owner's cursor to the room's last message and answer 200 `{roomId, unread: 0}`, so the room's `unread` drops to 0 (404 for an unknown room); WHEN `POST /api/rooms/{id}/directory {directory}` is called on a room with no directory THE SYSTEM SHALL validate and bind exactly as A2 and answer 200; on a room that has one SHALL answer 409.
- A6 WHEN a spawn launches in a directory room THE SYSTEM SHALL start Claude with working directory = the room directory and exactly the argument list of decision 9 (in that order), with a `settings.json` in the scratch folder whose `permissions.deny` contains `Bash(git commit *)`, `Bash(git commit)`, `Bash(git push *)`, `Bash(git -*)`, `Bash(git.exe *)`, `Edit(.git/**)`, `Write(.git/**)`, `Read(~/.ssh/**)`, `Write(~/.claude/**)` and `Edit(~/.codex/**)` (among the full list) and no `allow`; SHALL start Codex with `-C <room directory>`, `--json`, `-c sandbox_workspace_write.network_access=true` and without `--skip-git-repo-check`; `mcp.json`, `settings.json` and `last.txt` SHALL sit under `<data>\spawns\<spawnId>\` and never inside the room; a room without a directory SHALL launch byte-for-byte today's command lines.
- A7 WHEN a spawn launches in a directory room THE SYSTEM SHALL render a `Files:` section in the prompt naming the directory, that it is a shared git repository, that the model may read, create, edit and run shell commands there with network and nowhere else, that git is read-only for it (naming the verbs), and that the hub commits its changes as it when the turn ends; a room without a directory SHALL keep the sentence `You have no files and no tools besides this hub; your memory is the section below.`
- A8 WHEN a spawn launches in a directory room whose tree is dirty THE SYSTEM SHALL first commit everything authored `Owner <owner@chopitup.local>` with subject `owner: edits before the next spawn in room <room>`; a clean tree SHALL get no owner commit.
- A9 WHEN a spawn in a directory room ends THE SYSTEM SHALL commit the tree with `--allow-empty`, author `<DisplayName> <id@chopitup.local>`, committer `ChopItUp hub <hub@chopitup.local>`, subject `<id>: turn n/b in room <room>`, a body `Shell commands run (k):` (or `Shell commands run: none.`) followed by one numbered line per Bash `tool_use` (Claude) or `command_execution` item (Codex) in stdout, denied ones suffixed ` [denied]`; SHALL post one hub note `Committed <hash> as <id>: f file(s) changed, k shell command(s).` (or `Not committed for <id>: <reason>.`), adding `HEAD moved during the spawn: <id> committed on its own.` when HEAD changed during the spawn; SHALL delete the scratch folder and SHALL NOT delete or modify the room directory beyond the commit.
- A10 WHEN two participants are pending in a directory room THE SYSTEM SHALL launch the second only after the first's spawn has ended; in a room without a directory both SHALL launch as today.
- A11 WHEN `GET /api/rooms/{id}/trail` is called THE SYSTEM SHALL return `{directory, commits:[{hash, author, at, subject}], error}` with the last 20 commits newest first; a room without a directory SHALL return `directory: null`, an empty list and `error: null`; a room whose git call failed SHALL return an empty list and the failure in `error`; an unknown room 404.
- A12 WHEN the owner opens the web client THE SYSTEM SHALL show the rail as a chat list newest first with an unread badge on rooms that have unread messages and are not open, a "New room" control that opens a dialog with a title field, a directory field, a blank-means-hub-created hint and an error line, a header that shows the room's directory (or a "Bind directory" control on a legacy room), Archive/Unarchive, and a Trail control whose dialog lists hash, author, time and subject; archived rooms SHALL appear only when "Show archived" is on. Verified by the screenshot judge and the UIA gate.
- A13 WHEN `tools\Invoke-M9RoomCheck.ps1` runs against a scratch hub on this machine THE SYSTEM SHALL pass every leg (`Results: n/n PASS`, exit 0); README SHALL carry a `## Rooms (M9)` section that states the refused set, the trail and the confinement residuals of decision 8; `CLAUDE.md` SHALL carry the check line and stay under 4,096 bytes; row 13's Notes SHALL carry the Codex measurement.

## Claim ledger
| # | Claim | Verified at (commit) | Recheck (pwsh, exit 0 = holds) |
|---|-------|----------------------|--------------------------------|
| 1 | Baseline: 245 tests green (76 Core + 169 Hub) | 4965d3ad | `dotnet test ChopItUp.slnx -c Debug --nologo -v minimal 2>&1 \| Select-String 'Passed:\s+(76\|169),' \| Measure-Object \| % { if ($_.Count -eq 2) { exit 0 } else { exit 1 } }` |
| 2 | `ChopDb.LatestSchemaVersion = 5` (line 10); ladder ends `if (GetUserVersion(conn) < 5) ApplyV5(conn);` (line 104); `ApplyV5` (361–386) stamps `PRAGMA user_version = 5;` inside its DDL string | 4965d3ad | `Select-String -Path src/ChopItUp.Core/Storage/ChopDb.cs -Pattern 'LatestSchemaVersion = 5;' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 3 | Schema-5 literals to sweep: `Assert.Equal(5, db.GetSchemaVersion())` at `SchemaMigrationTests.cs` 187, 210, 221, 246; `schema -eq 5` at `Invoke-M10MemoryCheck.ps1:71`, `Invoke-M2DryRun.ps1:146`, `Invoke-M4SelfCheck.ps1:321`, `Invoke-M5SpawnCheck.ps1:62` | 4965d3ad | `$n = (Select-String -Path tests/ChopItUp.Core.Tests/Storage/SchemaMigrationTests.cs -Pattern 'Assert\.Equal\(5, db\.GetSchemaVersion\(\)\)').Count + (Select-String -Path tools/Invoke-M10MemoryCheck.ps1,tools/Invoke-M2DryRun.ps1,tools/Invoke-M4SelfCheck.ps1,tools/Invoke-M5SpawnCheck.ps1 -Pattern 'schema -eq 5').Count; if ($n -eq 8) { exit 0 } else { exit 1 }` |
| 4 | `rooms` DDL (v1, `ChopDb.cs:243-247`) has three columns `id, name, created_at`; the only room ever inserted is `INSERT OR IGNORE INTO rooms (id, name, created_at) VALUES ('general', 'General', $at);` (line 266); `ApplyV3` (306–343) is the probe-then-`ALTER TABLE ... ADD COLUMN` shape via `pragma_table_info` | 4965d3ad | `Select-String -Path src/ChopItUp.Core/Storage/ChopDb.cs -Pattern "VALUES \('general', 'General', \`$at\);" -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 5 | `Room` is `public sealed record Room(string Id, string Name, DateTimeOffset CreatedAt, long LastMessageId, int MessageCount);` (`Message.cs:10`); `MessageStore.ListRooms()` (11–25) orders `ORDER BY r.created_at, r.id`; `RoomExists` (27–34); `GetCursor` (190–198) and `SetCursor` (201–213, `MAX(last_read_id, excluded.last_read_id)`); `Post` advances the author's own cursor in the same transaction (124–133), so an owner's own posts are never unread to the owner | 4965d3ad | `Select-String -Path src/ChopItUp.Core/Storage/MessageStore.cs -Pattern 'GROUP BY r\.id ORDER BY r\.created_at, r\.id' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 6 | `Timestamps.Stamp` = `at.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)` (line 8): every stored stamp is UTC round-trip text, so `MAX(created_at)` and `ORDER BY ... DESC` on the text are chronological | 4965d3ad | `Select-String -Path src/ChopItUp.Core/Storage/Timestamps.cs -Pattern 'ToString\("o", CultureInfo\.InvariantCulture\)' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 7 | `SpawnCommands.Claude` (22–29) argv is `-p --tools "" --strict-mcp-config --mcp-config <p> --allowedTools <ClaudeToolAllowed> --no-session-persistence --model <m> --output-format json --disable-slash-commands --setting-sources ""`; `Codex` (44–55) ends `--approve-for-me -C <workDir> --skip-git-repo-check -m <m> --color never -o <last> -`; `ClaudeToolAllowed` (line 16) is the three-MCP-tool string; `ClaudeFinalText` (59–71) parses stdout as ONE JSON object; `CodexFinalText` (74–79) reads the `-o` file; the class comment says `--bare` is never used (API-key-only auth) | 4965d3ad | `Select-String -Path src/ChopItUp.Hub/Spawning/SpawnCommands.cs -Pattern '"--approve-for-me", "-C", workDir, "--skip-git-repo-check"' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 8 | `SpawnerService`: `FinishedEvent(SpawnHandle Handle, ProcessResult Result)` (35); `SpawnHandle` (39–50) has `WorkDir`, `Token`, `Posted`; ctor (76–78) ends `CliLocator cliLocator, MemoryStore memory`; `InFlightIn` (203–204); `Launch` (206–262) sets `workDir = Path.GetFullPath(Path.Combine(_options.DataDir, "spawns", spawnId))` (210), renders the prompt (218–221), writes `mcp.json` (228–229), builds `handle` (239–243), `Interlocked.Increment(ref _live)` (246), `handle.Run = Task.Run(async () => { ... _events.Writer.TryWrite(new FinishedEvent(handle, result)); })` (247–253); `OnFinished` (264–303) calls `TryDeleteDir(h.WorkDir)` (298) then `ExchangePolicy.Finished` (300); `LaunchDue` (190–201) and `ArmWake` (323–341) call `_policy.Due`/`NextWake` with `InFlightIn(...)`; `RoomName` (380) scans `ListRooms()`; `Scrub`/`StripAnsi`/`Truncate`/`Tail`/`TryDeleteDir` are `private static` (397–422) | 4965d3ad | `Select-String -Path src/ChopItUp.Hub/Spawning/SpawnerService.cs -Pattern 'private sealed record FinishedEvent\(SpawnHandle Handle, ProcessResult Result\) : Event;' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 9 | `ExchangePolicy.Due(Exchange x, DateTimeOffset now, IReadOnlyDictionary<string, DateTimeOffset> lastStartByParticipant, IReadOnlySet<string> inFlightInRoom)` (line 87) and `NextWake(...)` (line 103) have four parameters; `Due` skips a participant already in flight (`if (inFlightInRoom.Contains(id)) continue;`, 93) and numbers turns `x.TurnsStarted + due.Count + 1` (96) | 4965d3ad | `Select-String -Path src/ChopItUp.Hub/Spawning/ExchangePolicy.cs -Pattern 'public IReadOnlyList<SpawnRequest> Due\(Exchange x, DateTimeOffset now, IReadOnlyDictionary<string, DateTimeOffset> lastStartByParticipant, IReadOnlySet<string> inFlightInRoom\)' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 10 | `SpawnPrompt.cs:49` appends `"You are stateless: this transcript is all you know of the room. You have no files and no tools besides this hub; your memory is the section below.\n"`; `SpawnPromptInput` (8–22) is positional and ends `IReadOnlyList<string>? MemoryTopics = null` | 4965d3ad | `Select-String -Path src/ChopItUp.Hub/Spawning/SpawnPrompt.cs -Pattern 'You have no files and no tools besides this hub; your memory is the section below' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 11 | `MemoryGit` (`Memory/MemoryGit.cs`): ctor `(string root, Func<ResolvedCli>? resolve = null, IProcessRunner? runner = null)` (28); `Reason` (36); `CommitAsync(string message, CancellationToken cancellation = default)` (41) returns HEAD's short hash also when there was "nothing to commit"; `Identity` (18–19) is `user.name=ChopItUp hub`, `user.email=hub@chopitup.local`, `commit.gpgsign=false`, `core.autocrlf=false`; `Env` has `GIT_TERMINAL_PROMPT=0`, `LC_ALL=C`; `MemoryGitTests` assert `Reason` contains `git is not available` (47), starts with `git init exited 128: boom` (57), and the log line starts `ChopItUp hub <hub@chopitup.local> Approve memory proposal` (39); `MemoryApiGuardTests.cs:23` constructs `new MemoryGit(root, () => throw new FileNotFoundException(...))` | 4965d3ad | `Select-String -Path src/ChopItUp.Hub/Memory/MemoryGit.cs -Pattern 'public async Task<string\?> CommitAsync\(string message, CancellationToken cancellation = default\)' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 12 | `HubHost.Build(HubOptions options, IProcessRunner? processRunner = null, SpawnLimits? limits = null, CliLocator? cliLocator = null, Func<string, MemoryGit>? memoryGit = null)` (line 22); registers `MemoryStore` (74), `MemoryProposalStore` (75), `(memoryGit ?? (root => new MemoryGit(root)))(memory.Root)` (76), `.WithTools<RoomTools>().WithTools<MemoryTools>()` (79); maps `app.MapChatApi(); app.MapExchangeApi(); app.MapMemoryApi();` (109–111) | 4965d3ad | `Select-String -Path src/ChopItUp.Hub/Hosting/HubHost.cs -Pattern 'Func<string, MemoryGit>\? memoryGit = null\)' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 13 | `HubOptions(string DataDir, int Port, HubCommand Command = HubCommand.Serve, string? RotateParticipant = null, string? WebRoot = null)` (line 12); `Parse` (16–52) reads `--data`, `--port`, `--rotate-token`, `--print-config`, then `CHOPITUP_DATA`/`CHOPITUP_PORT`, and returns a positional `new HubOptions(...)` (47–51); `Program.cs` is 9 lines calling `HubOptions.Parse(args, Environment.GetEnvironmentVariable)` | 4965d3ad | `Select-String -Path src/ChopItUp.Hub/Hosting/HubOptions.cs -Pattern 'string\? WebRoot = null\)' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 14 | `SpawnCommandsTests` asserts the exact Claude argv (16–20, opening `"-p", "--tools", ""`) and Codex argv (43–51, containing `"--skip-git-repo-check"`); `SpawnerServiceTests.Memory.cs:22` asserts `--allowedTools` equals the three-tool string for a spawn in `general`; both must keep passing (decision 1: a `NULL` room is unchanged) | 4965d3ad | `Select-String -Path tests/ChopItUp.Hub.Tests/Spawning/SpawnCommandsTests.cs -Pattern '"--approve-for-me", "-C", @"C:\\data\\spawns\\s2", "--skip-git-repo-check"' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 15 | `RoomTools.ListRooms` (41–52) serialises `new { r.Id, r.Name, r.CreatedAt, r.MessageCount, r.LastMessageId, UnreadCount = ... }` with `RosterJsonOptions` (snake_case, nulls kept); `RoomToolsTests` reads `rooms[0].unread_count` (97, 153) and `message_count` (170) — an added `directory` key is additive; `Tools_list_is_exactly_the_six_tools` (15–26) pins the tool count at 6 (M9 adds no tool) | 4965d3ad | `Select-String -Path src/ChopItUp.Hub/Mcp/RoomTools.cs -Pattern 'UnreadCount = r\.MessageCount == 0 \? 0 : CountUnread\(r, me\),' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 16 | `ChatApi.MapChatApi` maps a `/api` group with `GET /rooms` → `GetRooms(MessageStore store)` (32–33) and `private static object MapRoom(Room r) => new { r.Id, r.Name, r.CreatedAt, r.MessageCount, r.LastMessageId };` (142); `MemoryApi.SpawnRunning` (19) and its `if (spawner.AnySpawnInFlight) return Results.Conflict(new { error = SpawnRunning });` guard (44) are the 409 template; `SpawnerService.AnySpawnInFlight` (93) is hub-wide; `Snapshot(roomId).InFlight` is the room's live spawns (345–351) | 4965d3ad | `Select-String -Path src/ChopItUp.Hub/Web/ChatApi.cs -Pattern 'private static object MapRoom\(Room r\) => new \{ r\.Id, r\.Name, r\.CreatedAt, r\.MessageCount, r\.LastMessageId \};' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 17 | `HubTestHost.StartAsync(string dir, bool deleteOnDispose = true, string? webRoot = null, IProcessRunner? processRunner = null, SpawnLimits? limits = null, CliLocator? cliLocator = null, Func<string, MemoryGit>? memoryGit = null)` (39) builds `new HubOptions(dir, Port: 0, WebRoot: webRoot)` (41) and deletes with `TestDirs.DeleteTree(_dir)` (75); `FakeProcessRunner.Handler` is `Func<ProcessSpec, TimeSpan, CancellationToken, Task<ProcessResult>>` (14), `McpJsonOf` (25), `NextSpecAsync` (42), `NoSpecWithin` (48), `Ok(stdout)` (54), `ParticipantOf` (55); `FakeCli.Locate` (78); `RefusingProcessRunner` is the default | 4965d3ad | `Select-String -Path tests/ChopItUp.Hub.Tests/HubTestHost.cs -Pattern 'new HubOptions\(dir, Port: 0, WebRoot: webRoot\)' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 18 | `SpawnerServiceTests` is `public sealed partial class` (9) with `Fast` limits (11: Budget 4, Debounce 150 ms, MinSpacing 0, Timeout 1 s), `Wait` 15 s (12), `_dir`, `_runner`, `_host`, helpers `PostAsOwner` (23), `PostAs` (29), `Messages()` (37), `WaitForMessage` (44), `WaitForStatus` (56), `ClaudeTokenIn` (68); every helper is hard-wired to room `general` | 4965d3ad | `Select-String -Path tests/ChopItUp.Hub.Tests/Spawning/SpawnerServiceTests.cs -Pattern 'private async Task PostAsOwner\(string body\)' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 19 | `SchemaMigrationTests.WriteRawV4()` (130–177) is raw SQL ending `PRAGMA user_version = 4;`; `M10_A7_...` (180–211) asserts `.v4.`, roster, 2 messages, the cursor, `pragma_table_info('memory_proposals')` = 12; `ApplyV5`'s DDL (the `memory_proposals` table and `ix_memory_proposals_status`) is what a raw v5 fixture must add on top of v4 | 4965d3ad | `Select-String -Path tests/ChopItUp.Core.Tests/Storage/SchemaMigrationTests.cs -Pattern 'private void WriteRawV4\(\)' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 20 | `ParticipantStore.HumanId()` (35–41); `Participant(Id, DisplayName, Kind, Host, Model, Note)` (`Message.cs:25`); the human seed row is `("owner", "Owner", "human", "human", null, null)` (`ChopDb.cs:23`) | 4965d3ad | `Select-String -Path src/ChopItUp.Core/Storage/ChopDb.cs -Pattern 'new\("owner",\s+"Owner",\s+"human", "human",' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 21 | Client: `RoomRail.tsx` props `{rooms, activeRoomId, liveness, onSelect}` (5–10), renders `<ul className="room-list">` with `aria-current` (35) and `.room-count` (38); `RoomHeader.tsx` props `{room, loadedCount, onImport, onImportMemory}` (5–10); `App.tsx` state (26–37), `refreshRooms` picks `loaded[0]` (74–78), `MessagePosted` handler (112–126) bumps `messageCount`, `activeRoom` (284), render (286–334); `types.ts` `Room` (11–17); `api.ts` `listRooms` (42–44), `unwrap`/`failureText` (27–40); `MemoryImportDialog.tsx` is the overlay/dialog/`field-label`/`field`/`dialog-error`/`dialog-actions` pattern (72–155); `styles.css` has `.rail`, `.rail-head`, `.room-list`, `.room-item`, `.room-name`, `.room-count`, `.overlay`, `.dialog`, `.field-group`, `.field-label`, `.field`, `.quiet-note`, `.link`; `time.ts` exports `clockTime`, `dayLabel`, `exactTime`; `participants.ts` exports `displayName`, `isSystem` | 4965d3ad | `Select-String -Path src/ChopItUp.Hub/client/src/RoomRail.tsx -Pattern "aria-current=\{room\.id === activeRoomId \? 'true' : undefined\}" -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 22 | `client/package.json` scripts are `dev`, `build` (`tsc --noEmit && vite build`), `typecheck`, `preview` — no test runner; CI (`.github/workflows/ci.yml`) runs restore/build/test on `windows-latest` only, and the build runs `ClientBuild` | 4965d3ad | `if (-not (Select-String -Path src/ChopItUp.Hub/client/package.json -Pattern '"test"' -Quiet)) { exit 0 } else { exit 1 }` |
| 23 | Measured (two Haiku probes, temp repos, this session): `claude -p --permission-mode dontAsk --tools Read,Edit,Write,Glob,Grep,Bash --allowedTools Read,Edit,Write,Glob,Grep,Bash --settings <json> --setting-sources "" --strict-mcp-config --no-session-persistence --model haiku --output-format stream-json --verbose --disable-slash-commands` with `{"permissions":{"deny":[...]}}`: `echo hello` and `git log --oneline -1` ran; `git commit --allow-empty -m probe` and `git -c user.name=x -c user.email=x@x commit --allow-empty -m probe` were denied (`Permission to use Bash with command ... has been denied.`; HEAD unchanged; both listed under the result's `permission_denials` with `tool_name`, `tool_use_id`, `tool_input.command`); `Write` inside cwd succeeded; `Read` of `C:\Windows\win.ini` succeeded despite `blockReadsOutsideWorkingDirectories: true` AND despite deny `Read(//C:/Windows/**)`; deny `Read(~/m9probe-decoy/**)` blocked a read under the profile (`File is in a directory that is denied by your permission settings.`); stream lines are `{"type":"system",...}`, `{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_...","name":"Bash","input":{"command":"...","description":"..."}}]}}`, `{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"...","is_error":true,"content":"..."}]}}`, and last `{"type":"result","subtype":"success","is_error":false,"num_turns":6,"result":"...","permission_denials":[...],"total_cost_usd":...}` | session probe (`scratchpad\probe-claude.ps1`, `probe-claude2.ps1`) | — |
| 24 | Measured (one gpt-5.4-mini probe, temp repo, this session): `codex exec --ephemeral --ignore-user-config --json -c sandbox_workspace_write.network_access=true --approve-for-me -C <dir> -m gpt-5.4-mini --color never -o last.txt -` exited 0 with no config error; the agent's `git commit --allow-empty -m probe` **succeeded** (HEAD moved) and its file write succeeded — the workspace-write `.git` protection did not hold on native Windows 0.153.3; every command ran as `"C:\Program Files\PowerShell\7\pwsh.exe" -Command <text>`; stdout lines are `{"type":"thread.started",...}`, `{"type":"turn.started"}`, `{"type":"item.started"\|"item.completed","item":{"id":"item_1","type":"command_execution","command":"...","aggregated_output":"...","exit_code":0,"status":"completed"}}`, `{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"..."}}`, `{"type":"turn.completed","usage":{...}}`; `-o last.txt` still received the final message | session probe (`scratchpad\probe-codex.ps1`) | — |
| 25 | Measured (git 2.45.2, temp repo): `git -c user.name="ChopItUp hub" -c user.email=hub@chopitup.local commit --author="Opus <opus@chopitup.local>"` → `%an <%ae>` = `Opus <opus@chopitup.local>`, `%cn <%ce>` = `ChopItUp hub <hub@chopitup.local>`; `commit --allow-empty` creates a commit; `git show --name-only --format= HEAD` prints one path per changed file and nothing for an empty commit; `rev-parse --show-toplevel` prints forward slashes (`C:/Users/...`) and the same root from a subfolder, exits 128 outside a repository; `status --porcelain` is empty when clean and `?? b.txt` when dirty; a multi-line `-m` body survives `log --format=%B` | session probe (`scratchpad\probe-git-paths.ps1`) | — |
| 26 | Measured (.NET 10 on this machine): `Path.GetFullPath(@"C:\")` = `C:\`; `Path.GetPathRoot(@"C:\foo\bar")` = `C:\`; `GetFullPath(@"C:\foo\..\")` = `C:\`; `GetFullPath` keeps a trailing separator; `IsPathRooted(@"\\server\share")` and `IsPathFullyQualified(@"\\server\share\x")` are both true (UNC needs its own check); `Environment.GetFolderPath(UserProfile)` = `C:\Users\cayov`; `SystemRoot` = `C:\Windows`, `ProgramFiles` = `C:\Program Files`, `ProgramFiles(x86)` = `C:\Program Files (x86)`, `ProgramData` = `C:\ProgramData` | session probe | — |
| 27 | `.gitignore` ignores `.claude/` (9), `.scratch/` (10), `.data/` (19), `data/` (20) | 4965d3ad | `if ((Select-String -Path .gitignore -Pattern '^\.scratch/$' -Quiet) -and (Select-String -Path .gitignore -Pattern '^data/$' -Quiet)) { exit 0 } else { exit 1 }` |
| 28 | `CLAUDE.md` is 3,775 bytes (target 4,096; ratcheted) — one ~110-byte line fits | 4965d3ad | `if ((Get-Item CLAUDE.md).Length -lt 3900) { exit 0 } else { exit 1 }` |
| 29 | `C:\Agent Zone\.claude\launch.json` has the `chopitup-hub` entry (line 37) that runs the repo hub on port 8795 for the browser gate (LESSONS M8) | session cwd | `Select-String -Path 'C:\Agent Zone\.claude\launch.json' -Pattern '"name": "chopitup-hub"' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 30 | `HubNotes.Post(MessageStore store, MessageSignal signal, string roomId, string text)` (16–21); the client refreshes the memory panel only on hub notes that `startsWith('Memory ')` (`App.tsx:116`) — trail notes start `Committed ` / `Not committed for `, so they never trigger it | 4965d3ad | `Select-String -Path src/ChopItUp.Hub/client/src/App.tsx -Pattern "message\.body\.startsWith\('Memory '\)" -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 31 | `ProcessSpec(FileName, Arguments, Environment, WorkingDirectory, StandardInput, Label)` (`ProcessRunner.cs:7-13`); `ProcessResult(ExitCode, TimedOut, Cancelled, StandardOutput, StandardError, Elapsed, ProcessId = 0)` (17); `IProcessRunner.RunAsync(ProcessSpec, TimeSpan, CancellationToken)` (21); `ResolvedCli(FileName, LeadingArguments, ResolvedPath)` (`CliResolver.cs:8`); `CliLocator` delegate (14); `CliResolver.Resolve(name)` throws `FileNotFoundException` when absent | 4965d3ad | `Select-String -Path src/ChopItUp.Hub/Spawning/ProcessRunner.cs -Pattern 'public sealed record ProcessResult\(int\? ExitCode, bool TimedOut, bool Cancelled, string StandardOutput, string StandardError, TimeSpan Elapsed, int ProcessId = 0\);' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 32 | `ExchangePolicyTests` builds `Policy()` from `ChopDb.SeedRoster` with `Limits` (Debounce 2 s, MinSpacing 10 s) and calls `Due`/`NextWake` with `NoStarts` and `Nobody` (lines 9–14) — a trailing optional `exclusive` parameter leaves every call compiling | 4965d3ad | `Select-String -Path tests/ChopItUp.Hub.Tests/Spawning/ExchangePolicyTests.cs -Pattern 'private static readonly HashSet<string> Nobody = new\(\);' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 33 | `README.md` has sections `## Spawning (M5)` (77) and `## Memory (M10)` (108) — the M9 section goes after the memory one; `CLAUDE.md` `## Deploy` ends with the memory-check line | 4965d3ad | `Select-String -Path README.md -Pattern '^## Memory \(M10\)$' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 34 | `TestDirs.DeleteTree` clears attributes before a recursive delete (git objects are read-only) — every test that makes a repository disposes through it | 4965d3ad | `Select-String -Path tests/ChopItUp.Hub.Tests/TestDirs.cs -Pattern 'File\.SetAttributes\(f, FileAttributes\.Normal\);' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 35 | Probe 3 (2026-09-06, Haiku, 2.1.220, throwaway repo under `%TEMP%`, after critique pass 1): `Write(~/m9probe-decoy/**)` in `--settings` denied a `Write` there ("File is in a directory that is denied by your permission settings.") while a `Write` into the cwd succeeded; `Bash(git.exe *)` denied `git.exe commit --allow-empty`; the leading-wildcard `Bash(*git.exe *)` did **not** match `"C:/Program Files/Git/mingw64/bin/git.exe" commit --allow-empty` — it ran and HEAD moved; `git log` ran; `--append-system-prompt <prompt>` is listed in `claude --help` | session probe | `—` |

## Task table

Order is the dependency order; a linear chain top to bottom satisfies every edge. Builder pin per task class: `sonnet` for everything the owner does not look at, `opus` for Task 7.

| # | Task | Builder | Files | Blocked by |
|---|------|---------|-------|-----------|
| 1 | Schema v6 (`directory`, `archived_at`), `Room` fields, `RoomIds`, `MessageStore` room operations (create, get, list by recency with unread and archive filter, archive, bind), literal sweep, guard tests | sonnet | `src/ChopItUp.Core/Storage/ChopDb.cs`, `src/ChopItUp.Core/Model/Message.cs`, new `src/ChopItUp.Core/Storage/RoomIds.cs`, `src/ChopItUp.Core/Storage/MessageStore.cs`; `tests/ChopItUp.Core.Tests/Storage/SchemaMigrationTests.cs`, new `tests/ChopItUp.Core.Tests/Storage/RoomStoreTests.cs`; `tools/Invoke-M10MemoryCheck.ps1`, `tools/Invoke-M2DryRun.ps1`, `tools/Invoke-M4SelfCheck.ps1`, `tools/Invoke-M5SpawnCheck.ps1` | — |
| 2 | `SpawnOutput` (Claude stream-json + Codex JSONL shell-command extraction, JSONL-aware `ClaudeFinalText`), `SpawnCommands` directory variants + per-spawn `settings.json`, deny list, credential folders | sonnet | new `src/ChopItUp.Hub/Spawning/SpawnOutput.cs`, `src/ChopItUp.Hub/Spawning/SpawnCommands.cs`, `src/ChopItUp.Hub/Spawning/SpawnPrompt.cs` (`DirectoryRules` only); new `tests/ChopItUp.Hub.Tests/Spawning/SpawnOutputTests.cs`, `tests/ChopItUp.Hub.Tests/Spawning/SpawnCommandsTests.cs` | — |
| 3 | `RoomPaths` + `RoomPathRules` (refused roots, normalisation, link targets), `HubOptions.RoomsRoot` (`--rooms-root`, `CHOPITUP_ROOMS`) | sonnet | new `src/ChopItUp.Hub/Rooms/RoomPaths.cs`, `src/ChopItUp.Hub/Hosting/HubOptions.cs`; new `tests/ChopItUp.Hub.Tests/Rooms/RoomPathsTests.cs`, `tests/ChopItUp.Hub.Tests/HubHostTests.cs` (the existing `HubOptions.Parse` facts live there, lines 68–91) | 2 |
| 4 | `GitTrail` (generalised from `MemoryGit`: author/committer, allow-empty, stdin message, head, dirty, toplevel, log), `MemoryGit` as a subclass with its surface intact, `RoomTrails` registry | sonnet | new `src/ChopItUp.Hub/Git/GitTrail.cs`, `src/ChopItUp.Hub/Memory/MemoryGit.cs`, new `src/ChopItUp.Hub/Rooms/RoomTrails.cs`; new `tests/ChopItUp.Hub.Tests/Git/GitTrailTests.cs` (existing `MemoryGitTests` unchanged and green) | 3 |
| 5 | `RoomDirectories` (validate, create, init, overlap, repo-root check), `RoomsApi` (create, archive, unarchive, bind, read, trail), `ChatApi.GetRooms` fields + `?archived`, `list_rooms` `directory`, `HubHost`/`HubTestHost` wiring (`roomGit` factory, rooms root) | sonnet | new `src/ChopItUp.Hub/Rooms/RoomDirectories.cs`, new `src/ChopItUp.Hub/Web/RoomsApi.cs`, `src/ChopItUp.Hub/Web/ChatApi.cs`, `src/ChopItUp.Hub/Mcp/RoomTools.cs`, `src/ChopItUp.Hub/Hosting/HubHost.cs`; `tests/ChopItUp.Hub.Tests/HubTestHost.cs`, new `tests/ChopItUp.Hub.Tests/RoomsApiTests.cs`, `tests/ChopItUp.Hub.Tests/RoomToolsTests.cs` | 1, 3, 4 |
| 6 | Spawner: exclusive directory rooms in `ExchangePolicy`, prompt `Files:` section, directory command lines + scratch/cwd split, owner pre-commit, agent post-commit with shell log, HEAD-moved detection, trail notes, `RoomCommits` message builder, `HubNotes` trail texts | sonnet | `src/ChopItUp.Hub/Spawning/ExchangePolicy.cs`, `src/ChopItUp.Hub/Spawning/SpawnPrompt.cs`, `src/ChopItUp.Hub/Spawning/SpawnerService.cs`, new `src/ChopItUp.Hub/Spawning/RoomCommits.cs`, `src/ChopItUp.Hub/Memory/HubNotes.cs`; `tests/ChopItUp.Hub.Tests/Spawning/ExchangePolicyTests.cs`, `tests/ChopItUp.Hub.Tests/Spawning/SpawnPromptTests.cs`, new `tests/ChopItUp.Hub.Tests/Spawning/RoomCommitsTests.cs`, new `tests/ChopItUp.Hub.Tests/Spawning/SpawnerServiceTests.Rooms.cs` | 1, 2, 4, 5 |
| 7 | Web UI: rail as chat list (recency, unread badge, New room, Show archived), `NewRoomDialog` (create + bind modes), header directory + Archive + Trail, `TrailDialog`, read-cursor advance, wiring, styles | **opus** | `client/src/types.ts`, `client/src/api.ts`, `client/src/RoomRail.tsx`, `client/src/RoomHeader.tsx`, new `client/src/NewRoomDialog.tsx`, new `client/src/TrailDialog.tsx`, `client/src/App.tsx`, `client/src/styles.css` | 5 |
| 8 | Live check `Invoke-M9RoomCheck.ps1`, README `## Rooms (M9)` section, `CLAUDE.md` one line, row 13 Notes measurement | sonnet | new `tools/Invoke-M9RoomCheck.ps1`; `README.md`; `CLAUDE.md`; `ROADMAP.md` (row 13 Notes only) | 6, 7 |

Edges: 3←2 (the credential-folder list lives in `SpawnCommands`); 4←3 (`RoomTrails.For` normalises with `RoomPaths`); 5←1,3,4; 6←1,2,4,5; 7←5; 8←6,7. Tasks 1 and 2 are independent of everything else; the build runs the table in order (sequential, synchronous dispatch).

## Task 1 — Schema v6, `Room` fields, `RoomIds`, `MessageStore` room operations, literal sweep (builder: sonnet)

Ticket: `.scratch/m9-rooms-as-chats/issues/01-schema-and-store.md`. Acceptance: A1, the store half of A2–A5.

### `src/ChopItUp.Core/Model/Message.cs` — replace line 10

```csharp
/// <summary>A room (M9: a conversation with a directory). <see cref="Directory"/> is the normalised
/// path of its git working tree, or null for a room made before M9 that the owner has not bound;
/// <see cref="ArchivedAt"/> hides the room without touching disk; <see cref="LastActivityAt"/> is the
/// newest message's time, or the room's creation when it has none — the chat-list order;
/// <see cref="Unread"/> counts messages past the cursor of whoever asked (0 when nobody did).</summary>
public sealed record Room(string Id, string Name, DateTimeOffset CreatedAt, long LastMessageId, int MessageCount,
    string? Directory = null, DateTimeOffset? ArchivedAt = null, DateTimeOffset? LastActivityAt = null, long Unread = 0);
```

Every existing `new Room(id, name, createdAt, lastId, count)` keeps compiling (the only one is in `MessageStore.ListRooms`, replaced below).

### `src/ChopItUp.Core/Storage/RoomIds.cs` (new)

```csharp
using System.Text.RegularExpressions;

namespace ChopItUp.Core.Storage;

/// <summary>Room ids are slugs of the owner's title (M9 plan decision 13): they are MCP <c>room_id</c>
/// values, URL segments and, for a hub-created directory, folder names — so lowercase ASCII, capped,
/// unique, and never a Windows reserved device name.</summary>
public static class RoomIds
{
    public const int MaxChars = 40;
    private static readonly Regex NotSlug = new("[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    public static string Slug(string name)
    {
        var s = NotSlug.Replace((name ?? "").Trim().ToLowerInvariant(), "-").Trim('-');
        if (s.Length > MaxChars) s = s[..MaxChars].TrimEnd('-');
        if (s.Length == 0) return "room";
        return Reserved.Contains(s) ? s + "-room" : s;
    }

    /// <summary>The slug, or the first of <c>slug-2</c>, <c>slug-3</c>, … that <paramref name="taken"/> does not claim.</summary>
    public static string Unique(string name, Func<string, bool> taken)
    {
        var slug = Slug(name);
        if (!taken(slug)) return slug;
        for (int n = 2; ; n++)
        {
            var candidate = $"{slug}-{n}";
            if (!taken(candidate)) return candidate;
        }
    }
}
```

### `src/ChopItUp.Core/Storage/ChopDb.cs` — three edits

1. Line 10: `public const int LatestSchemaVersion = 6;`
2. After line 104 (`if (GetUserVersion(conn) < 5) ApplyV5(conn);`) add: `if (GetUserVersion(conn) < 6) ApplyV6(conn);`
3. After `ApplyV5` (ends line 386) add:

```csharp
    /// <summary>v6 (M9): a room can carry a directory (its git working tree) and an archive stamp. Two
    /// nullable columns, each probed before its ALTER so a torn v6 — columns present, stamp still 5 —
    /// re-runs safely (the v3 shape), stamped last in the same transaction (LESSONS M1).</summary>
    private static void ApplyV6(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        var ddl = new System.Text.StringBuilder();
        foreach (var column in new[] { "directory", "archived_at" })
        {
            using var probe = conn.CreateCommand();
            probe.Transaction = tx;
            probe.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('rooms') WHERE name = '{column}'";
            if (Convert.ToInt64(probe.ExecuteScalar()) == 0)
                ddl.Append($"ALTER TABLE rooms ADD COLUMN {column} TEXT;\n");
        }
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = ddl + "PRAGMA user_version = 6;";
        cmd.ExecuteNonQuery();
        tx.Commit();
    }
```

The v1 `rooms` DDL (lines 243–247) is untouched: a fresh database reaches v6 through the same ALTERs a migrated one does, so both end identical.

### `src/ChopItUp.Core/Storage/MessageStore.cs` — replace `ListRooms` (lines 11–25) with the block below; leave everything else

```csharp
    // One shape for every room read: the aggregate columns, then the two M9 columns, then activity and
    // unread. {0} is the WHERE; $p (the asker, or NULL) is bound by every caller.
    private const string RoomSelect = """
        SELECT r.id, r.name, r.created_at, COALESCE(MAX(m.id), 0), COUNT(m.id), r.directory, r.archived_at,
               COALESCE(MAX(m.created_at), r.created_at),
               CASE WHEN $p IS NULL THEN 0 ELSE (
                   SELECT COUNT(*) FROM messages u
                   WHERE u.room_id = r.id
                     AND u.id > COALESCE((SELECT c.last_read_id FROM read_cursors c WHERE c.participant_id = $p AND c.room_id = r.id), 0)) END
        FROM rooms r LEFT JOIN messages m ON m.room_id = r.id
        WHERE {0}
        GROUP BY r.id
        ORDER BY COALESCE(MAX(m.created_at), r.created_at) DESC, r.id
        """;

    /// <summary>Rooms newest activity first (the newest message's time, or the room's creation when it
    /// has none — every stamp is UTC round-trip text, so the text order is the time order), archived
    /// rooms excluded unless asked for (M9 decision 14). <paramref name="unreadFor"/> fills
    /// <see cref="Room.Unread"/> from that participant's cursor; null leaves it 0.</summary>
    public IReadOnlyList<Room> ListRooms(bool includeArchived = false, string? unreadFor = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = string.Format(RoomSelect, "($all = 1 OR r.archived_at IS NULL)");
        cmd.Parameters.AddWithValue("$p", (object?)unreadFor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$all", includeArchived ? 1 : 0);
        using var reader = cmd.ExecuteReader();
        var rooms = new List<Room>();
        while (reader.Read()) rooms.Add(ReadRoom(reader));
        return rooms;
    }

    /// <summary>One room by id, archived or not; null when unknown.</summary>
    public Room? GetRoom(string roomId, string? unreadFor = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = string.Format(RoomSelect, "r.id = $id");
        cmd.Parameters.AddWithValue("$id", roomId);
        cmd.Parameters.AddWithValue("$p", (object?)unreadFor ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRoom(reader) : null;
    }

    private static Room ReadRoom(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), Timestamps.Parse(r.GetString(2)), r.GetInt64(3), r.GetInt32(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : Timestamps.Parse(r.GetString(6)),
        Timestamps.Parse(r.GetString(7)),
        r.GetInt64(8));

    /// <summary>Inserts a room. The id is the caller's (<see cref="RoomIds"/>); the primary key is the
    /// arbiter for a duplicate, surfaced as <see cref="ArgumentException"/>. <paramref name="directory"/>
    /// is stored as given: the hub validates and normalises it before it gets here.</summary>
    public Room CreateRoom(string id, string name, string? directory)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Room id is empty.", nameof(id));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Room name is empty.", nameof(name));
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO rooms (id, name, created_at, directory) VALUES ($id, $name, $at, $dir)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name.Trim());
        cmd.Parameters.AddWithValue("$at", Timestamps.Stamp(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$dir", (object?)directory ?? DBNull.Value);
        try { cmd.ExecuteNonQuery(); }
        catch (SqliteException e) when (e.SqliteExtendedErrorCode == 1555)   // SQLITE_CONSTRAINT_PRIMARYKEY
        {
            throw new ArgumentException($"Room '{id}' already exists.", nameof(id), e);
        }
        return GetRoom(id)!;
    }

    /// <summary>Archive (a stamp) or unarchive (null). False for an unknown room. Never touches disk.</summary>
    public bool SetArchived(string roomId, DateTimeOffset? at)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE rooms SET archived_at = $at WHERE id = $id";
        cmd.Parameters.AddWithValue("$at", at is null ? DBNull.Value : (object)Timestamps.Stamp(at.Value));
        cmd.Parameters.AddWithValue("$id", roomId);
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>Binds a directory to a room that has none (M9 decision 1). False when the room is
    /// unknown or already bound — the WHERE is the arbiter, so two racing binds cannot both win.</summary>
    public bool BindDirectory(string roomId, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Directory is empty.", nameof(directory));
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE rooms SET directory = $dir WHERE id = $id AND directory IS NULL";
        cmd.Parameters.AddWithValue("$dir", directory);
        cmd.Parameters.AddWithValue("$id", roomId);
        return cmd.ExecuteNonQuery() == 1;
    }
```

`RoomExists` (27–34) stays: an archived room exists, so posting into it still works (decision 14).

### Literal sweep (claim 3)

- `tests/ChopItUp.Core.Tests/Storage/SchemaMigrationTests.cs` lines 187, 210, 221, 246: `Assert.Equal(5, db.GetSchemaVersion())` → `Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion())` (the form the newer tests already use).
- `tools/Invoke-M10MemoryCheck.ps1:71`, `tools/Invoke-M2DryRun.ps1:146`, `tools/Invoke-M4SelfCheck.ps1:321`, `tools/Invoke-M5SpawnCheck.ps1:62`: `schema -eq 5` → `schema -eq 6`; in the two scripts whose check is named `hub.health-schema-5`, rename it `hub.health-schema-6`.

### `tests/ChopItUp.Core.Tests/Storage/SchemaMigrationTests.cs` — add after `WriteRawV4()` (ends line 177)

```csharp
    private void WriteRawV5()
    {
        // v4 shape plus exactly what ApplyV5 adds: the proposals table and its index. Raw SQL on
        // purpose (LESSONS M2): this must keep describing v5 after ChopDb can no longer produce one.
        Directory.CreateDirectory(_dir);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        conn.Open();
        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE participants (id TEXT PRIMARY KEY, display_name TEXT NOT NULL, kind TEXT NOT NULL, host TEXT, model TEXT, note TEXT);
            CREATE TABLE rooms (id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, room_id TEXT NOT NULL REFERENCES rooms(id),
                author_id TEXT NOT NULL REFERENCES participants(id), body TEXT NOT NULL, created_at TEXT NOT NULL,
                client_key TEXT);
            CREATE INDEX ix_messages_room_id ON messages(room_id, id);
            CREATE UNIQUE INDEX ux_messages_client_key ON messages(room_id, author_id, client_key) WHERE client_key IS NOT NULL;
            CREATE TABLE read_cursors (participant_id TEXT NOT NULL REFERENCES participants(id),
                room_id TEXT NOT NULL REFERENCES rooms(id), last_read_id INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (participant_id, room_id));
            CREATE TABLE memory_proposals (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                room_id     TEXT NOT NULL REFERENCES rooms(id),
                author_id   TEXT NOT NULL REFERENCES participants(id),
                topic       TEXT NOT NULL,
                title       TEXT NOT NULL,
                body        TEXT NOT NULL,
                status      TEXT NOT NULL DEFAULT 'pending',
                source      TEXT,
                created_at  TEXT NOT NULL,
                decided_at  TEXT,
                written_to  TEXT,
                commit_hash TEXT
            );
            CREATE INDEX ix_memory_proposals_status ON memory_proposals(status, room_id, id);
            INSERT INTO participants (id, display_name, kind, host, model, note) VALUES
                ('owner','Owner','human','human',NULL,NULL),
                ('claude','Claude','model','claude',NULL,'App-backed: Claude Desktop or Claude Code, whatever model the app has selected.'),
                ('codex','Codex','model','codex',NULL,'App-backed: the Codex app or CLI, whatever model the app has selected.'),
                ('opus','Opus','model','claude','opus',NULL),
                ('sonnet','Sonnet','model','claude','sonnet',NULL),
                ('fable','Fable','model','claude','fable','May bill to usage credits instead of the plan''s included limits.'),
                ('gpt-6-astra','GPT-6 Astra','model','codex','gpt-6-astra',NULL),
                ('gpt-5.6-sol','GPT-5.6 Sol','model','codex','gpt-5.6-sol',NULL),
                ('gpt-5.6-terra','GPT-5.6 Terra','model','codex','gpt-5.6-terra',NULL),
                ('gpt-5.6-luna','GPT-5.6 Luna','model','codex','gpt-5.6-luna',NULL),
                ('gpt-5.5','GPT-5.5','model','codex','gpt-5.5',NULL),
                ('gpt-5.4-mini','GPT-5.4 Mini','model','codex','gpt-5.4-mini',NULL),
                ('hub','Hub','system','hub',NULL,'The hub itself. Posts exchange notes: timeouts, budget, conclusions. Cannot be mentioned or spawned.');
            INSERT INTO rooms (id, name, created_at) VALUES ('general','General','2026-09-01T10:00:00.000+00:00');
            INSERT INTO messages (id, room_id, author_id, body, created_at, client_key) VALUES
                (1,'general','owner','@opus first v3 message','2026-09-01T10:01:00.000+00:00',NULL),
                (2,'general','opus','second v3 message','2026-09-01T10:02:00.000+00:00','k-1');
            INSERT INTO read_cursors (participant_id, room_id, last_read_id) VALUES ('opus','general',2);
            INSERT INTO memory_proposals (room_id, author_id, topic, title, body, status, created_at) VALUES
                ('general','opus','user','Likes tests','Yes.','pending','2026-09-01T10:03:00.000+00:00');
            PRAGMA user_version = 5;
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void M9_A1_v5_database_is_backed_up_then_migrated_to_v6_with_two_nullable_room_columns_and_nothing_else_changed()
    {
        WriteRawV5();

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(6, db.GetSchemaVersion());
        Assert.NotNull(db.LastBackupPath);
        Assert.Contains(".v5.", Path.GetFileName(db.LastBackupPath!));

        Assert.Equal(ChopDb.SeedRoster.Select(p => p.Id), new ParticipantStore(db).List().Select(p => p.Id));

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT body FROM messages WHERE id = 1";
        Assert.Equal("@opus first v3 message", (string)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT last_read_id FROM read_cursors WHERE participant_id = 'opus' AND room_id = 'general'";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT COUNT(*) FROM memory_proposals WHERE status = 'pending'";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('rooms')";
        Assert.Equal(5L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT COUNT(*) FROM rooms WHERE id = 'general' AND directory IS NULL AND archived_at IS NULL";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);

        var general = new MessageStore(db).GetRoom("general");
        Assert.NotNull(general);
        Assert.Null(general!.Directory);
        Assert.Null(general.ArchivedAt);
        Assert.Equal(2, general.MessageCount);

        db.EnsureDatabase();
        Assert.Null(db.LastBackupPath);
        Assert.Equal(6, db.GetSchemaVersion());
    }

    [Fact]
    public void M9_A1_torn_v6_with_both_columns_present_but_stamp_5_is_repaired_not_crashed()
    {
        WriteRawV5();
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE rooms ADD COLUMN directory TEXT; ALTER TABLE rooms ADD COLUMN archived_at TEXT;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(6, db.GetSchemaVersion());
        using var check = db.Open();
        using var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM pragma_table_info('rooms')";
        Assert.Equal(5L, (long)count.ExecuteScalar()!);
    }
```

### `tests/ChopItUp.Core.Tests/Storage/RoomStoreTests.cs` (new)

```csharp
using ChopItUp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Core.Tests.Storage;

public sealed class RoomStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_rooms_" + Guid.NewGuid().ToString("N"));
    private readonly ChopDb _db;
    private readonly MessageStore _store;

    public RoomStoreTests()
    {
        _db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        _db.EnsureDatabase();
        _store = new MessageStore(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void M9_A2_A3_create_stores_the_directory_and_rooms_list_newest_activity_first()
    {
        Thread.Sleep(2);
        var lab = _store.CreateRoom("lab", "  Lab  ", @"C:\Projects\lab");
        Thread.Sleep(2);
        var notes = _store.CreateRoom("notes", "Notes", null);

        Assert.Equal(("lab", "Lab", @"C:\Projects\lab", 0, 0L), (lab.Id, lab.Name, lab.Directory, lab.MessageCount, lab.Unread));
        Assert.Null(lab.ArchivedAt);
        Assert.Equal(lab.CreatedAt, lab.LastActivityAt);
        Assert.Null(notes.Directory);

        Assert.Equal(["notes", "lab", "general"], _store.ListRooms().Select(r => r.Id));   // nothing posted: creation order, newest first

        Thread.Sleep(2);
        var posted = _store.Post("lab", "owner", "hello");
        var rooms = _store.ListRooms();
        Assert.Equal(["lab", "notes", "general"], rooms.Select(r => r.Id));
        Assert.Equal(posted.CreatedAt, rooms[0].LastActivityAt);
        Assert.Equal(1, rooms[0].MessageCount);
        Assert.Equal(posted.Id, rooms[0].LastMessageId);
    }

    [Fact]
    public void M9_A3_A4_archived_rooms_are_hidden_unless_asked_for_and_unarchive_restores()
    {
        _store.CreateRoom("lab", "Lab", @"C:\Projects\lab");
        var at = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

        Assert.True(_store.SetArchived("lab", at));
        Assert.Equal(["general"], _store.ListRooms().Select(r => r.Id));
        var all = _store.ListRooms(includeArchived: true);
        Assert.Contains(all, r => r.Id == "lab" && r.ArchivedAt == at);
        Assert.Equal(at, _store.GetRoom("lab")!.ArchivedAt);    // GetRoom never filters
        Assert.True(_store.RoomExists("lab"));                    // archive hides, never blocks

        Assert.True(_store.SetArchived("lab", null));
        Assert.Null(_store.GetRoom("lab")!.ArchivedAt);
        Assert.Equal(2, _store.ListRooms().Count);
        Assert.False(_store.SetArchived("nope", at));
    }

    [Fact]
    public void M9_A5_unread_counts_past_the_askers_cursor_and_the_askers_own_post_clears_it()
    {
        _store.Post("general", "owner", "@opus hi");
        var first = _store.Post("general", "opus", "one");
        _store.Post("general", "opus", "two");

        Assert.Equal(2, _store.ListRooms(unreadFor: "owner").Single().Unread);
        Assert.Equal(0, _store.ListRooms().Single().Unread);                    // nobody asked
        Assert.Equal(2, _store.GetRoom("general", "owner")!.Unread);

        _store.SetCursor("owner", "general", first.Id);
        Assert.Equal(1, _store.ListRooms(unreadFor: "owner").Single().Unread);

        _store.Post("general", "owner", "seen");                                  // Post advances the author's own cursor (claim 5)
        Assert.Equal(0, _store.ListRooms(unreadFor: "owner").Single().Unread);
    }

    [Fact]
    public void M9_A2_A5_duplicate_id_is_refused_and_bind_only_fills_a_null()
    {
        _store.CreateRoom("lab", "Lab", @"C:\Projects\lab");
        var e = Assert.Throws<ArgumentException>(() => _store.CreateRoom("lab", "Lab again", null));
        Assert.Contains("already exists", e.Message);

        Assert.Null(_store.GetRoom("general")!.Directory);
        Assert.True(_store.BindDirectory("general", @"C:\Projects\general"));
        Assert.Equal(@"C:\Projects\general", _store.GetRoom("general")!.Directory);
        Assert.False(_store.BindDirectory("general", @"C:\Elsewhere"));          // already bound
        Assert.Equal(@"C:\Projects\general", _store.GetRoom("general")!.Directory);
        Assert.False(_store.BindDirectory("nope", @"C:\Projects\x"));
        Assert.Throws<ArgumentException>(() => _store.BindDirectory("lab", " "));
    }

    [Theory]
    [InlineData("General", "general")]
    [InlineData("  Résumé Review!  ", "r-sum-review")]
    [InlineData("CON", "con-room")]
    [InlineData("lpt1", "lpt1-room")]
    [InlineData("", "room")]
    [InlineData("--x--", "x")]
    [InlineData("aaaaaaaaaabbbbbbbbbbccccccccccddddddddddeeeeeeeeee", "aaaaaaaaaabbbbbbbbbbccccccccccdddddddddd")]
    public void M9_A2_slugs_are_lowercase_ascii_capped_and_never_a_reserved_device_name(string name, string expected) =>
        Assert.Equal(expected, RoomIds.Slug(name));

    [Fact]
    public void M9_A2_unique_appends_a_counter_until_the_id_is_free()
    {
        var taken = new HashSet<string> { "general", "general-2" };
        Assert.Equal("general-3", RoomIds.Unique("General", taken.Contains));
        Assert.Equal("lab", RoomIds.Unique("Lab", taken.Contains));
    }
}
```

Expected: build 0 warnings; Core tests 76 + 2 (migration) + 5 facts + 7 theory cases = **90** cases; Hub tests unchanged at 169 (the four `schema -eq` scripts are not tests). The builder reports the real numbers.

## Task 2 — `SpawnOutput` parsers, `SpawnCommands` directory variants, per-spawn settings (builder: sonnet)

Ticket: `.scratch/m9-rooms-as-chats/issues/02-spawn-output-and-commands.md`. Acceptance: A6 (the command-line half), A9 (the shell-log half). Pure code, no hub wiring: the spawner picks these up in Task 6.

### `src/ChopItUp.Hub/Spawning/SpawnPrompt.cs` — one addition (the rest of the file changes in Task 6)

```csharp
    /// <summary>The fence for a spawn in a directory room (M9 decision 8, F10): sent to Claude as an
    /// appended system prompt — a channel the room transcript on stdin cannot write into — and repeated
    /// in the stdin prompt's Files section for both CLIs. A rule, not a wall: the plan says which parts
    /// are also enforced (git verbs, credential folders) and which are not (reads, the loopback API).</summary>
    public static string DirectoryRules(string directory) =>
        $"Stay inside your working directory, {directory}: do not read, list, create or change anything outside this directory, and do not touch its .git folder. "
        + "Do not run git commands that write (commit, add, checkout, reset, stash, push and the like); the hub commits your work under your name when you finish and records every shell command you run in the room's commit trail. git log, git status and git diff are fine. "
        + "Do not call the hub's HTTP API or read its data folder; the chopitup MCP tools you were given are your only channel to the hub.";
```

### `src/ChopItUp.Hub/Spawning/SpawnOutput.cs` (new)

```csharp
using System.Text.Json;

namespace ChopItUp.Hub.Spawning;

public sealed record ShellCommand(string Command, bool Denied);

/// <summary>Reads the two CLIs' structured stdout for the one thing the trail needs (M9 decision 6):
/// which shell commands the model ran. Shapes measured 2026-09-06 (plan claims 23, 24): Claude
/// `--output-format stream-json` emits one JSON object per line, Bash calls as
/// <c>{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_…","name":"Bash","input":{"command":"…"}}]}}</c>
/// and a final <c>{"type":"result",…,"result":"…","permission_denials":[{"tool_use_id":"…",…}]}</c>;
/// Codex `--json` emits <c>{"type":"item.started"|"item.completed","item":{"id":"item_1","type":"command_execution","command":"…",…}}</c>.
/// Lenient by design: a line that is not JSON, or JSON of another shape, contributes nothing and
/// nothing here throws — the commit still happens, with an empty log.</summary>
public static class SpawnOutput
{
    public const int MaxCommands = 50;
    public const int MaxCommandChars = 400;

    /// <summary>Claude: every Bash tool_use in assistant messages, in order; one the final result lists
    /// under permission_denials (by tool_use_id) is marked denied — it never ran.</summary>
    public static IReadOnlyList<ShellCommand> ClaudeShellCommands(string stdout)
    {
        var found = new List<(string Id, string Command)>();
        var denied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in Lines(stdout))
        {
            var type = Str(root, "type");
            if (type == "assistant"
                && root.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (Str(block, "type") != "tool_use" || Str(block, "name") != "Bash") continue;
                    if (!block.TryGetProperty("input", out var input) || Str(input, "command") is not { } command) continue;
                    found.Add((Str(block, "id") ?? "", command));
                }
            }
            else if (type == "result"
                && root.TryGetProperty("permission_denials", out var denials)
                && denials.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in denials.EnumerateArray())
                    if (Str(d, "tool_use_id") is { Length: > 0 } id) denied.Add(id);
            }
        }
        return found
            .Select(f => new ShellCommand(Flatten(f.Command), f.Id.Length > 0 && denied.Contains(f.Id)))
            .Take(MaxCommands)
            .ToList();
    }

    /// <summary>Codex: every command_execution item in first-seen order, the completed form replacing
    /// the started one (an item that only ever started is still listed — it was launched).</summary>
    public static IReadOnlyList<ShellCommand> CodexShellCommands(string stdout)
    {
        var order = new List<string>();
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);
        int anonymous = 0;
        foreach (var root in Lines(stdout))
        {
            if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) continue;
            if (Str(item, "type") != "command_execution" || Str(item, "command") is not { } command) continue;
            var id = Str(item, "id") ?? $"anon-{anonymous++}";
            if (!byId.ContainsKey(id)) order.Add(id);
            byId[id] = command;
        }
        return order.Select(id => new ShellCommand(Flatten(byId[id]), false)).Take(MaxCommands).ToList();
    }

    /// <summary>The model's final text from Claude stdout: the <c>result</c> string of a single JSON
    /// object (`--output-format json`, M5) or of the last <c>type: result</c> line of a stream
    /// (`stream-json`, M9). Null when neither is there.</summary>
    public static string? ClaudeFinalText(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            return ResultOf(doc.RootElement);
        }
        catch (JsonException) { }
        string? last = null;
        foreach (var root in Lines(stdout))
            if (Str(root, "type") == "result") last = ResultOf(root) ?? last;
        return last;
    }

    /// <summary>One line, at most <see cref="MaxCommandChars"/> characters; a line break becomes ` ⏎ `.</summary>
    public static string Flatten(string command)
    {
        var one = command.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", " ⏎ ").Trim();
        if (one.Length <= MaxCommandChars) return one;
        var cut = MaxCommandChars;
        if (char.IsHighSurrogate(one[cut - 1])) cut--;
        return one[..cut] + "…";
    }

    private static string? ResultOf(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()?.Trim() is { Length: > 0 } s ? s : null
            : null;

    private static IEnumerable<JsonElement> Lines(string stdout)
    {
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object) yield return doc.RootElement.Clone();
            }
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
```

### `src/ChopItUp.Hub/Spawning/SpawnCommands.cs` — edits

1. Replace `ClaudeFinalText` (lines 57–71) with a forwarder, so M5 callers and `SpawnCommandsTests.Final_text_…` are untouched:

```csharp
    /// <summary>The model's final text from a `--output-format json` or `stream-json` run (see
    /// <see cref="SpawnOutput.ClaudeFinalText"/>); null when stdout is neither.</summary>
    public static string? ClaudeFinalText(string stdout) => SpawnOutput.ClaudeFinalText(stdout);
```

2. Leave `Claude(...)`, `ClaudeMcpConfigJson`, `Codex(...)`, `CodexFinalText` exactly as they are (a `NULL`-directory room launches them byte for byte — decision 1, claim 14).

3. After `ClaudeToolAllowed` (line 16) add:

```csharp
    /// <summary>M9 decision 9: the built-in tools a spawn in a directory room gets, and the allow list
    /// that pre-approves them beside the three MCP tools. `dontAsk` denies anything not on the list.</summary>
    public const string ClaudeBuiltins = "Read,Edit,Write,Glob,Grep,Bash";
    public const string ClaudeDirectoryToolsAllowed = ClaudeBuiltins + "," + ClaudeToolAllowed;

    /// <summary>Profile folders that hold credentials — refused as room directories (RoomPathRules) and
    /// denied to Claude's file tools through the settings deny list (M9 decisions 3, 8). `~/` patterns
    /// are the one path form measured to bind on 2.1.220 (claim 23).</summary>
    public static readonly string[] CredentialFolders = [".claude", ".codex", ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker"];

    /// <summary>Every git subcommand that writes the index, the tree, refs, config or remotes.
    /// `Bash(git -*)` (below) covers any invocation that opens with an option (`-c`, `-C`, `--git-dir`,
    /// `--work-tree`), which is the form that reaches these verbs sideways. Read-only verbs (log, status,
    /// diff, show, blame, grep, ls-files, rev-parse) stay allowed: D11 says read-only, not off.</summary>
    public static readonly string[] GitWriteVerbs =
    [
        "add", "am", "apply", "bisect", "branch", "checkout", "cherry-pick", "clean", "clone", "commit", "config",
        "fast-import", "fetch", "filter-branch", "gc", "init", "merge", "mv", "notes", "prune", "pull", "push",
        "rebase", "reflog", "remote", "repack", "replace", "reset", "restore", "revert", "rm", "stash",
        "submodule", "switch", "symbolic-ref", "tag", "update-index", "update-ref", "worktree", "write-tree",
    ];

    /// <summary>The deny list of the per-spawn settings file: measured on 2.1.220 to block `git commit`,
    /// `git -c … commit` and `git.exe commit` while `echo`, `git log` and in-room writes ran, and to block
    /// a Write under a denied `~/` folder while a cwd Write succeeded (claims 23, 35). Deny rules apply in
    /// every permission mode and are prefix rules: an absolute-path `git.exe` is NOT caught (measured) and
    /// is left to the trail. There is deliberately no allow list here (the command line carries it) and
    /// no read fence (measured ineffective on this version — a rule in the prompt instead, decision 8).</summary>
    public static IReadOnlyList<string> ClaudeDenyRules()
    {
        var rules = new List<string>();
        foreach (var verb in GitWriteVerbs)
        {
            rules.Add($"Bash(git {verb} *)");
            rules.Add($"Bash(git {verb})");
        }
        rules.Add("Bash(git -*)");
        rules.Add("Bash(git.exe *)");
        rules.Add("Bash(git.exe)");
        rules.Add("Edit(.git/**)");
        rules.Add("Write(.git/**)");
        foreach (var folder in CredentialFolders)
        {
            rules.Add($"Read(~/{folder}/**)");
            rules.Add($"Write(~/{folder}/**)");
            rules.Add($"Edit(~/{folder}/**)");
        }
        return rules;
    }

    public static string ClaudeSettingsJson() =>
        JsonSerializer.Serialize(new { permissions = new { deny = ClaudeDenyRules() } });

    /// <summary>A spawn in a directory room (M9 decision 9): cwd is the room's tree; `dontAsk` plus the
    /// allow list runs the six built-ins and the three MCP tools without a prompt and auto-denies
    /// everything else (protected-path writes included — under `bypassPermissions` they would be
    /// auto-approved); the deny list rides in <paramref name="settingsPath"/>, which sits in the scratch
    /// folder beside <paramref name="mcpConfigPath"/>, never in the room; `stream-json` + `--verbose` is
    /// what carries the Bash calls the trail records; <paramref name="systemRules"/> (`SpawnPrompt.DirectoryRules`)
    /// rides as an appended system prompt (F10) so the fence is not only in the transcript channel.</summary>
    public static ProcessSpec ClaudeInDirectory(ResolvedCli cli, string model, string mcpConfigPath, string settingsPath, string systemRules, string roomDir, string prompt, string label) =>
        new(cli.FileName,
            [.. cli.LeadingArguments,
             "-p", "--permission-mode", "dontAsk", "--tools", ClaudeBuiltins, "--strict-mcp-config", "--mcp-config", mcpConfigPath,
             "--allowedTools", ClaudeDirectoryToolsAllowed, "--settings", settingsPath, "--append-system-prompt", systemRules, "--no-session-persistence", "--model", model,
             "--output-format", "stream-json", "--verbose", "--disable-slash-commands", "--setting-sources", ""],
            new Dictionary<string, string>(),
            roomDir, prompt, label);

    /// <summary>A Codex spawn in a directory room: `-C` is the room (a repository, so the repo check is
    /// not skipped), `--json` carries the command_execution items the trail records, and network is on
    /// inside workspace-write (D10). Measured 2026-09-06 (claim 24): every flag accepted; note the
    /// sandbox did NOT stop a `git commit` — the prompt rule and the trail are the mechanism (decision 7).</summary>
    public static ProcessSpec CodexInDirectory(ResolvedCli cli, string model, string mcpUrl, string token, string roomDir, string lastMessagePath, string prompt, string label) =>
        new(cli.FileName,
            [.. cli.LeadingArguments,
             "exec", "--ephemeral", "--ignore-user-config", "--json",
             "-c", $"mcp_servers.{McpServerName}.url={mcpUrl}",
             "-c", $"mcp_servers.{McpServerName}.bearer_token_env_var={TokenEnvVar}",
             "-c", $"mcp_servers.{McpServerName}.startup_timeout_sec=20",
             "-c", $"mcp_servers.{McpServerName}.tool_timeout_sec=60",
             "-c", "sandbox_workspace_write.network_access=true",
             "--approve-for-me", "-C", roomDir, "-m", model,
             "--color", "never", "-o", lastMessagePath, "-"],
            new Dictionary<string, string> { [TokenEnvVar] = token },
            roomDir, prompt, label);
```

### `tests/ChopItUp.Hub.Tests/Spawning/SpawnOutputTests.cs` (new)

```csharp
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Spawning;

/// <summary>Fixtures are the shapes measured on 2026-09-06 (plan claims 23, 24), cut to the fields the
/// parser reads; the paths and text are fabricated.</summary>
public sealed class SpawnOutputTests
{
    private const string ClaudeStream = """
        {"type":"system","subtype":"init","session_id":"s"}
        {"type":"assistant","message":{"content":[{"type":"text","text":"I'll do it."}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01A","name":"Bash","input":{"command":"echo hello","description":"Say hi"}}]}}
        {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_01A","content":"hello"}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01B","name":"Bash","input":{"command":"git -c user.name=x commit --allow-empty -m probe"}}]}}
        {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_01B","is_error":true,"content":"Permission to use Bash with command git -c user.name=x commit --allow-empty -m probe has been denied."}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01C","name":"Write","input":{"file_path":"C:\\room\\probe.txt","content":"PROBE"}}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01D","name":"Bash","input":{"command":"dir\r\ntype probe.txt"}}]}}
        {"type":"result","subtype":"success","is_error":false,"num_turns":6,"result":"STEP 1: OK\nSTEP 2: DENIED","permission_denials":[{"tool_name":"Bash","tool_use_id":"toolu_01B","tool_input":{"command":"git -c user.name=x commit --allow-empty -m probe"}}],"total_cost_usd":0.01}
        """;

    private const string CodexStream = """
        {"type":"thread.started","thread_id":"t"}
        {"type":"turn.started"}
        {"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"Working."}}
        {"type":"item.started","item":{"id":"item_1","type":"command_execution","command":"\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -Command dir","aggregated_output":"","exit_code":null,"status":"in_progress"}}
        {"type":"item.completed","item":{"id":"item_1","type":"command_execution","command":"\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -Command dir","aggregated_output":"listing","exit_code":0,"status":"completed"}}
        {"type":"item.started","item":{"id":"item_3","type":"command_execution","command":"\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -Command 'git commit --allow-empty -m probe'","aggregated_output":"","exit_code":null,"status":"in_progress"}}
        {"type":"turn.completed","usage":{"input_tokens":1}}
        """;

    [Fact]
    public void M9_A9_claude_stream_yields_bash_commands_in_order_with_denials_marked_and_other_tools_ignored()
    {
        var commands = SpawnOutput.ClaudeShellCommands(ClaudeStream);
        Assert.Equal(
            [new ShellCommand("echo hello", false), new ShellCommand("git -c user.name=x commit --allow-empty -m probe", true), new ShellCommand("dir ⏎ type probe.txt", false)],
            commands);
    }

    [Fact]
    public void M9_A9_claude_final_text_comes_from_the_last_result_line_or_from_a_single_object()
    {
        Assert.Equal("STEP 1: OK\nSTEP 2: DENIED", SpawnOutput.ClaudeFinalText(ClaudeStream));
        Assert.Equal("hello", SpawnOutput.ClaudeFinalText("""{"type":"result","subtype":"success","is_error":false,"result":"hello"}"""));
        Assert.Equal("hello", SpawnCommands.ClaudeFinalText("""{"type":"result","subtype":"success","is_error":false,"result":"hello"}"""));
        Assert.Null(SpawnOutput.ClaudeFinalText("not json"));
        Assert.Null(SpawnOutput.ClaudeFinalText(""));
        Assert.Null(SpawnOutput.ClaudeFinalText("{\"type\":\"assistant\"}\n{\"type\":\"result\",\"result\":\"   \"}"));
    }

    [Fact]
    public void M9_A9_codex_stream_yields_command_executions_once_each_in_first_seen_order_including_one_that_only_started()
    {
        var commands = SpawnOutput.CodexShellCommands(CodexStream);
        Assert.Equal(
            [new ShellCommand("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -Command dir", false),
             new ShellCommand("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -Command 'git commit --allow-empty -m probe'", false)],
            commands);
    }

    [Fact]
    public void M9_A9_garbage_and_foreign_shapes_yield_nothing_and_never_throw()
    {
        const string junk = "not json\n{\"type\":\"assistant\"}\n{\"item\":5}\n{\"type\":\"assistant\",\"message\":{\"content\":\"text\"}}\n[1,2]\n{\"item\":{\"type\":\"command_execution\"}}\n";
        Assert.Empty(SpawnOutput.ClaudeShellCommands(junk));
        Assert.Empty(SpawnOutput.CodexShellCommands(junk));
        Assert.Empty(SpawnOutput.ClaudeShellCommands(""));
        Assert.Empty(SpawnOutput.CodexShellCommands(""));
        Assert.Null(SpawnOutput.ClaudeFinalText(junk));
    }

    [Fact]
    public void M9_A9_flatten_folds_line_breaks_caps_at_400_characters_and_the_list_caps_at_50()
    {
        Assert.Equal("a ⏎ b ⏎ c", SpawnOutput.Flatten("a\r\nb\nc"));
        var flat = SpawnOutput.Flatten(new string('x', 500));
        Assert.Equal(401, flat.Length);
        Assert.EndsWith("…", flat);

        var many = string.Join('\n', Enumerable.Range(0, 60).Select(i =>
            $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t{{{i}}}","name":"Bash","input":{"command":"echo {{{i}}}"}}]}}"""));
        Assert.Equal(50, SpawnOutput.ClaudeShellCommands(many).Count);
        Assert.Equal("echo 0", SpawnOutput.ClaudeShellCommands(many)[0].Command);
    }
}
```

### `tests/ChopItUp.Hub.Tests/Spawning/SpawnCommandsTests.cs` — append three facts (the four existing ones stay verbatim)

```csharp
    [Fact]
    public void M9_A6_claude_directory_command_line_runs_in_the_room_under_dontAsk_with_six_builtins_and_the_settings_file()
    {
        var spec = SpawnCommands.ClaudeInDirectory(ClaudeExe, "opus", @"C:\data\spawns\s1\mcp.json", @"C:\data\spawns\s1\settings.json", "RULES", @"C:\Rooms\lab", "PROMPT", "opus/s1");
        Assert.Equal(@"C:\tools\claude.exe", spec.FileName);
        Assert.Equal(
            ["-p", "--permission-mode", "dontAsk", "--tools", "Read,Edit,Write,Glob,Grep,Bash", "--strict-mcp-config", "--mcp-config", @"C:\data\spawns\s1\mcp.json",
             "--allowedTools", "Read,Edit,Write,Glob,Grep,Bash,mcp__chopitup__post_message,mcp__chopitup__recall,mcp__chopitup__propose_memory",
             "--settings", @"C:\data\spawns\s1\settings.json", "--append-system-prompt", "RULES", "--no-session-persistence", "--model", "opus",
             "--output-format", "stream-json", "--verbose", "--disable-slash-commands", "--setting-sources", ""],
            spec.Arguments);
        Assert.Equal(@"C:\Rooms\lab", spec.WorkingDirectory);
        Assert.Empty(spec.Environment);
        Assert.Equal("PROMPT", spec.StandardInput);
        Assert.DoesNotContain("bypassPermissions", spec.Arguments);
        Assert.DoesNotContain("--bare", spec.Arguments);
    }

    [Fact]
    public void M9_A6_claude_settings_deny_every_git_write_verb_any_option_form_git_exe_dot_git_and_the_credential_folders_for_read_write_edit_and_allow_nothing()
    {
        using var doc = JsonDocument.Parse(SpawnCommands.ClaudeSettingsJson());
        var permissions = doc.RootElement.GetProperty("permissions");
        var deny = permissions.GetProperty("deny").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.False(permissions.TryGetProperty("allow", out _));
        Assert.False(permissions.TryGetProperty("blockReadsOutsideWorkingDirectories", out _));   // measured ineffective on 2.1.220 (claim 23)
        foreach (var expected in new[]
                 {
                     "Bash(git commit *)", "Bash(git commit)", "Bash(git push *)", "Bash(git add *)", "Bash(git reset)", "Bash(git -*)", "Bash(git.exe *)", "Bash(git.exe)",
                     "Edit(.git/**)", "Write(.git/**)", "Read(~/.ssh/**)", "Read(~/.claude/**)", "Read(~/.codex/**)", "Write(~/.claude/**)", "Edit(~/.codex/**)", "Write(~/.ssh/**)",
                 })
            Assert.Contains(expected, deny);
        Assert.DoesNotContain("Bash(git log *)", deny);
        Assert.DoesNotContain("Bash(git status *)", deny);
        Assert.DoesNotContain("Bash(git *)", deny);
        Assert.DoesNotContain("Bash(*git.exe *)", deny);   // leading wildcards do not match on 2.1.220 (claim 35); a rule that looks like a fence and is not
        Assert.Equal(SpawnCommands.GitWriteVerbs.Length * 2 + 5 + SpawnCommands.CredentialFolders.Length * 3, deny.Count);
    }

    [Fact]
    public void M9_A7_directory_rules_name_the_directory_the_fence_the_git_rule_and_the_api_rule()
    {
        var rules = SpawnPrompt.DirectoryRules(@"C:\Rooms\lab");
        Assert.StartsWith(@"Stay inside your working directory, C:\Rooms\lab:", rules);
        Assert.Contains("do not read, list, create or change anything outside this directory", rules);
        Assert.Contains("Do not run git commands that write", rules);
        Assert.Contains("git log, git status and git diff are fine.", rules);
        Assert.Contains("Do not call the hub's HTTP API", rules);
        Assert.DoesNotContain("\n", rules);
    }

    [Fact]
    public void M9_A6_codex_directory_command_line_runs_in_the_room_with_json_and_network_and_without_the_repo_check_skip()
    {
        var spec = SpawnCommands.CodexInDirectory(CodexShim, "gpt-6-astra", "http://127.0.0.1:8790/mcp", "tok456", @"C:\Rooms\lab", @"C:\data\spawns\s2\last.txt", "PROMPT", "gpt-6-astra/s2");
        Assert.Equal(@"C:\Windows\System32\cmd.exe", spec.FileName);
        Assert.Equal(
            ["/d", "/c", @"C:\tools\codex.cmd", "exec", "--ephemeral", "--ignore-user-config", "--json",
             "-c", "mcp_servers.chopitup.url=http://127.0.0.1:8790/mcp",
             "-c", "mcp_servers.chopitup.bearer_token_env_var=CHOPITUP_TOKEN",
             "-c", "mcp_servers.chopitup.startup_timeout_sec=20",
             "-c", "mcp_servers.chopitup.tool_timeout_sec=60",
             "-c", "sandbox_workspace_write.network_access=true",
             "--approve-for-me", "-C", @"C:\Rooms\lab", "-m", "gpt-6-astra",
             "--color", "never", "-o", @"C:\data\spawns\s2\last.txt", "-"],
            spec.Arguments);
        Assert.Equal(@"C:\Rooms\lab", spec.WorkingDirectory);
        Assert.Equal("tok456", spec.Environment["CHOPITUP_TOKEN"]);
        Assert.DoesNotContain(spec.Arguments, a => a.Contains("tok456"));
        Assert.DoesNotContain("--skip-git-repo-check", spec.Arguments);
    }
```

Expected: Hub tests 169 + 5 + 4 = **178** cases; the four existing `SpawnCommandsTests` facts unchanged and green.

## Task 3 — `RoomPaths` rules, `HubOptions.RoomsRoot` (builder: sonnet)

Ticket: `.scratch/m9-rooms-as-chats/issues/03-room-paths-and-rooms-root.md`. Acceptance: the refusal half of A2. Depends on Task 2 for `SpawnCommands.CredentialFolders`.

### `src/ChopItUp.Hub/Rooms/RoomPaths.cs` (new)

```csharp
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Rooms;

public sealed record RefusedSubtree(string Path, string Reason);

/// <summary>What a room directory may not be (D12, M9 plan decision 3): the profile folder itself, and
/// anything under the listed subtrees. Built once per hub from the data dir, the install dir, the
/// profile and four environment folders; pure string rules after that.</summary>
public sealed record RoomPathRules(string UserProfile, IReadOnlyList<RefusedSubtree> Subtrees)
{
    public const string SelfApps = @"C:\Self Apps";

    public static RoomPathRules ForHub(string dataDir) =>
        ForHub(dataDir, AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable);

    public static RoomPathRules ForHub(string dataDir, string installDir, string userProfile, Func<string, string?> getEnv)
    {
        var profile = RoomPaths.Normalize(userProfile);
        var subtrees = new List<RefusedSubtree>
        {
            new(RoomPaths.Normalize(dataDir), "the hub's data folder"),
            new(RoomPaths.Normalize(installDir), "the hub's install folder"),
            new(SelfApps, @"C:\Self Apps (installed apps and their data)"),
        };
        foreach (var name in new[] { "SystemRoot", "ProgramFiles", "ProgramFiles(x86)", "ProgramData" })
            if (getEnv(name) is { Length: > 0 } value) subtrees.Add(new(RoomPaths.Normalize(value), $"a Windows system folder ({name})"));
        foreach (var folder in SpawnCommands.CredentialFolders)
            subtrees.Add(new(Path.Combine(profile, folder), $"a credential folder ({folder})"));
        return new(profile, subtrees);
    }
}

/// <summary>Path shape, normalisation and the refusal sentence. Comparisons are case-insensitive on
/// normalised full paths with no trailing separator (a drive root keeps its one backslash).</summary>
public static class RoomPaths
{
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
    }

    public static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static bool IsUnderOrEqual(string path, string root)
    {
        if (Same(path, root)) return true;
        var prefix = root.EndsWith('\\') ? root : root + '\\';
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Null when the typed path may be a room directory; otherwise the sentence the owner sees.
    /// Checks the shape, then the normalised path against the rules, then the path with every existing
    /// junction or symbolic link on it resolved (the leaf or any ancestor: `Path.GetFullPath` resolves
    /// neither) against the same rules.</summary>
    public static string? Refusal(string? typed, RoomPathRules rules)
    {
        var t = (typed ?? "").Trim();
        if (t.Length == 0) return "Directory is empty.";
        if (t.StartsWith(@"\\", StringComparison.Ordinal) || t.StartsWith("//", StringComparison.Ordinal))
            return @"Network and device paths (\\server\share, \\?\...) cannot be room directories.";
        if (t.Length < 3 || !char.IsAsciiLetter(t[0]) || t[1] != ':' || (t[2] != '\\' && t[2] != '/'))
            return @"The directory must be an absolute local path such as C:\Projects\thing.";
        string full;
        try { full = Normalize(t); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return "The directory is not a valid Windows path.";
        }
        if (RefusalOfNormalized(full, rules) is { } refused) return refused;
        var resolved = ResolveLinks(full);
        if (!Same(resolved, full) && RefusalOfNormalized(resolved, rules) is { } viaLink)
            return $"'{full}' is a link to '{resolved}': {viaLink}";
        return null;
    }

    /// <summary>The path with each existing segment that is a junction or symbolic link replaced by its
    /// final target, walking from the drive root; segments that do not exist yet are appended as typed.
    /// A link that cannot be resolved (a loop, no access) is left as is — the refusal then rests on the
    /// typed path, which is the conservative side only when the target is outside the refused set, so
    /// <see cref="RoomDirectories"/> also refuses a folder whose git top level differs from itself.</summary>
    public static string ResolveLinks(string full)
    {
        var root = Path.GetPathRoot(full) ?? full;
        var segments = full[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (int i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            if (!Directory.Exists(current))
                return i + 1 < segments.Length ? Path.Combine(current, Path.Combine(segments[(i + 1)..])) : current;
            if (LinkTarget(current) is { } target) current = Normalize(target);
        }
        return Normalize(current);
    }

    public static string? RefusalOfNormalized(string full, RoomPathRules rules)
    {
        if (Same(full, Path.GetPathRoot(full) ?? full)) return $"'{full}' is a drive root; use a folder on the drive.";
        if (Same(full, rules.UserProfile)) return $"'{full}' is your user profile folder; use a folder inside it.";
        foreach (var s in rules.Subtrees)
            if (IsUnderOrEqual(full, s.Path)) return $"'{full}' is refused: it is {s.Reason}, or inside it.";
        return null;
    }

    /// <summary>The final target when the folder exists and is a junction or symbolic link; null otherwise.</summary>
    public static string? LinkTarget(string full)
    {
        try
        {
            var info = new DirectoryInfo(full);
            if (!info.Exists || info.LinkTarget is null) return null;
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
```

### `src/ChopItUp.Hub/Hosting/HubOptions.cs` — edits

Record (line 12) becomes:

```csharp
public sealed record HubOptions(string DataDir, int Port, HubCommand Command = HubCommand.Serve, string? RotateParticipant = null, string? WebRoot = null, string? RoomsRoot = null)
{
    public const int DefaultPort = 8790;

    /// <summary>Where hub-created room directories go (M9 decision 2): `--rooms-root`, then
    /// `CHOPITUP_ROOMS`, then `%USERPROFILE%\ChopItUp\rooms` — a folder inside the profile, which D12
    /// allows, because the install dir is under C:\Self Apps and the data dir holds the tokens.</summary>
    public string RoomsRootPath => Path.GetFullPath(string.IsNullOrWhiteSpace(RoomsRoot) ? DefaultRoomsRoot() : RoomsRoot);

    public static string DefaultRoomsRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ChopItUp", "rooms");
```

In `Parse`: declare `string? rooms = null;` beside `rotate`; add a branch after the `--print-config` one:

```csharp
            else if (args[i] == "--rooms-root")
            {
                if (i + 1 >= args.Length) throw new ArgumentException("--rooms-root requires a value.");
                rooms = args[++i];
            }
```

after `port ??= getEnv("CHOPITUP_PORT");` add `rooms ??= getEnv("CHOPITUP_ROOMS");`, and the return becomes:

```csharp
        return new HubOptions(
            Path.GetFullPath(string.IsNullOrWhiteSpace(data) ? Path.Combine(AppContext.BaseDirectory, "data") : data),
            int.TryParse(port, out var p) ? p : DefaultPort,
            command,
            rotate,
            RoomsRoot: string.IsNullOrWhiteSpace(rooms) ? null : rooms);
```

The class doc comment gains one sentence: "`RoomsRoot` is where hub-created room directories go (M9)."

### `tests/ChopItUp.Hub.Tests/HubHostTests.cs` — add one fact beside the existing `HubOptions.Parse` facts (lines 68–91)

```csharp
    [Fact]
    public void M9_A2_rooms_root_comes_from_the_flag_then_the_environment_then_the_profile()
    {
        var fromArgs = HubOptions.Parse(["--rooms-root", @"C:\Rooms\flag"], _ => null);
        Assert.Equal(@"C:\Rooms\flag", fromArgs.RoomsRootPath);

        var fromEnv = HubOptions.Parse([], name => name == "CHOPITUP_ROOMS" ? @"C:\Rooms\env" : null);
        Assert.Equal(@"C:\Rooms\env", fromEnv.RoomsRootPath);

        var both = HubOptions.Parse(["--rooms-root", @"C:\Rooms\flag"], name => name == "CHOPITUP_ROOMS" ? @"C:\Rooms\env" : null);
        Assert.Equal(@"C:\Rooms\flag", both.RoomsRootPath);

        var defaults = HubOptions.Parse([], _ => null);
        Assert.Null(defaults.RoomsRoot);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ChopItUp", "rooms"), defaults.RoomsRootPath);

        Assert.Throws<ArgumentException>(() => HubOptions.Parse(["--rooms-root"], _ => null));
    }
```

### `tests/ChopItUp.Hub.Tests/Rooms/RoomPathsTests.cs` (new)

```csharp
using System.Diagnostics;
using ChopItUp.Hub.Rooms;

namespace ChopItUp.Hub.Tests.Rooms;

public sealed class RoomPathsTests
{
    // A fabricated machine: nothing here reads the real profile or environment.
    private static readonly RoomPathRules Rules = RoomPathRules.ForHub(
        dataDir: @"C:\hub\data", installDir: @"C:\hub\app", userProfile: @"C:\Users\me",
        getEnv: name => name switch
        {
            "SystemRoot" => @"C:\Windows",
            "ProgramFiles" => @"C:\Program Files",
            "ProgramFiles(x86)" => @"C:\Program Files (x86)",
            "ProgramData" => @"C:\ProgramData",
            _ => null,
        });

    [Theory]
    [InlineData(@"C:\", "drive root")]
    [InlineData(@"C:\Users\me\..\..", "drive root")]
    [InlineData(@"D:", "absolute local path")]
    [InlineData(@"relative\path", "absolute local path")]
    [InlineData(@"\\server\share\x", "Network and device paths")]
    [InlineData(@"\\?\C:\x", "Network and device paths")]
    [InlineData("", "Directory is empty")]
    [InlineData(@"C:\Users\me", "user profile folder")]
    [InlineData(@"c:\users\ME\", "user profile folder")]
    [InlineData(@"C:\Self Apps\ChopItUp\data", @"C:\Self Apps")]
    [InlineData(@"C:\Self Apps", @"C:\Self Apps")]
    [InlineData(@"C:\hub\data\rooms\x", "the hub's data folder")]
    [InlineData(@"C:\hub\data", "the hub's data folder")]
    [InlineData(@"C:\hub\app\wwwroot", "the hub's install folder")]
    [InlineData(@"C:\Users\me\.ssh", "a credential folder (.ssh)")]
    [InlineData(@"C:\Users\me\.codex\memories", "a credential folder (.codex)")]
    [InlineData(@"C:\Users\me\.claude\projects\x", "a credential folder (.claude)")]
    [InlineData(@"C:\Windows\Temp", "a Windows system folder (SystemRoot)")]
    [InlineData(@"C:\Program Files\x", "a Windows system folder (ProgramFiles)")]
    [InlineData(@"C:\Program Files (x86)\x", "a Windows system folder (ProgramFiles(x86))")]
    [InlineData(@"C:\ProgramData\x", "a Windows system folder (ProgramData)")]
    public void M9_A2_refused_paths_name_the_reason(string typed, string reasonFragment)
    {
        var refusal = RoomPaths.Refusal(typed, Rules);
        Assert.NotNull(refusal);
        Assert.Contains(reasonFragment, refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\Users\me\ChopItUp\rooms\lab")]
    [InlineData(@"C:\Users\me\Documents\resume")]
    [InlineData(@"C:\Agent Projects\thing")]
    [InlineData(@"C:\hub\data2")]                 // a sibling of the data folder, not inside it
    [InlineData(@"C:\hub\application")]           // shares a prefix with C:\hub\app, is not under it
    [InlineData("C:/Users/me/proj/")]             // forward slashes and a trailing slash normalise
    [InlineData(@"C:\Self Apps2\x")]              // prefix, not subtree
    public void M9_A2_accepted_paths_return_null(string typed) =>
        Assert.Null(RoomPaths.Refusal(typed, Rules));

    [Theory]
    [InlineData(@"C:\foo\bar\", @"C:\foo\bar")]
    [InlineData(@"C:\foo\bar", @"C:\foo\bar")]
    [InlineData("C:/foo/bar/", @"C:\foo\bar")]
    [InlineData(@"C:\foo\..\bar", @"C:\bar")]
    [InlineData(@"C:\", @"C:\")]
    public void M9_A2_normalize_drops_trailing_separators_except_on_a_drive_root(string input, string expected) =>
        Assert.Equal(expected, RoomPaths.Normalize(input));

    [Fact]
    public void M9_A2_is_under_or_equal_is_case_insensitive_and_boundary_aware()
    {
        Assert.True(RoomPaths.IsUnderOrEqual(@"C:\HUB\Data\x", @"C:\hub\data"));
        Assert.True(RoomPaths.IsUnderOrEqual(@"C:\hub\data", @"C:\hub\data"));
        Assert.False(RoomPaths.IsUnderOrEqual(@"C:\hub\data2", @"C:\hub\data"));
        Assert.True(RoomPaths.IsUnderOrEqual(@"C:\x", @"C:\"));
    }

    [Fact]
    public void M9_A2_a_junction_into_a_refused_place_is_refused_by_its_target_whether_it_is_the_leaf_or_an_ancestor()
    {
        Assert.True(OperatingSystem.IsWindows());
        var root = Path.Combine(Path.GetTempPath(), "chopitup_paths_" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var inner = Path.Combine(data, "inner");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(inner);
        try
        {
            using var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{inner}\"")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            mk.WaitForExit();
            Assert.Equal(0, mk.ExitCode);

            var rules = RoomPathRules.ForHub(data, Path.Combine(root, "app"), Path.Combine(root, "profile"), _ => null);
            var refusal = RoomPaths.Refusal(link, rules);                            // the leaf is the link
            Assert.NotNull(refusal);
            Assert.Contains("is a link to", refusal);
            Assert.Contains("the hub's data folder", refusal);
            var below = RoomPaths.Refusal(Path.Combine(link, "sub", "deeper"), rules);   // an ancestor is the link; the leaf does not exist yet
            Assert.NotNull(below);
            Assert.Contains("is a link to", below);
            Assert.Contains("the hub's data folder", below);
            Assert.Equal(Path.Combine(inner, "sub", "deeper"), RoomPaths.ResolveLinks(Path.Combine(link, "sub", "deeper")));
            Assert.Null(RoomPaths.Refusal(Path.Combine(root, "plain"), rules));   // a sibling that is not a link, existing or not
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);   // removes the junction, not its target
            Directory.Delete(root, recursive: true);
        }
    }
}
```

Expected: Hub tests 178 + 1 + (21 + 7 + 5 theory cases) + 2 facts = **214** cases. The builder reports the real number.

## Task 4 — `GitTrail`, `MemoryGit` on top of it, `RoomTrails` (builder: sonnet)

Ticket: `.scratch/m9-rooms-as-chats/issues/04-git-trail.md`. Acceptance: the git half of A8, A9, A11. Blocked by Task 3 (`RoomTrails` normalises through `RoomPaths`). `MemoryGitTests` (3 facts) and `MemoryApiGuardTests` must stay green untouched — they are the proof the surface did not move.

### `src/ChopItUp.Hub/Git/GitTrail.cs` (new)

```csharp
using System.Globalization;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Git;

public sealed record GitIdentity(string Name, string Email)
{
    public override string ToString() => $"{Name} <{Email}>";
}

public sealed record TrailCommit(string Hash, string Author, DateTimeOffset At, string Subject);

/// <summary><see cref="Hash"/> is HEAD after the call — the new commit, or the unchanged HEAD when there
/// was nothing to commit and empties were not allowed — or null on failure with <see cref="Reason"/>;
/// <see cref="Created"/> says whether a commit was made; <see cref="FilesChanged"/> counts the paths in
/// the commit that was made (0 for an empty one).</summary>
public sealed record CommitOutcome(string? Hash, bool Created, int FilesChanged, string? Reason);

/// <summary>One git working tree the hub commits into — the memory store (D15) and every room
/// directory (D11). Generalised from M10's MemoryGit: the committer is always the hub, the author is
/// whoever the caller says (a spawned model, the owner). git is resolved directly (a real git.exe on
/// PATH), not through the spawner's CliLocator seam, so hub tests exercise the real trail (M10
/// decision 4). Nothing here throws: a failure is a null/false/empty result with <see cref="Reason"/>
/// set and one line in the hub log. Serialised per instance — one tree, one writer at a time.</summary>
public class GitTrail
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    public static readonly GitIdentity Hub = new("ChopItUp hub", "hub@chopitup.local");
    // LC_ALL=C: the no-op detection reads git's English "nothing to commit" (M10 critique, P2-7).
    private static readonly IReadOnlyDictionary<string, string> Env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C" };
    // A fixed committer and no signing: an unconfigured machine (or a CI runner) must still commit.
    private static readonly string[] Committer =
        ["-c", "user.name=" + Hub.Name, "-c", "user.email=" + Hub.Email, "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false"];

    private readonly Func<ResolvedCli> _resolve;
    private readonly IProcessRunner _runner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ResolvedCli? _git;
    private bool _unavailable;

    public GitTrail(string root, Func<ResolvedCli>? resolve = null, IProcessRunner? runner = null)
    {
        Root = Path.GetFullPath(root);
        _resolve = resolve ?? (() => CliResolver.Resolve("git"));
        _runner = runner ?? new ProcessRunner();
    }

    public string Root { get; }

    /// <summary>Why the last call failed; null after a success.</summary>
    public string? Reason { get; private set; }

    /// <summary>Prefix of this trail's hub-log lines.</summary>
    protected virtual string LogName => "room";

    /// <summary>True when git.exe was found (resolved once; a miss is remembered for this instance).</summary>
    public bool IsAvailable() => Resolve() is not null;

    /// <summary>The repository root that contains <see cref="Root"/>, as a Windows path, or null when
    /// <see cref="Root"/> is not inside any repository (or does not exist, or git is unavailable).</summary>
    public async Task<string?> TopLevelAsync(CancellationToken cancellation = default)
    {
        var git = Resolve();
        if (git is null || !Directory.Exists(Root)) return null;
        var r = await Run(git, ["rev-parse", "--show-toplevel"], "", cancellation);
        if (r.ExitCode != 0) return null;
        var text = r.StandardOutput.Trim();
        return text.Length == 0 ? null : Path.GetFullPath(text.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>`git init` when <see cref="Root"/> is not yet a repository (creating the folder if needed).</summary>
    public async Task<bool> InitAsync(CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            var git = Resolve();
            if (git is null) return false;
            Directory.CreateDirectory(Root);
            if (Directory.Exists(Path.Combine(Root, ".git"))) { Reason = null; return true; }
            var init = await Run(git, ["init", "-q"], "", cancellation);
            if (init.ExitCode != 0) { Fail("git init", init); return false; }
            Reason = null;
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>HEAD's short hash, or null before the first commit (quietly — an unborn HEAD is a
    /// normal state, not a failure) or when git is unavailable.</summary>
    public async Task<string?> HeadAsync(CancellationToken cancellation = default)
    {
        var git = Resolve();
        if (git is null || !Directory.Exists(Path.Combine(Root, ".git"))) return null;
        return await HeadUnlocked(git, logFailure: false, cancellation);
    }

    /// <summary>True when `git status --porcelain` lists anything (modified, added, deleted, untracked).
    /// False when clean — and false, with <see cref="Reason"/>, when the question could not be asked.</summary>
    public async Task<bool> IsDirtyAsync(CancellationToken cancellation = default)
    {
        var git = Resolve();
        if (git is null || !Directory.Exists(Path.Combine(Root, ".git"))) return false;
        var r = await Run(git, ["status", "--porcelain"], "", cancellation);
        if (r.ExitCode != 0) { Fail("git status", r); return false; }
        Reason = null;
        return r.StandardOutput.Trim().Length > 0;
    }

    /// <summary>`git add -A` then a commit authored by <paramref name="author"/>; the committer is the
    /// hub. The message travels on stdin (`-F -`): a room commit carries a shell log, and a Windows
    /// command line is capped at 32,767 characters. Initialises the repository if it is missing. With
    /// <paramref name="allowEmpty"/> false, "nothing to commit" is not a failure: the outcome is HEAD
    /// with <see cref="CommitOutcome.Created"/> false.</summary>
    public async Task<CommitOutcome> CommitAllAsync(string message, GitIdentity author, bool allowEmpty, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            var git = Resolve();
            if (git is null) return new(null, false, 0, Reason);
            Directory.CreateDirectory(Root);
            if (!Directory.Exists(Path.Combine(Root, ".git")))
            {
                var init = await Run(git, ["init", "-q"], "", cancellation);
                if (init.ExitCode != 0) return new(null, false, 0, Fail("git init", init));
            }
            var add = await Run(git, ["add", "-A", "--", "."], "", cancellation);
            if (add.ExitCode != 0) return new(null, false, 0, Fail("git add", add));

            var args = new List<string>(Committer) { "commit", "-q", "--author=" + author, "-F", "-" };
            if (allowEmpty) args.Insert(args.Count - 2, "--allow-empty");
            var commit = await Run(git, args, message, cancellation);
            if (commit.ExitCode != 0)
            {
                if (!allowEmpty && (commit.StandardOutput + commit.StandardError).Contains("nothing to commit", StringComparison.Ordinal))
                {
                    Reason = null;
                    return new(await HeadUnlocked(git, logFailure: true, cancellation), false, 0, Reason);
                }
                return new(null, false, 0, Fail("git commit", commit));
            }
            var head = await HeadUnlocked(git, logFailure: true, cancellation);
            if (head is null) return new(null, true, 0, Reason);
            var names = await Run(git, ["show", "--name-only", "--format=", "HEAD"], "", cancellation);
            var files = names.ExitCode == 0
                ? names.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length
                : 0;
            Reason = null;
            return new(head, true, files, null);
        }
        finally { _gate.Release(); }
    }

    /// <summary>The newest <paramref name="limit"/> commits, newest first; empty before the first commit
    /// (quietly) or on failure (with <see cref="Reason"/>).</summary>
    public async Task<IReadOnlyList<TrailCommit>> LogAsync(int limit, CancellationToken cancellation = default)
    {
        var git = Resolve();
        if (git is null || !Directory.Exists(Path.Combine(Root, ".git"))) return [];
        var r = await Run(git, ["log", "--format=%h%x1f%an <%ae>%x1f%aI%x1f%s", "-n", Math.Clamp(limit, 1, 200).ToString(CultureInfo.InvariantCulture)], "", cancellation);
        if (r.ExitCode != 0)
        {
            if (!r.StandardError.Contains("does not have any commits", StringComparison.Ordinal)) Fail("git log", r);
            return [];
        }
        var commits = new List<TrailCommit>();
        foreach (var line in r.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\u001f');
            if (parts.Length < 4 || !DateTimeOffset.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)) continue;
            commits.Add(new TrailCommit(parts[0], parts[1], at, parts[3]));
        }
        Reason = null;
        return commits;
    }

    private async Task<string?> HeadUnlocked(ResolvedCli git, bool logFailure, CancellationToken cancellation)
    {
        var head = await Run(git, ["rev-parse", "--short", "HEAD"], "", cancellation);
        if (head.ExitCode == 0) return head.StandardOutput.Trim();
        if (logFailure) Fail("git rev-parse", head);
        return null;
    }

    private ResolvedCli? Resolve()
    {
        if (_git is not null) return _git;
        if (_unavailable) return null;
        try { return _git = _resolve(); }
        catch (Exception e) when (e is FileNotFoundException or InvalidOperationException)
        {
            _unavailable = true;
            Reason = "git is not available: " + e.Message;
            Console.Error.WriteLine($"{LogName}: {Reason}");
            return null;
        }
    }

    private Task<ProcessResult> Run(ResolvedCli git, IReadOnlyList<string> args, string stdin, CancellationToken cancellation) =>
        _runner.RunAsync(new ProcessSpec(git.FileName, [.. git.LeadingArguments, .. args], Env, Root, stdin, LogName + "-git"), Timeout, cancellation);

    private string Fail(string step, ProcessResult r)
    {
        Reason = $"{step} exited {(r.ExitCode?.ToString() ?? "killed")}: {r.StandardError.Trim()}";
        Console.Error.WriteLine($"{LogName}: {Reason}");
        return Reason;
    }
}
```

### `src/ChopItUp.Hub/Memory/MemoryGit.cs` — replace the whole file

```csharp
using ChopItUp.Hub.Git;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Memory;

/// <summary>The trail behind the memory store (D15): every approval is one commit, as the hub, in a
/// repository inside <c>&lt;data&gt;\memory\</c>, initialised lazily on the first commit. Since M9 this is
/// <see cref="GitTrail"/> with the hub as author; the surface M10 callers and tests use is unchanged:
/// a machine without git loses the record, not the memory (<see cref="GitTrail.Reason"/> says why).</summary>
public sealed class MemoryGit(string root, Func<ResolvedCli>? resolve = null, IProcessRunner? runner = null) : GitTrail(root, resolve, runner)
{
    protected override string LogName => "memory";

    /// <summary>Stages everything under the root and commits it. Returns the short hash of HEAD — the
    /// new commit, or the unchanged HEAD when there was nothing to commit — or null with
    /// <see cref="GitTrail.Reason"/> set. Serialised: two approvals never race inside one repository.</summary>
    public async Task<string?> CommitAsync(string message, CancellationToken cancellation = default) =>
        (await CommitAllAsync(message, Hub, allowEmpty: false, cancellation)).Hash;
}
```

`MemoryGitTests` expectations map one to one: first commit → a hash and `Reason` null; a no-diff commit → the unchanged HEAD; a throwing resolver → `Reason` starting `git is not available` and no `.git` created (the resolve happens before `Directory.CreateDirectory`/`init`); the failing runner → `git init exited 128: boom`. `MemoryApi` and `HubHost` reference only the constructor and `CommitAsync`.

### `src/ChopItUp.Hub/Rooms/RoomTrails.cs` (new)

```csharp
using System.Collections.Concurrent;
using ChopItUp.Hub.Git;

namespace ChopItUp.Hub.Rooms;

/// <summary>One <see cref="GitTrail"/> per room directory, created on first use and kept for the hub's
/// life so the per-tree gate is shared by the spawner and the API. Keyed by the normalised path.</summary>
public sealed class RoomTrails(Func<string, GitTrail> factory)
{
    private readonly ConcurrentDictionary<string, GitTrail> _trails = new(StringComparer.OrdinalIgnoreCase);

    public GitTrail For(string directory) => _trails.GetOrAdd(RoomPaths.Normalize(directory), factory);
}
```

### `tests/ChopItUp.Hub.Tests/Git/GitTrailTests.cs` (new)

```csharp
using ChopItUp.Hub.Git;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Git;

public sealed class GitTrailTests : IDisposable
{
    private static readonly GitIdentity Opus = new("Opus", "opus@chopitup.local");
    private static readonly GitIdentity Owner = new("Owner", "owner@chopitup.local");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_trail_" + Guid.NewGuid().ToString("N"));

    public GitTrailTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => TestDirs.DeleteTree(_dir);

    private static async Task<string> GitOut(string dir, params string[] args)
    {
        var r = await new ProcessRunner().RunAsync(
            new ProcessSpec(CliResolver.Resolve("git").FileName, args, new Dictionary<string, string>(), dir, "", "test-git"),
            TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(0, r.ExitCode);
        return r.StandardOutput;
    }

    [Fact]
    public async Task M9_A9_commit_all_splits_author_from_the_hub_committer_and_counts_the_files()
    {
        var git = new GitTrail(_dir);
        Assert.True(git.IsAvailable());
        Assert.Null(await git.HeadAsync());
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "b");

        var first = await git.CommitAllAsync("opus: turn 1/4 in room lab\n\nShell commands run (1):\n  1. dir\n", Opus, allowEmpty: false);
        Assert.True(first.Created);
        Assert.Equal(2, first.FilesChanged);
        Assert.Matches("^[0-9a-f]{7,}$", first.Hash);
        Assert.Null(first.Reason);
        Assert.Equal(first.Hash, await git.HeadAsync());

        var line = (await GitOut(_dir, "log", "-1", "--format=%an <%ae>|%cn <%ce>|%s")).Trim();
        Assert.Equal("Opus <opus@chopitup.local>|ChopItUp hub <hub@chopitup.local>|opus: turn 1/4 in room lab", line);
        var body = await GitOut(_dir, "log", "-1", "--format=%B");
        Assert.Contains("Shell commands run (1):", body);
        Assert.Contains("  1. dir", body);
    }

    [Fact]
    public async Task M9_A9_allow_empty_makes_a_commit_with_no_files_and_without_it_nothing_to_commit_returns_head_uncreated()
    {
        var git = new GitTrail(_dir);
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        var first = await git.CommitAllAsync("owner: edits", Owner, allowEmpty: false);

        var none = await git.CommitAllAsync("nothing", Owner, allowEmpty: false);
        Assert.False(none.Created);
        Assert.Equal(first.Hash, none.Hash);
        Assert.Null(none.Reason);
        Assert.Null(git.Reason);

        var empty = await git.CommitAllAsync("sonnet: turn 2/4 in room lab", Opus, allowEmpty: true);
        Assert.True(empty.Created);
        Assert.Equal(0, empty.FilesChanged);
        Assert.NotEqual(first.Hash, empty.Hash);
        Assert.Equal(2, (await git.LogAsync(10)).Count);
    }

    [Fact]
    public async Task M9_A8_dirty_is_false_before_init_false_when_clean_and_true_for_untracked_or_modified_files()
    {
        var git = new GitTrail(_dir);
        Assert.False(await git.IsDirtyAsync());              // not a repository yet
        Assert.True(await git.InitAsync());
        Assert.True(Directory.Exists(Path.Combine(_dir, ".git")));
        Assert.False(await git.IsDirtyAsync());              // empty repository, nothing to report
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        Assert.True(await git.IsDirtyAsync());               // untracked counts
        await git.CommitAllAsync("owner: edits", Owner, allowEmpty: false);
        Assert.False(await git.IsDirtyAsync());
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "changed");
        Assert.True(await git.IsDirtyAsync());
        Assert.True(await git.InitAsync());                  // idempotent
    }

    [Fact]
    public async Task M9_A2_toplevel_is_null_outside_a_repository_the_root_inside_one_and_the_same_root_from_a_subfolder()
    {
        var git = new GitTrail(_dir);
        Assert.Null(await git.TopLevelAsync());
        Assert.True(await git.InitAsync());
        Assert.Equal(Path.GetFullPath(_dir), await git.TopLevelAsync());

        var sub = Path.Combine(_dir, "sub", "deeper");
        Directory.CreateDirectory(sub);
        Assert.Equal(Path.GetFullPath(_dir), await new GitTrail(sub).TopLevelAsync());
        Assert.Null(await new GitTrail(Path.Combine(_dir, "missing")).TopLevelAsync());
    }

    [Fact]
    public async Task M9_A11_log_is_newest_first_parsed_and_limited_and_empty_before_the_first_commit()
    {
        var git = new GitTrail(_dir);
        Assert.Empty(await git.LogAsync(20));                // not a repository
        await git.InitAsync();
        Assert.Empty(await git.LogAsync(20));                // unborn HEAD, no failure
        Assert.Null(git.Reason);

        for (int i = 1; i <= 3; i++)
        {
            File.WriteAllText(Path.Combine(_dir, $"f{i}.txt"), i.ToString());
            await git.CommitAllAsync($"opus: turn {i}/4 in room lab", Opus, allowEmpty: false);
        }
        var log = await git.LogAsync(2);
        Assert.Equal(2, log.Count);
        Assert.Equal("opus: turn 3/4 in room lab", log[0].Subject);
        Assert.Equal("opus: turn 2/4 in room lab", log[1].Subject);
        Assert.Equal("Opus <opus@chopitup.local>", log[0].Author);
        Assert.Matches("^[0-9a-f]{7,}$", log[0].Hash);
        Assert.True(log[0].At >= log[1].At);
        Assert.True((DateTimeOffset.UtcNow - log[0].At).Duration() < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task M9_A9_a_long_multi_line_message_survives_because_it_travels_on_stdin()
    {
        var git = new GitTrail(_dir);
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        var lines = Enumerable.Range(1, 60).Select(i => $"  {i}. {new string('x', 350)}");
        var message = "opus: turn 1/4 in room lab\n\nShell commands run (60):\n" + string.Join('\n', lines) + "\n";
        Assert.True(message.Length > 20_000);

        var outcome = await git.CommitAllAsync(message, Opus, allowEmpty: false);
        Assert.True(outcome.Created);
        var body = await GitOut(_dir, "log", "-1", "--format=%B");
        Assert.Contains("  60. xxx", body);
        Assert.Contains("Shell commands run (60):", body);
    }

    [Fact]
    public async Task M9_A9_without_git_every_call_is_quiet_and_reasoned_and_nothing_is_created()
    {
        var git = new GitTrail(_dir, () => throw new FileNotFoundException("'git' was not found on PATH"));
        Assert.False(git.IsAvailable());
        Assert.Contains("git is not available", git.Reason);
        Assert.False(await git.InitAsync());
        Assert.Null(await git.TopLevelAsync());
        Assert.Null(await git.HeadAsync());
        Assert.False(await git.IsDirtyAsync());
        Assert.Empty(await git.LogAsync(5));
        var outcome = await git.CommitAllAsync("x", Opus, allowEmpty: true);
        Assert.Null(outcome.Hash);
        Assert.False(outcome.Created);
        Assert.Contains("git is not available", outcome.Reason);
        Assert.False(Directory.Exists(Path.Combine(_dir, ".git")));
    }

    [Fact]
    public async Task M9_A9_a_failing_git_command_names_the_step_and_stderr()
    {
        var git = new GitTrail(_dir, () => new ResolvedCli("fake-git.exe", [], "fake-git.exe"), new FailingRunner());
        var outcome = await git.CommitAllAsync("x", Opus, allowEmpty: false);
        Assert.Null(outcome.Hash);
        Assert.StartsWith("git init exited 128: boom", outcome.Reason);
        Assert.StartsWith("git init exited 128: boom", git.Reason);
    }

    [Fact]
    public void M9_A9_room_trails_hand_out_one_trail_per_normalised_directory()
    {
        var trails = new RoomTrails(dir => new GitTrail(dir));
        var a = trails.For(@"C:\Rooms\lab\");
        var b = trails.For(@"c:\rooms\LAB");
        Assert.Same(a, b);
        Assert.NotSame(a, trails.For(@"C:\Rooms\other"));
    }

    private sealed class FailingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellation) =>
            Task.FromResult(new ProcessResult(128, false, false, "", "boom\n", TimeSpan.Zero));
    }
}
```

(`using ChopItUp.Hub.Rooms;` for `RoomTrails` in the last fact.) Expected: Hub tests 214 + 9 = **223** cases; `MemoryGitTests` 3/3 green without edits.

## Task 5 — `RoomDirectories`, `RoomsApi`, room fields on the read surfaces, wiring (builder: sonnet)

Ticket: `.scratch/m9-rooms-as-chats/issues/05-rooms-api.md`. Acceptance: A1, A2 (the API half), A3, A4, A5 (server half), A11 (endpoint), A12. Blocked by Tasks 1, 3, 4.

### `src/ChopItUp.Hub/Rooms/RoomDirectories.cs` (new)

```csharp
using ChopItUp.Core.Storage;

namespace ChopItUp.Hub.Rooms;

/// <summary>A refusal the owner reads verbatim; never a hub bug.</summary>
public sealed class RoomDirectoryException(string message) : Exception(message);

/// <summary>Turns what the owner typed into a room directory (M9 decisions 2–5): blank means a
/// hub-created folder under the rooms root; the path must pass <see cref="RoomPaths.Refusal"/>; it
/// may not sit inside, or contain, another room's directory; it is created when its parent exists;
/// and it ends up as the root of a git repository (initialised here when it is not one; refused when
/// it is inside someone else's repository, so the hub never commits into a tree it does not own).</summary>
public sealed class RoomDirectories(MessageStore store, RoomTrails trails, RoomPathRules rules, string roomsRoot)
{
    public string RoomsRoot { get; } = RoomPaths.Normalize(roomsRoot);

    public async Task<string> PrepareAsync(string roomId, string? typed, CancellationToken cancellation)
    {
        var hubCreated = string.IsNullOrWhiteSpace(typed);
        var target = hubCreated ? Path.Combine(RoomsRoot, roomId) : typed!.Trim();
        if (RoomPaths.Refusal(target, rules) is { } refused) throw new RoomDirectoryException(refused);
        var full = RoomPaths.Normalize(target);
        var trail = trails.For(full);
        if (!trail.IsAvailable()) throw new RoomDirectoryException("git was not found on PATH; a room directory needs git for its commit trail.");   // before anything touches disk

        foreach (var other in store.ListRooms(includeArchived: true))
        {
            if (other.Id == roomId || other.Directory is null) continue;
            var theirs = RoomPaths.Normalize(other.Directory);
            if (RoomPaths.IsUnderOrEqual(full, theirs) || RoomPaths.IsUnderOrEqual(theirs, full))
                throw new RoomDirectoryException($"'{full}' overlaps room '{other.Id}' ({theirs}); rooms cannot share or nest directories.");
        }

        if (!Directory.Exists(full))
        {
            if (hubCreated) Directory.CreateDirectory(RoomsRoot);
            var parent = Path.GetDirectoryName(full);
            if (parent is null || !Directory.Exists(parent))
                throw new RoomDirectoryException($"'{full}' does not exist and neither does its parent folder; create the parent first.");
            try { Directory.CreateDirectory(full); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new RoomDirectoryException($"'{full}' could not be created: {e.Message}");
            }
        }

        var top = await trail.TopLevelAsync(cancellation);
        if (top is not null && !RoomPaths.Same(RoomPaths.Normalize(top), full))
            throw new RoomDirectoryException($"'{full}' is inside the repository at '{top}'; a room directory must be a repository root.");
        if (top is null && !await trail.InitAsync(cancellation))
            throw new RoomDirectoryException($"git init failed in '{full}': {trail.Reason}");
        return full;
    }
}
```

### `src/ChopItUp.Hub/Web/RoomsApi.cs` (new)

```csharp
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Rooms;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Web;

/// <summary>Room lifecycle for the web UI (M9): create, archive, bind a directory, mark read, and the
/// commit trail. Same no-auth loopback boundary as <see cref="ChatApi"/>; directory work is the hub's
/// alone (D11) — a browser never sends a git command. Room create/bind/archive are refused while a
/// spawn is in flight (plan decision 11).</summary>
public static class RoomsApi
{
    public const string SpawnRunning = "A spawn is in flight; change rooms when the exchange has finished.";
    public const string GeneralStays = "The general room cannot be archived.";
    public const int MaxNameChars = 80;
    public const int TrailLength = 20;

    public static void MapRoomsApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/rooms");
        api.MapPost("", CreateRoom);
        api.MapPost("/{roomId}/archive", Archive);
        api.MapPost("/{roomId}/unarchive", Unarchive);
        api.MapPost("/{roomId}/directory", BindDirectory);
        api.MapPost("/{roomId}/read", MarkRead);
        api.MapGet("/{roomId}/trail", GetTrail);
    }

    private static async Task<IResult> CreateRoom(CreateRoomBody body, MessageStore store, ParticipantStore participants, RoomDirectories directories, SpawnerService spawner, CancellationToken cancellation)
    {
        var name = (body.Name ?? "").Trim();
        if (name.Length is 0 or > MaxNameChars) return Results.BadRequest(new { error = $"name must be 1 to {MaxNameChars} characters." });
        if (spawner.AnySpawnInFlight) return Results.Conflict(new { error = SpawnRunning });
        var id = RoomIds.Unique(name, store.RoomExists);
        string directory;
        try { directory = await directories.PrepareAsync(id, body.Directory, cancellation); }
        catch (RoomDirectoryException e) { return Results.BadRequest(new { error = e.Message }); }
        try { store.CreateRoom(id, name, directory); }
        catch (ArgumentException e) { return Results.Conflict(new { error = e.Message }); }   // lost a race for the id
        return Results.Json(ChatApi.MapRoom(store.GetRoom(id, participants.HumanId())!), statusCode: StatusCodes.Status201Created);
    }

    private static IResult Archive(string roomId, MessageStore store, ParticipantStore participants, SpawnerService spawner)
    {
        if (store.GetRoom(roomId) is not { } room) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        if (roomId == "general") return Results.BadRequest(new { error = GeneralStays });
        if (spawner.AnySpawnInFlight) return Results.Conflict(new { error = SpawnRunning });
        if (room.ArchivedAt is null) store.SetArchived(roomId, DateTimeOffset.UtcNow);
        return Results.Json(ChatApi.MapRoom(store.GetRoom(roomId, participants.HumanId())!));
    }

    private static IResult Unarchive(string roomId, MessageStore store, ParticipantStore participants, SpawnerService spawner)
    {
        if (store.GetRoom(roomId) is null) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        if (spawner.AnySpawnInFlight) return Results.Conflict(new { error = SpawnRunning });
        store.SetArchived(roomId, null);
        return Results.Json(ChatApi.MapRoom(store.GetRoom(roomId, participants.HumanId())!));
    }

    /// <summary>Binds a directory to a legacy (M1–M10) room once. A room created after M9 always has one.</summary>
    private static async Task<IResult> BindDirectory(string roomId, DirectoryBody body, MessageStore store, ParticipantStore participants, RoomDirectories directories, SpawnerService spawner, CancellationToken cancellation)
    {
        if (store.GetRoom(roomId) is not { } room) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        if (room.Directory is not null) return Results.Conflict(new { error = $"Room '{roomId}' already has the directory '{room.Directory}'; a directory is bound once." });
        if (spawner.AnySpawnInFlight) return Results.Conflict(new { error = SpawnRunning });
        string directory;
        try { directory = await directories.PrepareAsync(roomId, body.Directory, cancellation); }
        catch (RoomDirectoryException e) { return Results.BadRequest(new { error = e.Message }); }
        if (!store.BindDirectory(roomId, directory))
            return Results.Conflict(new { error = $"Room '{roomId}' was bound by another request; reload." });
        return Results.Json(ChatApi.MapRoom(store.GetRoom(roomId, participants.HumanId())!));
    }

    /// <summary>The owner's read cursor moves to the room's last message: the same row an MCP
    /// participant's read_messages advances (plan decision 10).</summary>
    private static IResult MarkRead(string roomId, MessageStore store, ParticipantStore participants)
    {
        if (store.GetRoom(roomId) is not { } room) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        if (room.LastMessageId > 0) store.SetCursor(participants.HumanId(), roomId, room.LastMessageId);
        return Results.Json(new { roomId, unread = 0L });
    }

    private static async Task<IResult> GetTrail(string roomId, MessageStore store, RoomTrails trails, CancellationToken cancellation)
    {
        if (store.GetRoom(roomId) is not { } room) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        if (room.Directory is null) return Results.Json(new { directory = (string?)null, commits = Array.Empty<object>(), error = (string?)null });
        var trail = trails.For(room.Directory);
        var commits = await trail.LogAsync(TrailLength, cancellation);
        return Results.Json(new
        {
            directory = room.Directory,
            commits = commits.Select(c => new { c.Hash, c.Author, c.At, c.Subject }),
            error = trail.Reason,
        });
    }

    internal sealed record CreateRoomBody(string? Name, string? Directory);
    internal sealed record DirectoryBody(string? Directory);
}
```

`MessageStore.SetCursor(participant, room, id)` is the existing M1 method (lines 201–213); confirm the parameter order at HEAD before use.

### `src/ChopItUp.Hub/Web/ChatApi.cs` — edits

Line 32–33 becomes:

```csharp
    private static IResult GetRooms(MessageStore store, ParticipantStore participants, bool archived = false) =>
        Results.Json(store.ListRooms(includeArchived: archived, unreadFor: participants.HumanId()).Select(MapRoom));
```

Line 142 becomes (internal so `RoomsApi` shares the one shape):

```csharp
    internal static object MapRoom(Room r) => new { r.Id, r.Name, r.CreatedAt, r.MessageCount, r.LastMessageId, r.Directory, r.ArchivedAt, r.LastActivityAt, r.Unread };
```

### `src/ChopItUp.Hub/Mcp/RoomTools.cs` — edits

The `list_rooms` anonymous object (lines 44–47) gains `r.Directory`; the description gains the sentence: "A room with a directory gives a spawned participant file and shell access inside it; archived rooms are not listed." Archived rooms drop out by the store default — nothing else changes (`RoomExists`, so `post_message` and `read_messages` into an archived room still work, decision 14).

### `src/ChopItUp.Hub/Hosting/HubHost.cs` — edits

`Build` (line 22) gains a trailing `Func<string, GitTrail>? roomGit = null`. After the `MemoryGit` registration (line 76, before `AddMcpServer`):

```csharp
            builder.Services.AddSingleton(new RoomTrails(roomGit ?? (dir => new GitTrail(dir))));
            builder.Services.AddSingleton(sp => new RoomDirectories(
                sp.GetRequiredService<MessageStore>(), sp.GetRequiredService<RoomTrails>(), RoomPathRules.ForHub(options.DataDir), options.RoomsRootPath));
```

After `app.MapChatApi();` (line 109): `app.MapRoomsApi();`. `RoomPathRules.ForHub(dataDir)` reads `AppContext.BaseDirectory` for the install dir, so a deployed hub under `C:\Self Apps\ChopItUp\` refuses its own folder twice over (D12 and the install rule).

### `tests/ChopItUp.Hub.Tests/HubTestHost.cs` — edits

`StartAsync` (line 39) gains `Func<string, GitTrail>? roomGit = null, string? roomsRoot = null`; line 41 becomes

```csharp
        var app = HubHost.Build(new HubOptions(dir, Port: 0, WebRoot: webRoot, RoomsRoot: roomsRoot ?? dir + "_rooms"), processRunner ?? new RefusingProcessRunner(), limits, cliLocator ?? FakeCli.Locate, memoryGit, roomGit);
```

so no test can ever touch `%USERPROFILE%\ChopItUp\rooms`; `RoomsRoot` is exposed as a property (`public string RoomsRoot { get; }` set from the options the host was built with), and `DisposeAsync` also runs `TestDirs.DeleteTree(RoomsRoot)` when `_deleteOnDispose` (a `.git` inside — `TestDirs.DeleteTree` already clears read-only pack files for the memory repo tests).

### `tests/ChopItUp.Hub.Tests/RoomsApiTests.cs` (new; the test project keeps API tests at its root — `ChatApiTests.cs`, `MemoryApiTests.cs`)

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Git;
using ChopItUp.Hub.Rooms;
using ChopItUp.Hub.Spawning;
using ChopItUp.Hub.Tests.Spawning;
using ChopItUp.Hub.Web;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests;

public sealed class RoomsApiTests : IAsyncLifetime
{
    private static readonly SpawnLimits Fast = new(Budget: 4, Debounce: TimeSpan.FromMilliseconds(150), MinSpacing: TimeSpan.Zero, Timeout: TimeSpan.FromSeconds(30), TranscriptMessages: 60, TranscriptChars: 24_000);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_rooms_" + Guid.NewGuid().ToString("N"));
    private readonly FakeProcessRunner _runner = new();
    private HubTestHost _host = null!;

    public async Task InitializeAsync() => _host = await HubTestHost.StartAsync(_dir, processRunner: _runner, limits: Fast);
    public async Task DisposeAsync() => await _host.DisposeAsync();

    private string Sibling(string name) => Path.Combine(_dir + "_side", name);   // outside the data dir and the rooms root; deleted below
    private MessageStore Store => _host.Services.GetRequiredService<MessageStore>();

    private async Task<JsonElement> Rooms(bool archived = false)
    {
        using var doc = JsonDocument.Parse(await _host.Client.GetStringAsync("api/rooms" + (archived ? "?archived=true" : "")));
        return doc.RootElement.Clone();
    }

    private static string[] Ids(JsonElement rooms) => rooms.EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToArray();

    private async Task<(HttpStatusCode Status, JsonElement Body)> Post(string path, object? body = null)
    {
        var r = body is null ? await _host.Client.PostAsync(path, null) : await _host.Client.PostAsJsonAsync(path, body);
        var text = await r.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text.Length == 0 ? "null" : text);
        return (r.StatusCode, doc.RootElement.Clone());
    }

    [Fact]
    public async Task M9_A2_A3_creating_a_room_slugs_the_title_makes_a_git_repository_under_the_rooms_root_and_lists_it_first()
    {
        var (status, room) = await Post("api/rooms", new { name = "Lab Notes" });
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("lab-notes", room.GetProperty("id").GetString());
        Assert.Equal("Lab Notes", room.GetProperty("name").GetString());
        var directory = room.GetProperty("directory").GetString()!;
        Assert.Equal(Path.Combine(_host.RoomsRoot, "lab-notes"), directory);
        Assert.True(Directory.Exists(Path.Combine(directory, ".git")));
        Assert.Equal(JsonValueKind.Null, room.GetProperty("archivedAt").ValueKind);
        Assert.Equal(0, room.GetProperty("unread").GetInt64());

        Assert.Equal(["lab-notes", "general"], Ids(await Rooms()));            // newest activity first
        var (again, second) = await Post("api/rooms", new { name = "Lab Notes" });
        Assert.Equal(HttpStatusCode.Created, again);
        Assert.Equal("lab-notes-2", second.GetProperty("id").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await Post("api/rooms", new { name = "   " })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("api/rooms", new { name = new string('x', 81) })).Status);
    }

    [Fact]
    public async Task M9_A2_refused_directories_come_back_400_with_the_reason_and_no_room_is_created()
    {
        foreach (var (typed, fragment) in new[]
                 {
                     (@"C:\", "drive root"),
                     (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "user profile folder"),
                     (Path.Combine(_dir, "inside"), "the hub's data folder"),
                     (@"C:\Self Apps\ChopItUp", @"C:\Self Apps"),
                     (@"\\server\share\x", "Network and device paths"),
                     ("relative", "absolute local path"),
                 })
        {
            var (status, body) = await Post("api/rooms", new { name = "Nope", directory = typed });
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains(fragment, body.GetProperty("error").GetString());
        }
        Assert.Equal(["general"], Ids(await Rooms(archived: true)));
    }

    [Fact]
    public async Task M9_A2_a_directory_inside_another_repository_or_overlapping_another_room_is_refused_and_an_existing_repository_is_adopted()
    {
        var outer = Sibling("outer");
        Assert.True(await new GitTrail(outer).InitAsync());
        Directory.CreateDirectory(Path.Combine(outer, "inner"));
        var (nested, nestedBody) = await Post("api/rooms", new { name = "Nested", directory = Path.Combine(outer, "inner") });
        Assert.Equal(HttpStatusCode.BadRequest, nested);
        Assert.Contains("inside the repository at", nestedBody.GetProperty("error").GetString());

        var (adopted, adoptedBody) = await Post("api/rooms", new { name = "Outer", directory = outer });   // root of its own repo: fine
        Assert.Equal(HttpStatusCode.Created, adopted);
        Assert.Equal(RoomPaths.Normalize(outer), adoptedBody.GetProperty("directory").GetString());

        var (overlapBelow, b1) = await Post("api/rooms", new { name = "Below", directory = Path.Combine(outer, "inner") });
        Assert.Equal(HttpStatusCode.BadRequest, overlapBelow);
        Assert.Contains("overlaps room 'outer'", b1.GetProperty("error").GetString());
        var (overlapAbove, b2) = await Post("api/rooms", new { name = "Above", directory = Path.GetDirectoryName(outer)! });
        Assert.Equal(HttpStatusCode.BadRequest, overlapAbove);
        Assert.Contains("overlaps room 'outer'", b2.GetProperty("error").GetString());

        var orphan = Path.Combine(Sibling("missing-parent"), "child");
        var (noParent, b3) = await Post("api/rooms", new { name = "Orphan", directory = orphan });
        Assert.Equal(HttpStatusCode.BadRequest, noParent);
        Assert.Contains("parent folder", b3.GetProperty("error").GetString());
    }

    [Fact]
    public async Task M9_A4_archive_hides_a_room_keeps_it_reachable_and_unarchive_restores_it_and_general_stays()
    {
        await Post("api/rooms", new { name = "Old" });
        var (status, archived) = await Post("api/rooms/old/archive");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotEqual(JsonValueKind.Null, archived.GetProperty("archivedAt").ValueKind);
        Assert.Equal(["general"], Ids(await Rooms()));
        Assert.Equal(["old", "general"], Ids(await Rooms(archived: true)));

        Assert.Equal(HttpStatusCode.Created, (await Post("api/rooms/old/messages", new { body = "still here" })).Status);   // hides, never blocks
        Assert.True(Directory.Exists(Path.Combine(_host.RoomsRoot, "old", ".git")));
        Assert.Equal(HttpStatusCode.OK, (await Post("api/rooms/old/archive")).Status);                                      // idempotent

        var (back, restored) = await Post("api/rooms/old/unarchive");
        Assert.Equal(HttpStatusCode.OK, back);
        Assert.Equal(JsonValueKind.Null, restored.GetProperty("archivedAt").ValueKind);
        Assert.Equal(["old", "general"], Ids(await Rooms()));

        var (general, body) = await Post("api/rooms/general/archive");
        Assert.Equal(HttpStatusCode.BadRequest, general);
        Assert.Equal(RoomsApi.GeneralStays, body.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Post("api/rooms/nope/archive")).Status);
    }

    [Fact]
    public async Task M9_A5_a_legacy_room_binds_a_directory_once_then_409()
    {
        Assert.Equal(JsonValueKind.Null, (await Rooms()).EnumerateArray().Single().GetProperty("directory").ValueKind);   // general is NULL after migration
        var typed = Sibling("gen");
        Directory.CreateDirectory(Path.GetDirectoryName(typed)!);
        var (status, room) = await Post("api/rooms/general/directory", new { directory = typed });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(RoomPaths.Normalize(typed), room.GetProperty("directory").GetString());
        Assert.True(Directory.Exists(Path.Combine(typed, ".git")));

        var (again, body) = await Post("api/rooms/general/directory", new { directory = Sibling("gen2") });
        Assert.Equal(HttpStatusCode.Conflict, again);
        Assert.Contains("bound once", body.GetProperty("error").GetString());
        Assert.Equal(RoomPaths.Normalize(typed), Store.GetRoom("general")!.Directory);

        var (blank, hubMade) = await Post("api/rooms/blank-legacy/directory", new { directory = "" });   // unknown room
        Assert.Equal(HttpStatusCode.NotFound, blank);
        Store.CreateRoom("legacy", "Legacy", null);
        var (made, madeBody) = await Post("api/rooms/legacy/directory", new { directory = "" });        // blank = hub-created
        Assert.Equal(HttpStatusCode.OK, made);
        Assert.Equal(Path.Combine(_host.RoomsRoot, "legacy"), madeBody.GetProperty("directory").GetString());
    }

    [Fact]
    public async Task M9_A3_A5_unread_is_what_lies_past_the_owners_cursor_and_mark_read_zeroes_it()
    {
        await using var claude = await _host.ClientFor("claude");
        foreach (var body in new[] { "one", "two" })
            HubTestHost.Json(await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = body, ["client_key"] = Guid.NewGuid().ToString() }));
        Assert.Equal(2, (await Rooms()).EnumerateArray().Single().GetProperty("unread").GetInt64());

        await Post("api/rooms/general/messages", new { body = "mine" });                   // own post moves own cursor past everything before it (claim 5)
        Assert.Equal(0, (await Rooms()).EnumerateArray().Single().GetProperty("unread").GetInt64());
        HubTestHost.Json(await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "three", ["client_key"] = Guid.NewGuid().ToString() }));
        Assert.Equal(1, (await Rooms()).EnumerateArray().Single().GetProperty("unread").GetInt64());

        var (status, read) = await Post("api/rooms/general/read");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, read.GetProperty("unread").GetInt64());
        Assert.Equal(0, (await Rooms()).EnumerateArray().Single().GetProperty("unread").GetInt64());
        Assert.Equal(HttpStatusCode.NotFound, (await Post("api/rooms/nope/read")).Status);
    }

    [Fact]
    public async Task M9_A11_the_trail_is_empty_for_a_fresh_or_directoryless_room_and_lists_commits_newest_first()
    {
        var (_, general) = (HttpStatusCode.OK, JsonDocument.Parse(await _host.Client.GetStringAsync("api/rooms/general/trail")).RootElement);
        Assert.Equal(JsonValueKind.Null, general.GetProperty("directory").ValueKind);
        Assert.Empty(general.GetProperty("commits").EnumerateArray());

        var (_, room) = await Post("api/rooms", new { name = "Lab" });
        var directory = room.GetProperty("directory").GetString()!;
        var fresh = JsonDocument.Parse(await _host.Client.GetStringAsync("api/rooms/lab/trail")).RootElement;
        Assert.Equal(directory, fresh.GetProperty("directory").GetString());
        Assert.Empty(fresh.GetProperty("commits").EnumerateArray());

        var trail = _host.Services.GetRequiredService<RoomTrails>().For(directory);
        File.WriteAllText(Path.Combine(directory, "a.txt"), "a");
        await trail.CommitAllAsync("owner: edits before the next spawn in room lab", new GitIdentity("Owner", "owner@chopitup.local"), allowEmpty: false);
        await trail.CommitAllAsync("opus: turn 1/4 in room lab\n\nShell commands run: none.\n", new GitIdentity("Opus", "opus@chopitup.local"), allowEmpty: true);

        var commits = JsonDocument.Parse(await _host.Client.GetStringAsync("api/rooms/lab/trail")).RootElement.GetProperty("commits").EnumerateArray().ToList();
        Assert.Equal(2, commits.Count);
        Assert.Equal("opus: turn 1/4 in room lab", commits[0].GetProperty("subject").GetString());
        Assert.Equal("Opus <opus@chopitup.local>", commits[0].GetProperty("author").GetString());
        Assert.Equal("owner: edits before the next spawn in room lab", commits[1].GetProperty("subject").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Client.GetAsync("api/rooms/nope/trail")).StatusCode);
    }

    [Fact]
    public async Task M9_A2_A4_A5_create_bind_and_archive_are_409_while_a_spawn_is_in_flight_and_work_after_it_ends()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (_, _, ct) => { await release.Task.WaitAsync(ct); return FakeProcessRunner.Ok("""{"result":"done"}"""); };
        Store.CreateRoom("side", "Side", null);
        Assert.Equal(HttpStatusCode.Created, (await Post("api/rooms/general/messages", new { body = "@opus hi" })).Status);
        await _runner.NextSpecAsync(Wait);
        var spawner = _host.Services.GetRequiredService<SpawnerService>();
        Assert.True(spawner.AnySpawnInFlight);

        foreach (var (path, body) in new (string, object?)[]
                 {
                     ("api/rooms", new { name = "Planted" }),
                     ("api/rooms/side/directory", new { directory = "" }),
                     ("api/rooms/side/archive", null),
                     ("api/rooms/side/unarchive", null),
                 })
        {
            var (status, reply) = await Post(path, body);
            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Equal(RoomsApi.SpawnRunning, reply.GetProperty("error").GetString());
        }
        Assert.Equal(["general", "side"], Ids(await Rooms(archived: true)));   // general's message is newer than side's creation
        Assert.Null(Store.GetRoom("side")!.Directory);

        release.SetResult();
        var deadline = DateTime.UtcNow + Wait;
        while (spawner.AnySpawnInFlight && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.False(spawner.AnySpawnInFlight);
        Assert.Equal(HttpStatusCode.Created, (await Post("api/rooms", new { name = "Planted" })).Status);
        Assert.Equal(HttpStatusCode.OK, (await Post("api/rooms/side/archive")).Status);
    }

    [Fact]
    public async Task M9_A2_without_git_creating_or_binding_a_directory_room_is_400_and_leaves_no_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_roomsnogit_" + Guid.NewGuid().ToString("N"));
        await using var host = await HubTestHost.StartAsync(dir, roomGit: d => new GitTrail(d, () => throw new FileNotFoundException("'git' was not found on PATH")));
        var r = await host.Client.PostAsJsonAsync("api/rooms", new { name = "No Git" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("git was not found on PATH", await r.Content.ReadAsStringAsync());
        Assert.False(Directory.Exists(Path.Combine(host.RoomsRoot, "no-git")));
        Assert.Empty(host.Services.GetRequiredService<MessageStore>().ListRooms(includeArchived: true).Where(x => x.Id == "no-git"));
        var bind = await host.Client.PostAsJsonAsync("api/rooms/general/directory", new { directory = "" });
        Assert.Equal(HttpStatusCode.BadRequest, bind.StatusCode);
        Assert.Null(host.Services.GetRequiredService<MessageStore>().GetRoom("general")!.Directory);
    }
}
```

The `Sibling` folder (`<dir>_side`) is deleted in `DisposeAsync` with `TestDirs.DeleteTree(_dir + "_side")` after the host is disposed (add that line). The `M9_A2` profile row uses the real `%USERPROFILE%` string only as input to a refusal — nothing is created there.

### `tests/ChopItUp.Hub.Tests/RoomToolsTests.cs` (test-project root) — add one fact

```csharp
    [Fact]
    public async Task M9_A3_list_rooms_carries_the_directory_and_omits_archived_rooms()
    {
        var store = _host.Services.GetRequiredService<MessageStore>();
        store.CreateRoom("lab", "Lab", @"C:\Rooms\lab");      // the store does not validate; the API does
        store.CreateRoom("old", "Old", null);
        store.SetArchived("old", DateTimeOffset.UtcNow);
        await using var client = await _host.ClientFor("claude");
        var rooms = HubTestHost.Json(await client.CallToolAsync("list_rooms", new Dictionary<string, object?>())).GetProperty("rooms").EnumerateArray().ToList();
        Assert.Equal(["lab", "general"], rooms.Select(r => r.GetProperty("id").GetString()).ToArray());
        Assert.Equal(@"C:\Rooms\lab", rooms[0].GetProperty("directory").GetString());
        Assert.Equal(JsonValueKind.Null, rooms[1].GetProperty("directory").ValueKind);
    }
```

(Match the class's existing field names for the host and the JSON casing `RosterJsonOptions` produces — open the file first; the property is `rooms`/`directory` if the options are snake_case or camelCase, `Rooms`/`Directory` if they are not.)

Expected: Hub tests 223 + 9 + 1 = **233** cases; `ChatApiTests` unchanged (the room JSON only gained fields).

## Task 6 — Spawner integration: directory spawns, exclusivity, the commit trail (builder: sonnet)

Ticket: `.scratch/m9-rooms-as-chats/issues/06-spawner-trail.md`. Acceptance: A6, A7, A8, A9, A10. Blocked by Tasks 1, 2, 4, 5.

### `src/ChopItUp.Hub/Spawning/ExchangePolicy.cs` — edits

`Due` (line 87) and `NextWake` (line 103) gain a trailing `bool exclusive = false`:

```csharp
    /// <summary>… <paramref name="exclusive"/> (a directory room, M9 decision 6): at most one spawn
    /// in the room at a time — nothing is due while anything is in flight, and only the first
    /// pending spawn launches per pass; the completion wakes the loop for the next.</summary>
    public IReadOnlyList<SpawnRequest> Due(Exchange x, DateTimeOffset now, IReadOnlyDictionary<string, DateTimeOffset> lastStartByParticipant, IReadOnlySet<string> inFlightInRoom, bool exclusive = false)
    {
        if (x.Status != ExchangeStatus.Open) return [];
        if (exclusive && inFlightInRoom.Count > 0) return [];
        var due = new List<SpawnRequest>();
        foreach (var (id, pending) in x.Pending)
        {
            if (inFlightInRoom.Contains(id)) continue;
            if (now - pending.LastTriggerAt < _limits.Debounce) continue;
            if (lastStartByParticipant.TryGetValue(id, out var last) && now - last < _limits.MinSpacing) continue;
            due.Add(new SpawnRequest(x.RoomId, id, pending.TriggerIds.ToList(), x.RootMessageId, x.TurnsStarted + due.Count + 1, x.Budget - x.TurnsCommitted));
            if (exclusive) break;
        }
        return due;
    }

    public DateTimeOffset? NextWake(Exchange x, DateTimeOffset now, IReadOnlyDictionary<string, DateTimeOffset> lastStartByParticipant, IReadOnlySet<string> inFlightInRoom, bool exclusive = false)
    {
        if (x.Status != ExchangeStatus.Open) return null;
        if (exclusive && inFlightInRoom.Count > 0) return null;   // the completion wakes the loop
        … (body unchanged)
```

### `src/ChopItUp.Hub/Spawning/SpawnPrompt.cs` — edits

`SpawnPromptInput` gains a trailing `string? Directory = null`. The sentence at line 49 is the last `.Append` of one fluent chain that starts at line 46: terminate the chain after the `Mention a participant…` `Append` with `;` and write the replacement below as a new statement:

```csharp
        if (input.Directory is null)
            sb.Append("You are stateless: this transcript is all you know of the room. You have no files and no tools besides this hub; your memory is the section below.\n");
        else
        {
            sb.Append("You are stateless: this transcript is all you know of the room; your memory is the section below.\n\n");
            sb.Append("Files: this room's directory is ").Append(input.Directory).Append(", a git repository and your working directory. ")
              .Append("You can read, edit, create, search and run shell commands there, with network access. ")
              .Append(DirectoryRules(input.Directory)).Append(' ')
              .Append("Files you create or change are the deliverable; still post your reply to the room as described above.\n");
        }
```

### `src/ChopItUp.Hub/Spawning/RoomCommits.cs` (new)

```csharp
using System.Text;
using ChopItUp.Core.Model;
using ChopItUp.Hub.Git;

namespace ChopItUp.Hub.Spawning;

/// <summary>Who a room commit is by and what it says (M9 decisions 6, 7). The author is the participant
/// row — display name and <c>&lt;id&gt;@chopitup.local</c> — so `git log` in the room reads as the chat
/// does; the committer is always the hub (<see cref="GitTrail.Hub"/>). The subject is one line the
/// trail dialog shows; the body carries the shell log.</summary>
public static class RoomCommits
{
    public const string Domain = "chopitup.local";
    public const string ShellHeader = "Shell commands run";

    public static GitIdentity IdentityOf(Participant p) => new(p.DisplayName, p.Id + "@" + Domain);

    public static string OwnerMessage(Participant owner, string roomId) =>
        $"{owner.Id}: edits before the next spawn in room {roomId}\n";

    public static string AgentSubject(Participant agent, string roomId, int turn, int budget) =>
        $"{agent.Id}: turn {turn}/{budget} in room {roomId}";

    public static string AgentMessage(Participant agent, string roomId, int turn, int budget, IReadOnlyList<ShellCommand> commands, bool headMoved)
    {
        var sb = new StringBuilder(AgentSubject(agent, roomId, turn, budget)).Append("\n\n");
        if (commands.Count == 0) sb.Append(ShellHeader).Append(": none.\n");
        else
        {
            sb.Append(ShellHeader).Append(" (").Append(commands.Count).Append("):\n");
            for (int i = 0; i < commands.Count; i++)
                sb.Append("  ").Append(i + 1).Append(". ").Append(commands[i].Command).Append(commands[i].Denied ? " [denied]" : "").Append('\n');
        }
        if (headMoved) sb.Append("\nHEAD moved during this spawn: the model committed on its own; its commit(s) sit below this one in the log.\n");
        return sb.ToString();
    }
}
```

### `src/ChopItUp.Hub/Memory/HubNotes.cs` — additions (`using ChopItUp.Hub.Git;` joins the usings for `CommitOutcome`)

```csharp
    public const string TrailPrefix = "Committed ";
    public const string TrailFailedPrefix = "Not committed for ";

    /// <summary>The room's record of what the trail did around one spawn (M9 decision 6). One line;
    /// the commit itself is the detail.</summary>
    public static string Trail(string participantId, CommitOutcome? owner, CommitOutcome agent, int commands, bool headMoved)
    {
        if (agent.Hash is null || !agent.Created)
            return $"{TrailFailedPrefix}{participantId}: {agent.Reason ?? "git made no commit"}.";
        var text = $"{TrailPrefix}{agent.Hash} as {participantId}: {agent.FilesChanged} file(s) changed, {commands} shell command(s).";
        if (owner is { Created: true }) text += $" Your edits were committed first as {owner.Hash}.";
        if (headMoved) text += $" HEAD moved during the spawn: {participantId} committed on its own.";
        return text;
    }
```

### `src/ChopItUp.Hub/Spawning/SpawnerService.cs` — edits

1. `FinishedEvent` (line 35) becomes `FinishedEvent(SpawnHandle Handle, ProcessResult Result, TrailReport? Trail)`; add beside it `private sealed record TrailReport(CommitOutcome? Owner, CommitOutcome Agent, int Commands, bool HeadMoved);`.
2. `SpawnHandle` (39–50) gains `public string? Directory { get; init; }`.
3. Constructor (76–78) gains `RoomTrails trails, ParticipantStore participants` after `MemoryStore memory`; fields `_trails` and `_owner = roster.First(p => p.Id == participants.HumanId())`.
4. `LaunchDue` (190–201) and `ArmWake` (323–341) pass `exclusive: _store.GetRoom(x.RoomId)?.Directory is not null` to `Due`/`NextWake` (one indexed read per open exchange per pass; a bind mid-life is picked up on the next pass).
5. `Launch` (206–262):
   - after `var participant = …` add `var room = _store.GetRoom(request.RoomId); var directory = room?.Directory;` and use `room?.Name ?? request.RoomId` instead of `RoomName(request.RoomId)` (delete `RoomName`, line 380, if nothing else uses it);
   - the `SpawnPromptInput` gains `Directory: directory` as its last argument;
   - the switch becomes:

```csharp
                case "claude":
                {
                    var mcpPath = Path.Combine(workDir, "mcp.json");
                    File.WriteAllText(mcpPath, SpawnCommands.ClaudeMcpConfigJson(McpUrl(), token));
                    if (directory is null)
                        spec = SpawnCommands.Claude(Cli("claude"), participant.Model!, mcpPath, workDir, prompt, label);
                    else
                    {
                        var settingsPath = Path.Combine(workDir, "settings.json");     // scratch, never the room (decision 8)
                        File.WriteAllText(settingsPath, SpawnCommands.ClaudeSettingsJson());
                        spec = SpawnCommands.ClaudeInDirectory(Cli("claude"), participant.Model!, mcpPath, settingsPath, SpawnPrompt.DirectoryRules(directory), directory, prompt, label);
                    }
                    break;
                }
                case "codex":
                    spec = directory is null
                        ? SpawnCommands.Codex(Cli("codex"), participant.Model!, McpUrl(), token, workDir, Path.Combine(workDir, "last.txt"), prompt, label)
                        : SpawnCommands.CodexInDirectory(Cli("codex"), participant.Model!, McpUrl(), token, directory, Path.Combine(workDir, "last.txt"), prompt, label);
                    break;
```

   - the handle gets `Directory = directory`; capture `var turn = request.TurnNumber; var budget = x.Budget; var roomId = request.RoomId; var host = participant.Host;` before `Task.Run`; the `handle.Run = Task.Run(...)` block (247–253) becomes:

```csharp
            handle.Run = Task.Run(async () =>
            {
                var result = new ProcessResult(null, false, false, "", "not started", TimeSpan.Zero);
                TrailReport? trail = null;
                try
                {
                    GitTrail? git = null;
                    CommitOutcome? owner = null;
                    string? headBefore = null;
                    if (directory is not null)
                    {
                        // Before: the owner's edits since the last spawn become their own commit (decision 6).
                        git = _trails.For(directory);
                        if (await git.IsDirtyAsync(CancellationToken.None))
                            owner = await git.CommitAllAsync(RoomCommits.OwnerMessage(_owner, roomId), RoomCommits.IdentityOf(_owner), allowEmpty: false, CancellationToken.None);
                        headBefore = await git.HeadAsync(CancellationToken.None);
                    }
                    try { result = await _runner.RunAsync(spec, _limits.Timeout, handle.Cancel.Token); }
                    catch (Exception e) { result = new ProcessResult(null, false, false, "", "launch failed: " + e.Message, TimeSpan.Zero); }
                    if (git is not null)
                    {
                        // After: always a commit, empty or not, timed out or not — the trail says the spawn happened.
                        var commands = (host == "codex" ? SpawnOutput.CodexShellCommands(result.StandardOutput) : SpawnOutput.ClaudeShellCommands(result.StandardOutput))
                            .Select(c => c with { Command = Scrub(StripAnsi(c.Command), token) }).ToList();
                        var headMoved = await git.HeadAsync(CancellationToken.None) != headBefore;
                        var agent = await git.CommitAllAsync(RoomCommits.AgentMessage(participant, roomId, turn, budget, commands, headMoved), RoomCommits.IdentityOf(participant), allowEmpty: true, CancellationToken.None);
                        trail = new TrailReport(owner, agent, commands.Count, headMoved);
                    }
                }
                catch (Exception e) { Console.Error.WriteLine($"spawn {spawnId}: trail error {e.GetType().Name}: {e.Message}"); }
                finally { _events.Writer.TryWrite(new FinishedEvent(handle, result, trail)); }   // always: OnFinished is the only place _live is decremented
            });
```

   `CancellationToken.None` on the git calls is deliberate: a stop or a shutdown must not leave the tree uncommitted; each git call is bounded by `GitTrail.Timeout` (30 s).
6. `OnFinished(SpawnHandle h, ProcessResult r)` becomes `OnFinished(SpawnHandle h, ProcessResult r, TrailReport? trail)` (update the loop's dispatch of `FinishedEvent`); **after** `TryDeleteDir(h.WorkDir);` (line 298) and before `h.Cancel.Dispose();` add (after the delete, so a test that waits on the note can assert the scratch folder is gone):

```csharp
        if (trail is not null) PostNote(room, HubNotes.Trail(id, trail.Owner, trail.Agent, trail.Commands, trail.HeadMoved));
```

   `TryDeleteDir(h.WorkDir)` still deletes only the scratch folder under `<data>\spawns\`; the room directory is never touched by the spawner (A6).
7. The `StartAsync` sweep of `<data>\spawns` (line 109) is unchanged: `settings.json` lives there and is swept with the rest. Beside it, one warning pass (decision 6's crash window): for every room with a directory in `_store.ListRooms(includeArchived: true)`, `if (await _trails.For(room.Directory).IsDirtyAsync(stoppingToken)) Console.Error.WriteLine($"room {room.Id}: {room.Directory} has uncommitted changes at startup (a spawn may have ended without its commit); the next spawn commits them as the owner");` — a log line, no commit, no note.

### Tests

`tests/ChopItUp.Hub.Tests/Spawning/ExchangePolicyTests.cs` — add:

```csharp
    [Fact]
    public void M9_A10_an_exclusive_room_launches_one_pending_spawn_per_pass_and_nothing_while_one_is_in_flight()
    {
        var p = Policy();
        var (x, _) = p.OnMessage(null, Msg(10, "owner", "@opus @gpt-6-astra go"), T0);
        var later = T0 + Limits.Debounce;
        Assert.Equal(2, p.Due(x!, later, NoStarts, Nobody).Count);                          // a NULL-directory room: both
        var one = p.Due(x!, later, NoStarts, Nobody, exclusive: true);
        Assert.Equal(["opus"], one.Select(r => r.ParticipantId).ToArray());
        Assert.Equal(1, one[0].TurnNumber);
        Assert.Empty(p.Due(x!, later, NoStarts, new HashSet<string> { "opus" }, exclusive: true));
        Assert.Null(p.NextWake(x!, later, NoStarts, new HashSet<string> { "opus" }, exclusive: true));
        Assert.NotNull(p.NextWake(x!, T0, NoStarts, Nobody, exclusive: true));
        ExchangePolicy.Started(x!, one[0]);
        ExchangePolicy.Finished(x!, "opus");
        var next = p.Due(x!, later, NoStarts, Nobody, exclusive: true);
        Assert.Equal(["gpt-6-astra"], next.Select(r => r.ParticipantId).ToArray());
        Assert.Equal(2, next[0].TurnNumber);
    }
```

`tests/ChopItUp.Hub.Tests/Spawning/SpawnPromptTests.cs` — add:

```csharp
    [Fact]
    public void M9_A7_a_directory_room_prompt_names_the_directory_the_fence_and_the_git_rule_and_a_plain_room_keeps_the_no_files_sentence()
    {
        var plain = SpawnPrompt.Render(Input(1, 3, Msg(1, "owner", "@opus hi")), SpawnLimits.Default);
        Assert.Contains("You have no files and no tools besides this hub", plain);
        Assert.DoesNotContain("Files: this room's directory", plain);

        var withDir = SpawnPrompt.Render(Input(1, 3, Msg(1, "owner", "@opus hi")) with { Directory = @"C:\Rooms\lab" }, SpawnLimits.Default);
        Assert.Contains(@"Files: this room's directory is C:\Rooms\lab, a git repository and your working directory.", withDir);
        Assert.Contains("do not read, list, create or change anything outside this directory", withDir);
        Assert.Contains("Do not run git commands that write", withDir);
        Assert.Contains("git log, git status and git diff are fine.", withDir);
        Assert.Contains(SpawnPrompt.DirectoryRules(@"C:\Rooms\lab"), withDir);
        Assert.DoesNotContain("You have no files", withDir);
        Assert.Contains("post_message exactly once", withDir);
    }
```

`tests/ChopItUp.Hub.Tests/Spawning/RoomCommitsTests.cs` (new):

```csharp
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Git;
using ChopItUp.Hub.Memory;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Spawning;

public sealed class RoomCommitsTests
{
    private static readonly ChopItUp.Core.Model.Participant Sonnet = ChopDb.SeedRoster.Single(p => p.Id == "sonnet");
    private static readonly ChopItUp.Core.Model.Participant Owner = ChopDb.SeedRoster.Single(p => p.Id == "owner");

    [Fact]
    public void M9_A9_identity_is_display_name_and_id_at_the_hub_domain()
    {
        Assert.Equal("Sonnet <sonnet@chopitup.local>", RoomCommits.IdentityOf(Sonnet).ToString());
        Assert.Equal("Owner <owner@chopitup.local>", RoomCommits.IdentityOf(Owner).ToString());
    }

    [Fact]
    public void M9_A9_the_agent_message_lists_commands_numbered_marks_denials_and_says_none_when_empty()
    {
        Assert.Equal("sonnet: turn 2/4 in room lab\n\nShell commands run: none.\n", RoomCommits.AgentMessage(Sonnet, "lab", 2, 4, [], false));
        var m = RoomCommits.AgentMessage(Sonnet, "lab", 1, 4, [new("echo hi", false), new("git commit -m x", true)], true);
        Assert.StartsWith("sonnet: turn 1/4 in room lab\n\nShell commands run (2):\n  1. echo hi\n  2. git commit -m x [denied]\n", m);
        Assert.Contains("HEAD moved during this spawn", m);
        Assert.Equal("owner: edits before the next spawn in room lab\n", RoomCommits.OwnerMessage(Owner, "lab"));
    }

    [Fact]
    public void M9_A9_the_trail_note_reports_the_commit_the_owner_precommit_and_failures()
    {
        var agent = new CommitOutcome("abc1234", true, 2, null);
        Assert.Equal("Committed abc1234 as sonnet: 2 file(s) changed, 3 shell command(s).", HubNotes.Trail("sonnet", null, agent, 3, false));
        Assert.Equal("Committed abc1234 as sonnet: 2 file(s) changed, 3 shell command(s). Your edits were committed first as 9999999.",
            HubNotes.Trail("sonnet", new CommitOutcome("9999999", true, 1, null), agent, 3, false));
        Assert.Equal("Committed abc1234 as sonnet: 0 file(s) changed, 0 shell command(s). HEAD moved during the spawn: sonnet committed on its own.",
            HubNotes.Trail("sonnet", new CommitOutcome("9999999", false, 0, null), agent with { FilesChanged = 0 }, 0, true));
        Assert.Equal("Not committed for sonnet: git commit exited 128: boom.", HubNotes.Trail("sonnet", null, new CommitOutcome(null, false, 0, "git commit exited 128: boom"), 0, false));
    }
}
```

`tests/ChopItUp.Hub.Tests/Spawning/SpawnerServiceTests.Rooms.cs` (new; same partial class, so `Fast`, `Wait`, `_runner`, `_host`, `PostAs`, `WaitForMessage` are available — the general-room helpers are not reused because these tests post into `lab`):

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Git;
using ChopItUp.Hub.Memory;
using ChopItUp.Hub.Rooms;
using ChopItUp.Hub.Spawning;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests.Spawning;

public sealed partial class SpawnerServiceTests
{
    private const string ClaudeStreamWithTwoCommands = """
        {"type":"system","subtype":"init"}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"echo hello"}}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Bash","input":{"command":"git commit -m nope"}}]}}
        {"type":"result","subtype":"success","is_error":false,"result":"done","permission_denials":[{"tool_name":"Bash","tool_use_id":"t2","tool_input":{"command":"git commit -m nope"}}]}
        """;

    private async Task<string> MakeRoom(string id)
    {
        var dir = Path.Combine(_host.RoomsRoot, id);
        Assert.True(await new GitTrail(dir).InitAsync());
        _host.Services.GetRequiredService<MessageStore>().CreateRoom(id, id.ToUpperInvariant(), dir);
        return dir;
    }

    private async Task PostAsOwnerIn(string room, string body)
    {
        var r = await _host.Client.PostAsJsonAsync($"api/rooms/{room}/messages", new { body });
        Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode);
    }

    private async Task<List<(string Author, string Body)>> MessagesIn(string room)
    {
        using var doc = JsonDocument.Parse(await _host.Client.GetStringAsync($"api/rooms/{room}/messages?afterId=0&limit=200"));
        return doc.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => (m.GetProperty("authorId").GetString()!, m.GetProperty("body").GetString()!)).ToList();
    }

    private async Task<(string Author, string Body)> WaitForMessageIn(string room, Func<(string Author, string Body), bool> match)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            var hit = (await MessagesIn(room)).FirstOrDefault(match);
            if (hit != default) return hit;
            await Task.Delay(100);
        }
        throw new TimeoutException($"No matching message in '{room}' within {Wait}");
    }

    private static async Task<string> GitLog(string dir, string format, int n = 5)
    {
        var r = await new ProcessRunner().RunAsync(
            new ProcessSpec(CliResolver.Resolve("git").FileName, ["log", $"--format={format}", "-n", n.ToString()], new Dictionary<string, string>(), dir, "", "test-git"),
            TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(0, r.ExitCode);
        return r.StandardOutput.Trim();
    }

    [Fact]
    public async Task M9_A6_A8_A9_A10_a_directory_room_spawn_runs_in_the_room_with_settings_in_scratch_and_the_hub_commits_owner_then_agent()
    {
        var dir = await MakeRoom("lab");
        File.WriteAllText(Path.Combine(dir, "notes.md"), "owner wrote this before the spawn\n");
        string? settingsAtLaunch = null;
        _runner.Handler = async (spec, _, _) =>
        {
            var args = spec.Arguments.ToList();
            settingsAtLaunch = File.ReadAllText(args[args.IndexOf("--settings") + 1]);
            File.WriteAllText(Path.Combine(spec.WorkingDirectory, "hello.txt"), "made by the model\n");
            await PostAsIn("sonnet", "lab", "Wrote hello.txt.");
            return FakeProcessRunner.Ok(ClaudeStreamWithTwoCommands);
        };

        await PostAsOwnerIn("lab", "@sonnet create hello.txt");
        var spec = await _runner.NextSpecAsync(Wait);
        Assert.Equal(dir, spec.WorkingDirectory);
        Assert.Contains("dontAsk", spec.Arguments);
        Assert.Equal("stream-json", spec.Arguments[spec.Arguments.ToList().IndexOf("--output-format") + 1]);
        var settingsPath = spec.Arguments[spec.Arguments.ToList().IndexOf("--settings") + 1];
        Assert.StartsWith(Path.Combine(_dir, "spawns"), settingsPath);                         // scratch, not the room
        Assert.StartsWith(Path.Combine(_dir, "spawns"), spec.Arguments[spec.Arguments.ToList().IndexOf("--mcp-config") + 1]);
        Assert.Contains(@"Files: this room's directory is " + dir, spec.StandardInput);
        Assert.Equal(SpawnPrompt.DirectoryRules(dir), spec.Arguments[spec.Arguments.ToList().IndexOf("--append-system-prompt") + 1]);   // F10: the fence in the system channel

        var note = await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith(HubNotes.TrailPrefix));
        Assert.Contains("as sonnet: 1 file(s) changed, 2 shell command(s).", note.Body);
        Assert.Contains("Your edits were committed first as", note.Body);
        Assert.Contains("\"Bash(git commit *)\"", settingsAtLaunch);

        var log = (await GitLog(dir, "%an <%ae>|%cn|%s")).Split('\n');
        Assert.Equal(2, log.Length);
        Assert.Equal("Sonnet <sonnet@chopitup.local>|ChopItUp hub|sonnet: turn 1/4 in room lab", log[0]);
        Assert.Equal("Owner <owner@chopitup.local>|ChopItUp hub|owner: edits before the next spawn in room lab", log[1]);
        var body = await GitLog(dir, "%B", 1);
        Assert.Contains("Shell commands run (2):\n  1. echo hello\n  2. git commit -m nope [denied]", body.Replace("\r\n", "\n"));
        Assert.True(File.Exists(Path.Combine(dir, "hello.txt")));                             // the room survives the spawn
        Assert.True(File.Exists(Path.Combine(dir, "notes.md")));
        Assert.False(Directory.Exists(Path.GetDirectoryName(settingsPath)!));                 // the scratch folder does not
        Assert.False(File.Exists(Path.Combine(dir, "settings.json")));
        Assert.False(File.Exists(Path.Combine(dir, "mcp.json")));
    }

    [Fact]
    public async Task M9_A10_a_directory_room_runs_one_spawn_at_a_time_while_general_still_runs_them_side_by_side()
    {
        await MakeRoom("lab");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, ct) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "opus") await release.Task.WaitAsync(ct);
            return FakeProcessRunner.Ok("""{"type":"result","result":"done"}""");
        };

        await PostAsOwnerIn("lab", "@opus @gpt-6-astra both of you");
        var first = await _runner.NextSpecAsync(Wait);
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(first));
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromSeconds(1)));                    // the second waits
        release.SetResult();
        var second = await _runner.NextSpecAsync(Wait);
        Assert.Equal("gpt-6-astra", FakeProcessRunner.ParticipantOf(second));
        Assert.Contains("Turn 2 of 4", second.StandardInput);

        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"result":"done"}"""));
        await PostAsOwner("@opus @gpt-6-astra both of you");                                  // general: NULL directory
        var a = await _runner.NextSpecAsync(Wait);
        var b = await _runner.NextSpecAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new HashSet<string> { "opus", "gpt-6-astra" }, new HashSet<string> { FakeProcessRunner.ParticipantOf(a), FakeProcessRunner.ParticipantOf(b) });
    }

    [Fact]
    public async Task M9_A6_a_codex_spawn_in_a_directory_room_gets_the_room_as_its_workspace_and_json_output()
    {
        var dir = await MakeRoom("lab");
        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok(""));
        await PostAsOwnerIn("lab", "@gpt-6-astra look around");
        var spec = await _runner.NextSpecAsync(Wait);
        var args = spec.Arguments.ToList();
        Assert.Equal(dir, spec.WorkingDirectory);
        Assert.Equal(dir, args[args.IndexOf("-C") + 1]);
        Assert.Contains("--json", args);
        Assert.Contains("sandbox_workspace_write.network_access=true", args);
        Assert.DoesNotContain("--skip-git-repo-check", args);
        Assert.StartsWith(Path.Combine(_dir, "spawns"), args[args.IndexOf("-o") + 1]);
        var note = await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith(HubNotes.TrailPrefix));
        Assert.Contains("as gpt-6-astra: 0 file(s) changed, 0 shell command(s).", note.Body);   // empty commit, empty log
        Assert.Equal("GPT-6 Astra <gpt-6-astra@chopitup.local>", await GitLog(dir, "%an <%ae>", 1));
    }

    [Fact]
    public async Task M9_A9_a_model_that_commits_on_its_own_is_called_out_in_the_note_and_the_hub_still_commits_after_it()
    {
        var dir = await MakeRoom("lab");
        _runner.Handler = async (spec, _, _) =>
        {
            File.WriteAllText(Path.Combine(spec.WorkingDirectory, "rogue.txt"), "x");
            await new GitTrail(spec.WorkingDirectory).CommitAllAsync("rogue", new GitIdentity("Rogue", "rogue@example.test"), allowEmpty: false);
            return FakeProcessRunner.Ok("""{"type":"result","result":"done"}""");
        };
        await PostAsOwnerIn("lab", "@sonnet go");
        var note = await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith(HubNotes.TrailPrefix));
        Assert.Contains("HEAD moved during the spawn: sonnet committed on its own.", note.Body);
        var log = (await GitLog(dir, "%an|%s")).Split('\n');
        Assert.Equal("Sonnet|sonnet: turn 1/4 in room lab", log[0]);
        Assert.Equal("Rogue|rogue", log[1]);
    }

    [Fact]
    public async Task M9_A6_a_null_directory_room_keeps_the_m5_command_line_byte_for_byte()
    {
        await PostAsOwner("@opus hi");
        var spec = await _runner.NextSpecAsync(Wait);
        Assert.Equal(["-p", "--tools", "", "--strict-mcp-config", "--mcp-config", spec.Arguments[4], "--allowedTools", SpawnCommands.ClaudeToolAllowed,
                      "--no-session-persistence", "--model", "opus", "--output-format", "json", "--disable-slash-commands", "--setting-sources", ""],
            spec.Arguments);
        Assert.StartsWith(Path.Combine(_dir, "spawns"), spec.WorkingDirectory);
        Assert.DoesNotContain("Files: this room's directory", spec.StandardInput);
    }

    private async Task PostAsIn(string participant, string room, string body)
    {
        await using var client = await _host.ClientFor(participant);
        HubTestHost.Json(await client.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = room, ["body"] = body, ["client_key"] = Guid.NewGuid().ToString() }));
    }
}
```

`ProcessRunner` and `CliResolver` are the real ones from `ChopItUp.Hub.Spawning` (the fake only stands in for the two model CLIs). Expected: Hub tests 233 + 1 + 1 + 3 + 5 = **243** cases; every existing `SpawnerServiceTests` fact green (general is a `NULL`-directory room, so nothing in their command lines or notes changes).

## Task 7 — Web UI: chat list, new room, archive, directory, trail (builder: opus)

Ticket: `.scratch/m9-rooms-as-chats/issues/07-web-ui.md`. Acceptance: A12 (and the browser halves of A3, A4, A5, A11). Blocked by Task 5. Client root: `src/ChopItUp.Hub/client/src/`. No test runner exists (claim 21); the gates are `npm run build` (tsc + vite), the screenshot judge and the UIA gate in Verification. Conventions kept from M3/M10: `memo` components with a `Props` interface, `className="quiet"` secondary buttons, `className="link"` inline buttons, dialogs as `overlay` + `dialog` with `role="dialog"`, `aria-modal`, `aria-labelledby`, Escape closes, focus on the first field, `dialog-error` for the error line, `dialog-actions` footer. No `data-testid`: the UIA gate finds controls by role and accessible name.

### `types.ts` — `Room` gains four fields; two new types

```ts
export interface Room {
  id: string;
  name: string;
  createdAt: string;
  messageCount: number;
  lastMessageId: number;
  /** Absolute path of the room's git tree, or null for a room made before M9 and not yet bound. */
  directory: string | null;
  archivedAt: string | null;
  /** The newest message's time, or createdAt when there is none — the chat-list order. */
  lastActivityAt: string;
  /** Messages past the owner's read cursor. */
  unread: number;
}

export interface TrailCommit {
  hash: string;
  author: string;
  at: string;
  subject: string;
}

export interface Trail {
  directory: string | null;
  commits: TrailCommit[];
  error: string | null;
}
```

### `api.ts` — `listRooms` gains a flag; six new calls (same `unwrap` + `encodeURIComponent` shape as `stopExchange`; JSON POSTs follow `postMessage`, line 67)

```ts
export async function listRooms(includeArchived = false, signal?: AbortSignal): Promise<Room[]> {
  return unwrap<Room[]>(await fetch(includeArchived ? '/api/rooms?archived=true' : '/api/rooms', { signal }));
}

export async function createRoom(name: string, directory: string, signal?: AbortSignal): Promise<Room> {
  return unwrap<Room>(
    await fetch('/api/rooms', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ name, directory }),
      signal,
    }),
  );
}

export async function archiveRoom(roomId: string, signal?: AbortSignal): Promise<Room> {
  return unwrap<Room>(await fetch(`/api/rooms/${encodeURIComponent(roomId)}/archive`, { method: 'POST', signal }));
}

export async function unarchiveRoom(roomId: string, signal?: AbortSignal): Promise<Room> {
  return unwrap<Room>(await fetch(`/api/rooms/${encodeURIComponent(roomId)}/unarchive`, { method: 'POST', signal }));
}

export async function bindDirectory(roomId: string, directory: string, signal?: AbortSignal): Promise<Room> {
  return unwrap<Room>(
    await fetch(`/api/rooms/${encodeURIComponent(roomId)}/directory`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ directory }),
      signal,
    }),
  );
}

export async function markRead(roomId: string, signal?: AbortSignal): Promise<void> {
  await unwrap<unknown>(await fetch(`/api/rooms/${encodeURIComponent(roomId)}/read`, { method: 'POST', signal }));
}

export async function getTrail(roomId: string, signal?: AbortSignal): Promise<Trail> {
  return unwrap<Trail>(await fetch(`/api/rooms/${encodeURIComponent(roomId)}/trail`, { signal }));
}
```

(`Trail` joins the `import type` list at the top of `api.ts`.)

### `RoomRail.tsx` — replace the file

```tsx
import { memo } from 'react';
import type { Room } from './types';
import type { Liveness } from './realtime';

interface Props {
  rooms: Room[];
  activeRoomId: string | null;
  liveness: Liveness;
  showArchived: boolean;
  onSelect: (roomId: string) => void;
  onNewRoom: () => void;
  onToggleArchived: () => void;
}

const LIVENESS_LABEL: Record<Liveness, string> = {
  connecting: 'connecting',
  live: 'live',
  offline: 'offline',
};

/** M9: the rail is a chat list. Rooms arrive newest activity first (the hub orders them; App re-sorts
 *  after a live bump), an unread badge replaces the count on rooms with unread messages that are not
 *  open, a folder mark says the room has a directory, and archived rooms show only behind the toggle. */
function RoomRail({ rooms, activeRoomId, liveness, showArchived, onSelect, onNewRoom, onToggleArchived }: Props) {
  return (
    <nav className="rail" aria-label="Rooms">
      <div className="rail-head">
        <span className="wordmark">Chop It Up</span>
        <span className={`liveness liveness-${liveness}`} title={`Realtime connection: ${LIVENESS_LABEL[liveness]}`}>
          <span className="dot" aria-hidden="true" />
          {LIVENESS_LABEL[liveness]}
        </span>
      </div>
      <ul className="room-list">
        {rooms.map((room) => {
          const active = room.id === activeRoomId;
          const archived = room.archivedAt !== null;
          const unread = !active && room.unread > 0;
          return (
            <li key={room.id}>
              <button
                type="button"
                className={`room-item${active ? ' active' : ''}${archived ? ' archived' : ''}`}
                onClick={() => onSelect(room.id)}
                aria-current={active ? 'true' : undefined}
              >
                {room.directory !== null && (
                  <span className="room-dir" title={room.directory} aria-label="Has a directory">
                    ▣
                  </span>
                )}
                <span className="room-name">{room.name}</span>
                {archived && <span className="room-archived">archived</span>}
                {unread ? (
                  <span className="room-unread" aria-label={`${room.unread} unread`}>
                    {room.unread > 99 ? '99+' : room.unread}
                  </span>
                ) : (
                  <span className="room-count" title={`${room.messageCount} messages`}>
                    {room.messageCount}
                  </span>
                )}
              </button>
            </li>
          );
        })}
        {rooms.length === 0 && <li className="rail-empty">No rooms yet.</li>}
      </ul>
      <div className="rail-foot">
        <button type="button" className="quiet" onClick={onNewRoom}>
          New room
        </button>
        <label>
          <input type="checkbox" checked={showArchived} onChange={onToggleArchived} />
          Show archived
        </label>
      </div>
    </nav>
  );
}

export default memo(RoomRail);
```

### `NewRoomDialog.tsx` (new)

```tsx
import { useEffect, useRef, useState } from 'react';
import { bindDirectory, createRoom, describeError } from './api';
import type { Room } from './types';

interface Props {
  mode: 'create' | 'bind';
  /** The room being bound (bind mode only). */
  room?: Room;
  onClose: () => void;
  onDone: (room: Room) => void;
}

const DIRECTORY_HINT =
  'Leave blank and the hub creates a folder under its rooms root. Or type an absolute path such as C:\\Projects\\thing: it becomes a git repository, or is adopted if it already is one. Drive roots, your profile folder itself, C:\\Self Apps, the hub\'s own folders, credential folders and Windows folders are refused.';

/** One dialog for "new room" and for "bind a directory to a room made before M9": the same directory
 *  field, hint and refusal line. The hub's refusal sentences are written for the owner and shown
 *  verbatim; nothing is validated here except emptiness. */
export default function NewRoomDialog({ mode, room, onClose, onDone }: Props) {
  const [name, setName] = useState('');
  const [directory, setDirectory] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const first = useRef<HTMLInputElement>(null);

  useEffect(() => {
    first.current?.focus();
    function onKey(event: KeyboardEvent) {
      if (event.key === 'Escape') onClose();
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const canSubmit = !busy && (mode === 'bind' || name.trim().length > 0);

  async function submit() {
    if (!canSubmit) return;
    setBusy(true);
    setError(null);
    try {
      const done =
        mode === 'create' ? await createRoom(name.trim(), directory.trim()) : await bindDirectory(room!.id, directory.trim());
      onDone(done);
    } catch (failure) {
      setError(describeError(failure));
    } finally {
      setBusy(false);
    }
  }

  const title = mode === 'create' ? 'New room' : `Bind a directory to ${room?.name ?? 'this room'}`;

  return (
    <div className="overlay" role="presentation" onMouseDown={onClose}>
      <div
        className="dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="new-room-title"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <header className="dialog-head">
          <h2 id="new-room-title">{title}</h2>
          <button type="button" className="quiet close" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </header>

        {mode === 'create' && (
          <>
            <label className="field-label" htmlFor="new-room-name">
              Title
            </label>
            <input
              id="new-room-name"
              ref={first}
              className="field"
              type="text"
              value={name}
              maxLength={80}
              placeholder="What this room is for"
              onChange={(event) => setName(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === 'Enter') void submit();
              }}
              disabled={busy}
            />
          </>
        )}

        <label className="field-label" htmlFor="new-room-directory">
          Directory
        </label>
        <input
          id="new-room-directory"
          ref={mode === 'bind' ? first : undefined}
          className="field"
          type="text"
          value={directory}
          placeholder={'C:\\Projects\\thing (blank = hub-created)'}
          onChange={(event) => setDirectory(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter') void submit();
          }}
          disabled={busy}
          spellCheck={false}
        />
        <p className="dialog-note quiet-note">{DIRECTORY_HINT}</p>

        {error && (
          <p className="dialog-error" role="alert">
            {error}
          </p>
        )}

        <footer className="dialog-actions">
          <button type="button" className="quiet" onClick={onClose}>
            Cancel
          </button>
          <button type="button" className="send" onClick={() => void submit()} disabled={!canSubmit}>
            {mode === 'create' ? (busy ? 'Creating…' : 'Create room') : busy ? 'Binding…' : 'Bind directory'}
          </button>
        </footer>
      </div>
    </div>
  );
}
```

### `TrailDialog.tsx` (new)

```tsx
import { useCallback, useEffect, useState } from 'react';
import { describeError, getTrail } from './api';
import { exactTime } from './time';
import type { Room, Trail } from './types';

interface Props {
  room: Room;
  onClose: () => void;
}

/** The last 20 commits the hub made in this room's directory (D11), newest first. Read on open and on
 *  Refresh; nothing here writes. */
export default function TrailDialog({ room, onClose }: Props) {
  const [trail, setTrail] = useState<Trail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(
    async (signal?: AbortSignal) => {
      setBusy(true);
      try {
        const loaded = await getTrail(room.id, signal);
        if (signal?.aborted) return;
        setTrail(loaded);
        setError(loaded.error);
      } catch (failure) {
        if (!signal?.aborted) setError(describeError(failure));
      } finally {
        if (!signal?.aborted) setBusy(false);
      }
    },
    [room.id],
  );

  useEffect(() => {
    const abort = new AbortController();
    void load(abort.signal);
    return () => abort.abort();
  }, [load]);

  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      if (event.key === 'Escape') onClose();
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div className="overlay" role="presentation" onMouseDown={onClose}>
      <div className="dialog" role="dialog" aria-modal="true" aria-labelledby="trail-title" onMouseDown={(event) => event.stopPropagation()}>
        <header className="dialog-head">
          <h2 id="trail-title">Trail of {room.name}</h2>
          <button type="button" className="quiet close" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </header>

        <p className="dialog-note">
          {trail?.directory ? (
            <>
              Commits in <code>{trail.directory}</code>, newest first. The hub commits your edits before each spawn and the
              model's turn after it; nothing is ever pushed.
            </>
          ) : (
            'This room has no directory, so it has no trail.'
          )}
        </p>

        {error && <p className="dialog-error">{error}</p>}

        {trail && !error && trail.commits.length === 0 && (
          <p className="dialog-note quiet-note">
            No commits yet: the first spawn in this room, or your first edit before one, creates the first commit.
          </p>
        )}

        {trail && trail.commits.length > 0 && (
          <ol className="trail-list" aria-label="Commits">
            {trail.commits.map((commit) => (
              <li key={commit.hash}>
                <code>{commit.hash}</code>
                {commit.subject}
                <span className="trail-meta">
                  {commit.author} · {exactTime(commit.at)}
                </span>
              </li>
            ))}
          </ol>
        )}

        <footer className="dialog-actions">
          <button type="button" className="quiet" onClick={() => void load()} disabled={busy}>
            {busy ? 'Loading…' : 'Refresh'}
          </button>
          <button type="button" className="quiet" onClick={onClose}>
            Close
          </button>
        </footer>
      </div>
    </div>
  );
}
```

### `RoomHeader.tsx` — replace the file

```tsx
import { memo } from 'react';
import { exportUrl } from './api';
import type { Room } from './types';

interface Props {
  room: Room;
  loadedCount: number;
  /** An archive/unarchive/bind call is in flight. */
  busy: boolean;
  onImport: () => void;
  onImportMemory: () => void;
  onBind: () => void;
  onArchive: () => void;
  onTrail: () => void;
}

/** Import and export live here, quiet, rather than competing with the conversation. M9 adds the
 *  room's directory (or the one-time Bind control on a room made before M9), Archive/Unarchive
 *  (never on general — the hub refuses it), and the Trail dialog. */
function RoomHeader({ room, loadedCount, busy, onImport, onImportMemory, onBind, onArchive, onTrail }: Props) {
  const archived = room.archivedAt !== null;
  return (
    <header className="room-head">
      <div className="room-title">
        <h1>
          {room.name}
          {archived && <span className="room-archived"> archived</span>}
        </h1>
        <span className="room-sub">
          {loadedCount === 1 ? '1 message' : `${loadedCount} messages`}
          {' · '}
          {room.directory !== null ? (
            <code className="room-path" title={room.directory}>
              {room.directory}
            </code>
          ) : (
            <button type="button" className="link" onClick={onBind} disabled={busy}>
              Bind directory
            </button>
          )}
        </span>
      </div>
      <div className="room-actions">
        <button
          type="button"
          className="quiet"
          onClick={onTrail}
          disabled={room.directory === null}
          title={room.directory === null ? 'This room has no directory' : 'Commits the hub made in this room'}
        >
          Trail
        </button>
        {room.id !== 'general' && (
          <button type="button" className="quiet" onClick={onArchive} disabled={busy}>
            {archived ? 'Unarchive' : 'Archive'}
          </button>
        )}
        <button type="button" className="quiet" onClick={onImport}>
          Import transcript
        </button>
        <button type="button" className="quiet" onClick={onImportMemory}>
          Import memory
        </button>
        <a className="quiet" href={exportUrl(room.id)} download={`${room.id}.md`}>
          Export markdown
        </a>
      </div>
    </header>
  );
}

export default memo(RoomHeader);
```

### `App.tsx` — edits (line numbers are HEAD's)

1. Imports: add `import NewRoomDialog from './NewRoomDialog';` and `import TrailDialog from './TrailDialog';` beside the other dialogs; add `isHuman` to the `./participants` import that already brings `isSystem`.

2. Module level, after `applyExchange` (line 23): 

```tsx
/** The chat-list order. Every stamp is UTC round-trip text, so string order is time order. */
function byActivity(rooms: Room[]): Room[] {
  return [...rooms].sort((a, b) => (a.lastActivityAt < b.lastActivityAt ? 1 : a.lastActivityAt > b.lastActivityAt ? -1 : 0));
}
```

3. State, after `memoryImportOpen` (line 37):

```tsx
  const [showArchived, setShowArchived] = useState(false);
  const [roomDialog, setRoomDialog] = useState<null | { mode: 'create' } | { mode: 'bind'; room: Room }>(null);
  const [trailOpen, setTrailOpen] = useState(false);
  const [roomBusy, setRoomBusy] = useState(false);
```

   Refs, after `lastId` (line 41):

```tsx
  const showArchivedRef = useRef(false);
  const readTimer = useRef<number | null>(null);
```

4. `refreshRooms` (lines 74–78) becomes:

```tsx
  const refreshRooms = useCallback(async (signal?: AbortSignal) => {
    const loaded = await api.listRooms(showArchivedRef.current, signal);
    setRooms(loaded);
    // Keep the open room if it is still listed; otherwise (archived and hidden, or gone) the first one.
    setRoomId((current) => (current !== null && loaded.some((room) => room.id === current) ? current : (loaded[0]?.id ?? null)));
  }, []);

  const toggleArchived = useCallback(() => {
    showArchivedRef.current = !showArchivedRef.current;
    setShowArchived(showArchivedRef.current);
    refreshRooms().catch((failure) => setError(api.describeError(failure)));
  }, [refreshRooms]);

  /** M9: the owner's read cursor moves while the room is open. Debounced, so a burst of messages is
   *  one call; dropped if the room changed before it fired. */
  const scheduleRead = useCallback((room: string) => {
    if (readTimer.current !== null) window.clearTimeout(readTimer.current);
    readTimer.current = window.setTimeout(() => {
      readTimer.current = null;
      if (currentRoom.current === room) api.markRead(room).catch(() => undefined);
    }, 750);
  }, []);
```

5. The `MessagePosted` handler (lines 112–125) becomes:

```tsx
    connection.on('MessagePosted', (message: Message) => {
      const open = message.roomId === currentRoom.current;
      if (open) merge([message]);
      // Every memory state change is announced by a hub note that starts with "Memory " (a proposal,
      // an import, an approval, a rejection); that note IS the refresh signal — no second event.
      if (open && isSystem(message.authorId) && message.body.startsWith('Memory ')) {
        loadProposals(message.roomId).catch(() => undefined);
      }
      if (open && !isHuman(message.authorId)) scheduleRead(message.roomId);
      // The owner's own posts advance the owner's cursor on the hub, so they never count as unread.
      setRooms((previous) =>
        byActivity(
          previous.map((room) =>
            room.id === message.roomId
              ? {
                  ...room,
                  messageCount: room.messageCount + 1,
                  lastMessageId: message.id,
                  lastActivityAt: message.createdAt,
                  unread: open || isHuman(message.authorId) ? room.unread : room.unread + 1,
                }
              : room,
          ),
        ),
      );
    });
```

   and the effect's dependency list (line 164 at HEAD, `[merge, loadProposals]`) gains `scheduleRead`.

6. The room-switch effect (lines 170–204): its cleanup also clears the timer:

```tsx
      if (readTimer.current !== null) {
        window.clearTimeout(readTimer.current);
        readTimer.current = null;
      }
```

7. The messages-load effect (lines 206–224): after `merge(loaded);` add

```tsx
        if (loaded.length > 0) api.markRead(roomId).catch(() => undefined);   // opening a room reads it
```

8. Handlers, after `onMemoryImported` (line 283):

```tsx
  const selectRoom = useCallback((id: string) => {
    setRoomId(id);
    setRooms((previous) => previous.map((room) => (room.id === id ? { ...room, unread: 0 } : room)));
  }, []);

  const onRoomCreated = useCallback((room: Room) => {
    setRoomDialog(null);
    setRooms((previous) => byActivity([room, ...previous.filter((r) => r.id !== room.id)]));
    setRoomId(room.id);
  }, []);

  const onRoomBound = useCallback((room: Room) => {
    setRoomDialog(null);
    setRooms((previous) => previous.map((r) => (r.id === room.id ? room : r)));
  }, []);

  // Archive hides, never blocks (plan decision 14): the hub keeps the room and its folder; the list
  // is re-read so the selection moves to the first visible room when the open one leaves the list.
  const toggleArchive = useCallback(async () => {
    if (!roomId) return;
    const room = rooms.find((r) => r.id === roomId);
    if (!room) return;
    setRoomBusy(true);
    try {
      if (room.archivedAt !== null) await api.unarchiveRoom(roomId);
      else await api.archiveRoom(roomId);
      setError(null);
      await refreshRooms();
    } catch (failure) {
      setError(api.describeError(failure));
    } finally {
      setRoomBusy(false);
    }
  }, [roomId, rooms, refreshRooms]);
```

9. JSX (lines 288–333): `RoomRail` becomes

```tsx
      <RoomRail
        rooms={rooms}
        activeRoomId={roomId}
        liveness={liveness}
        showArchived={showArchived}
        onSelect={selectRoom}
        onNewRoom={() => setRoomDialog({ mode: 'create' })}
        onToggleArchived={toggleArchived}
      />
```

   `RoomHeader` gains `busy={roomBusy}`, `onBind={() => setRoomDialog({ mode: 'bind', room: activeRoom })}`, `onArchive={() => void toggleArchive()}`, `onTrail={() => setTrailOpen(true)}`; and after the `MemoryImportDialog` block:

```tsx
      {roomDialog && (
        <NewRoomDialog
          mode={roomDialog.mode}
          room={roomDialog.mode === 'bind' ? roomDialog.room : undefined}
          onClose={() => setRoomDialog(null)}
          onDone={roomDialog.mode === 'create' ? onRoomCreated : onRoomBound}
        />
      )}
      {trailOpen && activeRoom && <TrailDialog room={activeRoom} onClose={() => setTrailOpen(false)} />}
```

### `styles.css` — additions (existing tokens only: `--faint`, `--dim`, `--text`, `--raised`, `--panel`, `--accent-owner`)

```css
.room-unread {
  flex: none;
  min-width: 18px;
  text-align: center;
  font-size: 11px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
  color: var(--panel);
  background: var(--accent-owner);
  border-radius: 999px;
  padding: 1px 6px;
}

.room-dir {
  flex: none;
  font-size: 10px;
  color: var(--faint);
}

.room-item.archived .room-name {
  color: var(--faint);
  font-style: italic;
}

.room-archived {
  flex: none;
  font-size: 10px;
  color: var(--faint);
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.rail-foot {
  margin-top: auto;
  padding: 8px 12px 12px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  border-top: 1px solid var(--raised);
  font-size: 12px;
  color: var(--dim);
}

.rail-foot label {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  cursor: pointer;
}

.room-path {
  display: inline-block;
  max-width: 28rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  direction: rtl;
  text-align: left;
  vertical-align: bottom;
  font-size: 11px;
  color: var(--dim);
}

.trail-list {
  margin: 0;
  padding: 0 0 0 1.4rem;
  max-height: 50vh;
  overflow-y: auto;
  font-size: 13px;
}

.trail-list li {
  margin: 0 0 8px;
}

.trail-list code {
  font-size: 11px;
  color: var(--dim);
  margin-right: 6px;
}

.trail-meta {
  display: block;
  font-size: 11px;
  color: var(--faint);
}
```

`.rail` (line 98) must be a column flex container for `margin-top: auto` to push the footer down; if it is not, add `display: flex; flex-direction: column;` to `.rail` and `flex: 1; min-height: 0;` to `.room-list`.

### Build gate

```powershell
cd 'C:\Agent Projects\ChopItUp\src\ChopItUp.Hub\client'
npm run build
```

Expected: `tsc` clean, vite bundle written where `package.json`'s `build` script targets today, no new lint output. Commit built assets only if the repo tracks them (check `git ls-files` for the output folder first; M3 decided this — follow what is tracked).

## Task 8 — Live check script, README, CLAUDE.md line, row 13 note (builder: sonnet)

Ticket: `.scratch/m9-rooms-as-chats/issues/08-live-check-and-docs.md`. Acceptance: A10 end to end on the real CLI, A13. Blocked by Tasks 6, 7.

### `tools/Invoke-M9RoomCheck.ps1` (new; the M10 script is the model — same `Add-Check`, hub start, poll, PID stop, `Results: n/m PASS` line)

```powershell
<#
.SYNOPSIS
    M9 live check: starts a hub on a scratch data directory with a scratch rooms root, creates a room,
    proves a refused path is refused, mentions @sonnet in the room, and proves the model wrote a file
    inside the room's tree, that the hub committed the owner's edit and then the model's turn under
    the model's name with a shell log, and that unread, mark-read, archive and the trail endpoint work.

.DESCRIPTION
    Costs one short Sonnet call on the owner's subscription (two with -IncludeCodex). Never touches
    C:\Self Apps, %USERPROFILE%\ChopItUp or any real data directory: -DataDir and -RoomsRoot default
    to fresh folders under $env:TEMP and are left behind with the log. Every check prints PASS/FAIL;
    the last line is "Results: n/m PASS"; exit 0 only when all pass. The hub is stopped by PID, always.
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$DataDir = (Join-Path $env:TEMP ('chopitup_m9check_' + [guid]::NewGuid().ToString('N'))),
    [string]$RoomsRoot = '',
    [int]$Port = 8799,
    [int]$TimeoutSeconds = 240,
    [switch]$IncludeCodex
)

$ErrorActionPreference = 'Stop'
if (-not $RoomsRoot) { $RoomsRoot = "$DataDir.rooms" }   # a sibling: anything under the data dir is refused by design
$script:Checks = New-Object System.Collections.Generic.List[object]
$log = "$DataDir.m9-check.log"

function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail = '')
    $script:Checks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail })
    $line = ('{0}  {1}  {2}' -f ($(if ($Passed) { 'PASS' } else { 'FAIL' }), $Name, $Detail))
    Write-Host $line
    Add-Content -Path $log -Value $line
}

if (-not (Test-Path -LiteralPath $HubExe -PathType Leaf)) {
    Write-Error "Hub exe not found at '$HubExe'. Build first, or pass -HubExe." -ErrorAction Continue
    exit 2
}
foreach ($d in @($DataDir, $RoomsRoot)) {
    if (Test-Path -LiteralPath $d) {
        Write-Error "'$d' already exists; this script only ever runs against fresh directories." -ErrorAction Continue
        exit 2
    }
}
New-Item -ItemType Directory -Path $DataDir | Out-Null
Add-Content -Path $log -Value ("M9 room check {0} exe={1} data={2} rooms={3} port={4}" -f (Get-Date -Format o), $HubExe, $DataDir, $RoomsRoot, $Port)

$claude = Get-Command claude -ErrorAction SilentlyContinue
Add-Check -Name 'cli.claude-on-path' -Passed ([bool]$claude) -Detail ($claude.Source ?? 'not found')
$git = Get-Command git -ErrorAction SilentlyContinue
Add-Check -Name 'cli.git-on-path' -Passed ([bool]$git) -Detail ($git.Source ?? 'not found')

$base = "http://127.0.0.1:$Port"
$hub = $null
function Invoke-Api([string]$Method, [string]$Path, $Body = $null) {
    $args = @{ Uri = "$base$Path"; Method = $Method; TimeoutSec = 30 }
    if ($null -ne $Body) { $args.ContentType = 'application/json'; $args.Body = ($Body | ConvertTo-Json -Compress) }
    Invoke-RestMethod @args
}
function Get-StatusOf([scriptblock]$Call) {
    try { & $Call | Out-Null; 200 } catch { $_.Exception.Response.StatusCode.value__ }
}
try {
    $hub = Start-Process -FilePath $HubExe -ArgumentList @('--data', $DataDir, '--port', "$Port", '--rooms-root', $RoomsRoot) -WindowStyle Hidden -PassThru `
        -RedirectStandardError (Join-Path $DataDir 'hub.stderr.log') -RedirectStandardOutput (Join-Path $DataDir 'hub.stdout.log')
    $health = $null
    foreach ($i in 1..40) {
        try { $health = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 }
    }
    Add-Check -Name 'hub.started' -Passed ($null -ne $health) -Detail "pid=$($hub.Id)"
    Add-Check -Name 'hub.health-schema-6' -Passed ($health.schema -eq 6) -Detail "schema=$($health.schema)"

    # Rooms: create, refuse, list.
    $room = Invoke-Api POST '/api/rooms' @{ name = 'Live check' }
    $roomDir = Join-Path $RoomsRoot 'live-check'
    Add-Check -Name 'room.created' -Passed ($room.id -eq 'live-check' -and $room.directory -eq $roomDir) -Detail "id=$($room.id) dir=$($room.directory)"
    Add-Check -Name 'room.git-initialised' -Passed (Test-Path -LiteralPath (Join-Path $roomDir '.git') -PathType Container) -Detail $roomDir
    Add-Check -Name 'room.refuses-drive-root' -Passed ((Get-StatusOf { Invoke-Api POST '/api/rooms' @{ name = 'Nope'; directory = 'C:\' } }) -eq 400) -Detail 'C:\ is 400'
    Add-Check -Name 'room.refuses-data-dir' -Passed ((Get-StatusOf { Invoke-Api POST '/api/rooms' @{ name = 'Nope'; directory = (Join-Path $DataDir 'x') } }) -eq 400) -Detail 'inside the data dir is 400'
    Add-Check -Name 'room.refuses-self-apps' -Passed ((Get-StatusOf { Invoke-Api POST '/api/rooms' @{ name = 'Nope'; directory = 'C:\Self Apps\ChopItUp\rooms' } }) -eq 400) -Detail 'C:\Self Apps is 400'
    # PowerShell 7.6 hands a top-level JSON array back as ONE nested Object[]; enumerate before filtering (measured 2026-09-06).
    $rooms = @(Invoke-Api GET '/api/rooms' | ForEach-Object { $_ })
    Add-Check -Name 'room.listed-first' -Passed ($rooms.Count -eq 2 -and $rooms[0].id -eq 'live-check') -Detail (($rooms | ForEach-Object id) -join ',')

    # The owner edits before the spawn: that edit must become its own commit, authored as the owner.
    Set-Content -LiteralPath (Join-Path $roomDir 'owner.md') -Encoding utf8 -Value "# Owner notes`n`nWritten before the spawn.`n"

    function Wait-Exchange([string]$RoomId, [string]$Until, [int]$Seconds) {
        $deadline = (Get-Date).AddSeconds($Seconds)
        $state = $null
        while ((Get-Date) -lt $deadline) {
            try { $state = Invoke-RestMethod -Uri "$base/api/rooms/$RoomId/exchange" -TimeoutSec 10 }
            catch { Add-Content -Path $log -Value "poll exchange failed: $($_.Exception.Message)" }
            if ($state -and $state.status -in ($Until -split ',')) { return $state }
            Start-Sleep -Seconds 3
        }
        return $state
    }
    function Read-Room([string]$RoomId) {
        try { @((Invoke-RestMethod -Uri "$base/api/rooms/$RoomId/messages?afterId=0&limit=200" -TimeoutSec 10).messages) }
        catch { Add-Content -Path $log -Value "read room failed: $($_.Exception.Message)"; @() }
    }
    function Test-Spawn([string]$Participant, [string]$FileName, [string]$ExpectAuthor, [string]$Prefix) {
        # The codeword exists nowhere but this prompt: a file that carries it was written by the model, in the room.
        $codeword = 'HERON-' + (Get-Random -Minimum 100 -Maximum 999)
        $body = "@$Participant Three things, then stop: (1) create a file named $FileName in your working directory whose only content is the line $codeword; (2) run one shell command that lists the files in your working directory; (3) post one line to the room saying done. Do not mention anyone."
        $posted = Invoke-Api POST "/api/rooms/live-check/messages" @{ body = $body }
        Add-Check -Name "$Prefix.post.owner-message" -Passed ($posted.id -ge 1) -Detail "id=$($posted.id)"
        $state = Wait-Exchange -RoomId 'live-check' -Until 'concluded,stopped' -Seconds $TimeoutSeconds
        $messages = Read-Room 'live-check'
        Add-Content -Path $log -Value ("exchange: " + ($state | ConvertTo-Json -Compress))
        foreach ($m in $messages) { Add-Content -Path $log -Value ("#{0} {1}: {2}" -f $m.id, $m.authorId, ($m.body -replace "`r?`n", ' / ')) }
        $replies = @($messages | Where-Object { $_.authorId -eq $Participant -and $_.id -gt $posted.id })
        $hubNotes = @($messages | Where-Object { $_.authorId -eq 'hub' -and $_.id -gt $posted.id })
        Add-Check -Name "$Prefix.spawn.replied" -Passed ($replies.Count -ge 1) -Detail "count=$($replies.Count)"
        Add-Check -Name "$Prefix.exchange.concluded" -Passed ($state.status -eq 'concluded') -Detail "status=$($state.status)"
        Add-Check -Name "$Prefix.exchange.no-failure-notes" -Passed (-not ($hubNotes | Where-Object { $_.body -match 'did not reply|without posting|could not be started|exited with code|^Not committed' })) -Detail (($hubNotes | ForEach-Object { $_.body.Split("`n")[0] }) -join ' | ')
        $file = Join-Path $roomDir $FileName
        $text = if (Test-Path -LiteralPath $file) { (Get-Content -LiteralPath $file -Raw) } else { '' }
        Add-Check -Name "$Prefix.file.created-in-room" -Passed (Test-Path -LiteralPath $file) -Detail $file
        Add-Check -Name "$Prefix.file.carries-codeword" -Passed ($text -like "*$codeword*") -Detail "codeword=$codeword"
        $trailNote = @($hubNotes | Where-Object body -like 'Committed *')
        Add-Check -Name "$Prefix.trail.note" -Passed ($trailNote.Count -eq 1 -and $trailNote[0].body -like "*as $Participant*") -Detail ($trailNote | ForEach-Object body | Select-Object -First 1)
        $top = & git -C $roomDir log -1 --format='%an|%cn|%s' 2>&1
        Add-Check -Name "$Prefix.git.author-is-model" -Passed ("$top" -like "$ExpectAuthor|ChopItUp hub|$Participant`: turn *") -Detail "$top"
        $bodyText = (& git -C $roomDir log -1 --format=%B 2>&1) -join "`n"
        Add-Check -Name "$Prefix.git.shell-log" -Passed ($bodyText -match 'Shell commands run \(\d+\):') -Detail (($bodyText -split "`n" | Select-Object -Skip 2 -First 2) -join ' / ')
        $tracked = & git -C $roomDir ls-files 2>&1
        Add-Check -Name "$Prefix.git.file-committed" -Passed (@($tracked) -contains $FileName) -Detail (@($tracked) -join ',')
    }

    Test-Spawn -Participant 'sonnet' -FileName 'hello.txt' -ExpectAuthor 'Sonnet' -Prefix 'claude'
    $authors = @(& git -C $roomDir log --format='%an' 2>&1)
    Add-Check -Name 'git.owner-commit-first' -Passed ($authors.Count -eq 2 -and $authors[1] -eq 'Owner') -Detail ($authors -join ',')
    Add-Check -Name 'git.owner-file-committed' -Passed ((& git -C $roomDir show --name-only --format= HEAD~1 2>&1) -contains 'owner.md') -Detail 'owner.md in the owner commit'
    if ($IncludeCodex) {
        Test-Spawn -Participant 'gpt-6-astra' -FileName 'hello-codex.txt' -ExpectAuthor 'GPT-6 Astra' -Prefix 'codex'
    }

    # Trail endpoint, unread, mark read, archive.
    $trail = Invoke-Api GET '/api/rooms/live-check/trail'
    $commits = @($trail.commits | ForEach-Object { $_ })
    $expectedCommits = if ($IncludeCodex) { 3 } else { 2 }
    Add-Check -Name 'trail.endpoint' -Passed (($trail.directory -eq $roomDir) -and ($commits.Count -ge $expectedCommits) -and ($commits[0].author -like '* <*@chopitup.local>')) -Detail "commits=$($commits.Count) top=$($commits[0].author)"
    $before = @(Invoke-Api GET '/api/rooms' | ForEach-Object { $_ }) | Where-Object id -eq 'live-check'
    Add-Check -Name 'unread.counts-model-and-hub-messages' -Passed ($before.unread -ge 2) -Detail "unread=$($before.unread)"
    Invoke-Api POST '/api/rooms/live-check/read' | Out-Null
    $after = @(Invoke-Api GET '/api/rooms' | ForEach-Object { $_ }) | Where-Object id -eq 'live-check'
    Add-Check -Name 'unread.zero-after-read' -Passed ($after.unread -eq 0) -Detail "unread=$($after.unread)"
    Invoke-Api POST '/api/rooms/live-check/archive' | Out-Null
    $visible = @(Invoke-Api GET '/api/rooms' | ForEach-Object { $_ })
    $all = @(Invoke-Api GET '/api/rooms?archived=true' | ForEach-Object { $_ })
    Add-Check -Name 'archive.hides' -Passed (($visible | ForEach-Object id) -notcontains 'live-check' -and ($all | ForEach-Object id) -contains 'live-check') -Detail ("visible=" + (($visible | ForEach-Object id) -join ','))
    Add-Check -Name 'archive.keeps-directory' -Passed (Test-Path -LiteralPath (Join-Path $roomDir '.git')) -Detail 'nothing deleted'
    Invoke-Api POST '/api/rooms/live-check/unarchive' | Out-Null
    $restored = @(Invoke-Api GET '/api/rooms' | ForEach-Object { $_ })
    Add-Check -Name 'archive.unarchive-restores' -Passed (($restored | ForEach-Object id) -contains 'live-check') -Detail ''
    Add-Check -Name 'archive.general-refused' -Passed ((Get-StatusOf { Invoke-Api POST '/api/rooms/general/archive' }) -eq 400) -Detail 'general stays'
}
finally {
    if ($hub -and -not $hub.HasExited) { Stop-Process -Id $hub.Id -Force -ErrorAction SilentlyContinue }
    $passed = @($script:Checks | Where-Object Passed).Count
    $total = $script:Checks.Count
    $line = "Results: $passed/$total PASS"
    Write-Host $line
    Add-Content -Path $log -Value $line
    Write-Host "Log: $log"
    Write-Host "Room dir: $roomDir"
}
exit $(if ($passed -eq $total -and $total -gt 0) { 0 } else { 1 })
```

Expected without `-IncludeCodex`: **29/29 PASS** (2 cli + 2 hub + 6 room + 10 `claude.*` + 2 `git.owner-*` + 1 trail + 2 unread + 4 archive; 39 with `-IncludeCodex`); the builder records the real total in the README line. PowerShell's `-and` and `-or` share one precedence level and evaluate left to right (measured in critique pass 2), which is why the `trail.endpoint` condition above is fully parenthesised.

### `README.md` — new section `## Rooms (M9)` after `## Memory (M10)`

Content, in prose (adapt to the README's voice):

- A room is one conversation with its own directory: a git repository the hub owns. New rooms get one at creation (blank = `<rooms root>\<room id>`, rooms root = `--rooms-root` / `CHOPITUP_ROOMS` / `%USERPROFILE%\ChopItUp\rooms`); rooms from before M9 have none until the owner binds one, once.
- Refused directories: drive roots, the profile folder itself, anything under `C:\Self Apps`, the hub's data and install folders, credential folders (`.claude .codex .ssh .gnupg .aws .azure .kube .docker`), Windows and Program Files folders, network paths, a folder inside another repository, a folder overlapping another room's.
- What a spawned participant can do there: read, edit, write, search, run shell commands with network, cwd = the room; git read-only by rule; one spawn at a time per directory room.
- The trail: before each spawn the owner's uncommitted edits are committed as the owner; after each spawn the hub commits as the participant (`Name <id@chopitup.local>`, committer `ChopItUp hub`), always, with the shell commands run in the body; a hub note `Committed <hash> as <id>: …` lands in the room; nothing is ever pushed; the Trail button lists the last 20 commits.
- Confinement is asymmetric (D13, F5), stated plainly: Codex runs under its own sandbox (workspace-write, network on), Claude Code runs as the owner's user with deny rules and the prompt as the fence; on Claude Code 2.1.220 the read fence is not enforceable, so reads outside the room are a rule, not a wall. Row 13 is the OS-level route.
- Archive hides a room from the list and from `list_rooms`; nothing on disk changes; unarchive restores it. `general` cannot be archived.
- The crash window (decision 6): a hub that dies between the CLI exiting and the after-spawn commit leaves the model's edits uncommitted, and the next pre-spawn commit takes them as the owner's; the hub log warns at startup when a room is dirty.
- Live check: `pwsh tools\Invoke-M9RoomCheck.ps1` (spends one Sonnet call; `-IncludeCodex` adds one Codex call), what it proves, where the log and room dir are left.

### `CLAUDE.md` — one line after line 31

```
Room check (real Sonnet, scratch hub + scratch room dir, spends): `pwsh tools\Invoke-M9RoomCheck.ps1`.
```

3,775 + ~105 bytes < 4,096 (claim 33).

### `ROADMAP.md` row 13 Notes — confirm, do not edit

The Codex measurement (`Codex 0.153.3 --approve-for-me on native Windows did not stop a git commit inside the workspace`) was written into row 13's Notes at the Phase-A board flip; this task only confirms it is still there (`Select-String -Path ROADMAP.md -Pattern 'Codex 0.153.3' -Quiet`). Row 9's own flip to ✅ and the paired deletes happen in Phase B step 7, by the orchestrator, not in this task.

## Verification (orchestrator, Phase B)

1. **Build quietly**: `dotnet build -v minimal` at the repo root — 0 warnings introduced (compare warning count with the baseline build).
2. **Suites**: `dotnet test tests/ChopItUp.Core.Tests -v minimal` → expected 90 passed; `dotnet test tests/ChopItUp.Hub.Tests -v minimal` → expected 243 passed. The builders' per-task numbers are the truth; the plan's counts are the forecast (claim 1 baseline 76 + 169).
3. **Preflight recheck flips**: after Task 1 the claims whose rechecks assert the old state flip nonzero by design (schema literal 5, `Room` arity, `ListRooms()` shape, `MapRoom` private, `HubOptions` arity, `FinishedEvent` arity, `SpawnPromptInput` tail, `Due` arity, `MemoryGit` standalone, `HubTestHost.StartAsync` arity, `ClaudeFinalText` body). `Check-PlanClaims.ps1` is run once before Task 1 (must be all-green) and not re-run as a gate afterwards; the tests are the gate from then on.
4. **Dry run**: `pwsh tools\Invoke-M2DryRun.ps1` with the schema-6 literal → all PASS (the M2 synthetic corpus still migrates v1→v6).
5. **Live check**: `pwsh tools\Invoke-M9RoomCheck.ps1` → `Results: n/n PASS`; read the log; stamp the count in the row 9 Notes at the flip. Then `pwsh tools\Invoke-M10MemoryCheck.ps1` once more (M10 regression: `MemoryGit` moved onto `GitTrail`). **Docs gate (A13):** `(Get-Item CLAUDE.md).Length -lt 4096`, `Select-String -Path README.md -Pattern '^## Rooms \(M9\)$' -Quiet`, and `Select-String -Path ROADMAP.md -Pattern 'Codex 0.153.3' -Quiet` all `True` — the roadmap budget gate (step 12) covers the rest.
6. **Diff lenses per builder commit** (`git show`): (a) the NULL-directory path byte-identical (`SpawnCommands.Claude`/`Codex` untouched, `SpawnerServiceTests` untouched); (b) no path under the room is ever deleted by hub code (`TryDeleteDir` callers); (c) settings/mcp/token files only under `<data>\spawns\`; (d) every `RoomsApi` write route checks `AnySpawnInFlight` before it touches the store or disk; (e) no `git push`, `remote`, `fetch` anywhere in `GitTrail`; (f) `CancellationToken.None` on the after-spawn commit; (g) `general` unarchivable.
7. **Screenshot gate** (owner-visible UI, Task 7): `preview_start chopitup-hub` (`C:\Agent Zone\.claude\launch.json` entry, port 8795, `.data\` under the session cwd — LESSONS M8). Seeding, so the states the judge scores exist: (a) `POST /api/rooms` twice (`Lab` blank directory; `Old`, then `POST /api/rooms/old/archive`); (b) the **unread badge cannot be seeded through `/api`** — every `/api` post is the owner's and advances the owner's own cursor (claim 5) — so post into `general` as `claude` through the MCP endpoint: read the claude token from the host file that `ChopItUp.Hub.exe --data .data --print-config` regenerates under `.data\` (that command is the sanctioned way to a token; `tokens.json` itself is never opened), then one JSON-RPC `tools/call` of `post_message` to `http://127.0.0.1:8795/mcp` with `Authorization: Bearer <token>` from a scratch PowerShell script (stateless session mode; if a bare `tools/call` is refused, send `initialize` first in the same script); (c) trail rows: two `git commit`s in the Lab room folder from the shell (any author — the endpoint reads `git log`). Open `Lab` so `general` shows the badge. Capture with headless Edge at `--window-size=1400,900 --virtual-time-budget=8000` (LESSONS M16) into the scratchpad: the rail, the Trail dialog open, the New-room dialog after a `C:\` submit; run `tools\Test-CaptureSane.ps1`; a pinned `sonnet` judge returns a text verdict on: rail order (`Lab`, `general`) and the badge on `general`, the folder mark on `Lab`, "New room" and "Show archived" in the footer, the header directory line on `Lab` and the Bind control on `general`, two Trail rows newest first, the refusal sentence in the dialog. No PNG is read into this session.
8. **UIA / interactive gate** (LESSONS M16: the pane drops clicks — drive with `javascript_tool` `element.click()` / value setters and assert through `read_page`, the API and the hub log): New room → dialog → `C:\` → the `dialog-error` text equals the hub's refusal sentence; blank directory → the room appears first and selected and `GET /api/rooms` lists it; with `Lab` open, post into `general` as `claude` through the MCP call of step 7 → the `general` badge increments without a reload; select `general` → a `POST /api/rooms/general/read` line in the hub log and the badge clears; Archive on `Lab` → it leaves the list, `?archived=true` lists it, Show archived reveals it dimmed and italic; Unarchive restores it; Trail on `Lab` lists the two commits newest first; Bind directory on `general` with a scratch path → the header shows the path and `GET /api/rooms` carries it.
9. **`mattpocock-skills:code-review`** on the branch (Standards + Spec axes, no agents spawned); fix before merge.
10. **Git flow** per CLAUDE.md: push → PR → `gh pr checks <PR#> --watch` → `gh pr merge --squash --delete-branch` → `git pull`. CI runs the suites on `windows-latest` where git is present (claim 30).
11. **Deploy**: `tools\Deploy-ChopItUp.ps1` (one harness approval prompt per write under `C:\Self Apps\`), then `tools\Invoke-M4SelfCheck.ps1`; the live DB migrates v5→v6 on first start, the `.v5.` backup lands beside it (rollback = stop hub, restore the backup, redeploy the previous exe). The deployed hub's default rooms root is `%USERPROFILE%\ChopItUp\rooms`; nothing is created there until the owner makes a room.
12. **Board flip** (Phase B step 7): row 9 → ✅ / DONE with merge ref, check counts and the "Unverified" line; delete row 10's ✅ row; delete this plan and the tickets; LESSONS entry only if one of the Could-not-verify items turned out to change a future decision; gate exit 0; commit.

## Critique dispositions

### Pass 1 — `opus`, security-minded platform engineer, 7.0 FIX-THEN-SHIP (13 findings + 1 note)

| # | Finding | Disposition |
|---|---------|-------------|
| P1-1 | `Write`/`Edit` allowed with no `~/` deny mirror: a spawn could plant `~/.claude/settings.json` | **Fixed, measured.** Probe 3 (claim 35) shows `Write(~/…/**)` binds; `ClaudeDenyRules` now emits Read, Write and Edit for every credential folder; decision 8 and A6 updated. |
| P1-2 | `git.exe commit` and absolute-path git escape the verb rules | **Fixed in part, residual stated.** `Bash(git.exe *)` / `Bash(git.exe)` added (measured to bind). The leading-wildcard form was measured NOT to bind and the absolute-path commit ran — decision 7 now says so, and the mechanism for it is the trail (HEAD-moved note, measured in the same probe). |
| P1-3 | The no-auth `/api` write routes (`messages`, `import`, `exchange/stop`) and plaintext `tokens.json` are reachable from a spawn's shell | **Declined as a guard, fixed as a statement.** A 409 on posting/stopping would break the exchange model (the owner posts and stops during spawns). No header or port separates the browser from a same-user process. Decision 8 now lists the three residuals in words, the README repeats them, the appended system prompt adds the rule, and row 13 is named as the fix. |
| P1-4 | Five acceptance criteria contradict the code (A5, A8, A9 body, HEAD-moved text, A11 `reason`), A13 undefined | **Fixed.** A5, A8, A9, A11 rewritten to the code; decision 6 aligned; A13 defined (live check + docs). |
| P1-5 | `M9_A<n>` test tags systematically off | **Fixed.** Every test re-tagged to the criterion it proves. |
| P1-6 | Task 7 in prose while every C# task is verbatim | **Fixed.** `RoomRail.tsx`, `RoomHeader.tsx`, `NewRoomDialog.tsx`, `TrailDialog.tsx` written in full; `App.tsx` edits given as code blocks (state, refs, `refreshRooms`, `MessagePosted`, debounce, handlers, JSX). |
| P1-7 | Screenshot/UIA seeding cannot reach the states asserted (owner posts never unread; no route posts as claude) | **Fixed.** Step 7 seeds unread through the MCP endpoint with the claude token from `--print-config`, seeds trail rows with two shell commits, pins the window size; step 8 rewritten to match. |
| P1-8 | Ancestor junction bypasses the refused set | **Fixed.** `RoomPaths.ResolveLinks` walks every existing segment; the junction test covers leaf and ancestor. |
| P1-9 | Ticket/file path mismatches | **Fixed.** Ticket 01 name; `RoomsApiTests.cs` and `RoomToolsTests.cs` at the test-project root; `RoomCommits(.Tests)` under `Spawning/`. |
| P1-10 | `HubHost` insertion line | **Fixed** (line 76). |
| P1-11 | Core count 91 vs 5 facts | **Fixed** (90). |
| P1-12 | Lost id race leaves an orphan hub-created repository | **Declined.** Single-owner UI; the orphan is an empty repository under the rooms root with no data; adding a delete path to the one component that must never delete under a room is the riskier change. The plan's git check now runs before anything touches disk, which removes the other orphan source. |
| P1-13 | `_live` latch if the finished event is never written | **Fixed.** The write sits in a `finally`. |
| note | F10 (`--append-system-prompt`) named but unused | **Fixed.** `SpawnPrompt.DirectoryRules` rides as the appended system prompt on the Claude directory command line and is repeated in the stdin prompt for both hosts. |

### Pass 2 — `fable`, .NET/React hub maintainer reading tests as a compiler, 7.3 FIX-THEN-SHIP (9 findings; settled seven "could not verify" items by measurement)

| # | Finding | Disposition |
|---|---------|-------------|
| P2-1 | `RoomsApiTests` unread test asserts 2 after the owner's own post; `Post` moves the owner's cursor past everything, so 0 | **Fixed.** Asserts 0, then one more claude post → 1, then read → 0; ticket 05 reworded. |
| P2-2 | 409 test expects `side` before `general`; `general` has the newer activity | **Fixed** (`["general", "side"]`). |
| P2-3 | Task 4 depends on Task 3 (`RoomTrails.For` → `RoomPaths.Normalize`) but the graph said `—` | **Fixed.** Edge 4←3 in the table, the edges line, Task 4's header and ticket 04. |
| P2-4 | A13's docs half had no gate; live-check count forecast was 32 | **Fixed.** Verification step 5 gains the three docs checks; forecast 29 (39 with Codex). |
| P2-5 | Plan asserted `-and` binds tighter than `-or` (false: one level, left to right) | **Fixed.** Sentence replaced by the measurement; the `trail.endpoint` line is fully parenthesised and simplified. |
| P2-6 | Trail note posted before the scratch delete; the spawner test asserts the folder is gone after seeing the note | **Fixed.** The note is posted after `TryDeleteDir`. |
| P2-7 | Hub crash between CLI exit and the after-spawn commit mis-attributes the model's edits to the owner | **Fixed as a statement plus a warning.** Decision 6 and README state it; `StartAsync` logs a warning per dirty directory room at boot. |
| P2-8 | Builder traps: fluent-chain termination in `SpawnPrompt`, missing `using` in `HubNotes`, App.tsx deps line | **Fixed** (all three). |
| P2-9 | `--print-config` wording contradicted itself | **Fixed.** |

Settled by pass 2's own measurements (moved out of "Could not verify"): SQLite extended error 1555 for a TEXT primary-key duplicate; `git rev-parse --show-toplevel` and `Path.GetFullPath` agree on short-name and case-folded paths on git 2.45.2; xunit 2.9.3 resolves `Assert.Equal([...], string[])` unambiguously under C# 14; every raw-string literal's closing indentation is met; `Lines()` yields inside `using`, not `try/catch`; `RoomSelect` column indexes match `ReadRoom`; the Task 6 closure touches no loop-owned state; `.rail` is already a column flex container.

## Could not verify in this environment

- **MCP tools under `dontAsk` with six built-ins allowed**: the probe (claim 23) ran with the three MCP tools on the allow list and they were called; that the same holds with `--tools Read,Edit,Write,Glob,Grep,Bash` beside them is inferred from the flag semantics, not measured with a hub. The live check is the measurement.
- **Codex `--json` item shapes beyond the probe**: `command_execution` items were measured; `file_change` or other item types were not and are ignored by design.
- **`--verbose` requirement for `stream-json`**: on 2.1.220 `-p --output-format stream-json` without `--verbose` printed a usage error in the probe; a later CLI may relax it. The flag is harmless when not required.
- **`Read(~/x/**)` deny rules also fencing Glob and Grep**: measured for `Read` and `Write` (claim 35); Edit is inferred from Write; Glob/Grep are not measured — the prompt rule covers them.
- **Absolute-path git and interpreters**: measured to escape the deny list (claim 35); the trail is the mechanism, and the HEAD-moved note was seen in the same probe's shape but not through the hub — the spawner test `M9_A9_a_model_that_commits_on_its_own…` is the hub-side proof.
- **The owner's browser rendering**: screenshots come from headless Edge, not the owner's Chrome; the UIA gate drives the pane.
- **CI git identity**: `GitTrail` passes `-c user.name/email` on every commit, so an unconfigured runner works in theory; confirmed only when the PR's checks pass.
- **`Invoke-M9RoomCheck.ps1`** is written to the M10 script's proven skeleton but has not been executed before Phase B; its first run is in Verification step 5, and any PowerShell slip is fixed there.
- **Windows path length**: a rooms root deep under the profile plus a 40-character slug stays far under 260, but long-path behaviour with `git` was not measured.

