# Chop It Up — ROADMAP
<!-- roadmap-schema: whitelist-v3 -->

## Definition
Local Windows hub where Yovan, Claude (Claude Desktop) and GPT (Codex UI inside the ChatGPT desktop app) chat in shared rooms over MCP, each on its own subscription — no API keys, no consumer-UI automation, loopback-only. Repo: github.com/yovanmc/ChopItUp (public since 2026-09-04).

## Milestones
| # | Title | Status | Ready | Plan | Notes |
|---|-------|--------|-------|------|-------|
| 33 | The M9 room check posts to `/api` with no bearer | ✅ | DONE | — | Merged `228ef14` (#87); `tools/` only, deploy unchanged. The check seeds a scratch `owner` token and sends it on non-GET `/api` calls. Before: 4/4 then a 401 at the first POST, no spawn. After: 29/29 PASS with one real Sonnet spawn. `-IncludeCodex` not run (spend). 931 tests green, PR CI passed. |
| 31 | The deploy-day self-check has no agent runner | [ ] | OWNER: run `tools\Invoke-Row28SelfCheck.ps1` yourself with `-OwnerToken` and without `-SkipDataChecks`, or add ChopItUp to `selfAppsExemptApps` and let a session do the data legs | — | Two leg classes, one remedy. The data legs read under `C:\Self Apps\ChopItUp\data\`, denied in every session [V 2026-09-10 32847a8]. The auth legs (`auth.owner-token-post-accepted-201` and its `-ipv6` twin) need `-OwnerToken`, which a session cannot get: reading the deployed `tokens.json` is denied and `--rotate-token` would write the deployed data dir [V 2026-09-10 5ad8c57]. Row 29's AC3 holds in 930 tests and in token-free deployed probes, never as an owner-authenticated 201 against the real install. |
| 32 | Two human prompts in one room run two exchanges side by side | 📝 | READY | docs/superpowers/plans/row32-side-by-side-exchanges.md | HIGH. Amends D5 (owner rulings 09-13): a prompt supersedes only exchanges it shares a mentioned model with; no mention supersedes nothing. Hub + API only; a run room keeps one exchange. Critique fable 6.8, opus 6.6, both folded. |
| 34 | One exchange bar per exchange, each with its own stop | [ ] | READY | — | Row 32's UI half. Until it ships the one Stop ends every exchange in the room and the bar shows the newest open one. |
| 35 | A git worktree per exchange in a directory room, auto-merged on conclude | [ ] | READY | — | Owner ruling 09-13: both exchanges really edit at once; a conflict leaves the branch and posts a note naming it. Until then a directory room runs one spawn at a time. |
| 36 | Reply-to in the UI picks which exchange a post joins | [ ] | READY | — | Owner ruling 09-13: the true join, keeping budget and skill. Needs a reply-to field on messages. |
| 26 | Owner probe: does a running Claude Code session actually read an exported memory directory | [ ] | OWNER: point `autoMemoryDirectory` at an exported scratch dir in a throwaway project, start a session, ask it — fixture per `docs/verification.md` "Owner probe" | — | Carried out of row 24 (`f964ece`, PR #63) when that row closed. Physically owner-only; not a merge gate. |
| 12 | Desktop shell: WebView2 window that starts the hub, hosts the room UI, sits in the tray | [ ] | READY | — | Grill D18. After the autonomy rows. |
| 14 | Roles per participant and per-room personas | [ ] | READY | — | Grill D2/D4 v1.1. |
| 6 | Town view: walkable characters per participant (Octopath-style) | [ ] | DEFERRED: long-term vision, owner ruling 2026-09-04 | — | Renderer and art were deferred with the milestone: working assumptions were Phaser 3 in-page and CC0 placeholder art with simple avatars. Neither is decided — re-open both when this row starts. |
| 7 | ChatGPT chat-tab read-only connector via tunnel | [ ] | DEFERRED: Plus-plan gating unverified; owner ruled tunnels out of v1 | — | help.openai.com 2026-09-04: write actions Business/Enterprise only; Pro read-only; Plus unlisted. |

**Legend:** ✅ Merged · 📝 Plan ready · 🔬 Researching/Planning · [ ] Not started

## Pointers
- BINDING: `docs/superpowers/plans/grill-notes-m5-autonomy.md` (M5/M8/M9/M10 and the rows above amend it)
- Conventions + safety invariants: [CLAUDE.md](CLAUDE.md) · Lessons: [docs/LESSONS.md](docs/LESSONS.md)
- Tracker: [docs/agents/issue-tracker.md](docs/agents/issue-tracker.md) · Declined ideas: `.out-of-scope/` · History: git log
