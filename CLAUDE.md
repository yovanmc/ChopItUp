# Chop It Up — agent/developer contract

State lives in `ROADMAP.md` (whitelist-v3 board). Durable lessons → `docs/LESSONS.md`. Open decisions → `.scratch/decisions/` (gitignored) · declined ideas → `.out-of-scope/`. This file is the how-to-work-here layer; keep it under 4 KB.

## What this is
Single-user local hub: shared chat rooms where the owner, Claude (Claude Desktop) and GPT (Codex UI in the ChatGPT desktop app) talk in one thread. Every model joins through **MCP on its own subscription**. One long-running .NET process owns SQLite, the MCP Streamable HTTP endpoint and the web UI; hosts reach it over loopback (Claude Desktop via `mcp-remote`, Codex UI by URL).

## Safety invariants (override workflow rules on conflict)
- **No API keys, ever.** The app holds no Anthropic or OpenAI credential and makes no model calls itself. A plan that adds one is wrong.
- **No automation of claude.ai / chatgpt.com** (browser driving, session cookies, reverse-engineered endpoints): banned by both consumer ToS.
- **Loopback only.** The hub binds `127.0.0.1`; no tunnel, no LAN bind, without a board row that says why.
- **Never commit** `*.db*`, generated host tokens, `data\`, `.scratch\`, `.claude\`. Room content is private even though the repo will be public.
- **No confidentiality gate here** (owner ruling 2026-09-04): a from-scratch personal app with no employer content, so `confidentiality-review` does not run per push. The "never commit" line above still binds.
- Never `Stop-Process -Name` a GUI app (Claude, ChatGPT); kill only PIDs you launched.

## Git flow
`main` is protected: branch → PR → `gh pr checks --watch` → `gh pr merge --squash --delete-branch` → `git pull`. Commit as the repo-configured identity, plain `git commit`. Commits with substantive Codex-generated changes append `Co-authored-by: Codex <noreply@openai.com>` (folder `AGENTS.md`).

## Layout + commands (created in M1; keep in sync)
```powershell
dotnet build ChopItUp.slnx -c Debug -warnaserror -v minimal   # 0 warnings
dotnet test ChopItUp.slnx -c Debug --nologo -v minimal
```
`src/ChopItUp.Hub` (ASP.NET Core + `ModelContextProtocol.AspNetCore` + SignalR) · `src/ChopItUp.Core` (domain, SQLite) · `tests/*` (xUnit, one per project) · `src/ChopItUp.Hub/client` (React + Vite + TS, M3).

## Deploy
Release = single-file exe in `C:\Self Apps\ChopItUp\` with `data\` beside it (`chopitup.db`, host tokens). Dev runs from the repo with data under a gitignored `.data\`. Merged-but-not-deployed is not done.

## Gate
`ROADMAP.md` is whitelist-v3; gate with `pwsh -NoProfile -File ~\.claude\skills\roadmap\preflight\Check-RoadmapBudget.ps1 -RoadmapPath ROADMAP.md -RequireSchema -RepoRoot .` on every board touch.

## Agent skills
### Issue tracker
GitHub Issues on `yovanmc/ChopItUp` via `gh`. See `docs/agents/issue-tracker.md`.
### Domain docs
Single-context: `CONTEXT.md` + `docs/adr/` at the root, created lazily by `domain-modeling`.
