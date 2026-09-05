# Chop It Up — ROADMAP
<!-- roadmap-schema: whitelist-v3 -->

## Definition
Local Windows hub where Yovan, Claude (Claude Desktop) and GPT (Codex UI inside the ChatGPT desktop app) chat in shared rooms over MCP, each on its own subscription — no API keys, no consumer-UI automation, loopback-only. Repo: github.com/yovanmc/ChopItUp (public since 2026-09-04).

## Milestones
| # | Title | Status | Ready | Plan | Notes |
|---|-------|--------|-------|------|-------|
| 4b | Flip the repository public | ✅ | DONE | — | Public 2026-09-04 on Yovan's explicit yes; closes the v1 release started in M4 (`deb52c4`, PR #12, archived). No `confidentiality-review` (CLAUDE.md ruling: no employer content); full history scanned clean at `cdb0732` — no db/token/data file ever committed. Paired delete: the grill-notes ledger. |
| 8 | Roster as data: participants become rows named for models, tokens and host configs per row | 📝 | READY | [plan](docs/superpowers/plans/m8-roster-as-data.md) | Grill D14. HIGH: schema v3 (host/model/note + 9 spawn rows); tokens, configs, prompt, list_rooms, client all read the roster. Ids kept. Critique 6.8 → 6.4 → recheck clean; tickets `.scratch/m8-roster-as-data/`. M5 prerequisite. |
| 5 | Spawner core: the hub spawns a headless participant when a message mentions it | [ ] | BLOCKED: M8 (participant rows carry host + model) | — | Grill D5–D9, D17. Stateless spawns for both hosts, mention-only trigger, debounce, budget 4, rate limit, timeout, close mechanics, stop button, indicators. No files, memory or skills. Contracts F1–F4. |
| 10 | Centralised memory: one store both vendors' agents read; agents propose, the owner approves in the room | [ ] | BLOCKED: M5 (spawn prompt rendering) | — | Grill D15, F9–F10. `data\memory\` markdown, git-backed; ~1,500-token core injected; `recall(topic)` tool for the rest and for interactive hosts; importers from both vendors' memory as proposals. |
| 9 | Rooms as chats with a directory each: file + shell access inside the room's git tree, hub-owned commit trail | [ ] | BLOCKED: M5 | — | Grill D3, D10–D13. Chat-list UI; refused roots; git read-only for agents; commit per spawn with author + shell log; no push; no reads outside. Confinement asymmetric (F5), stated in the plan. |
| 11 | Hub-owned skills: grilling, codebase-design, roadmap as host-neutral copies, slash-invoked per exchange | [ ] | BLOCKED: M5 | — | Grill D16. `data\skills\`; runs on whoever the message addresses so two models' output to one skill sit side by side. |
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
