# Chop It Up — ROADMAP
<!-- roadmap-schema: whitelist-v3 -->

## Definition
Local Windows hub where Yovan, Claude and GPT share rooms over MCP on their own subscriptions. No API keys or consumer-UI automation. Loopback-only. Direction: persistent participant sessions, bounded delegation from joined native hosts, and phone access through an optional Tailscale proxy. Public repo: github.com/yovanmc/ChopItUp.

## Milestones
| # | Title | Status | Ready | Plan | Notes |
|---|-------|--------|-------|------|-------|
| 84 | Run budgets and job waits | [ ] | BLOCKED: parked - reviewer failed twice (READ below FILES); OWNER: rule on a third review · branch row-84-run-budgets | — | `hours:` 1-8 (default 4), `/continue` revives cap-spent runs, ping passes caps, job wait neither stalls nor counts, `JobFinished`. Caps hard-coded (RunLimits.cs:30) [V 2026-11-17 eadc59c8]. Risk lane. Spec: `.scratch/goal-runs/spec.md` in the main clone. |
| 85 | Background jobs and the GPU lock | [ ] | READY | — | `start_job`/`job_status`/`cancel_job` for in-flight directory spawns, job objects, token stripped, machine-wide GPU semaphore, service cancel. Risk lane. Needs 79, 84. Spec: `.scratch/goal-runs/spec.md` in the main clone. |
| 87 | Usage limits hold and resume | [ ] | READY | — | Keep raw failed-spawn output, detect limits from cited strings, refund, hold, resume after reset. No handling today (SpawnerService.cs:1158) [V 2026-11-17 eadc59c8]. Risk lane. Needs 84. |
| 88 | Windows toast for run events | [ ] | READY | — | Shell listens while hidden; toast on present, park, usage hold; click opens the room. Needs 84. Spec: `.scratch/goal-runs/spec.md` in the main clone. |
| 86 | Goal skill | [ ] | READY | — | `tools/skills/goal/` `run: true` skill: GOAL.md ledger, rounds, two judges, jobs, presentation with images. Owner imports it. Needs 79 to 85. Spec: `.scratch/goal-runs/spec.md` in the main clone. |
| 89 | Goal pilot | [ ] | BLOCKED: rows 79 to 88 | — | Scratch hub, real CLIs, fake GPU service: flow steps 1 to 5 within budget, images inline, one toast. Spec: `.scratch/goal-runs/spec.md` in the main clone. |
| 66 | CI never runs everything on main | [ ] | LEAD: `ci.yml` triggers only on `pull_request` and `workflow_dispatch` | — | The PR run is the affected selector, so no run covers the whole suite after merges. Verify read-only, then add a full run on push to `main` or on a schedule. |
| 50 | Persistent sessions per participant per room | [ ] | BACKLOG | — | `claude -p --resume` / `codex exec resume` per (participant, room); `/reset @id`; auto-fresh at a threshold; tests for interruption, reconnect, delta sync, worktree changes; usage measured first; never inside a run. |
| 52 | Research tools in plain rooms | [ ] | BACKLOG | — | Web search and fetch for plain-room spawns with honest unavailable-tool messages; files and shell stay directory-room only (SpawnCommands.cs:24-32). |
| 53 | Task usage telemetry | [ ] | BACKLOG | — | Model, effort, duration, tool and retry counts, termination reason, token metrics when reported (else unknown), per task. |
| 65 | Keep the spawn result usage on the message row | [ ] | BACKLOG | — | `--output-format json` results carry usage fields (SpawnCommands.cs:29); SpawnOutput.cs:77 keeps only the text. Retain the block per reply for reported-model attribution only, not provider-route proof. Acceptance: one real retained payload on a message row, linked to its spawn, with explicit missing-data behavior. |
| 54 | Joined hosts with bounded delegation | [ ] | BACKLOG | — | A `claude-code` row and a mention-capable `codex` row for the native sessions, bounded delegation per room, never an owner credential in a worker; the README's Claude Code paragraph rewritten. |
| 55 | Phone access via Tailscale | [ ] | BACKLOG | — | `tailscale serve localhost:8790` plus an allowed-host knob for the Host gate (Hosting/LoopbackHostFilter.cs); then a layout under ~640px (styles.css:1554 is the only width breakpoint). Tailscale not installed yet. |
| 56 | Memory import at scale | [ ] | BACKLOG | — | 67 topic files become 67 single approvals (MemoryApi.cs:357-362); approve-all per import; warn when the core would exceed 6,000. |
| 59 | Mention reader hardening | [ ] | BACKLOG | — | Find/client reference use `\w` (Unicode .NET, ASCII V8; `é@opus`/`@opusé` differ). Client SLASH/PHASE copies diverge on lone CR. Strip names inline non-spawnable rows the hub silently ignores; @hub wording differs. None dispatch. |
| 41 | Memory editor polish | [ ] | BACKLOG | — | Room topics show 24,000 vs spawn 2,000 (SpawnerService.cs:978); saved count sits 11px above picker; clipped header glyphs; typo "Saving files an approved rewrite"; soft wrap unmeasured; imported pill resembles a speaker label. |
| 39 | Roles dialog polish | [ ] | BACKLOG | — | Participant boxes ~14px narrow; dim row links; second row clips; missing role placeholders and backdrop scrim; duplicate Close/X. |
| 6 | Town view: walkable characters per participant (Octopath-style) | [ ] | DEFERRED: long-term vision | — | Renderer and art are undecided. Working assumptions: Phaser 3 in-page and CC0 placeholder art with simple avatars. Decide both when this row starts. |
| 7 | ChatGPT chat-tab read-only connector via tunnel | [ ] | DEFERRED: Plus-plan gating unverified, tunnels out of v1 | — | help.openai.com: write actions Business/Enterprise only; Pro read-only; Plus unlisted. |

**Legend:** ✅ Merged · 📝 Plan ready · 🔬 Researching/Planning · [ ] Not started

## Pointers
- Conventions + safety invariants: [CLAUDE.md](CLAUDE.md) · Lessons: [docs/LESSONS.md](docs/LESSONS.md)
- Tracker: [docs/agents/issue-tracker.md](docs/agents/issue-tracker.md) · History: git log
