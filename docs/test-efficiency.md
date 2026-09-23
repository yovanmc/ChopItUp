# Test timing and coverage

## Run affected checks

Build once after code changes, then exercise the changed behavior. For scheduler/snapshot or deployment changes:

```powershell
dotnet build ChopItUp.slnx -c Debug -warnaserror -v minimal
dotnet test tests/ChopItUp.Hub.Tests --no-build --filter 'FullyQualifiedName~SpawnerTimingTests|FullyQualifiedName~R44_continue_requeues|FullyQualifiedName~ExchangeApiTests|FullyQualifiedName~WithRoot_shares_the_gate|FullyQualifiedName~DeployScriptTests'
```

Use [affected verification](affected-tests.md) for the final gate. CI and local execution share the selector; full runs are reserved for its explicit broad/unknown triggers. Keep complete output and TRX so a failed test name is never lost to output filtering.

## Profile a complete run

Use a new results directory each time. Optional fixture logging adds setup/disposal measurements; it does not change test scheduling or acceptance.

```powershell
$results = Join-Path $env:TEMP ('chop-tests-' + [guid]::NewGuid().ToString('N'))
$priorTimings = $env:TEST_FIXTURE_TIMINGS
try {
    $env:TEST_FIXTURE_TIMINGS = Join-Path $results 'fixtures'
    dotnet test ChopItUp.slnx -c Debug --no-build --logger trx --results-directory $results
    if ($LASTEXITCODE -ne 0) { throw 'Full test gate failed; preserve its logs.' }
} finally { $env:TEST_FIXTURE_TIMINGS = $priorTimings }
pwsh -NoProfile -File tools/Measure-TestTimings.ps1 -ResultsDirectory $results
```

The JSON report records each suite's elapsed interval, slowest individual tests and fixture phase distributions. Concurrent durations overlap: their sum is not wall time. Compare matched commands and machine load; one faster run does not establish a percentage saving.

## Coverage decisions

| Behavior | Where it is covered |
| --- | --- |
| Idle stop answers 409 | The idle/unknown-room stop test asserts 409 as well as 404. There is no separate test. |
| Owner stop reports `stoppedBy: owner` | The full stop/SignalR test checks both the response and the next snapshot. There is no separate test. |
| `WithRoot` shares the Git gate | Two deliberately overlapping real commits: hold the first process call, and a second `WithRoot` must share the gate. Both commits must succeed. An independent-gate mutation must fail with peak concurrency two. |
| Debounce and spacing | Advance a fake clock across the exact boundaries. Observe timer registration after the scheduler's launch pass before negative assertions. Real spawn/process tests remain. |
| Queued snapshot state | Wait for published in-flight/stopped state. Hold the continuation spawn while checking its open budget. A visible note or drained event alone is not a published-snapshot barrier. |

Tests with distinct inputs, authentication boundaries, persisted state or process integration remain separate. Lower test counts are not the objective; retained failure detection is. Deployment path coverage adds forward-slash variants and rejects ambiguous/root paths before any writes. Normalize paths before both process guards and containment checks; keep the sibling-prefix guard, data-preservation assertions and restoration checks.

The deployment helper is exercised against synthetic install directories, including a running scratch executable, without touching a live installation.
