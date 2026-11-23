# Row 52 run notes

Spec for the review: the done-when criteria in the first commit (6bde580), as folded in `design.md` ("Criteria as folded"), plus its "Deviations" entry.

## Punch list (resolved without the owner)
- `-c web_search="live"` dropped: Codex 0.169.1 searches by default (measured), so the flag changed nothing.
- `[::1]` is not an open path today (Claude refuses dotless hosts). The deny rule is added as a second lock.
- Plain Codex keeps `--approve-for-me` with no fence (BUGS 90 class). Queued as BUGS 131, disclosed in its prompt. Measured: every hub-launched plain Codex run first read the owner's Codex guidance file through pwsh (the user-level AGENTS.md loads despite `--ignore-user-config`), so most plain Codex notes carry that path.
- Directory-room and panel Codex web searches stay unrecorded. Queued as BUGS 130.
- Hub-launched Codex searches through its own `codex_apps` MCP server, not a `web_search` item (the design deviation). `CodexCalls` now lists every MCP call outside the hub. The `codex_apps` reach in every Codex shape is queued as BUGS 132. No disable flag was added, because criterion 2 adds no settings.

## Evidence
- Spawning and Mcp tests at c94b1b6's code: 578 of 581 passed. Two failures pinned the retired plain command line and are rewritten with `retired:` reasons in the commit. The third, JobToolsTests (BUGS 122 race), passed on a targeted rerun.
- Spawning, Mcp and RoomCommitsTests after the codex_apps fix: 589 of 589 passed.
- The codex_apps fix: 7 new `SpawnOutputTests` cases RED before the change, then 47 of 47 green. 16 plain-room spawner tests green, including the new Codex hub-calls-only row.
- Live check `tools/Invoke-PlainWebCheck.ps1` on the dev exe: runs 1 to 3 at 12 of 13 (the Codex note had no search line). A pass-through shim on PATH captured run 3's Codex stream: no `web_search` item, one `codex_apps` `search_service.web_run` call. After the fix, run 4: 13 of 13 PASS. The Claude note listed the search, the example.com fetch and `http://127.0.0.1:<port>/health` marked `[denied]`. The Codex note listed its shell read and `WebSearch "Model Context Protocol specification latest revision"`.

## Could not verify in this environment
- The deployed hub's own plain-room command line is not observable without a spawn on real data. The desk check reads the deployed exe's strings and schema instead.
