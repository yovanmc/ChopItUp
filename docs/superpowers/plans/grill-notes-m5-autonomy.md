# Grill notes — autonomy, memory, rooms (definition ledger)

Interview completed 2026-09-05 (six rounds, frontier empty, owner confirmed). BINDING for rows M5, M8, M9, M10, M11: a plan for any of them is checked against this file, and a plan that contradicts a decision below is wrong until the owner re-rules. Facts are labelled by how they were established; decisions carry no label because they are the owner's.

Paired delete: this file is deleted in the commit that flips the LAST of M5/M8/M9/M10/M11 to DONE.

## What the app is

An owner-led multi-agent work session, not a chat app. The owner leads each session with a skill or a plan, as in Claude Code, but with several models in the room at once. The value is disagreement and building — one proposes, another attacks or extends — on resume review, design critique, code. The biggest goal beyond that: one centralised memory both vendors' agents use effortlessly, so the owner never re-explains himself.

## Decisions

| # | Area | Decision |
|---|------|----------|
| D1 | Purpose | Adversarial pair AND shared workspace; sometimes one leads and the other builds. Balance or opposing opinions, e.g. resume building/review. |
| D2 | Conductor | Every exchange is rooted in an owner message. Models never open threads. Roles per participant (builder, designer) are v1.1. |
| D3 | Rooms | A room is a conversation: per-task, owner-created and titled, many over time, listed by recency, kept forever, never deleted from disk (archive hides). UI becomes a chat list. |
| D4 | Symmetry | Participants are symmetric peers in v1. |
| D5 | Close | An exchange ends with a conclusion on the original ask. Budget **4 model turns**. The last-turn model summarises AND asks "continue?". An owner message mid-exchange lets the running spawn finish, then closes the exchange; the owner's message starts a fresh one. Whoever holds the last turn closes. |
| D6 | Mode | Designed for walk-away and periodic check-ins; live sessions (resume building) are a mode of the same mechanics: owner types in the room, a spawn replies. Interactive hosts (Claude Desktop, Codex app) are optional windows, not the reply path. |
| D7 | Cost | Posture (a): fires only on explicit mention; caps are hard code, not config. 10 s minimum spacing per participant; 5 min wall-clock timeout per spawn; one in flight per (participant, room); parallel across rooms. |
| D8 | Trigger | A participant spawns only when a message mentions it. Mention is the trigger and the only trigger. Mentions control spawning, never the context window. Debounce per burst. Never spawn on your own message. |
| D9 | Context | Spawns are fully stateless (`--no-session-persistence` / `--ephemeral`). The room is the conversation; the hub renders it into the prompt with the trigger ids, the remaining budget and the standing rules. No session ids, no resume. |
| D10 | Files | Both agents get read, edit AND shell inside the room's git tree, effectively unrestrained there, with network ON. No reads outside the room. Everything an agent needs is in memory or the room directory. |
| D11 | Git | The room directory is a git repo (hub-created if needed). Git is read-only for agents. The hub commits after every spawn with the agent as author and every shell command it ran in the message; the owner's own edits are committed before each spawn. No push by anyone in v1. |
| D12 | Roots | A room binds to an existing or hub-created directory. The hub refuses drive roots, `%USERPROFILE%`, anything under `C:\Self Apps\`. |
| D13 | Confinement | Asymmetric in v1, stated: Codex is OS-confined by its workspace-write sandbox; Claude Code has no sandbox on native Windows (F5) and is confined by its own permission logic plus the trail. A restricted Windows account for symmetry is its own later row. |
| D14 | Roster | Participants are named for **models**, not vendors: a row with host, model flag, display name, token. `TokenStore.Participants` stops being a constant. Roster as large as wanted; cost scales with participants addressed, not available. App-backed rows (`claude-desktop`, `codex-app`) stay as optional windows. Codex half discoverable at runtime (F7). `fable` flagged "may bill usage credits" (F8). |
| D15 | Memory | Centralised markdown store in `data\memory\` (index + topic files, git-backed). Always-on core ≈ 1,500 tokens injected into every spawn; the rest via a `recall(topic)` MCP tool, which is also how interactive hosts get it. Agents PROPOSE, the owner APPROVES, in the room, on a special message. Seeded by importing both vendors' existing memory as proposals. No agent writes memory directly. |
| D16 | Skills | Hub-owned, host-neutral copies in `data\skills\`: grilling, codebase-design, roadmap. Invoked by slash in an owner message, expanded into that exchange, run on whoever the message addresses (so two models' output to one skill sit side by side). |
| D17 | Live UI | v1: per-participant working indicator, a stop button (= "step in and end it"), remaining budget visible, unread counts, a concluded marker. No mobile push (F6). |
| D18 | Shell | Desktop WebView2 shell is its own row after the autonomy rows. |
| D19 | Acceptance | Resume in a room directory → `/grill @opus @gpt-6-astra` → close the laptop → return to a concluded exchange with a summary, resume edits committed per model, one memory proposal awaiting accept, both models' answers to one skill side by side. |

## Facts (verified this session unless labelled)

| # | Fact | How |
|---|------|-----|
| F1 | `claude -p` and `codex exec` each post to the live hub headless on subscription auth, no API key, including the fully isolated form (`--ignore-user-config`, config via `-c`, token via env var). | Messages 8 and 9 in `general`, hub-stamped. |
| F2 | `claude -p --tools ""` drops every built-in and keeps MCP tools; with the full built-in set, MCP tools defer behind ToolSearch. | Three-run control. |
| F3 | `codex exec` runs `approval: never`, which denies MCP tool calls; `--approve-for-me` is the only route; `-c approval_policy=...` is silently ignored; `--approve-for-me` is mutually exclusive with `--sandbox`. | Runs E1/E2/F/G. |
| F4 | `npx` and `codex` exist only as `.cmd` on this machine; bare-name CreateProcess finds nothing, silently. One resolver in the hub. | On disk; LESSONS. |
| F5 | Claude Code's sandbox runs on macOS/Linux/WSL2 only — "Native Windows is not supported." `dontAsk` auto-denies rather than prompting. | code.claude.com/docs/en/sandboxing, /permissions (DOCUMENTED). |
| F6 | No documented mechanism for headless `claude -p` or `codex exec` to notify a phone. | Docs + `--help` (UNKNOWN beyond that). |
| F7 | Codex account roster: `gpt-6-astra` (default), `gpt-5.6-sol`, `gpt-5.6-terra`, `gpt-5.6-luna`, `gpt-5.5`, `gpt-5.4-mini`. Enumerable at runtime via `codex app-server --stdio` + JSON-RPC `model/list`. | Codex, message 11 (VERIFIED by it). |
| F8 | Claude aliases for `--model`: `fable`, `opus`, `sonnet`, `haiku`, `opus[1m]`, `sonnet[1m]`, `opusplan`, `best`, `default`; Fable "can bill to usage credits instead of drawing on your plan's included limits." | code.claude.com/docs/en/model-config (DOCUMENTED). |
| F9 | Codex has persistent memory at `~/.codex/memories/` (`MEMORY.md`, `memory_summary.md`, `raw_memories.md`, own `.git`), documented as generated state. It reads `AGENTS.md` walking root→cwd, plus a global `~/.codex/AGENTS.md`; `-C` sets cwd. Whether `--ignore-user-config` suppresses the global `AGENTS.md` is UNKNOWN. | Names only; learn.chatgpt.com docs (DOCUMENTED). |
| F10 | Claude injection: `--append-system-prompt <string>` (verified), or `CLAUDE.md` via `--add-dir`. The `-file` variant is mentioned only in `--bare`'s help text; `--version` short-circuits flag validation, so acceptance of a flag proves nothing. | `claude --help`. |
| F11 | Subscription auth survives a spawn from a detached console-less parent in session 1. Session-0 service is untested. | Detached pwsh probe. |

## Explicitly v1.1 or later

Roles per participant · desktop WebView2 shell (D18) · restricted-account confinement for Claude (D13) · hub push · reads outside the room · mobile notifications (revisit when a vendor ships a path) · a memory editing page · Claude Desktop as a live participant.

## Milestone split (rows on the board)

M8 roster as data → M5 spawner core → M10 centralised memory → M9 rooms as chats + directories + trail → M11 hub-owned skills. Later rows: shell, confinement, roles. Each plan pulls this file by its D/F ids.
