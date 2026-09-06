# Chop It Up — ROADMAP
<!-- roadmap-schema: whitelist-v3 -->

## Definition
Local Windows hub where Yovan, Claude (Claude Desktop) and GPT (Codex UI inside the ChatGPT desktop app) chat in shared rooms over MCP, each on its own subscription — no API keys, no consumer-UI automation, loopback-only. Repo: github.com/yovanmc/ChopItUp (public since 2026-09-04).

## Milestones
| # | Title | Status | Ready | Plan | Notes |
|---|-------|--------|-------|------|-------|
| 16 | Live exchange UI: working indicators, remaining budget, stop button, concluded marker | ✅ | DONE | — | Merged `5c6fa73` (PR #29) 2026-09-05: `ExchangeBar` (chips, turns left, Stop, outcome marker), hub notes as system rows, `seq`-guarded snapshot. Gated on the dev hub vs real Sonnet spawns (open/stop/concluded live, judge PASS); self-check 27/27; deployed, live hub restarted. Unverified: room switch, 409 banner. |
| 10 | Centralised memory: one store both vendors' agents read; agents propose, the owner approves in the room | [ ] | READY | — | Grill D15, F9–F10. `data\memory\` markdown, git-backed; ~1,500-token core injected; `recall(topic)` tool for the rest and for interactive hosts; importers from both vendors' memory as proposals. |
| 9 | Rooms as chats with a directory each: file + shell access inside the room's git tree, hub-owned commit trail | [ ] | READY | — | Grill D3, D10–D13. Chat-list UI; refused roots; git read-only for agents; commit per spawn with author + shell log; no push; no reads outside. Confinement asymmetric (F5), stated in the plan. |
| 11 | Hub-owned skills: grilling, codebase-design, roadmap as host-neutral copies, slash-invoked per exchange | [ ] | READY | — | Grill D16. `data\skills\`; runs on whoever the message addresses so two models' output to one skill sit side by side. |
| 12 | Desktop shell: WebView2 window that starts the hub, hosts the room UI, sits in the tray | [ ] | BACKLOG | — | Grill D18. After the autonomy rows. |
| 13 | Symmetric confinement: run Claude spawns under a restricted Windows account | [ ] | BACKLOG | — | Grill D13, F5. Only route to OS-level confinement for Claude Code on native Windows. |
| 14 | Roles per participant and per-room personas | [ ] | BACKLOG | — | Grill D2/D4 v1.1. |
| 6 | Town view: walkable characters per participant (Octopath-style) | [ ] | DEFERRED: long-term vision, owner ruling 2026-09-04 | — | Renderer and art were deferred with the milestone: working assumptions were Phaser 3 in-page and CC0 placeholder art with simple avatars. Neither is decided — re-open both when this row starts. |
| 7 | ChatGPT chat-tab read-only connector via tunnel | [ ] | DEFERRED: Plus-plan gating unverified; owner ruled tunnels out of v1 | — | help.openai.com 2026-09-04: write actions Business/Enterprise only; Pro read-only; Plus unlisted. |

**Legend:** ✅ Merged · 📝 Plan ready · 🔬 Researching/Planning · [ ] Not started

## Pointers
- BINDING: `docs/superpowers/plans/grill-notes-m5-autonomy.md` (definition ledger for M5/M8/M9/M10/M11 — read before planning)
- Conventions + safety invariants: [CLAUDE.md](CLAUDE.md) · Lessons: [docs/LESSONS.md](docs/LESSONS.md)
- Tracker: [docs/agents/issue-tracker.md](docs/agents/issue-tracker.md) · Declined ideas: `.out-of-scope/` · History: git log
