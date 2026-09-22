[CmdletBinding()]
param(
    [string]$Base = 'main',
    [switch]$CommittedOnly, [switch]$Full, [switch]$PlanOnly,
    [string]$LogDir = (Join-Path $env:TEMP ('chop-affected-' + [guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$arguments = @((Join-Path $PSScriptRoot 'affected-tests.mjs'), '--root', $repo, '--base', $Base)
if ($CommittedOnly) { $arguments += '--committed-only' }
if ($Full) { $arguments += '--full' }
$json = & node @arguments
if ($LASTEXITCODE -ne 0) { throw 'Test selection failed' }
$plan = ($json -join "`n") | ConvertFrom-Json
if ($PlanOnly) { $json; exit 0 }
if ($CommittedOnly) {
    $dirty = & git -C $repo status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw '-CommittedOnly execution requires a clean checkout; omit it for local changes' }
}
$null = New-Item -ItemType Directory -Force -Path $LogDir
$json | Set-Content -LiteralPath (Join-Path $LogDir 'selection.json')
Write-Host "Verification: $($plan.mode); suites=$($plan.suites -join ', '); client=$($plan.client). Plan: $LogDir/selection.json"
if ($plan.noProductTests) { Write-Host 'No product tests affected (see recorded paths and reasons).'; exit 0 }
Push-Location $repo
try {
    if ($plan.mode -eq 'full') {
        & dotnet build ChopItUp.slnx -c Debug -warnaserror -v minimal
        if ($LASTEXITCODE -ne 0) { throw 'Solution build failed' }
    } else {
        foreach ($suite in $plan.suites) {
            & dotnet build "tests/$suite/$suite.csproj" -c Debug -warnaserror -v minimal
            if ($LASTEXITCODE -ne 0) { throw "Build failed: $suite" }
        }
        # A client-only change still builds the project whose ClientBuild target typechecks and bundles it.
        foreach ($project in @($plan.builds)) {
            if (-not $project) { continue }
            & dotnet build $project -c Debug -warnaserror -v minimal
            if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
        }
    }
    # Each selected suite retains its count guard; an empty/partial green run is not evidence.
    $floors = @{ 'ChopItUp.Core.Tests' = 341; 'ChopItUp.Hub.Tests' = 983; 'ChopItUp.Desktop.Tests' = 108 }
    foreach ($suite in $plan.suites) {
        $results = Join-Path $LogDir ($suite + '-' + [guid]::NewGuid().ToString('N'))
        & dotnet test "tests/$suite/$suite.csproj" -c Debug --no-build --nologo -v minimal --logger 'trx;LogFileName=result.trx' --results-directory $results
        if ($LASTEXITCODE -ne 0) { throw "Tests failed: $suite" }
        [xml]$trx = Get-Content -LiteralPath (Join-Path $results 'result.trx') -Raw
        $count = $trx.TestRun.ResultSummary.Counters
        if (-not $count -or [int]$count.passed -lt $floors[$suite] -or [int]$count.failed -ne 0 -or [int]$count.executed -ne [int]$count.total -or [int]$count.passed -ne [int]$count.total) {
            throw "Incomplete or skipped tests in $suite (expected at least $($floors[$suite]) passing tests)"
        }
    }
    if ($plan.client) {
        Push-Location src/ChopItUp.Hub/client
        try {
            & npm test
            if ($LASTEXITCODE -ne 0) { throw 'Client tests failed' }
        } finally { Pop-Location }
    }
} finally { Pop-Location }
Write-Host 'RESULT: PASS (selected checks)'
