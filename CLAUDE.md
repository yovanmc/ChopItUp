# Chop It Up agent contract

State: `ROADMAP.md`. Lessons: `docs/LESSONS.md`. Exploring code: `docs/MAP.md` says where each part lives. Working files go in `.scratch/`. `docs/` holds only maintained docs.

## What this is
Single-user local hub: shared chat rooms where Yovan, Claude (Claude Desktop) and GPT (Codex UI in the ChatGPT desktop app) talk in one thread. Every model joins through **MCP on its own subscription**. One long-running .NET process owns SQLite, the MCP Streamable HTTP endpoint and the web UI. Hosts reach it over loopback (Claude Desktop via `mcp-remote`, Codex UI by URL).

## Safety invariants (override workflow rules on conflict)
- **No API keys, ever.** The app holds no Anthropic or OpenAI credential and makes no model calls itself. A plan that adds one is wrong.
- **No automation of claude.ai / chatgpt.com** (browser driving, session cookies, reverse-engineered endpoints): banned by both consumer ToS.
- **Loopback only.** The hub binds `127.0.0.1`. A tunnel or LAN bind needs a board row that says why.
- **Never commit** `*.db*`, generated host tokens, `data\`, `.scratch\`, `.claude\`. Room content is private even though the repo is public.
- **No agent writes the deployed hub's data directory**, including through `--import-skill` in any argument form (omitting `--data` defaults there). Its `tokens.json` is never read for a credential. Skills reach a deployed hub only through propose-and-approve.

## Git
`main` is protected. PRs land with `gh pr merge --squash --delete-branch`. Commits with substantive Codex-generated changes append `Co-authored-by: Codex <noreply@openai.com>` (`C:\Agent Projects\AGENTS.md`).

## Commands
```powershell
pwsh -File tools/Invoke-AffectedTests.ps1  # local affected gate; -PlanOnly / -Full
dotnet run --project src/ChopItUp.Hub -- --data .data --print-config      # host configs into .data\host-configs\
dotnet run --project src/ChopItUp.Hub -- --data .data --rotate-token claude
dotnet run --project src/ChopItUp.Desktop -- --data .data --hub src/ChopItUp.Hub/bin/Debug/net10.0/ChopItUp.Hub.exe  # desktop shell, dev
```
`tools/*` is dev only, never referenced by `src/`.

Affected checks are the default. Full fallback and reuse: `docs/affected-tests.md`.

Test gate: `.github/workflows/ci.yml` · selector · ci · 10.4 min [V 2026-09-28 82b51ad1]

## Deploy
Release = two single-file exes, `ChopItUp.Hub.exe` and `ChopItUp.Desktop.exe`, in `C:\Self Apps\ChopItUp\` with `wwwroot\` and `data\` beside them. Deploy with `tools\Deploy-ChopItUp.ps1`, never by hand. Verify with `tools\Invoke-M4SelfCheck.ps1 -PublishDir <staging> -TargetDir <target>`. Dev runs from the repo with data under a gitignored `.data\`. A change is done once deployed, not when merged.
Live checks (real CLIs and models, scratch hub, real spend): `docs/verification.md`. UI proof: `.claude/skills/verify-chopitup/` (gitignored).

## Agent skills
### Issue tracker
GitHub Issues on `yovanmc/ChopItUp` via `gh`. See `docs/agents/issue-tracker.md`.
### Domain docs
Single-context: `CONTEXT.md` + `docs/adr/` at the root, created lazily by `domain-modeling`.
