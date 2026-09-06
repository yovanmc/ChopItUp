# Chop It Up — ROADMAP
<!-- roadmap-schema: whitelist-v3 -->

## Definition
Local Windows hub where Yovan, Claude (Claude Desktop) and GPT (Codex UI inside the ChatGPT desktop app) chat in shared rooms over MCP, each on its own subscription — no API keys, no consumer-UI automation, loopback-only. Repo: github.com/yovanmc/ChopItUp (public since 2026-09-04).

## Milestones
| # | Title | Status | Ready | Plan | Notes |
|---|-------|--------|-------|------|-------|
| 10 | Centralised memory: one store both vendors' agents read; agents propose, the owner approves in the room | ✅ | DONE | — | Merged `e838645` (PR #32) 2026-09-06: memory store + lazy git trail, schema v5 proposals, `recall`/`propose_memory`, approval API + panel, vendor import. Live check 19/19, dry run 24/24, self-check 27/27, screenshot + UIA PASS; deployed, live DB at v5 (`.v4.` backup beside it). Unverified: real Codex memory shape. |
| 9 | Rooms as chats with a directory each: file + shell access inside the room's git tree, hub-owned commit trail | [ ] | READY | — | Grill D3, D10–D13. Chat-list UI; refused roots; git read-only for agents; commit per spawn with author + shell log; no push; no reads outside. Confinement asymmetric (F5), stated in the plan. |
| 11 | Hub-owned skills: grilling, codebase-design, roadmap as host-neutral copies, slash-invoked per exchange | [ ] | READY | — | Grill D16. `data\skills\`; runs on whoever the message addresses so two models' output to one skill sit side by side. |
| 12 | Desktop shell: WebView2 window that starts the hub, hosts the room UI, sits in the tray | [ ] | BACKLOG | — | Grill D18. After the autonomy rows. |
| 13 | Symmetric confinement: run Claude spawns under a restricted Windows account | [ ] | BACKLOG | — | Grill D13, F5. Only route to OS-level confinement for Claude Code on native Windows. |
| 14 | Roles per participant and per-room personas | [ ] | BACKLOG | — | Grill D2/D4 v1.1. |
| 17 | Spawner test `A5_a_participant_is_never_in_flight_twice_in_one_room` flakes on slow runners | [ ] | LEAD: cause unverified; existence [V 2026-09-06 930be773] CI fail on docs-only PR #31 (run 34013942998) plus one local one-off | — | Pending mark not published within the 500 ms quiet window; inferred: timing, not logic. Reproduce under load, then fix the wait or the publish path. |
| 6 | Town view: walkable characters per participant (Octopath-style) | [ ] | DEFERRED: long-term vision, owner ruling 2026-09-04 | — | Renderer and art were deferred with the milestone: working assumptions were Phaser 3 in-page and CC0 placeholder art with simple avatars. Neither is decided — re-open both when this row starts. |
| 7 | ChatGPT chat-tab read-only connector via tunnel | [ ] | DEFERRED: Plus-plan gating unverified; owner ruled tunnels out of v1 | — | help.openai.com 2026-09-04: write actions Business/Enterprise only; Pro read-only; Plus unlisted. |

**Legend:** ✅ Merged · 📝 Plan ready · 🔬 Researching/Planning · [ ] Not started

## Pointers
- BINDING: `docs/superpowers/plans/grill-notes-m5-autonomy.md` (definition ledger for M5/M8/M9/M10/M11 — read before planning)
- Conventions + safety invariants: [CLAUDE.md](CLAUDE.md) · Lessons: [docs/LESSONS.md](docs/LESSONS.md)
- Tracker: [docs/agents/issue-tracker.md](docs/agents/issue-tracker.md) · Declined ideas: `.out-of-scope/` · History: git log
