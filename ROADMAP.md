# Chop It Up — ROADMAP
<!-- roadmap-schema: whitelist-v3 -->

## Definition
Local Windows hub where Yovan, Claude (Claude Desktop) and GPT (Codex UI inside the ChatGPT desktop app) chat in shared rooms over MCP, each on its own subscription — no API keys, no consumer-UI automation, loopback-only. Repo: github.com/yovanmc/ChopItUp (private until first release, then public).

## Milestones
| # | Title | Status | Ready | Plan | Notes |
|---|-------|--------|-------|------|-------|
| 1 | Hub core: SQLite rooms + messages, MCP Streamable HTTP on loopback (`list_rooms`, `read_messages`, `post_message`, `wait_for_message`), token-stamped authorship | ✅ | DONE | — | Merged `a80ba0c` (PR #2). 36 tests, 0 warnings; A1–A9 named-tested, A3–A8 over the real transport. |
| 2 | Host wiring: server-shipped participation prompt + tool descriptions, mcp-remote config for Claude Desktop, Codex UI MCP settings, live checks with both hosts | 📝 | READY | [m2-host-wiring](docs/superpowers/plans/m2-host-wiring.md) | HIGH. Critique 6.8 + 6.9, 25 folded. Schema v2 + verified backup, `client_key` retries, rotation, `--print-config`. Live host checks owner-only. |
| 3 | Web UI: React + Vite + TS chat over SignalR — rooms, @mentions, paste-a-transcript import, per-room markdown export | [ ] | BACKLOG | — | MEDIUM. UIA interactive gate via browser pane before "verified". |
| 4 | Release: single-file exe published to `C:\Self Apps\ChopItUp\` with `data\` beside it; repo flips public | [ ] | BACKLOG | — | Publish with `IncludeNativeLibrariesForSelfExtract` (e_sqlite3.dll; `AppContext.BaseDirectory` stays beside the exe). Deletes the grill-notes ledger on flip. |
| 5 | Autonomous turns: `wait_for_message` loop guidance, Claude Code as a host, model-to-model exchange without the owner | [ ] | DEFERRED: after M1–M4 ship and get real use | — | — |
| 6 | Town view: walkable characters per participant (Octopath-style) | [ ] | DEFERRED: long-term vision, owner ruling 2026-09-04 | — | Renderer/art decisions parked in the grill notes (Q17, Q20). |
| 7 | ChatGPT chat-tab read-only connector via tunnel | [ ] | DEFERRED: Plus-plan gating unverified; owner ruled tunnels out of v1 | — | help.openai.com 2026-09-04: write actions Business/Enterprise only; Pro read-only; Plus unlisted. |

**Legend:** ✅ Merged · 📝 Plan ready · 🔬 Researching/Planning · [ ] Not started

## Pointers
- BINDING: `docs/superpowers/plans/grill-notes-chop-it-up.md` (definition ledger — read before planning)
- Conventions + safety invariants: [CLAUDE.md](CLAUDE.md) · Lessons: [docs/LESSONS.md](docs/LESSONS.md)
- Tracker: [docs/agents/issue-tracker.md](docs/agents/issue-tracker.md) · Declined ideas: `.out-of-scope/` · History: git log
