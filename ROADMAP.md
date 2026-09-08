# Chop It Up — ROADMAP
<!-- roadmap-schema: whitelist-v3 -->

## Definition
Local Windows hub where Yovan, Claude (Claude Desktop) and GPT (Codex UI inside the ChatGPT desktop app) chat in shared rooms over MCP, each on its own subscription — no API keys, no consumer-UI automation, loopback-only. Repo: github.com/yovanmc/ChopItUp (public since 2026-09-04).

## Milestones
| # | Title | Status | Ready | Plan | Notes |
|---|-------|--------|-------|------|-------|
| 19 | Runs: a conductor participant re-spawned per phase, steer/stop/park, phase-tagged posts checked by the hub, run caps, effort by class, `run_gate` | ✅ | DONE | — | Merged `608c676` (PR #44) 2026-09-07: schema v8, runs tables, RunPolicy, caps, `run_gate`, run strip. 594 tests, M19 live 12/12, M2 corpus 24/24. Deployed; /health schema 8; M4 27/27, desk check 7/7. Rollback exe: `C:\Self Apps\ChopItUp.backup-20260907-195240`. Unverified: MCP_TOOL_TIMEOUT inferred; no Codex row has a class, so Codex effort/timeout untested live. |
| 20 | Roadmap skill ported: import + overlay, phases cut to 4-turn exchanges, gate manifest, ping as a post, scratch run then ChopItUp dogfood from a phone | 📝 | READY | docs/superpowers/plans/row20-roadmap-port.md | HIGH; critique 6.2 (opus) then 6.0 (fable), all folded. F10 measured. Overlay + gate scripts under `tools/skills/roadmap-hub/`. Not DONE until the dogfood run has shipped a row through `owner-remote`. |
| 18 | Memory v1.1: supersession, core-cap refusal, consolidation, recall search, fenced injection, approval card, room scope, vendor export | [ ] | BLOCKED: rows 19, 20 first (ledger order) | — | Ledger: docs/superpowers/plans/memory-v1-1-findings.md (BINDING). Defect: `core` approvals past 6,000 chars are silently cut from every spawn [V 2026-09-06 1b701b29]. |
| 12 | Desktop shell: WebView2 window that starts the hub, hosts the room UI, sits in the tray | [ ] | BACKLOG | — | Grill D18. After the autonomy rows. |
| 13 | Symmetric confinement: run Claude spawns under a restricted Windows account | [ ] | BACKLOG | — | Grill D13, F5. Only OS-level confinement works on native Windows. Measured: Codex `--approve-for-me` did not stop an in-workspace `git commit` (09-06); the abs-path deny binds Claude file tools but NOT `Bash`, so BOTH hosts reach the data dir (09-07). M9 residuals (no-auth `/api`, `tokens.json`, abs-path git) land here. |
| 14 | Roles per participant and per-room personas | [ ] | BACKLOG | — | Grill D2/D4 v1.1. |
| 17 | Spawner test `A5_a_participant_is_never_in_flight_twice_in_one_room` flakes on slow runners | [ ] | LEAD: cause unverified; existence [V 2026-09-06 930be773] CI fail on docs-only PR #31 (run 34013942998) plus one local one-off | — | Pending mark not published within the 500 ms quiet window; inferred: timing, not logic. Reproduce under load, then fix the wait or the publish path. |
| 6 | Town view: walkable characters per participant (Octopath-style) | [ ] | DEFERRED: long-term vision, owner ruling 2026-09-04 | — | Renderer and art were deferred with the milestone: working assumptions were Phaser 3 in-page and CC0 placeholder art with simple avatars. Neither is decided — re-open both when this row starts. |
| 7 | ChatGPT chat-tab read-only connector via tunnel | [ ] | DEFERRED: Plus-plan gating unverified; owner ruled tunnels out of v1 | — | help.openai.com 2026-09-04: write actions Business/Enterprise only; Pro read-only; Plus unlisted. |

**Legend:** ✅ Merged · 📝 Plan ready · 🔬 Researching/Planning · [ ] Not started

## Pointers
- BINDING: `docs/superpowers/plans/grill-notes-row11-harness-in-room.md` (rows 11/19/20; supersedes M5-D16) · `docs/superpowers/plans/grill-notes-m5-autonomy.md` (M5/M8/M9/M10 and the rows above amend it)
- Conventions + safety invariants: [CLAUDE.md](CLAUDE.md) · Lessons: [docs/LESSONS.md](docs/LESSONS.md)
- Tracker: [docs/agents/issue-tracker.md](docs/agents/issue-tracker.md) · Declined ideas: `.out-of-scope/` · History: git log
