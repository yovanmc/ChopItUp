# Chop It Up — ROADMAP
<!-- roadmap-schema: whitelist-v3 -->

## Definition
Local Windows hub where Yovan, Claude and GPT talk in one thread over MCP, each model on its own subscription: no API keys, no consumer-UI automation, loopback-only (a Tailscale proxy may front it). Direction set 2026-09-17: spawned models answer in the hub UI, an un-mentioned message gets a relay answer (first model answers, the second replies having read it; primary and panel modes per room), only leading mentions spawn, exchanges continue on demand with a reserved synthesis turn, participants keep a persistent CLI session per room, and the native Claude Code and Codex sessions join rooms with bounded delegation instead of the hub rebuilding an orchestrator. Repo: github.com/yovanmc/ChopItUp (public since 2026-09-04).

## Milestones
| # | Title | Status | Ready | Plan | Notes |
|---|-------|--------|-------|------|-------|
| 47 | Honest feedback | ✅ | DONE | — | DONE 2026-09-19 (#127, main 50d117c; deployed, backup `ChopItUp.backup-20260919-084902`, self-check 29/29). Codex exit notes quote the `turn.failed`/`error` reason from stdout (measured on 0.153.3); `/stop` with no run says so; a non-human leading mention with no open exchange gets a note. MCP required-params clause dissolved (bug 61). Hub 925. Unverified: a real Codex failure through the deployed hub. |
| 48 | Governing context survives the window | [ ] | READY | — | A spawn sees the last 60 messages / 24,000 chars with no author filter (SpawnerService.cs:969; the ChopItUp room is 20/28 hub notes). Pin the objective and the latest correction independent of the tail with an explicit omitted statement; tested at both limits with two 11k reviews, a rebuttal and a correction. |
| 49 | Room modes and on-call answers | [ ] | BACKLOG | — | An un-mentioned message gets a reply: `/mode relay` (default: first answers, second replies having read it), `primary`, `panel` (parallel, blind, then synthesis); mode, participants and per-turn cost visible. |
| 50 | Persistent sessions per participant per room | [ ] | BACKLOG | — | `claude -p --resume` / `codex exec resume` per (participant, room); `/reset @id`; auto-fresh at a threshold; tests for interruption, reconnect, delta sync, worktree changes; usage measured first; never inside a run. |
| 51 | Classes, model and effort in the Roles dialog | [ ] | BACKLOG | — | Classes need `--set-classes` with the hub stopped (HubOptions.cs:114) and are invisible in Roles, while runs dispatch by class (ExchangePolicy.cs:299). |
| 52 | Research tools in plain rooms | [ ] | BACKLOG | — | Web search and fetch for plain-room spawns with honest unavailable-tool messages; files and shell stay directory-room only (SpawnCommands.cs:26-34). |
| 53 | Task usage telemetry | [ ] | BACKLOG | — | Model, effort, duration, tool and retry counts, termination reason, token metrics when reported (else unknown), per task. |
| 54 | Joined hosts with bounded delegation | [ ] | BACKLOG | — | A `claude-code` row and a mention-capable `codex` row for the native sessions, bounded delegation per room, never an owner credential in a worker; README.md:21 rewritten. |
| 55 | Phone access via Tailscale | [ ] | BACKLOG | — | `tailscale serve localhost:8790` plus an allowed-host knob for the Host gate (Hosting/LoopbackHostFilter.cs); then a layout under ~640px (styles.css:1470 is the only breakpoint). Tailscale not installed yet. |
| 56 | Memory import at scale | [ ] | BACKLOG | — | 67 topic files become 67 single approvals (MemoryApi.cs:353-363); approve-all per import; warn when the core would exceed 6,000. |
| 57 | Outside-run spawn timeout | [ ] | BACKLOG | — | 5 min outside a run (SpawnLimits.cs:16) vs 30 inside; raise for directory rooms; elapsed on the working chip. |
| 58 | Live overlay re-import | [ ] | OWNER: with the live hub stopped, run `ChopItUp.Hub.exe --import-skill <roadmap skill dir> --overlay tools\skills\roadmap-hub` (an agent never writes the deployed data dir) | — | The live hub's imported `roadmap` copy predates rows 43 and 44: its re-dispatch template and critique shape put no mention on the phase line, so a conductor following it is refused once per punch list until the re-import (`docs/verification.md:115`), and its overlay still says 4 turns per phase (now 8). Then flip DONE. |
| 59 | Mention reader hardening | [ ] | BACKLOG | — | From the row 43 interrogation: `Find` and the client `reference` regex keep `\w` (Unicode in .NET, ASCII in V8: `é@opus`, `@opusé` diverge); client `SLASH`/`PHASE` are hand copies of `SlashCommands`/`PhaseTag` (lone-CR first line diverges); the strip names inline non-spawnable rows the hub never notes; `@hub` chip wording differs from the hub's sentence. Nothing spawns on any of them. |
| 41 | Memory editor polish | [ ] | BACKLOG | — | From the row 40 look and review: `room-*` topics show the 24,000 cap while a spawn reads 2,000 (`SpawnerService.cs:961`); after a save the count line sits 11 chars above the picker (placeholder id in the preview); the card's top edge shows dimmed header glyphs; hint `Saving files an approved rewrite` reads as a typo; soft wrap unmeasured; the `imported` pill can read as a speaker label. |
| 39 | Roles dialog polish | [ ] | BACKLOG | — | Opus look at the verify-skill capture, 2026-09-17 (PASS): participant-row boxes about 14 px narrower than the persona box; row actions are dim text links beside a filled Save persona button; the list panel clips the second row under its scrollbar; role boxes lack placeholder text; no backdrop scrim; the bottom Close duplicates the X. |
| 6 | Town view: walkable characters per participant (Octopath-style) | [ ] | DEFERRED: long-term vision, owner ruling 2026-09-04 | — | Renderer and art were deferred with the milestone: working assumptions were Phaser 3 in-page and CC0 placeholder art with simple avatars. Neither is decided — re-open both when this row starts. |
| 7 | ChatGPT chat-tab read-only connector via tunnel | [ ] | DEFERRED: Plus-plan gating unverified; owner ruled tunnels out of v1 | — | help.openai.com 2026-09-04: write actions Business/Enterprise only; Pro read-only; Plus unlisted. |

**Legend:** ✅ Merged · 📝 Plan ready · 🔬 Researching/Planning · [ ] Not started

## Pointers
- BINDING: `docs/superpowers/plans/grill-notes-m5-autonomy.md` (M5/M8/M9/M10 and the rows above amend it)
- Conventions + safety invariants: [CLAUDE.md](CLAUDE.md) · Lessons: [docs/LESSONS.md](docs/LESSONS.md)
- Tracker: [docs/agents/issue-tracker.md](docs/agents/issue-tracker.md) · Declined ideas: `.out-of-scope/` · History: git log
