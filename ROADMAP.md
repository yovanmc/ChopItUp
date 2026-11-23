# Chop It Up — ROADMAP
<!-- roadmap-schema: whitelist-v3 -->

## Definition
Local Windows hub where Yovan, Claude and GPT share rooms over MCP on their own subscriptions. No API keys or consumer-UI automation. Loopback-only. Direction: persistent participant sessions, bounded delegation from joined native hosts, and phone access through an optional Tailscale proxy. Public repo: github.com/yovanmc/ChopItUp.

## Milestones
| # | Title | Status | Ready | Plan | Notes |
|---|-------|--------|-------|------|-------|
| 109 | Room reads from inside a spawn | [ ] | OWNER: choose A document it, B room-scoped spawn tokens plus peer read refusal, C B plus a read fence | — | A spawn reads any room by `GET /api` (`Security/BearerTokenMiddleware.cs:40`), its own bearer via `read_messages` (`Mcp/RoomTools.cs:56`), or `chopitup.db` on disk; no CLI confines reads [V 2026-11-23 c8b347e2]. |
| 54 | Joined hosts with bounded delegation | [ ] | BACKLOG | — | A `claude-code` row and a mention-capable `codex` row for the native sessions, bounded delegation per room, never an owner credential in a worker; the README's Claude Code paragraph rewritten. |
| 55 | Phone access via Tailscale | [ ] | BACKLOG | — | `tailscale serve localhost:8790` plus an allowed-host knob for the Host gate (Hosting/LoopbackHostFilter.cs); then a layout under ~640px (styles.css:1554 is the only width breakpoint). Tailscale not installed yet. |
| 56 | Memory import at scale | [ ] | BACKLOG | — | 67 topic files become 67 single approvals (MemoryApi.cs:357-362); approve-all per import; warn when the core would exceed 6,000. |
| 59 | Mention reader hardening | [ ] | BACKLOG | — | Find/client reference use `\w` (Unicode .NET, ASCII V8; `é@opus`/`@opusé` differ). Client SLASH/PHASE copies diverge on lone CR. Strip names inline non-spawnable rows the hub silently ignores; @hub wording differs. None dispatch. |
| 41 | Memory editor polish | [ ] | BACKLOG | — | Room topics show 24,000 vs spawn 2,000 (SpawnerService.cs:978); saved count sits 11px above picker; clipped header glyphs; typo "Saving files an approved rewrite"; soft wrap unmeasured; imported pill resembles a speaker label. |
| 39 | Roles dialog polish | [ ] | BACKLOG | — | Participant boxes ~14px narrow; dim row links; second row clips; missing role placeholders and backdrop scrim; duplicate Close/X. |
| 6 | Town view: walkable characters per participant (Octopath-style) | [ ] | DEFERRED: long-term vision | — | Renderer and art are undecided. Working assumptions: Phaser 3 in-page and CC0 placeholder art with simple avatars. Decide both when this row starts. |
| 7 | ChatGPT chat-tab read-only connector via tunnel | [ ] | DEFERRED: Plus-plan gating unverified, tunnels out of v1 | — | help.openai.com: write actions Business/Enterprise only; Pro read-only; Plus unlisted. |
| 50 | Persistent sessions per participant per room | [ ] | DEFERRED: owner parked it, a resumed turn saves no usage | — | Measurements in `.scratch/row50-measurements.md` (local): a resumed turn costs about what a fresh hub prompt costs; Codex resume drops `--approve-for-me` (shell blocked) and reports running-total usage; session files keep room text in `~/.claude/projects` and `~/.codex/sessions` [V 2026-11-23 77554de2]. Scope: `--resume` per (participant, room), `/reset @id`, auto-fresh, never inside a run. |

**Legend:** ✅ Merged · 📝 Plan ready · 🔬 Researching/Planning · [ ] Not started

## Pointers
- Conventions + safety invariants: [CLAUDE.md](CLAUDE.md) · Lessons: [docs/LESSONS.md](docs/LESSONS.md)
- Tracker: [docs/agents/issue-tracker.md](docs/agents/issue-tracker.md) · History: git log
