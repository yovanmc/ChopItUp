# Chop It Up — ROADMAP
<!-- roadmap-schema: whitelist-v3 -->

## Definition
Local Windows hub where Yovan, Claude (Claude Desktop) and GPT (Codex UI inside the ChatGPT desktop app) chat in shared rooms over MCP, each on its own subscription — no API keys, no consumer-UI automation, loopback-only. Repo: github.com/yovanmc/ChopItUp (public since 2026-09-04).

## Milestones
| # | Title | Status | Ready | Plan | Notes |
|---|-------|--------|-------|------|-------|
| 27 | A cap-driven park tells the owner they stopped it | ✅ | DONE | — | Merged `ff6aa43` (PR #70); deployed 09-09, self-check 27/27. **Rollback: `C:\Self Apps\ChopItUp.backup-20260909-225323`**. The stop cause is passed, never inferred: `ExchangeStopCause` rides `RunDecision.End` and the snapshot, so the note and the bar name the run. .NET 867 → 873; client 51 → 54. |
| 28 | A spawn can act as the owner | 🔨 | OWNER: deploy at the desk, then run `tools\Invoke-Row28SelfCheck.ps1` (its header carries the order) | .scratch/m28-spawn-owner-escalation/plan.md | Merged `d4a209f` (#73) + `6ffad42` (#74); NOT deployed, decision D-28-c: the client cannot post until the owner pastes the token. Dry run 30/30, self-check 7/7 on scratch. .NET 873 → 896. |
| 29 | Symmetric confinement: an owner-class credential still sits outside the data dir | [ ] | READY | — | Row 28 closed the data-dir paths. `owner-remote`’s token is pasted outside them, `Bash` has no read fence and Codex spawns no deny list, so a spawn that finds it posts as owner [V 2026-09-10 6ffad42]. Row 13’s residual. |
| 30 | Four live-check legs lost their cheap spawnable-row bearer | [ ] | READY | — | Legs that read a spawnable row’s plaintext from `tokens.json` cannot: those rows are ephemeral now. `Invoke-M18MemoryCheck` (3 opus legs) and `Invoke-M25SkillProposalCheck` (1, plus `ac3.no-second-card` passing by coincidence) [V 2026-09-10 6ffad42]. Move them to an app-backed row or spend a real spawn. |
| 26 | Owner probe: does a running Claude Code session actually read an exported memory directory | [ ] | OWNER: point `autoMemoryDirectory` at an exported scratch dir in a throwaway project, start a session, ask it — fixture per `docs/verification.md` "Owner probe" | — | Carried out of row 24 (`f964ece`, PR #63) when that row closed. Physically owner-only; not a merge gate. |
| 12 | Desktop shell: WebView2 window that starts the hub, hosts the room UI, sits in the tray | [ ] | BACKLOG | — | Grill D18. After the autonomy rows. |
| 14 | Roles per participant and per-room personas | [ ] | BACKLOG | — | Grill D2/D4 v1.1. |
| 17 | Spawner test A5 races its own HTTP round trip | [ ] | READY | — | TEST defect, not product [V 2026-09-10 c09ac48]: 6/55 under pinned-CPU load, 0/15 solo. Line 287 asserts `Pending` immediately after a real MCP HTTP round trip on a 500 ms budget; the failure is `Collection: []`, an early read, not a dropped mark. The product has no 500 ms window — hypothesis refuted. Fix: poll, plus a direct `ExchangePolicy.Accept` test. |
| 6 | Town view: walkable characters per participant (Octopath-style) | [ ] | DEFERRED: long-term vision, owner ruling 2026-09-04 | — | Renderer and art were deferred with the milestone: working assumptions were Phaser 3 in-page and CC0 placeholder art with simple avatars. Neither is decided — re-open both when this row starts. |
| 7 | ChatGPT chat-tab read-only connector via tunnel | [ ] | DEFERRED: Plus-plan gating unverified; owner ruled tunnels out of v1 | — | help.openai.com 2026-09-04: write actions Business/Enterprise only; Pro read-only; Plus unlisted. |

**Legend:** ✅ Merged · 📝 Plan ready · 🔬 Researching/Planning · [ ] Not started

## Pointers
- BINDING: `docs/superpowers/plans/grill-notes-m5-autonomy.md` (M5/M8/M9/M10 and the rows above amend it)
- Conventions + safety invariants: [CLAUDE.md](CLAUDE.md) · Lessons: [docs/LESSONS.md](docs/LESSONS.md)
- Tracker: [docs/agents/issue-tracker.md](docs/agents/issue-tracker.md) · Declined ideas: `.out-of-scope/` · History: git log
