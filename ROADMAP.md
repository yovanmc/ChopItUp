# Chop It Up — ROADMAP
<!-- roadmap-schema: whitelist-v3 -->

## Definition
Local Windows hub where Yovan, Claude (Claude Desktop) and GPT (Codex UI inside the ChatGPT desktop app) chat in shared rooms over MCP, each on its own subscription — no API keys, no consumer-UI automation, loopback-only. Repo: github.com/yovanmc/ChopItUp (public since 2026-09-04).

## Milestones
| # | Title | Status | Ready | Plan | Notes |
|---|-------|--------|-------|------|-------|
| 9 | Rooms as chats with a directory each: file + shell access inside the room's git tree, hub-owned commit trail | ✅ | DONE | — | Merged `cf237e8` (PR #36) 2026-09-06: schema v6, `/api/rooms`, refused roots, `GitTrail`, `dontAsk` directory spawns, owner+agent commits with the shell log, chat-list rail. 333 tests; live 29/29, self-check 27/27, UI gates PASS (5 defects fixed). Deployed, DB v6. |
| 11 | Skill substrate: hub-owned skills in `data/skills/`, slash invocation, roster classes, owner-remote credential | [ ] | READY | — | Grill 2026-09-06: ledger D3, D5, D7, D11 (BINDING file in Pointers). Ships grilling + codebase-design as plain skills; `/grill @opus @gpt-6-astra` side by side (M5-D19); a remote-control session posts as `owner-remote` and opens an exchange. HIGH. |
| 19 | Runs: a conductor participant re-spawned per phase, steer/stop/park, phase-tagged posts checked by the hub, run caps, effort by class, `run_gate` | [ ] | BLOCKED: row 11 not shipped | — | Ledger D1, D2, D4, D6, D8, D9, D10, A2, A3. Amends M5-D2/D5/D7 inside a run only. Real-CLI check: a two-phase toy skill reaches its ping with no owner post between phases. HIGH. |
| 20 | Roadmap skill ported: import + overlay, phases cut to 4-turn exchanges, gate manifest, ping as a post, scratch run then ChopItUp dogfood from a phone | [ ] | BLOCKED: row 19 not shipped | — | Ledger D12, D13, F10 probe. Not DONE until the dogfood run on this repo has shipped a row driven through `owner-remote`. |
| 18 | Memory v1.1: supersession, core-cap refusal, consolidation, recall search, fenced injection, approval card, room scope, vendor export | [ ] | BLOCKED: rows 11, 19, 20 first (ledger order) | — | Ledger: docs/superpowers/plans/memory-v1-1-findings.md (BINDING). Defect: `core` approvals past 6,000 chars are silently cut from every spawn [V 2026-09-06 1b701b29]. |
| 12 | Desktop shell: WebView2 window that starts the hub, hosts the room UI, sits in the tray | [ ] | BACKLOG | — | Grill D18. After the autonomy rows. |
| 13 | Symmetric confinement: run Claude spawns under a restricted Windows account | [ ] | BACKLOG | — | Grill D13, F5. Only route to OS-level confinement on native Windows. Measured 2026-09-06: Codex 0.153.3 `--approve-for-me` did not stop a `git commit` inside the workspace — cover both CLIs. M9 residuals (no-auth `/api`, `tokens.json`, absolute-path git) land here. |
| 14 | Roles per participant and per-room personas | [ ] | BACKLOG | — | Grill D2/D4 v1.1. |
| 17 | Spawner test `A5_a_participant_is_never_in_flight_twice_in_one_room` flakes on slow runners | [ ] | LEAD: cause unverified; existence [V 2026-09-06 930be773] CI fail on docs-only PR #31 (run 34013942998) plus one local one-off | — | Pending mark not published within the 500 ms quiet window; inferred: timing, not logic. Reproduce under load, then fix the wait or the publish path. |
| 6 | Town view: walkable characters per participant (Octopath-style) | [ ] | DEFERRED: long-term vision, owner ruling 2026-09-04 | — | Renderer and art were deferred with the milestone: working assumptions were Phaser 3 in-page and CC0 placeholder art with simple avatars. Neither is decided — re-open both when this row starts. |
| 7 | ChatGPT chat-tab read-only connector via tunnel | [ ] | DEFERRED: Plus-plan gating unverified; owner ruled tunnels out of v1 | — | help.openai.com 2026-09-04: write actions Business/Enterprise only; Pro read-only; Plus unlisted. |

**Legend:** ✅ Merged · 📝 Plan ready · 🔬 Researching/Planning · [ ] Not started

## Pointers
- BINDING: `docs/superpowers/plans/grill-notes-row11-harness-in-room.md` (rows 11/19/20; supersedes M5-D16) · `docs/superpowers/plans/grill-notes-m5-autonomy.md` (M5/M8/M9/M10 and the rows above amend it)
- Conventions + safety invariants: [CLAUDE.md](CLAUDE.md) · Lessons: [docs/LESSONS.md](docs/LESSONS.md)
- Tracker: [docs/agents/issue-tracker.md](docs/agents/issue-tracker.md) · Declined ideas: `.out-of-scope/` · History: git log
