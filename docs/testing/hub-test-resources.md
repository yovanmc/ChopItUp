# Hub.Tests shared resources

Inventory of every resource in `tests/ChopItUp.Hub.Tests` that outlives a single test or is visible outside it, first measured at 084e9f4 and revised when the assembly moved to parallel collections. Hub.Tests now runs test collections in parallel, at most four at once (`[assembly: CollectionBehavior(MaxParallelThreads = 4)]` in `AssemblyInfo.cs`). Classes that touch process or machine state sit in `ProcessStateCollection`, declared with `DisableParallelization = true`, which xUnit runs alone after every parallel collection has finished. `ParallelismPolicyTests` fails when a test class calls a process-state or machine-table API outside that collection. `ChopItUp.Core.Tests` and `ChopItUp.Desktop.Tests` still disable parallelization. There is no `xunit.runner.json`.

The compiled-in cap is a default: `-- xUnit.MaxParallelThreads=<n>` or `-- xUnit.ParallelizeTestCollections=false` on the `dotnet test` line overrides it for one run, and so does a runsettings file. CI passes neither. The xUnit start banner shows the runner's setting, not the attribute, so it is not evidence of the cap: the per-test start and end times in the TRX are.

Set `CHOPITUP_TEST_ORDER_SEED` to an integer to shuffle collection and test-case order (`SeededOrder.cs`). Unset, xUnit's default order applies. Under parallel execution only the start order follows the seed.

## Method

A grep started the audit: per file, the first hit of `HubTestHost.StartAsync`, `ClearAllPools`, `SetEnvironmentVariable`, `Console.Set*`, `Global\`, process launches (`ProcessStartInfo(`, `Process.Start(`, `new ProcessRunner(`), temp-directory helpers and waits of 10 s or more. The grep is only the index. The evidence is the reading: every line cited below was opened, and grep hits that turned out to be `using` lines, comments or string literals were discarded.

## Resources

| # | Resource | Scope | Where | Classes |
|---|----------|-------|-------|---------|
| R1 | SQLite connection pools, cleared by `SqliteConnection.ClearAllPools()`. Inert: every connection in `src/` and these tests sets `Pooling=false` (`ChopDb.cs:65`, `:159`, `HostCommands.cs:48`), so no pool exists to clear and R1 serializes nothing | process | `HubTestHost.cs:122` (every host dispose), `DryRunTests.cs:44`, `HostCommandsTests.cs:37`, `:83`, `:487`, `Skills/ConsolidateMemorySkillTests.cs:43`, `Skills/SkillImportTests.cs:32`, `Spawning/SpawnerServiceTests.cs:840` | every host user (R4) plus DryRunTests, HostCommandsTests, ConsolidateMemorySkillTests, SkillImportTests, SpawnerTimingTests |
| R2 | Environment variables | process | `HubHostTests.cs:390`, `:397` (`CHOPITUP_SHELL_TOKEN`), `Spawning/PanelProcessTests.cs:28-29`, `:41-42` (synthetic sentinel names). Production code writes it too: `HubHost.Build` clears `CHOPITUP_SHELL_TOKEN` on every host build (`src/ChopItUp.Hub/Hosting/HubHost.cs:72`), so every R4 class writes R2; that is safe only while HubHostTests stays in the non-parallel collection | HubHostTests, PanelProcessTests (writers); every R4 class through `HubHost.Build` |
| R3 | `Console.Error` redirected by `Console.SetError` | process | `HubHostTests.cs:227`, `:271`, `:322`, `:337`, `Skills/SkillStoreTests.cs:193`, `Spawning/SpawnerShutdownConsoleTests.cs:45` | HubHostTests, SkillStoreTests, SpawnerShutdownConsoleTests |
| R4 | In-process hub (`HubTestHost`): Kestrel on 127.0.0.1, port 0 unless given, data under the caller's directory | per test | `HubTestHost.cs:58` | see the class table |
| R5 | Fixed loopback port: reserved with a port-0 `TcpListener`, released, then bound by the host | machine | `Security/OwnerPeerCheckEndToEndTests.cs:36-41` | OwnerPeerCheckEndToEndTests |
| R6 | Machine TCP table and process table (WMI `Win32_Process`) read and matched by pid | machine | `Security/PeerProcessTests.cs:44`, `Spawning/ProcessRunnerTests.cs:58`, `Spawning/SpawnJobsTests.cs:41`, `Security/OwnerPeerCheckEndToEndTests.cs:118` | PeerProcessTests, ProcessRunnerTests, SpawnJobsTests, OwnerPeerCheckEndToEndTests |
| R7 | Named kernel mutex `Global\ChopItUp.Skills.<path>` | machine, name unique per test directory | `Skills/SkillImportTests.cs:784`, `:791` | SkillImportTests |
| R8 | Real child processes (git, pwsh, cmd, ping, mklink) confined to the test's directory | machine process list, per-test files | `DeployScriptTests.cs:163`, `GateScriptTests.cs:205`, `Git/GitTrailTests.cs:23`, `Memory/MemoryGitTests.cs:34`, `MemoryEditApiTests.cs:70`, `RoomsApiTests.cs:289`, `Rooms/ExchangeWorktreesTests.cs:55`, `Rooms/RoomPathsTests.cs:89`, `Skills/SkillImportTests.cs:78`, `Spawning/PanelInputsTests.cs:109`, `Spawning/PanelProcessTests.cs:15`, `Spawning/SpawnerServiceTestBase.cs:160`, `Security/OwnerPeerCheckTests.cs:27`, plus the R6 classes | see the class table |
| R9 | Fixture timing log `fixture-<pid>.jsonl`, written only when `TEST_FIXTURE_TIMINGS` is set, under a lock | process (one file per test process) | `FixtureTiming.cs:12`, `:31` | every host user (R4) |
| R10 | Ephemeral ports and TIME_WAIT entries: every host opens a Kestrel listener plus HTTP, MCP and SignalR clients | machine | `HubTestHost.cs:66` | every host user (R4). A solution-level `dotnet test`, which runs the three assemblies at once, has failed a host bind with WSAENOBUFS; the runner runs one suite at a time |
| R11 | Process-wide statics in production code: `MemoryApi.Decisions` and `SkillsApi.Decisions` (`SemaphoreSlim(1, 1)`), `MemoryApi.RefusalNoted` keyed by data root and id, `SkillsApi.TreeCacheByDir` keyed by full path and cleared past 200 entries | process | `src/ChopItUp.Hub/Web/MemoryApi.cs:23`, `:30`, `src/ChopItUp.Hub/Web/SkillsApi.cs:43`, `:56` | MemoryApiTests, MemoryApiGuardTests, MemoryEditApiTests, SkillsApiTests, SkillsApiAuthTests, SkillsApiProposalsTests. Concurrent hosts queue on the two semaphores (a wait, not a correctness hazard); the keyed caches are per directory |

Everything else a test writes goes under its own unique temp directory, deleted by `TestDirs.DeleteTree` (`TestDirs.cs:8`). `DeployScriptTests` names the real deploy directory (`DeployScriptTests.cs:501`) and documents at `:761` that the test keeps the script's backup path out of it.

## Units

- **Execution unit.** `parallel`: the class is its own collection and may run beside up to three others. `process-state`: the class touches process state (R2, R3) or reads machine-wide tables (R5, R6), so it sits in `ProcessStateCollection` and runs only when nothing else in the process is running. A second test process on the same machine can still disturb the R5 and R6 classes; the runner never starts one.
- **Retry unit.** For every class it is the single test method, filtered by `FullyQualifiedName` in a fresh `dotnet test` of this project. Class-level `IAsyncLifetime` in xUnit runs once per test instance, and the two class fixtures (`DeployScriptFixture`, `GateScriptFixture`) are rebuilt by a filtered run, so no method depends on a sibling having run first. Repo policy allows one hypothesis-driven rerun, not repetition until green.
- **Receipt unit.** The reusable receipt is the whole `ChopItUp.Hub.Tests` run on one tree, which is the unit the affected-test selector already schedules (`docs/affected-tests.md`). No smaller unit can be reused, because R2, R3, R10 and R11 are shared by the whole process: a pass by a subset does not show how those tests behave next to the rest of the assembly.

## Classes

The SpawnerService tests were one partial class of 126 cases and 205 s, which xUnit cannot split across threads. They are now one sealed class per area over `SpawnerServiceTestBase`, which holds the hub, the fake runner and the helpers more than one area uses.

| Class | File | Resources | Execution unit |
|-------|------|-----------|----------------|
| BearerTokenMiddlewareTests | BearerTokenMiddlewareTests.cs | none | parallel |
| ChatApiTests | ChatApiTests.cs | R4 | parallel |
| CliResolverTests | Spawning/CliResolverTests.cs | none | parallel |
| ConsolidateMemorySkillTests | Skills/ConsolidateMemorySkillTests.cs | none | parallel |
| ContinueWhileOpenTests | Spawning/SpawnerServiceTests.Continue.cs | R4 | parallel |
| DeployScriptTests | DeployScriptTests.cs | R8 | parallel |
| DryRunTests | DryRunTests.cs | none | parallel |
| EffortPolicyTests | Spawning/EffortPolicyTests.cs | none | parallel |
| EscalationClosedTests | Security/EscalationClosedTests.cs | R4 | parallel |
| ExchangeApiTests | ExchangeApiTests.cs | R4 | parallel |
| ExchangePolicyTests | Spawning/ExchangePolicyTests.cs | none | parallel |
| ExchangeWorktreesTests | Rooms/ExchangeWorktreesTests.cs | R8 | parallel |
| ExportManifestTests | Memory/ExportManifestTests.cs | none | parallel |
| GateScriptTests | GateScriptTests.cs | R8 | parallel |
| GitTrailTests | Git/GitTrailTests.cs | R8 | parallel |
| GoverningContextTests | Spawning/GoverningContextTests.cs | R4 | parallel |
| HostCommandsTests | HostCommandsTests.cs | R4 | parallel |
| HubHostTests | HubHostTests.cs | R4, R2, R3 | process-state |
| MemoryApiGuardTests | MemoryApiGuardTests.cs | R4 | parallel |
| MemoryApiTests | MemoryApiTests.cs | R4 | parallel |
| MemoryEditApiTests | MemoryEditApiTests.cs | R4, R8 | parallel |
| MemoryExportRoundTripTests | Memory/MemoryExportRoundTripTests.cs | none | parallel |
| MemoryExportTests | Memory/MemoryExportTests.cs | none | parallel |
| MemoryExportWriterTests | Memory/MemoryExportWriterTests.cs | none | parallel |
| MemoryGitTests | Memory/MemoryGitTests.cs | R8 | parallel |
| MemoryImportTests | Memory/MemoryImportTests.cs | none | parallel |
| MemoryToolsTests | Memory/MemoryToolsTests.cs | R4 | parallel |
| OwnerPeerCheckEndToEndTests | Security/OwnerPeerCheckEndToEndTests.cs | R4, R5, R6, R8 | process-state |
| OwnerPeerCheckMiddlewareTests | Security/OwnerPeerCheckMiddlewareTests.cs | R4 | parallel |
| OwnerPeerCheckTests | Security/OwnerPeerCheckTests.cs | R8 | process-state |
| PanelInputsTests | Spawning/PanelInputsTests.cs | R8 | parallel |
| PanelProcessTests | Spawning/PanelProcessTests.cs | R2, R8 | process-state |
| ParallelismPolicyTests | ParallelismPolicyTests.cs | none (reads this project's sources) | parallel |
| ParticipationTests | ParticipationTests.cs | R4 | parallel |
| PeerProcessTests | Security/PeerProcessTests.cs | R6 | process-state |
| PersistenceTests | PersistenceTests.cs | R4 | parallel |
| ProcessRunnerTests | Spawning/ProcessRunnerTests.cs | R6, R8 | process-state |
| RealtimeTests | RealtimeTests.cs | R4 | parallel |
| RolesApiTests | RolesApiTests.cs | R4 | parallel |
| RoomCommitsTests | Spawning/RoomCommitsTests.cs | none | parallel |
| RoomPathsTests | Rooms/RoomPathsTests.cs | R8 | parallel |
| RoomsApiTests | RoomsApiTests.cs | R4, R8 | parallel |
| RoomToolsTests | RoomToolsTests.cs | R4 | parallel |
| RunLimitsTests | Spawning/RunLimitsTests.cs | none | parallel |
| RunPolicyTests | Spawning/RunPolicyTests.cs | none | parallel |
| RunsApiTests | RunsApiTests.cs | R4 | parallel |
| RunToolsTests | Mcp/RunToolsTests.cs | R4 | parallel |
| SeededOrderTests | SeededOrderTests.cs | none | parallel |
| SkillImportTests | Skills/SkillImportTests.cs | R7, R8 | parallel |
| SkillsApiAuthTests | SkillsApiAuthTests.cs | R4 | parallel |
| SkillsApiProposalsTests | SkillsApiProposalsTests.cs | R4 | parallel |
| SkillsApiTests | SkillsApiTests.cs | R4 | parallel |
| SkillStoreTests | Skills/SkillStoreTests.cs | R3 | process-state |
| SkillToolsTests | Skills/SkillToolsTests.cs | R4 | parallel |
| SmokeTests | SmokeTests.cs | none | parallel |
| SpaMissingClientTests | SpaServingTests.cs | R4 | parallel |
| SpaServingTests | SpaServingTests.cs | R4 | parallel |
| SpawnCommandsTests | Spawning/SpawnCommandsTests.cs | none | parallel |
| SpawnerServiceImportTests | Spawning/SpawnerServiceTests.Import.cs | R4 | parallel |
| SpawnerServiceMemoryTests | Spawning/SpawnerServiceTests.Memory.cs | R4 | parallel |
| SpawnerServiceModesTests | Spawning/SpawnerServiceTests.Modes.cs | R4 | parallel |
| SpawnerServiceRecipientsTests | Spawning/SpawnerServiceTests.Recipients.cs | R4 | parallel |
| SpawnerServiceRolesTests | Spawning/SpawnerServiceTests.Roles.cs | R4 | parallel |
| SpawnerServiceRoomsTests | Spawning/SpawnerServiceTests.Rooms.cs | R4, R8 | parallel |
| SpawnerServiceRunsTests | Spawning/SpawnerServiceTests.Runs.cs | R4, R8 | parallel |
| SpawnerServiceTests | Spawning/SpawnerServiceTests.cs | R4, R8 | parallel |
| SpawnerShutdownConsoleTests | Spawning/SpawnerShutdownConsoleTests.cs | R4, R3, R8 | process-state |
| SpawnerTimingTests | Spawning/SpawnerServiceTests.cs | none | parallel |
| SpawnJobsTests | Spawning/SpawnJobsTests.cs | R6, R8 | process-state |
| SpawnLimitsTests | Spawning/SpawnLimitsTests.cs | none | parallel |
| SpawnOutputTests | Spawning/SpawnOutputTests.cs | none | parallel |
| SpawnPromptTests | Spawning/SpawnPromptTests.cs | none | parallel |
| TokenScanTests | Security/TokenScanTests.cs | none | parallel |
| TokenStoreTests | TokenStoreTests.cs | none | parallel |

Totals: 74 classes, 65 `parallel`, 9 `process-state`. R1 is left out of the table because it is inert.

## Measured

CI runner (`windows-latest`, `Environment.ProcessorCount` 4), one clean runner per arm, three repetitions, 1000 tests each:

| Collections at once | Wall per run (s) | Median |
|---|---|---|
| serial | 811, 723, 757 | 757 |
| 2 | 310, 356, 394 | 356 |
| 4 | 401, 316, 293 | 316 |

2 and 4 are the same within the spread, and both are about 2.3 times faster than serial. The compiled default is 2: the extra speed at 4 is inside the noise, while the one crash this suite suffers (a host start failing with WSAENOBUFS) grows with the number of hubs alive at once.

At the setting that shipped, with nothing overriding it, the merge gate ran green three times in a row (PR 145, run 35791881099). The two attempts whose artifacts were retained show the Hub suite at 447 s and 527 s against the 599 s baseline (run 35730002193), each with a measured peak of 2 concurrent tests and the process-state collection starting only after the last parallel test ended.

One failure in nine arm runs, at 2: `DeployScriptTests` read a starting process's module path as `ntdll.dll` (`docs/BUGS.md` 71). It is a race in the test, not interference between classes, so the class stays parallel; if it repeats, it moves into `ProcessStateCollection` and this line says so.

Controls, each an attempt count rather than a proven rate. Seeded shuffle: 2 runs, 1 failure (a room archive returning 409, not reproducible with the same seed when the class runs alone). A looping build on the other cores: 1 run, 5 failures, all fixed-wait timeouts or temp-directory cleanup (`docs/BUGS.md` 68). Cancellation mid-run and a forced test-host kill: 1 run each, no test host or vstest process alive 30 s later, 8 and 6 GUID temp directories left behind, which is what a killed run is expected to leave.

The desk (16 cores, arms pinned to 4) could not produce a quiet venue: an unrelated test suite and ordinary desktop applications moved serial runs between 568 s and 2538 s and aborted two runs outright. Those numbers are kept in the pilot's scratch record but the CI numbers above are the ones that decide.

## Known hazards under concurrency

- R10 is the one that bites. On a busy desk a host start can fail with `SocketException (10055)`, "the system lacked sufficient buffer space". Hosting raises it on its own thread, so the test host dies and the whole run aborts. Measured at serial, at 2 and at 4 parallel collections, so it follows the machine's socket budget rather than the cap, and TIME_WAIT never rose above 190 while it happened. Queued as `docs/BUGS.md` 67.
- Fixed 15 s waits in the SpawnerService classes fail first whenever the machine is loaded, whether that load is a second test suite, a build on the other cores, or another application. Queued as `docs/BUGS.md` 68.
- R5: between `listener.Stop()` and the host bind another process can take the port.
- R6: `SpawnJobsTests.cs:27-30` documents a conhost job-membership race seen on isolated runs. Tests that count processes by parent pid can see processes another test started.
- R1 is no longer a hazard: with `Pooling=false` there is no pool to clear.

## Could not verify

- Actual interference under parallel execution. Nothing here was run with parallelization enabled. The execution units are derived from reading, and a capped parallel pilot is the measurement.
- Resources reached only through production code the tests call (for example process-wide statics inside `src/`). The audit read the test project and the helpers it owns.
- Whether any test depends on execution order within its class. xUnit order is not guaranteed, and no failure pointing to it was found, but no shuffled run was made.
