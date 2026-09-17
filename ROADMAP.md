# Chop It Up — ROADMAP
<!-- roadmap-schema: whitelist-v3 -->

## Definition
Local Windows hub where Yovan, Claude (Claude Desktop) and GPT (Codex UI inside the ChatGPT desktop app) chat in shared rooms over MCP, each on its own subscription — no API keys, no consumer-UI automation, loopback-only. Repo: github.com/yovanmc/ChopItUp (public since 2026-09-04).

## Milestones
| # | Title | Status | Ready | Plan | Notes |
|---|-------|--------|-------|------|-------|
| 40 | Memory editing page | ✅ | DONE | — | DONE 2026-09-17 (#110, main 5f858d3; deployed, live hub schema 12, backup `ChopItUp.backup-20260917-170635`). `Edit memory` dialog; `GET/PUT /api/memory/topics/{slug}` + `preview`; a save is an approved rewrite (`.bak`, commit, note) via the shared `ApproveCore`. .NET 1,219, vitest 188, `tools/Invoke-Row40MemoryEditCheck.ps1` 17/17, capture 25/25 (two saves), opus look PASS. Unverified: soft wrap. |
| 41 | Memory editor polish | [ ] | BACKLOG | — | From the row 40 look and review: `room-*` topics show the 24,000 cap while a spawn reads 2,000 (`SpawnerService.cs:961`); after a save the count line sits 11 chars above the picker (placeholder proposal id in the preview); the card's top edge shows dimmed header glyphs; hint `Saving files an approved rewrite` scans as a typo; soft wrap unmeasured. |
| 38 | Codex spawn failure note hides the stdout error | [ ] | BACKLOG | — | [V 2026-09-17 10d7875] `SpawnerService.cs:1136-1140` appends stderr only; under `--json` Codex writes its `error`/`turn.failed` events to stdout, so a rejected model reads as "exited with code 1 without replying". `Invoke-M20RoadmapCheck.ps1 -Worker` and `Probe-SpawnCli.ps1` still default to gpt-5.4-mini. |
| 39 | Roles dialog polish | [ ] | BACKLOG | — | Opus look at the verify-skill capture, 2026-09-17 (PASS): participant-row boxes about 14 px narrower than the persona box; row actions are dim text links beside a filled Save persona button; the list panel clips the second row under its scrollbar; role boxes lack placeholder text; no backdrop scrim; the bottom Close duplicates the X. |
| 6 | Town view: walkable characters per participant (Octopath-style) | [ ] | DEFERRED: long-term vision, owner ruling 2026-09-04 | — | Renderer and art were deferred with the milestone: working assumptions were Phaser 3 in-page and CC0 placeholder art with simple avatars. Neither is decided — re-open both when this row starts. |
| 7 | ChatGPT chat-tab read-only connector via tunnel | [ ] | DEFERRED: Plus-plan gating unverified; owner ruled tunnels out of v1 | — | help.openai.com 2026-09-04: write actions Business/Enterprise only; Pro read-only; Plus unlisted. |

**Legend:** ✅ Merged · 📝 Plan ready · 🔬 Researching/Planning · [ ] Not started

## Pointers
- BINDING: `docs/superpowers/plans/grill-notes-m5-autonomy.md` (M5/M8/M9/M10 and the rows above amend it)
- Conventions + safety invariants: [CLAUDE.md](CLAUDE.md) · Lessons: [docs/LESSONS.md](docs/LESSONS.md)
- Tracker: [docs/agents/issue-tracker.md](docs/agents/issue-tracker.md) · Declined ideas: `.out-of-scope/` · History: git log
