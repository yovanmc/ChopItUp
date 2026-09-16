# Chop It Up — ROADMAP
<!-- roadmap-schema: whitelist-v3 -->

## Definition
Local Windows hub where Yovan, Claude (Claude Desktop) and GPT (Codex UI inside the ChatGPT desktop app) chat in shared rooms over MCP, each on its own subscription — no API keys, no consumer-UI automation, loopback-only. Repo: github.com/yovanmc/ChopItUp (public since 2026-09-04).

## Milestones
| # | Title | Status | Ready | Plan | Notes |
|---|-------|--------|-------|------|-------|
| 31 | Deploy-day self-check runs from an agent session | ✅ | DONE | — | Merged `e97ccd2` (#104). `-OwnerToken`/`-PublishDir` optional (SKIP with reason); `data.*` legs deleted; `hub.host-configs-sweep-clean` reads the hub's own sweep note in `general` over the room API. RED/GREEN executed on a scratch hub; live hub 3 PASS / 4 SKIP / 0 FAIL. Owner 201 leg proven live 2026-09-10 (general msg 40). No deploy: script + docs only. |
| 26 | Probe: does a running Claude Code session read an exported memory directory | ⏸ | OWNER: run `claude auth login` once in a terminal (`claude auth status` says `loggedIn: false`); the agent then re-runs the probe | — | Script `tools/Invoke-Row26MemoryProbe.ps1` merged: fixture, real export, four `claude -p` legs, spend cap 4. 2026-09-16 run: mechanics pass, every leg failed on CLI OAuth (standalone `claude.exe` signed out; desktop-app auth does not carry over). Answer unrecorded; see `docs/verification.md` "Probe: does a session read the export?". |
| 6 | Town view: walkable characters per participant (Octopath-style) | [ ] | DEFERRED: long-term vision, owner ruling 2026-09-04 | — | Renderer and art were deferred with the milestone: working assumptions were Phaser 3 in-page and CC0 placeholder art with simple avatars. Neither is decided — re-open both when this row starts. |
| 7 | ChatGPT chat-tab read-only connector via tunnel | [ ] | DEFERRED: Plus-plan gating unverified; owner ruled tunnels out of v1 | — | help.openai.com 2026-09-04: write actions Business/Enterprise only; Pro read-only; Plus unlisted. |

**Legend:** ✅ Merged · 📝 Plan ready · 🔬 Researching/Planning · [ ] Not started

## Pointers
- BINDING: `docs/superpowers/plans/grill-notes-m5-autonomy.md` (M5/M8/M9/M10 and the rows above amend it)
- Conventions + safety invariants: [CLAUDE.md](CLAUDE.md) · Lessons: [docs/LESSONS.md](docs/LESSONS.md)
- Tracker: [docs/agents/issue-tracker.md](docs/agents/issue-tracker.md) · Declined ideas: `.out-of-scope/` · History: git log
