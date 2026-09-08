# Chop It Up — ROADMAP
<!-- roadmap-schema: whitelist-v3 -->

## Definition
Local Windows hub where Yovan, Claude (Claude Desktop) and GPT (Codex UI inside the ChatGPT desktop app) chat in shared rooms over MCP, each on its own subscription — no API keys, no consumer-UI automation, loopback-only. Repo: github.com/yovanmc/ChopItUp (public since 2026-09-04).

## Milestones
| # | Title | Status | Ready | Plan | Notes |
|---|-------|--------|-------|------|-------|
| 20 | Roadmap skill ported: import + overlay, phases cut to 4-turn exchanges, gate manifest, ping as a post, scratch run then ChopItUp dogfood from a phone | ✅ | DONE | — | Merged `2f787c8` (PR #48); closed by dogfood run #1 2026-09-08, which built row 21 end-to-end in 23 min (9 gates exit 0, PR #51 `3cdefc4`, ping posted). Rollback exe: `C:\Self Apps\ChopItUp.backup-20260908-052100`. Remote transport verified, nothing owner-pending: loopback-only (`HubHost.cs:40`) means a phone reaches the hub only through a session holding the `owner-remote` credential; `Run04` passes at HEAD. |
| 22 | Run strip: Stop-run button (D2's fourth stop; only `/stop` exists) | [ ] | BACKLOG | — | Gap found by the row 20 digest (plan R4). |
| 18 | Memory v1.1 core: supersession, core-cap refusal, recall search, keyed fenced injection, approval card, room scope (schema v9) | 📝 | READY | docs/superpowers/plans/memory-v1-1-core.md | Ledger: docs/superpowers/plans/memory-v1-1-findings.md (BINDING; items 1, 2, 4, 5, 6, 7). Defect re-verified [V 2026-09-08 b4c34621]: `core` approvals past 6,000 chars are silently cut from every spawn. Critique: opus 6.5, fable 7.0, both folded. Baseline 149 + 489 green. |
| 23 | Memory v1.1b: consolidation skill (`rewrite` proposals rendered as a diff) and Claude Code-shape vendor export | [ ] | BACKLOG | — | Ledger items 3 and 8, split from row 18 on 2026-09-08 (plan size; both depend on row 18's shapes). READY when 18 is DONE; the ledger's paired delete moves here. `autoMemoryDirectory` on the installed CLI still UNVERIFIED. |
| 12 | Desktop shell: WebView2 window that starts the hub, hosts the room UI, sits in the tray | [ ] | BACKLOG | — | Grill D18. After the autonomy rows. |
| 13 | Symmetric confinement: run Claude spawns under a restricted Windows account | [ ] | BACKLOG | — | Grill D13, F5. Only OS-level confinement works on native Windows. Measured: Codex `--approve-for-me` did not stop an in-workspace `git commit` (09-06); the abs-path deny binds Claude file tools but NOT `Bash`, so BOTH hosts reach the data dir (09-07). M9 residuals (no-auth `/api`, `tokens.json`, abs-path git) land here. |
| 14 | Roles per participant and per-room personas | [ ] | BACKLOG | — | Grill D2/D4 v1.1. |
| 17 | Spawner test `A5_a_participant_is_never_in_flight_twice_in_one_room` flakes on slow runners | [ ] | LEAD: cause unverified; existence [V 2026-09-06 930be773] CI fail on docs-only PR #31 (run 34013942998) plus one local one-off | — | Pending mark not published within the 500 ms quiet window; inferred: timing, not logic. Reproduce under load, then fix the wait or the publish path. |
| 6 | Town view: walkable characters per participant (Octopath-style) | [ ] | DEFERRED: long-term vision, owner ruling 2026-09-04 | — | Renderer and art were deferred with the milestone: working assumptions were Phaser 3 in-page and CC0 placeholder art with simple avatars. Neither is decided — re-open both when this row starts. |
| 7 | ChatGPT chat-tab read-only connector via tunnel | [ ] | DEFERRED: Plus-plan gating unverified; owner ruled tunnels out of v1 | — | help.openai.com 2026-09-04: write actions Business/Enterprise only; Pro read-only; Plus unlisted. |

**Legend:** ✅ Merged · 📝 Plan ready · 🔬 Researching/Planning · [ ] Not started

## Pointers
- BINDING: `docs/superpowers/plans/grill-notes-m5-autonomy.md` (M5/M8/M9/M10 and the rows above amend it)
- Conventions + safety invariants: [CLAUDE.md](CLAUDE.md) · Lessons: [docs/LESSONS.md](docs/LESSONS.md)
- Tracker: [docs/agents/issue-tracker.md](docs/agents/issue-tracker.md) · Declined ideas: `.out-of-scope/` · History: git log
