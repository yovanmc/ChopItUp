# Chop It Up — ROADMAP
<!-- roadmap-schema: whitelist-v3 -->

## Definition
Local Windows hub where Yovan, Claude (Claude Desktop) and GPT (Codex UI inside the ChatGPT desktop app) chat in shared rooms over MCP, each on its own subscription — no API keys, no consumer-UI automation, loopback-only. Repo: github.com/yovanmc/ChopItUp (public since 2026-09-04).

## Milestones
| # | Title | Status | Ready | Plan | Notes |
|---|-------|--------|-------|------|-------|
| 31 | Deploy-day self-check runs from an agent session | 📝 | READY | .scratch/m31-selfcheck-agent-runner/brief.md | Rescoped [V 2026-09-16 be930d8]: `-OwnerToken` is Mandatory (script:115) so nothing runs without it; the data legs re-check what the hub enforces at every start (`TokenStore.Load` rehash, `SweepLiveTokens`; a failure posts a `hub` note in general). Owner 201 leg ran live 2026-09-10 (general msg 40); `[::1]` answers 200/401 today. Make token+publish optional, data legs read hub surfaces. |
| 26 | Probe: does a running Claude Code session read an exported memory directory | [ ] | READY | — | Agent-doable [V 2026-09-16 vendor docs]: `claude -p` without `--bare` loads project `.claude/settings.json`; `autoMemoryDirectory` reads from any scope incl. `--settings` (code.claude.com/docs/en/memory.md, headless.md); index cap = first 200 lines or 25 KB; four `type` values. Fixture per `docs/verification.md` "Owner probe"; spends 2-3 CLI calls. Carried out of row 24 (`f964ece`). |
| 14 | Roles per participant and per-room personas | ✅ | DONE | — | Merged `000563b` (#101) + fixes `b38648e` (#102), deployed: self-check 29/29, live hub schema 12. 1145→1208 .NET, 137→162 client, `Invoke-Row14RolesCheck.ps1` 24/24. Post-merge read-the-code review: 2 MAJOR + 5 MINOR, fixed. Backup `ChopItUp.backup-20260916-102234`; store `.v11.<stamp>.bak`. Dialog visual sign-off pending. |
| 6 | Town view: walkable characters per participant (Octopath-style) | [ ] | DEFERRED: long-term vision, owner ruling 2026-09-04 | — | Renderer and art were deferred with the milestone: working assumptions were Phaser 3 in-page and CC0 placeholder art with simple avatars. Neither is decided — re-open both when this row starts. |
| 7 | ChatGPT chat-tab read-only connector via tunnel | [ ] | DEFERRED: Plus-plan gating unverified; owner ruled tunnels out of v1 | — | help.openai.com 2026-09-04: write actions Business/Enterprise only; Pro read-only; Plus unlisted. |

**Legend:** ✅ Merged · 📝 Plan ready · 🔬 Researching/Planning · [ ] Not started

## Pointers
- BINDING: `docs/superpowers/plans/grill-notes-m5-autonomy.md` (M5/M8/M9/M10 and the rows above amend it)
- Conventions + safety invariants: [CLAUDE.md](CLAUDE.md) · Lessons: [docs/LESSONS.md](docs/LESSONS.md)
- Tracker: [docs/agents/issue-tracker.md](docs/agents/issue-tracker.md) · Declined ideas: `.out-of-scope/` · History: git log
