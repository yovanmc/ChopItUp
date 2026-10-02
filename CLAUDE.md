# Chop It Up — agent/developer contract

State: `ROADMAP.md` (whitelist-v3). Lessons: `docs/LESSONS.md`. Keep this contract under 4 KB.
Map: read [docs/MAP.md](docs/MAP.md) before exploring. Working files go in `.scratch/`, never `docs/`.

## What this is
Single-user local hub: shared chat rooms where the owner, Claude (Claude Desktop) and GPT (Codex UI in the ChatGPT desktop app) talk in one thread. Every model joins through **MCP on its own subscription**. One long-running .NET process owns SQLite, the MCP Streamable HTTP endpoint and the web UI; hosts reach it over loopback (Claude Desktop via `mcp-remote`, Codex UI by URL).

## Safety invariants (override workflow rules on conflict)
- **No API keys, ever.** The app holds no Anthropic or OpenAI credential and makes no model calls itself. A plan that adds one is wrong.
- **No automation of claude.ai / chatgpt.com** (browser driving, session cookies, reverse-engineered endpoints): banned by both consumer ToS.
- **Loopback only.** The hub binds `127.0.0.1`; no tunnel, no LAN bind, without a board row that says why.
- **Never commit** `*.db*`, generated host tokens, `data\`, `.scratch\`, `.claude\`. Room content is private even though the repo is public.
- **No confidentiality gate here.** This is a from-scratch personal app with no employer content, so `confidentiality-review` does not run per push. The "never commit" line above still binds.
- Never `Stop-Process -Name` a GUI app (Claude, ChatGPT); kill only PIDs you launched.
- **No agent writes the deployed hub's data directory** — not via `--import-skill` in any argument form (omitting `--data` defaults there), and its `tokens.json` is never read for a credential; skills reach a deployed hub only through propose-and-approve.

## Git flow
`main` is protected: branch → PR → `gh pr checks --watch` → `gh pr merge --squash --delete-branch` → `git pull`. Commit as the repo-configured identity, plain `git commit`. Commits with substantive Codex-generated changes append `Co-authored-by: Codex <noreply@openai.com>` (folder `AGENTS.md`).

## Commands
```powershell
pwsh -File tools/Invoke-AffectedTests.ps1  # local affected gate; -PlanOnly / -Full
dotnet run --project src/ChopItUp.Hub -- --data .data --print-config      # host configs into .data\host-configs\
dotnet run --project src/ChopItUp.Hub -- --data .data --rotate-token claude
dotnet run --project src/ChopItUp.Desktop -- --data .data --hub src/ChopItUp.Hub/bin/Debug/net10.0/ChopItUp.Hub.exe  # desktop shell, dev
```
`tools/*` is dev only, never referenced by `src/`.

Affected checks are the default. Full fallback/reuse: `docs/affected-tests.md`.

Test gate: `.github/workflows/ci.yml` · selector · ci · 10.4 min [V 2026-09-28 82b51ad1]

## Deploy
Release = two single-file exes, `ChopItUp.Hub.exe` and `ChopItUp.Desktop.exe`, in `C:\Self Apps\ChopItUp\` with `wwwroot\` and `data\` beside them. Deploy with `tools\Deploy-ChopItUp.ps1`, never by hand; verify with `tools\Invoke-M4SelfCheck.ps1 -PublishDir <staging> -TargetDir <target>`. Dev runs from the repo with data under a gitignored `.data\`. Merged-but-not-deployed is not done.
Live checks (real CLIs/models, scratch hub, spend real calls): see `docs/verification.md`. UI proof: `.claude/skills/verify-chopitup/` (gitignored).

## Gate
`ROADMAP.md` is whitelist-v3; gate with `pwsh -NoProfile -File ~\.claude\skills\roadmap\preflight\Check-RoadmapBudget.ps1 -RoadmapPath ROADMAP.md -RequireSchema -RepoRoot .` on every board touch.

## Agent skills
### Issue tracker
GitHub Issues on `yovanmc/ChopItUp` via `gh`. See `docs/agents/issue-tracker.md`.
### Domain docs
Single-context: `CONTEXT.md` + `docs/adr/` at the root, created lazily by `domain-modeling`.
