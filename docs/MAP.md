# ChopItUp map

## Purpose
Where each part of ChopItUp lives, so a task opens the right files first.
Rules live in CLAUDE.md, live checks and runbooks in docs/verification.md.

## Entry points
- `ChopItUp.slnx` - solution: every project
- `src/ChopItUp.Hub/Program.cs` - hub exe: `HubOptions.Parse`, then `HostCommands.Run` or `HubHost.Build`
- `src/ChopItUp.Hub/Hosting/HubHost.cs` - composition root: loopback Kestrel, `/mcp`, SignalR `/hub/rooms`, every `Map*Api`
- `src/ChopItUp.Hub/client/src/main.tsx` - React client entry
- `src/ChopItUp.Desktop/App.xaml.cs` - desktop shell start: `HubChild` starts or attaches the hub, then `MainWindow`
- `tools/Invoke-AffectedTests.ps1` - local test gate; selection in `tools/affected-tests.mjs` and `tools/affected-tests.json`
- `tools/Deploy-ChopItUp.ps1` - release deploy of both exes
- `.github/workflows/ci.yml` - CI: affected selector plus selector tests

## Modules
- `src/ChopItUp.Core/` - domain and SQLite: `Storage/` (`ChopDb` schema and migrations, `*Store`), `Model/`, `Messaging/` (mentions), `Memory/`, `Skills/` (slash commands)
- `src/ChopItUp.Hub/` - ASP.NET Core + `ModelContextProtocol.AspNetCore` + SignalR: `Hosting/` (options, CLI commands, host configs), `Mcp/` (MCP tools), `Web/` (`*Api.cs` endpoints), `Spawning/` (spawner, exchanges, runs, prompts), `Security/` (tokens, peer check), `Memory/`, `Skills/`, `Rooms/`, `Git/`, `Realtime/`
- `src/ChopItUp.Hub/client/` - React + Vite + TS client: `src/api.ts`, `src/types.ts`, one component per file, `src/shell/` desktop bridge
- `src/ChopItUp.Desktop/` - WPF + WebView2 shell: `Hub/` child hub process, `Bridge/` host bridge, tray, single instance
- `tests/ChopItUp.Core.Tests/` - xUnit, one per src project; folders mirror `src/ChopItUp.Core/`
- `tests/ChopItUp.Hub.Tests/` - `*ApiTests.cs` at the root, `HubTestHost.cs`, folders mirror `src/ChopItUp.Hub/`; shared resources in `docs/testing/hub-test-resources.md`
- `tests/ChopItUp.Desktop.Tests/` - desktop shell tests
- `tools/` - dev only, never referenced by `src/`: `Invoke-*Check.ps1` and `Invoke-*DryRun.ps1` checks, `Build-RoomSkill.ps1`, `Measure-TestTimings.ps1`
- `tools/ChopItUp.Corpus/` - builds synthetic corpora for dry runs
- `tools/skills/` - room skills: `roadmap-hub/` overlay and gates, small fixture skills for checks

## Change routes
- To change spawning, exchanges or runs, start in `src/ChopItUp.Hub/Spawning/`, tests in `tests/ChopItUp.Hub.Tests/Spawning/` (prompt goldens there too)
- To change the schema, start in `src/ChopItUp.Core/Storage/ChopDb.cs`, then `tests/ChopItUp.Core.Tests/Storage/SchemaMigrationTests.cs` and `tools/Invoke-M2DryRun.ps1`
- To change a store, start in `src/ChopItUp.Core/Storage/`, tests in `tests/ChopItUp.Core.Tests/Storage/`
- To change an MCP tool, start in `src/ChopItUp.Hub/Mcp/`, then `src/ChopItUp.Core/Storage/` and `tests/ChopItUp.Hub.Tests/`
- To add or change an endpoint, start in `src/ChopItUp.Hub/Web/`, then `src/ChopItUp.Hub/client/src/types.ts` and `api.ts`, tests in `tests/ChopItUp.Hub.Tests/`
- To change a screen, start in `src/ChopItUp.Hub/client/src/`, with its `*.test.tsx` beside it
- To change CLI flags, host configs or tokens, start in `src/ChopItUp.Hub/Hosting/`, then `src/ChopItUp.Hub/Security/`
- To change memory export, import or git, start in `src/ChopItUp.Hub/Memory/`, tests in `tests/ChopItUp.Hub.Tests/Memory/`, runbook in `docs/verification.md`
- To change a skill or the room roadmap overlay, start in `src/ChopItUp.Hub/Skills/` or `tools/skills/roadmap-hub/`, tests in `tests/ChopItUp.Hub.Tests/Skills/`
- To change deploy, start in `tools/Deploy-ChopItUp.ps1`, tests in `tests/ChopItUp.Hub.Tests/DeployScriptTests.cs`

## Skip
- `src/ChopItUp.Hub/wwwroot/` - built client
- `src/ChopItUp.Hub/client/dist/` - Vite output
- `node_modules/` - any depth
- `bin/` - and `obj/`, any depth
- `.data/` - dev hub data, private
- `.scratch/` - working files
- `.claude/` - local agent files

## Docs
- `docs/MAP.md` - this file
- `docs/BUGS.md` - bug and chore queue
- `docs/LESSONS.md` - tagged gotchas
- `docs/affected-tests.md` - test selection, full fallback, reuse
- `docs/test-efficiency.md` - focused checks, timing, coverage
- `docs/verification.md` - live checks with real CLIs, runbooks
- `docs/governing-context.md` - `/objective` and `/correction` behavior
- `docs/testing/hub-test-resources.md` - Hub.Tests shared resources, parallel collections
- `docs/agents/issue-tracker.md` - where tickets live
- `docs/critique-scores.tsv` - design-review score log the hub's roadmap skill appends to
