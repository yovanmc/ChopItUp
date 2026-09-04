# Chop It Up — ROADMAP
<!-- roadmap-schema: whitelist-v3 -->

## Definition
Local Windows hub where Yovan, Claude (Claude Desktop) and GPT (Codex UI inside the ChatGPT desktop app) chat in shared rooms over MCP, each on its own subscription — no API keys, no consumer-UI automation, loopback-only. Repo: github.com/yovanmc/ChopItUp (private until first release, then public).

## Milestones
| # | Title | Status | Ready | Plan | Notes |
|---|-------|--------|-------|------|-------|
| 4 | Release: single-file exe published to `C:\Self Apps\ChopItUp\` with `data\` beside it | ✅ | DONE | — | Merged `deb52c4` (PR #12), deployed 2026-09-04. 109 tests, 0 warnings; self-check 27/27 PASS against the published artifact **and** the real target. Grill notes survive: they hold R7, which guards 4b. |
| 4b | Flip the repository public | [ ] | OWNER: needs Yovan's explicit yes; run `confidentiality-review` first | — | Split out of M4 so the release can ship without it. Once said, the grill-notes ledger goes with it. |
| 5 | Autonomous turns: `wait_for_message` loop guidance, Claude Code as a host, model-to-model exchange without the owner | [ ] | DEFERRED: after M1–M4 ship and get real use | — | — |
| 6 | Town view: walkable characters per participant (Octopath-style) | [ ] | DEFERRED: long-term vision, owner ruling 2026-09-04 | — | Renderer/art decisions parked in the grill notes (Q17, Q20). |
| 7 | ChatGPT chat-tab read-only connector via tunnel | [ ] | DEFERRED: Plus-plan gating unverified; owner ruled tunnels out of v1 | — | help.openai.com 2026-09-04: write actions Business/Enterprise only; Pro read-only; Plus unlisted. |

**Legend:** ✅ Merged · 📝 Plan ready · 🔬 Researching/Planning · [ ] Not started

## Pointers
- BINDING: `docs/superpowers/plans/grill-notes-chop-it-up.md` (definition ledger — read before planning)
- Conventions + safety invariants: [CLAUDE.md](CLAUDE.md) · Lessons: [docs/LESSONS.md](docs/LESSONS.md)
- Tracker: [docs/agents/issue-tracker.md](docs/agents/issue-tracker.md) · Declined ideas: `.out-of-scope/` · History: git log
