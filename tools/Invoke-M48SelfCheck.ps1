#Requires -Version 7
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $repo ('.scratch/m48-governing-context/check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidence | Out-Null
Set-Content -LiteralPath (Join-Path $evidence '.gitignore') -Value '*'
Push-Location $repo
try {
    $required = @{
        Core = @('Replacements_and_clears_survive_reopening', 'Only_accepted_human_commands', 'Retry_and_length_validation', 'Snapshot_keeps_one_version', 'Message_window_counts_rows', 'V13_history_never_becomes_accepted')
        Hub = @('Objective_and_correction_survive_the_message_window', 'Each_limit_independently_preserves_pins', 'Two_long_reviews_rebuttal_and_correction', 'Authenticated_commands_acknowledge', 'Newest_oversize_is_disclosed')
    }
    foreach ($project in @('Core', 'Hub')) {
        $resultName = "$project.trx"
        & dotnet test "tests/ChopItUp.$project.Tests/ChopItUp.$project.Tests.csproj" -c Debug --no-build --nologo -v minimal -m:1 -nr:false `
            --filter 'FullyQualifiedName~GoverningContextTests|FullyQualifiedName~V13_history' `
            --logger "trx;LogFileName=$resultName" --results-directory $evidence
        if ($LASTEXITCODE -ne 0) { throw "Synthetic $project checks failed. Evidence: $evidence" }
        [xml]$trx = Get-Content -LiteralPath (Join-Path $evidence $resultName) -Raw
        $counts = $trx.TestRun.ResultSummary.Counters
        if ([int]$counts.total -eq 0 -or [int]$counts.passed -ne [int]$counts.total) {
            throw "Synthetic $project checks were incomplete. Evidence: $evidence"
        }
        $names = @($trx.TestRun.Results.UnitTestResult | ForEach-Object { $_.testName })
        foreach ($name in $required[$project]) {
            if (-not @($names | Where-Object { $_ -like "*$name*" }).Count) {
                throw "Missing required synthetic check: $name"
            }
        }
        Write-Output "$project synthetic checks: $($counts.passed) passed."
    }
    Write-Output "GOVERNING_CONTEXT_CHECK: PASS; evidence=$evidence"
} finally {
    Pop-Location
}
