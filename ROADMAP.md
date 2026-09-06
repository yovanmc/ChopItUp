# Chop It Up — ROADMAP
<!-- roadmap-schema: whitelist-v3 -->

## Definition
Local Windows hub where Yovan, Claude (Claude Desktop) and GPT (Codex UI inside the ChatGPT desktop app) chat in shared rooms over MCP, each on its own subscription — no API keys, no consumer-UI automation, loopback-only. Repo: github.com/yovanmc/ChopItUp (public since 2026-09-04).

## Milestones
| # | Title | Status | Ready | Plan | Notes |
|---|-------|--------|-------|------|-------|
| 5 | Spawner core: the hub spawns a headless participant when a message mentions it | ✅ | DONE | — | Merged `0f32ae7` (PR #27) 2026-09-05: exchanges, claude/codex spawns, `hub` row (schema v4), stop + state API + `ExchangeChanged`, live check. 181 tests; dry run 24/24; spawn check 18/18 (Debug + deployed); self-check 27/27; screenshot PASS. Deployed, live DB on v4 (`.v3.` backup beside it). Fixes folded: rooted `--data`, ANSI-free notes, CLI locator seam. |
| 16 | Live exchange UI: working indicators, remaining budget, stop button, concluded marker | 📝 | READY | [brief](.scratch/m16-live-exchange-ui/brief.md) | LOW tier, lite path (brief is gitignored scratch, deleted at the flip; branch `m16-live-exchange-ui`, 2026-09-05). Grill D17. Renders `GET /api/rooms/{id}/exchange` + SignalR `ExchangeChanged`; stop = `POST .../exchange/stop`; styles `hub` system notes. opus builder (owner-visible). |
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
