# Chop It Up — ROADMAP
<!-- roadmap-schema: whitelist-v3 -->

## Definition
Local Windows hub where Yovan, Claude and GPT share rooms over MCP on their own subscriptions. No API keys or consumer-UI automation. Loopback-only, with an optional Tailscale proxy. Direction (2026-09-17): replies in the hub, intentional leading mentions, relay/primary/panel room modes, on-demand continuation with synthesis, persistent participant sessions, and bounded delegation from joined native hosts. Public repo: github.com/yovanmc/ChopItUp.

## Milestones
| # | Title | Status | Ready | Plan | Notes |
|---|-------|--------|-------|------|-------|
| 49 | Room modes and on-call answers | 📝 | READY | [plan](docs/superpowers/plans/2026-09-19-m49-room-modes.md) | Full primary/relay/panel delivery in progress: persisted ordered pair, bounded turns, blind advisory panel and revision-bound previews. |
| 50 | Persistent sessions per participant per room | [ ] | BACKLOG | — | `claude -p --resume` / `codex exec resume` per (participant, room); `/reset @id`; auto-fresh at a threshold; tests for interruption, reconnect, delta sync, worktree changes; usage measured first; never inside a run. |
| 52 | Research tools in plain rooms | [ ] | BACKLOG | — | Web search and fetch for plain-room spawns with honest unavailable-tool messages; files and shell stay directory-room only (SpawnCommands.cs:26-34). |
| 53 | Task usage telemetry | [ ] | BACKLOG | — | Model, effort, duration, tool and retry counts, termination reason, token metrics when reported (else unknown), per task. |
| 65 | Keep the spawn result usage on the message row | [ ] | BACKLOG | — | `--output-format json` results carry usage fields (SpawnCommands.cs:31); SpawnOutput.cs:75 keeps only the text. Retain the block per reply for reported-model attribution only, not provider-route proof. Acceptance: one real retained payload on a message row, its link to the spawn, explicit missing-data behavior. Narrower than 53. |
| 54 | Joined hosts with bounded delegation | [ ] | BACKLOG | — | A `claude-code` row and a mention-capable `codex` row for the native sessions, bounded delegation per room, never an owner credential in a worker; README.md:21 rewritten. |
| 55 | Phone access via Tailscale | [ ] | BACKLOG | — | `tailscale serve localhost:8790` plus an allowed-host knob for the Host gate (Hosting/LoopbackHostFilter.cs); then a layout under ~640px (styles.css:1470 is the only breakpoint). Tailscale not installed yet. |
| 56 | Memory import at scale | [ ] | BACKLOG | — | 67 topic files become 67 single approvals (MemoryApi.cs:353-363); approve-all per import; warn when the core would exceed 6,000. |
| 57 | Outside-run spawn timeout | ✅ | DONE | [plan](docs/superpowers/plans/m57-outside-run-timeout.md) | DONE 2026-09-20: dac009b (#135), deployed and actual UI verified. Directory spawns allow 30 min; working chips show elapsed time. |
| 59 | Mention reader hardening | [ ] | BACKLOG | — | Row 43 review: Find/client reference use `\w` (Unicode .NET, ASCII V8; `é@opus`/`@opusé` differ). Client SLASH/PHASE copies diverge on lone CR. Strip names inline non-spawnable rows the hub silently ignores; @hub wording differs. None dispatch. |
| 41 | Memory editor polish | [ ] | BACKLOG | — | Row 40: room topics show 24,000 vs spawn 2,000 (SpawnerService.cs:961); saved count sits 11px above picker; clipped header glyphs; typo "Saving files an approved rewrite"; soft wrap unmeasured; imported pill resembles a speaker label. |
| 39 | Roles dialog polish | [ ] | BACKLOG | — | 2026-09-17 visual PASS with polish: participant boxes ~14px narrow; dim row links; second row clips; missing role placeholders and backdrop scrim; duplicate Close/X. |
| 6 | Town view: walkable characters per participant (Octopath-style) | [ ] | DEFERRED: long-term vision, owner ruling 2026-09-04 | — | Renderer and art were deferred with the milestone: working assumptions were Phaser 3 in-page and CC0 placeholder art with simple avatars. Neither is decided — re-open both when this row starts. |
| 7 | ChatGPT chat-tab read-only connector via tunnel | [ ] | DEFERRED: Plus-plan gating unverified; owner ruled tunnels out of v1 | — | help.openai.com 2026-09-04: write actions Business/Enterprise only; Pro read-only; Plus unlisted. |

**Legend:** ✅ Merged · 📝 Plan ready · 🔬 Researching/Planning · [ ] Not started

## Pointers
- BINDING: `docs/superpowers/plans/grill-notes-m5-autonomy.md` (M5/M8/M9/M10 and the rows above amend it)
- Conventions + safety invariants: [CLAUDE.md](CLAUDE.md) · Lessons: [docs/LESSONS.md](docs/LESSONS.md)
- Tracker: [docs/agents/issue-tracker.md](docs/agents/issue-tracker.md) · Declined ideas: `.out-of-scope/` · History: git log
