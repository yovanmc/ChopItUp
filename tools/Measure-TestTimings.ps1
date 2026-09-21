<# Summarize TRX and optional TEST_FIXTURE_TIMINGS JSONL. Reporting only, never a gate verdict.
Use a fresh results directory per run. Concurrent per-test durations must not be added as wall time. #>
param(
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [string]$OutputPath,
    [ValidateRange(1,100)][int]$Top = 15
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $ResultsDirectory).Path
$files = @(Get-ChildItem -LiteralPath $root -Filter '*.trx' -Recurse -File)
if ($files.Count -eq 0) { throw "No TRX results under $root. Run tests with --logger trx." }
$tests = @()
$suites = foreach ($file in $files) {
    $document = [System.Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    $document.Load($file.FullName)
    $ns = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $rows = @($document.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $ns))
    $times = $document.SelectSingleNode('/t:TestRun/t:Times', $ns)
    if (-not $times -or $rows.Count -eq 0) { throw "Missing timings/results in $($file.FullName)" }
    $tests += foreach ($row in $rows) {
        [pscustomobject]@{ test = $row.testName; outcome = $row.outcome
            seconds = [TimeSpan]::Parse($row.duration, [Globalization.CultureInfo]::InvariantCulture).TotalSeconds
            source = $file.FullName }
    }
    [pscustomobject]@{ source = $file.FullName; tests = $rows.Count
        elapsedSeconds = ([DateTimeOffset]::Parse($times.finish) - [DateTimeOffset]::Parse($times.start)).TotalSeconds
        outcomes = @($rows | Group-Object outcome | ForEach-Object { [pscustomobject]@{ outcome=$_.Name; count=$_.Count } }) }
}
$phases = @(foreach ($file in Get-ChildItem -LiteralPath $root -Filter 'fixture-*.jsonl' -Recurse -File) {
    foreach ($line in Get-Content -LiteralPath $file.FullName) { if ($line) { $line | ConvertFrom-Json } }
})
$fixtureSummary = @($phases | Group-Object phase | ForEach-Object {
    $values = @($_.Group.milliseconds | Sort-Object)
    [pscustomobject]@{ phase=$_.Name; samples=$values.Count
        totalSeconds=($values | Measure-Object -Sum).Sum / 1000
        medianMilliseconds=$values[[int][Math]::Floor(($values.Count-1)/2)]
        p95Milliseconds=$values[[Math]::Max(0,[int][Math]::Ceiling($values.Count*.95)-1)] }
})
$report = [ordered]@{ resultsDirectory=$root; suites=@($suites)
    slowestTests=@($tests | Sort-Object seconds -Descending | Select-Object -First $Top)
    fixturePhases=$fixtureSummary
    notes=@('Reporting only. Test outcomes remain authoritative; timings never turn a failure into a pass.',
        'Concurrent durations and fixture phases overlap. Do not sum them into wall elapsed.',
        'Run-to-run changes can reflect machine load and caches. Use matched or same-process controls.') }
if (-not $OutputPath) { $OutputPath = Join-Path $root 'timings-summary.json' }
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8
$suites | Format-Table tests, elapsedSeconds, source -AutoSize
$report.slowestTests | Select-Object seconds, outcome, test | Format-Table -AutoSize
$fixtureSummary | Format-Table -AutoSize
Write-Host "Timing report: $OutputPath"
