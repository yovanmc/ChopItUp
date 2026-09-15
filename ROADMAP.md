# Chop It Up — ROADMAP
<!-- roadmap-schema: whitelist-v3 -->

## Definition
Local Windows hub where Yovan, Claude (Claude Desktop) and GPT (Codex UI inside the ChatGPT desktop app) chat in shared rooms over MCP, each on its own subscription — no API keys, no consumer-UI automation, loopback-only. Repo: github.com/yovanmc/ChopItUp (public since 2026-09-04).

## Milestones
| # | Title | Status | Ready | Plan | Notes |
|---|-------|--------|-------|------|-------|
| 31 | The deploy-day self-check has no agent runner | [ ] | OWNER: run `tools\Invoke-Row28SelfCheck.ps1` yourself with `-OwnerToken` and without `-SkipDataChecks`, or add ChopItUp to `selfAppsExemptApps` and let a session do the data legs | — | Two leg classes, one remedy. The data legs read under `C:\Self Apps\ChopItUp\data\`, denied in every session [V 2026-09-10 32847a8]. The auth legs (`auth.owner-token-post-accepted-201` and its `-ipv6` twin) need `-OwnerToken`, which a session cannot get: reading the deployed `tokens.json` is denied and `--rotate-token` would write the deployed data dir [V 2026-09-10 5ad8c57]. Row 29's AC3 holds in 930 tests and in token-free deployed probes, never as an owner-authenticated 201 against the real install. |
| 36 | Reply-to in the UI picks which exchange a post joins | ✅ | DONE | — | Merged `b90c6bf` (#96), deployed (self-check 27/27, live /health schema 11). 993→1027 .NET and 99→112 client tests, mutations 15/15, stub-CLI join dry run 9/9, M25 migration 35/35, UI gate pass. Run10 conductor test timed out once on CI, rerun green. Live Claude/Codex join not run (spend); rollback to v10 not rehearsed. |
| 26 | Owner probe: does a running Claude Code session actually read an exported memory directory | [ ] | OWNER: point `autoMemoryDirectory` at an exported scratch dir in a throwaway project, start a session, ask it — fixture per `docs/verification.md` "Owner probe" | — | Carried out of row 24 (`f964ece`, PR #63) when that row closed. Physically owner-only; not a merge gate. |
| 12 | Desktop shell: WebView2 window that starts the hub, hosts the room UI, sits in the tray | [ ] | READY | — | Grill D18. Owner rulings 09-13 (after stack research: Electron = ChatGPT/Codex/Cursor, WebView2 = new Teams, Tauri = Jan): a .NET host on system WebView2 (WinForms), the hub as a child process it stops on Quit, close hides to tray, no autostart. |
| 14 | Roles per participant and per-room personas | [ ] | READY | — | Grill D2/D4 v1.1. Owner rulings 09-13: a role is prompt text only (permissions unchanged); a global role per participant plus a per-room override and room persona; edited in the web UI, owner bearer only; kept separate from classes. |
| 6 | Town view: walkable characters per participant (Octopath-style) | [ ] | DEFERRED: long-term vision, owner ruling 2026-09-04 | — | Renderer and art were deferred with the milestone: working assumptions were Phaser 3 in-page and CC0 placeholder art with simple avatars. Neither is decided — re-open both when this row starts. |
| 7 | ChatGPT chat-tab read-only connector via tunnel | [ ] | DEFERRED: Plus-plan gating unverified; owner ruled tunnels out of v1 | — | help.openai.com 2026-09-04: write actions Business/Enterprise only; Pro read-only; Plus unlisted. |

**Legend:** ✅ Merged · 📝 Plan ready · 🔬 Researching/Planning · [ ] Not started

## Pointers
- BINDING: `docs/superpowers/plans/grill-notes-m5-autonomy.md` (M5/M8/M9/M10 and the rows above amend it)
- Conventions + safety invariants: [CLAUDE.md](CLAUDE.md) · Lessons: [docs/LESSONS.md](docs/LESSONS.md)
- Tracker: [docs/agents/issue-tracker.md](docs/agents/issue-tracker.md) · Declined ideas: `.out-of-scope/` · History: git log
