# Test timing and coverage

## Run focused checks before the full gate

Build once after code changes, then exercise the changed behavior. For scheduler/snapshot or deployment changes:

```powershell
dotnet build ChopItUp.slnx -c Debug -warnaserror -v minimal
dotnet test tests/ChopItUp.Hub.Tests --no-build --filter 'FullyQualifiedName~SpawnerTimingTests|FullyQualifiedName~R44_continue_requeues|FullyQualifiedName~ExchangeApiTests|FullyQualifiedName~WithRoot_shares_the_gate|FullyQualifiedName~DeployScriptTests'
```

This is an editing loop, not a replacement for the full solution and client gates in `CLAUDE.md` and CI. Run the required full gates once the focused regressions stabilize; rerun after relevant changes or an unresolved failure. Keep complete output and TRX so a failed test name is never lost to output filtering.

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

## Coverage decisions, September 2026

| Change | Coverage preserved |
| --- | --- |
| Remove the separate idle-stop 409 test | Existing idle/unknown-room stop test now asserts 409 as well as 404. |
| Remove the separate owner `stoppedBy` test | Full stop/SignalR test now checks both response and subsequent snapshot report `owner`. |
| Replace 20 Git commits with two deliberately overlapping real commits | Hold the first process call; a second `WithRoot` must share the gate. Both commits must succeed. An independent-gate mutation must fail with peak concurrency two. |
| Replace debounce/spacing wall-clock sleeps | Advance a fake clock across the exact boundaries; observe timer registration after the scheduler's launch pass before negative assertions. Real spawn/process tests remain. |
| Repair queued snapshot races | Wait for published in-flight/stopped state; hold the continuation spawn while checking its open budget. A visible note or drained event alone is not a published-snapshot barrier. |

Tests with distinct inputs, authentication boundaries, persisted state or process integration remain separate. Lower test counts are not the objective; retained failure detection is. Deployment path coverage adds forward-slash variants and rejects ambiguous/root paths before any writes. Normalize paths before both process guards and containment checks; keep the sibling-prefix guard, data-preservation assertions and restoration checks.

These changes affect the test harness and repository deployment helper. No application binary or installed UI behavior changes; the helper is exercised against synthetic install directories, including a running scratch executable, without touching a live installation.
