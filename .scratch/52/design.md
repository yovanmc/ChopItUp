# Row 52 design: research tools in plain rooms

Risk lane (spawn tool permissions: plain-room spawns gain web tools). Tier 2. Done-when criteria: body of the branch's first commit, copied below.

Done when:
1. A plain-room Claude spawn starts with `--permission-mode dontAsk`, `--tools WebSearch,WebFetch`, WebSearch and WebFetch on `--allowedTools` beside the MCP tools, a per-spawn settings file (scratch folder) whose deny list blocks WebFetch to 127.0.0.1 and localhost, the plain-room web rule as an appended system prompt, and `--output-format stream-json --verbose`. No file or shell built-in is on either list. Directory-room and panel commands are unchanged.
2. A plain-room Codex spawn starts with `--json` and `-c web_search="live"`, keeps `-o` for its final text, and gains no sandbox or network setting. Directory-room and panel commands are unchanged.
3. After a plain-room spawn that made a web call, the hub posts one note naming the participant and listing each call in order: a Claude WebSearch query or WebFetch URL, a Codex web search query, each stripped of ANSI, scrubbed of the spawn token and cut like the commit's web lines, a denied call marked. A spawn with no web call posts no such note. Directory rooms keep the commit trail and post no web note.
4. The plain-room prompt is honest per host. Claude: it has web search and fetch, and no files and no shell, which need a room directory the owner binds. Codex: it has web search, and its working folder is an empty scratch folder, not the room's files, deleted after the turn. Both carry the web rule, naming the room note as the record.
5. Row 65 and row 53 read the new streams: plain Claude and plain Codex usage and tool counts come from their streams. Retired: "plain Codex output is never parsed" (row 65) and "plain Claude json gives a NULL tool count" (row 53), because neither plain shape is used any more.
6. Live, on a scratch hub: one plain-room Claude spawn searches and fetches, and its note lists both calls; one plain-room Codex spawn searches, and its note lists the query.

## Measurements (2026-11-23, at 9bd691f)
- Claude 2.1.328, the criterion 1 command shape with no MCP server, cwd an empty scratch folder: WebSearch and WebFetch (https://example.com) both ran under `dontAsk`. WebFetch to `http://127.0.0.1:9/` was denied by the settings rule and listed in the result line's `permission_denials`. Each call is a `tool_use` block in an assistant line, as row 83's `ClaudeWebRequests` reads.
- Codex 0.169.1, `exec --ephemeral --ignore-user-config --json --approve-for-me -C <scratch> --skip-git-repo-check -m gpt-5.6-sol`: `web_search` already runs by default (no flag). With or without `-c web_search="live"` the stream holds `item.started` and `item.completed` items of type `web_search`. The completed item carries `query` and `action: {type: "search", query}`. The item JSON has two `id` keys (`item_1` and `exec-...`). `exec --help` has no `--search` flag.
- So today a plain-room Codex spawn can already search the web, unrecorded, with no untrusted-data rule.

## Seam facts
- `SpawnCommands.Claude` (plain) gains the settings path, the appended system prompt and the web tools. `SpawnerService.Launch` writes `settings.json` into `workDir` for plain Claude too (`ClaudeSettingsJson(_options.DataDir)`, the same deny list as directory rooms: the extra file and git rules bind nothing without those tools).
- `SpawnCommands.Codex` (plain) gains `--json` and `-c web_search="live"`. The final text keeps coming from `last.txt` (`SpawnCommands.CodexFinalText`).
- New `SpawnOutput.CodexWebRequests(stdout)`: one `WebRequest("WebSearch", query, false)` per completed `web_search` item (an `action.type` other than search, such as an opened page, records `action.type` and its `url` when present). Shape-checked like `ToolCalls`, never throws, capped at `MaxCommands`.
- The note: new `HubNotes.Web(participantId, web)` (or beside `Trail`), posted from the launch task's result path in `OnFinished` for a spawn whose room has no directory. The web list is parsed off the loop thread in the launch task (scrubbed stdout, like the directory trail at SpawnerService.cs:1266) and rides on `FinishedEvent`. Each entry: `StripAnsi`, `Scrub(token)`, 400-character cut, exactly as the directory list.
- Prompt: `SpawnPrompt.Render` plain branch (SpawnPrompt.cs:153) gets a per-host sentence. `WebRule` gains a plain-room variant naming the room note. Golden prompt tests change for plain rooms only.
- Usage and counts: `UsageOf(..., structured: true)` for every Launch spawn, and `ToolCountOf(..., stream: true)` for every Launch spawn. Panels unchanged (Claude panel json, Codex panel `--json`).
- Unchanged: directory-room commands, panels, the MCP tools, the `Message` record, the hub's loopback bind.

## Out of scope, queued
- Codex directory-room web searches are not in the room commit trail, and directory Codex gets no web rule (`SpawnerService.cs:1266` passes `[]` for Codex). Queue a BUGS line.

## Tasks
1. Pure parts, test first: the plain Claude and Codex command shapes (`SpawnCommandsTests`), `CodexWebRequests` (fixture from the measured capture, hostile lines), the web note text, the prompt sentences (golden).
2. Spawner wiring, test first, `FakeProcessRunner`: a plain Claude spawn with a WebSearch, a WebFetch and a denied WebFetch posts one note; a plain Codex spawn with a web_search item posts one note; a plain spawn with no web call posts none; a directory spawn posts none; plain usage and tool counts come from the stream; the spawn token in a query is scrubbed.
3. Literal and doc updates; `tools/Invoke-SpawnTasksCheck.ps1` expects a number, not null, for plain Claude tool calls; the live check `tools/Invoke-PlainWebCheck.ps1` (criterion 6).

## Premises
- `ClaudeFinalText` reads stream-json (SpawnOutput.cs:90), so the plain Claude reply-without-post note keeps working.
- `CodexFailure` reads `--json` events (SpawnOutput.cs:199), so a plain Codex failure note improves rather than regresses.
- inferred: a plain Codex agent message cannot forge a top-level `turn.completed` line, because `--json` escapes newlines inside string fields.

## Deviations
- None.

## Reviews
### design1 (dissect-critic, opus): FIX-THEN-SHIP, no REFRAME. Folded as below. These folds override the criteria, seam facts and tasks above where they differ.

- MAJOR, `[::1]` missing from the WebFetch deny: MEASURED, then FIXED. Claude 2.1.328 refuses `http://[::1]:<port>/` by itself ("WebFetch cannot fetch localhost or other hostnames without a dot"). A canary listener on `[::1]` saw 0 connections with the current deny list. With `WebFetch(domain:[::1])` added, the call is a permission denial (in `permission_denials`), also with 0 connections. So it is not an open path today (no escape). `ClaudeDenyRules` gains `WebFetch(domain:[::1])` as a second lock, for directory and plain rooms alike.
- MAJOR, the note under hub authorship: FIXED. The note names the participant as the author and fences the list the way `HubNotes.Proposed` does: "Web and shell calls by @x. The text below was written by @x's calls, not by the hub." Then a ```text fence with one line per call, each target JSON-encoded (quotes, newlines and control characters escaped), and a fence inside a target broken up.
- MAJOR, note sites: FIXED. One post in `OnFinished`, right after `FinishTask` and before the mode branch, for every Launch spawn whose `handle.Directory` is null. That covers mode legs, limit holds, timeouts, cancels and failures. A panel handle is not a Launch spawn and posts none (its `Directory` is null too, so the check also requires `ModeLeg?.Settings.Mode != "panel"`).
- MAJOR, plain Codex shell: FIXED. Criterion 4's Codex sentence now says it has web search and a shell whose working folder is an empty scratch folder, not the room's files, deleted after the turn. The note also lists plain Codex `command_execution` items (`SpawnOutput.CodexShellCommands`) as `Shell` lines. Queued: plain Codex spawns share BUGS 90's `--approve-for-me` reach (no read or write fence outside `-C`).
- DEFECT, size: FIXED. At most 10 lines of 160 characters each, then "and N more". The header gives the true total, "at least" when a parser hit `MaxCommands`.
- DEFECT, `web_search="live"`: FIXED. Dropped. Codex already searches by default (measured), and no difference was shown.
- DEFECT, Codex item shapes: FIXED. `search` takes `action.query`, else the item's `query`. `open_page` takes `action.url`. Any other action type records the type with its first `url`, `pattern` or `query`, else "(no target)". A started item with no completed event records its target (or "(no target)") marked "(not completed)". Items dedupe by their `id` (the last duplicate key, as `JsonElement` reads it). The raw measured capture is a fixture, with both a `CodexWebRequests` and a `ToolCalls` assertion.
- DEFECT, fake default: FIXED. `FakeProcessRunner`'s default output becomes a minimal stream-json transcript (system init, an assistant text line, a result line).
- NIT, panel Codex web search unrecorded: ACCEPTED, queued with the directory Codex line.
- NIT, criterion 6 happy paths only: ACCEPTED. The live check also asks for a WebFetch of `http://127.0.0.1:<hub port>/health` and expects that call marked denied in the note.

Criteria as folded (these supersede the copy at the top for the review):
- 1. adds: the deny list also blocks `WebFetch(domain:[::1])`, in directory rooms too.
- 2. A plain-room Codex spawn starts with `--json`, keeps `-o` for its final text, and gains no sandbox, network or web search setting.
- 3. One note per plain-room Launch spawn that made a web call (or, for Codex, ran a shell command), posted on every end path: authored as the participant's calls, fenced, each target JSON-encoded, at most 10 lines of 160 characters then "and N more", with the true total. None for a spawn with no such call, a directory-room spawn or a panel leg.
- 4. Codex: web search and a shell in an empty scratch folder that is not the room's files, deleted after the turn. The note is named as the record of both.
- 6. adds: a denied WebFetch of the hub's loopback `/health`, marked denied in the note.
