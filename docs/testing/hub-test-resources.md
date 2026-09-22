# Hub.Tests shared resources

Inventory of every resource in `tests/ChopItUp.Hub.Tests` that outlives a single test or is visible outside it, measured at 084e9f4. It exists so a parallel run can be planned from facts. It changes nothing: all three test assemblies still set `[assembly: CollectionBehavior(DisableTestParallelization = true)]` (`tests/ChopItUp.Hub.Tests/AssemblyInfo.cs:1`, and the same line in `ChopItUp.Core.Tests` and `ChopItUp.Desktop.Tests`). The project has no `[Collection]` or `[CollectionDefinition]` attributes and no `xunit.runner.json`.

## Method

A grep started the audit: per file, the first hit of `HubTestHost.StartAsync`, `ClearAllPools`, `SetEnvironmentVariable`, `Console.Set*`, `Global\`, process launches (`ProcessStartInfo(`, `Process.Start(`, `new ProcessRunner(`), temp-directory helpers and waits of 10 s or more. The grep is only the index. The evidence is the reading: every line cited below was opened, and grep hits that turned out to be `using` lines, comments or string literals were discarded.

## Resources

| # | Resource | Scope | Where | Classes |
|---|----------|-------|-------|---------|
| R1 | SQLite connection pools, cleared by `SqliteConnection.ClearAllPools()` | process | `HubTestHost.cs:122` (every host dispose), `DryRunTests.cs:44`, `HostCommandsTests.cs:37`, `:83`, `:487`, `Skills/ConsolidateMemorySkillTests.cs:43`, `Skills/SkillImportTests.cs:32`, `Spawning/SpawnerServiceTests.cs:917` | every host user (R4) plus DryRunTests, HostCommandsTests, ConsolidateMemorySkillTests, SkillImportTests, SpawnerTimingTests |
| R2 | Environment variables | process | `HubHostTests.cs:390`, `:397` (`CHOPITUP_SHELL_TOKEN`), `Spawning/PanelProcessTests.cs:28-29`, `:41-42` (synthetic sentinel names) | HubHostTests, PanelProcessTests |
| R3 | `Console.Error` redirected by `Console.SetError` | process | `HubHostTests.cs:227`, `:271`, `:322`, `:337`, `Skills/SkillStoreTests.cs:193`, `Spawning/SpawnerServiceTests.Rooms.cs:539` | HubHostTests, SkillStoreTests, SpawnerServiceTests |
| R4 | In-process hub (`HubTestHost`): Kestrel on 127.0.0.1, port 0 unless given, data under the caller's directory | per test | `HubTestHost.cs:58` | see the class table |
| R5 | Fixed loopback port: reserved with a port-0 `TcpListener`, released, then bound by the host | machine | `Security/OwnerPeerCheckEndToEndTests.cs:36-41` | OwnerPeerCheckEndToEndTests |
| R6 | Machine TCP table and process table (WMI `Win32_Process`) read and matched by pid | machine | `Security/PeerProcessTests.cs:44`, `Spawning/ProcessRunnerTests.cs:58`, `Spawning/SpawnJobsTests.cs:41`, `Security/OwnerPeerCheckEndToEndTests.cs:118` | PeerProcessTests, ProcessRunnerTests, SpawnJobsTests, OwnerPeerCheckEndToEndTests |
| R7 | Named kernel mutex `Global\ChopItUp.Skills.<path>` | machine, name unique per test directory | `Skills/SkillImportTests.cs:784`, `:791` | SkillImportTests |
| R8 | Real child processes (git, pwsh, cmd, ping, mklink) confined to the test's directory | machine process list, per-test files | `DeployScriptTests.cs:163`, `GateScriptTests.cs:205`, `Git/GitTrailTests.cs:23`, `Memory/MemoryGitTests.cs:34`, `MemoryEditApiTests.cs:70`, `RoomsApiTests.cs:289`, `Rooms/ExchangeWorktreesTests.cs:55`, `Rooms/RoomPathsTests.cs:89`, `Skills/SkillImportTests.cs:78`, `Spawning/PanelInputsTests.cs:109`, `Spawning/PanelProcessTests.cs:15`, `Spawning/SpawnerServiceTests.Rooms.cs:81`, `Security/OwnerPeerCheckTests.cs:27`, plus the R6 classes | see the class table |
| R9 | Fixture timing log `fixture-<pid>.jsonl`, written only when `TEST_FIXTURE_TIMINGS` is set | process (one file per test process) | `FixtureTiming.cs:12`, `:31` | every host user (R4) |

Everything else a test writes goes under its own unique temp directory, deleted by `TestDirs.DeleteTree` (`TestDirs.cs:8`). `DeployScriptTests` names the real deploy directory (`DeployScriptTests.cs:501`) and documents at `:761` that the test keeps the script's backup path out of it.

## Units

- **Execution unit.** `method`: the test can run alone and in any order. `serial`: correct only while no other test runs in the same process at the same time, because it touches process state (R1, R2, R3). `serial+machine`: the same, and it also reads machine-wide tables or races machine-level state (R5, R6), so a second test process on the same machine can disturb it.
- **Retry unit.** For every class it is the single test method, filtered by `FullyQualifiedName` in a fresh `dotnet test` of this project. Class-level `IAsyncLifetime` in xUnit runs once per test instance, and the two class fixtures (`DeployScriptFixture`, `GateScriptFixture`) are rebuilt by a filtered run, so no method depends on a sibling having run first. Repo policy allows one hypothesis-driven rerun, not repetition until green.
- **Receipt unit.** The reusable receipt is the whole `ChopItUp.Hub.Tests` run on one tree, which is the unit the affected-test selector already schedules (`docs/affected-tests.md`). No smaller unit can be reused, because R1, R2 and R3 are shared by the whole process: a pass by a subset does not show how those tests behave next to the rest of the assembly.

## Classes

64 test classes: 69 files hold a `[Fact]` or `[Theory]`, the partial `SpawnerServiceTests` spans 8 of them, and `SpaServingTests.cs` and `Spawning/SpawnerServiceTests.cs` each hold a second class.

| Class | File | Resources | Execution unit |
|-------|------|-----------|----------------|
| BearerTokenMiddlewareTests | BearerTokenMiddlewareTests.cs | none | method |
| ChatApiTests | ChatApiTests.cs | R4, R1 | serial |
| CliResolverTests | Spawning/CliResolverTests.cs | none | method |
| ConsolidateMemorySkillTests | Skills/ConsolidateMemorySkillTests.cs | R1 | serial |
| ContinueWhileOpenTests | Spawning/SpawnerServiceTests.Continue.cs | R4, R1 | serial |
| DeployScriptTests | DeployScriptTests.cs | R8 | method |
| DryRunTests | DryRunTests.cs | R1 | serial |
| EffortPolicyTests | Spawning/EffortPolicyTests.cs | none | method |
| EscalationClosedTests | Security/EscalationClosedTests.cs | R4, R1 | serial |
| ExchangeApiTests | ExchangeApiTests.cs | R4, R1 | serial |
| ExchangePolicyTests | Spawning/ExchangePolicyTests.cs | none | method |
| ExchangeWorktreesTests | Rooms/ExchangeWorktreesTests.cs | R8 | method |
| ExportManifestTests | Memory/ExportManifestTests.cs | none | method |
| GateScriptTests | GateScriptTests.cs | R8 | method |
| GitTrailTests | Git/GitTrailTests.cs | R8 | method |
| GoverningContextTests | Spawning/GoverningContextTests.cs | R4, R1 | serial |
| HostCommandsTests | HostCommandsTests.cs | R4, R1 | serial |
| HubHostTests | HubHostTests.cs | R4, R1, R2, R3 | serial |
| MemoryApiGuardTests | MemoryApiGuardTests.cs | R4, R1 | serial |
| MemoryApiTests | MemoryApiTests.cs | R4, R1 | serial |
| MemoryEditApiTests | MemoryEditApiTests.cs | R4, R1, R8 | serial |
| MemoryExportRoundTripTests | Memory/MemoryExportRoundTripTests.cs | none | method |
| MemoryExportTests | Memory/MemoryExportTests.cs | none | method |
| MemoryExportWriterTests | Memory/MemoryExportWriterTests.cs | none | method |
| MemoryGitTests | Memory/MemoryGitTests.cs | R8 | method |
| MemoryImportTests | Memory/MemoryImportTests.cs | none | method |
| MemoryToolsTests | Memory/MemoryToolsTests.cs | R4, R1 | serial |
| OwnerPeerCheckEndToEndTests | Security/OwnerPeerCheckEndToEndTests.cs | R4, R1, R5, R6, R8 | serial+machine |
| OwnerPeerCheckMiddlewareTests | Security/OwnerPeerCheckMiddlewareTests.cs | R4, R1 | serial |
| OwnerPeerCheckTests | Security/OwnerPeerCheckTests.cs | R8 | method |
| PanelInputsTests | Spawning/PanelInputsTests.cs | R8 | method |
| PanelProcessTests | Spawning/PanelProcessTests.cs | R2, R8 | serial |
| ParticipationTests | ParticipationTests.cs | R4, R1 | serial |
| PeerProcessTests | Security/PeerProcessTests.cs | R6 | serial+machine |
| PersistenceTests | PersistenceTests.cs | R4, R1 | serial |
| ProcessRunnerTests | Spawning/ProcessRunnerTests.cs | R6, R8 | serial+machine |
| RealtimeTests | RealtimeTests.cs | R4, R1 | serial |
| RolesApiTests | RolesApiTests.cs | R4, R1 | serial |
| RoomCommitsTests | Spawning/RoomCommitsTests.cs | none | method |
| RoomPathsTests | Rooms/RoomPathsTests.cs | R8 | method |
| RoomToolsTests | RoomToolsTests.cs | R4, R1 | serial |
| RoomsApiTests | RoomsApiTests.cs | R4, R1, R8 | serial |
| RunLimitsTests | Spawning/RunLimitsTests.cs | none | method |
| RunPolicyTests | Spawning/RunPolicyTests.cs | none | method |
| RunToolsTests | Mcp/RunToolsTests.cs | R4, R1 | serial |
| RunsApiTests | RunsApiTests.cs | R4, R1 | serial |
| SkillImportTests | Skills/SkillImportTests.cs | R1, R7, R8 | serial |
| SkillStoreTests | Skills/SkillStoreTests.cs | R3 | serial |
| SkillToolsTests | Skills/SkillToolsTests.cs | R4, R1 | serial |
| SkillsApiAuthTests | SkillsApiAuthTests.cs | R4, R1 | serial |
| SkillsApiProposalsTests | SkillsApiProposalsTests.cs | R4, R1 | serial |
| SkillsApiTests | SkillsApiTests.cs | R4, R1 | serial |
| SmokeTests | SmokeTests.cs | none | method |
| SpaMissingClientTests | SpaServingTests.cs | R4, R1 | serial |
| SpaServingTests | SpaServingTests.cs | R4, R1 | serial |
| SpawnCommandsTests | Spawning/SpawnCommandsTests.cs | none | method |
| SpawnJobsTests | Spawning/SpawnJobsTests.cs | R6, R8 | serial+machine |
| SpawnLimitsTests | Spawning/SpawnLimitsTests.cs | none | method |
| SpawnOutputTests | Spawning/SpawnOutputTests.cs | none | method |
| SpawnPromptTests | Spawning/SpawnPromptTests.cs | none | method |
| SpawnerServiceTests | Spawning/SpawnerServiceTests*.cs (8 files) | R4, R1, R3, R8 | serial |
| SpawnerTimingTests | Spawning/SpawnerServiceTests.cs | R1 | serial |
| TokenScanTests | Security/TokenScanTests.cs | none | method |
| TokenStoreTests | TokenStoreTests.cs | none | method |

Totals: 27 `method`, 33 `serial`, 4 `serial+machine`. R8 alone leaves a class at `method`: those processes run in the test's own directory and are matched by their own handles, not by scanning machine tables.

## Known hazards under concurrency

- R1: a host dispose clears every pool in the process, including connections another test is holding open. Whether that breaks a concurrent test is not measured.
- R5: between `listener.Stop()` and the host bind another process can take the port.
- R6: `SpawnJobsTests.cs:27-30` documents a conhost job-membership race seen on isolated runs. Tests that count processes by parent pid can see processes another test started.

## Could not verify

- Actual interference under parallel execution. Nothing here was run with parallelization enabled. The execution units are derived from reading, and a capped parallel pilot is the measurement.
- Resources reached only through production code the tests call (for example process-wide statics inside `src/`). The audit read the test project and the helpers it owns.
- Whether any test depends on execution order within its class. xUnit order is not guaranteed, and no failure pointing to it was found, but no shuffled run was made.
