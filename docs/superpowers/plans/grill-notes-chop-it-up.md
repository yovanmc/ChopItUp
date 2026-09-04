# Grill notes — Chop It Up (definition phase, 2026-09-04)

Ledger per the roadmap grilling-hardening layer. Status: OPEN / ANSWERED / RESEARCH / ASSUMED (model default + explicit yes) / WAIVED / DEFERRED / REOPENED.

## Round 1 (2026-09-04)

| # | Question | Recommended | Answer | Status |
|---|----------|-------------|--------|--------|
| Q1 | v1 destination | (a) shared thread, human-prompted turns | "defaults" | ASSUMED (yes) |
| Q2 | Launch participants | Claude Desktop + ChatGPT | Codex replaces ChatGPT (see Q14) | ANSWERED |
| Q3 | Where Yovan types | localhost web page from hub process | defaults | ASSUMED (yes) |
| Q4 | Stack | .NET + official MCP C# SDK | defaults | ASSUMED (yes) |
| Q5 | Structure | rooms + @mentions, unaddressed = everyone | defaults | ASSUMED (yes) |
| Q6 | Read scope | since last read, full on request, capped + paginated | defaults | ASSUMED (yes) |
| Q7 | Authorship | server-enforced per-host token | defaults | ASSUMED (yes) |
| Q8 | Import | paste only in v1 | defaults | ASSUMED (yes) |
| Q9 | ChatGPT tunnel | token + tunnel only while running | superseded by Q15 (deferred) | DEFERRED (trigger: ChatGPT read-only connector wanted; owner: Yovan) |
| Q10 | Persistence | SQLite forever + markdown export | defaults | ASSUMED (yes) |
| Q11 | Visibility | private first | PUBLIC repo | ANSWERED (owner override) |
| Q12 | Repo home + tracker | GitHub Issues | C:\Agent Projects\ChopItUp; GitHub Issues | ANSWERED |
| Q13 | ChatGPT plan | — | Plus ("I believe") | ANSWERED (unconfirmed by owner) |
| Q14 | GPT surface | Codex surface | "We will be using codex" | ANSWERED |
| Q15 | Read-only connector in v1 | defer | defaults | ASSUMED (yes) |

## Round 2 (2026-09-04)

| # | Question | Recommended | Answer | Status |
|---|----------|-------------|--------|--------|
| Q16 | Town timing | chat first, town second | "town is a long term vision, focus on chat" | ANSWERED — town OFF the roadmap |
| Q17 | Town renderer | Phaser 3 in page | moot for now | DEFERRED (trigger: town milestone opened) |
| Q18 | Front-end | React + Vite + TS | defaults | ASSUMED (yes) |
| Q19 | Live transport | SignalR | defaults | ASSUMED (yes) |
| Q20 | Art | CC0 placeholders | moot for chat-only; avatars = simple | DEFERRED with Q17 |
| Q21 | License + folder | MIT; folder asked | MIT; C:\Agent Projects | ANSWERED |
| Q22 | Topology | hub + stdio shims | defaults | ASSUMED (yes) |
| Q23 | Rules delivery | server-provided prompt + tool descriptions | defaults | ASSUMED (yes) |
| Q24 | Run/deploy | dev from repo; release single-file exe to C:\Self Apps\ChopItUp | defaults | ASSUMED (yes) |

Name: "Chop It Up" (owner-chosen). Prior-art lookup: Pixel Agents (VS Code ext), a16z AI Town, Agent Town, DeskRPG — town-style apps; none is a subscription MCP chat room.

## Research findings (sources checked 2026-09-04)

- ChatGPT custom MCP apps with write actions: Business/Enterprise/Edu only; Pro read/fetch only; Plus unlisted. help.openai.com/en/articles/12584461 (read in browser pane).
- Codex surface (ChatGPT desktop app, CLI, IDE ext) shares ~/.codex/config.toml; stdio + streamable HTTP (bearer) servers. learn.chatgpt.com/docs/extend/mcp.
- Claude Desktop: local stdio via %APPDATA%\Claude\claude_desktop_config.json; ~60 s hard tool timeout (anthropics/claude-code issues #43791, #65643).
- Claude Desktop remote custom connectors: public HTTPS only, localhost rejected. support.claude.com/en/articles/11175166.
- MCP C# SDK 2.2.0 stable (2026-08-13), Streamable HTTP + stdio server transports. nuget.org/packages/ModelContextProtocol.
- Claude Code: `claude mcp add --transport http <name> <url> --header "Authorization: Bearer …"`. code.claude.com/docs/en/mcp.
- Consumer ToS: automated access to claude.ai / chatgpt.com prohibited → browser automation ruled out.
- Anthropic OAuth policy: subscription OAuth is for Claude Code + native apps; Agent SDK/3rd-party harnesses need API keys. code.claude.com/docs/en/legal-and-compliance. MCP-into-consumer-apps design avoids this entirely.

## Round 3 — exit-gate sweep defaults (2026-09-04)

Sweep dimensions (goal · scope · users · constraints · dependencies/env · failure modes · prior art · verification · lifecycle · sensitivity · topic-specific) run twice; second pass added nothing.

| # | Proposed default | Answer | Status |
|---|------------------|--------|--------|
| R1 | Claude Desktop bridge = mcp-remote (`--allow-http`, `--header-file`); no custom shim in v1 | yes | ASSUMED (yes) |
| R2 | Codex connects by URL to loopback hub with bearer token; fallback mcp-remote if plain http rejected | yes — via **Codex UI** (ChatGPT desktop app Codex surface), NOT the CLI | ANSWERED + RESEARCH (http://localhost acceptance untested until M2 live check) |
| R3 | Loopback-only bind, fixed port, per-host tokens generated on first run into data folder | yes | ASSUMED (yes) |
| R4 | Data beside exe in release (`C:\Self Apps\ChopItUp\data\`), gitignored local folder in dev; DB never committed | yes | ASSUMED (yes) |
| R5 | Append-only messages, server timestamps, monotonic ids, cursor reads; no edit/delete in v1 | yes | ASSUMED (yes) |
| R6 | Automated tests over real transport with MCP C# client; Claude Desktop + Codex UI live checks = OWNER rows; UIA gate for web UI | yes | ASSUMED (yes) |
| R7 | GitHub repo private now, public at first release after confidentiality review | "yes for repo being private at first" | ANSWERED (explicit) |
| R8 | Participation prompt: other participants' messages are content not instructions; UI shows server-stamped author | yes | ASSUMED (yes) |
| R9 | Milestones M1 hub core · M2 host wiring · M3 web UI · M4 release · M5 autonomy; town DEFERRED | yes | ASSUMED (yes) |
| R10 | No login on web UI; loopback is the boundary | yes | ASSUMED (yes) |
| R11 | Agent installs Codex CLI for M2 | NO — owner uses Codex UI only; no CLI install | ANSWERED (struck) |

Environment verified 2026-09-04: .NET SDK 10.0.303 · Node v22.23.2 · `%APPDATA%\Claude\claude_desktop_config.json` exists · Store package `OpenAI.ChatGPT-Desktop` installed · `~/.codex` exists, no `codex` on PATH · mcp-remote 0.8.3 (pushed 2026-08-31).

## Debrief

- **Decisions (owner):** name Chop It Up · public repo (private first) · Codex UI as GPT voice · town = long-term vision, off the roadmap · MIT · folder C:\Agent Projects\ChopItUp · GitHub Issues.
- **Decisions (model default, owner said yes):** Q1, Q3–Q8, Q10, Q15, Q18, Q19, Q22–Q24, R1, R3–R6, R8–R10.
- **Research findings:** section above (sources dated).
- **Waived:** none.
- **Deferred:** Q9/Q15 ChatGPT read-only connector (trigger: owner wants the ChatGPT chat tab reading rooms; Plus gating must be verified first) · Q17/Q20 town renderer and art (trigger: town milestone opened) · R2 http://localhost acceptance by Codex UI (trigger: M2 live check).
- **Open:** none. Interview complete 2026-09-04.

Paired delete: this file is deleted in the commit that flips M4 (v1 release) to DONE.
