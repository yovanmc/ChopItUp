# Exercise the runner at its process boundary. No build, test host or npm process.
$ErrorActionPreference = 'Stop'
$probeRoot = Join-Path $env:TEMP ('chop-runner-control-' + [guid]::NewGuid().ToString('N'))
$global:AffectedProbe = @{ Mode = 'Success'; Commands = @() }
function node {
    $global:LASTEXITCODE = 0
    '{"mode":"affected","suites":["ChopItUp.Desktop.Tests"],"client":true,"noProductTests":false,"files":["synthetic"],"reasons":["runner control"]}'
}
function dotnet {
    $a = @($args)
    $global:AffectedProbe.Commands += $a[0]
    $global:LASTEXITCODE = 0
    if ($a[0] -eq 'build') {
        if ($global:AffectedProbe.Mode -eq 'FailedBuild') { $global:LASTEXITCODE = 17 }
        return
    }
    if ($global:AffectedProbe.Mode -eq 'FailedExit') { $global:LASTEXITCODE = 17; return }
    $directory = $a[[array]::IndexOf($a, '--results-directory') + 1]
    $null = New-Item -ItemType Directory -Force $directory
    $passed = if ($global:AffectedProbe.Mode -eq 'EmptyResults') { 0 } else { 108 }
    $total = if ($global:AffectedProbe.Mode -eq 'SkippedResults') { 109 } else { $passed }
    "<TestRun><ResultSummary><Counters passed='$passed' failed='0' executed='$passed' total='$total'/></ResultSummary></TestRun>" |
        Set-Content -LiteralPath (Join-Path $directory 'result.trx')
}
function npm {
    $global:AffectedProbe.Commands += 'npm'
    $global:LASTEXITCODE = if ($global:AffectedProbe.Mode -eq 'FailedClient') { 17 } else { 0 }
}
try {
    foreach ($mode in @('Success', 'FailedBuild', 'FailedExit', 'EmptyResults', 'SkippedResults', 'FailedClient')) {
        $global:AffectedProbe = @{ Mode = $mode; Commands = @() }
        $failure = $null
        try { & (Join-Path $PSScriptRoot 'Invoke-AffectedTests.ps1') -LogDir (Join-Path $probeRoot $mode) }
        catch { $failure = $_ }
        if (($mode -eq 'Success') -eq [bool]$failure) { throw "Wrong runner outcome for $mode : $failure" }
        if ($mode -eq 'Success' -and ($global:AffectedProbe.Commands -join ',') -ne 'build,test,npm') { throw 'Selected build/test/client commands were omitted or repeated' }
        if ($mode -eq 'FailedBuild' -and $global:AffectedProbe.Commands.Count -ne 1) { throw 'Tests ran after a failed build' }
        Write-Host "PASS: runner control $mode"
    }
} finally {
    # Only remove this control's freshly generated directory beneath TEMP.
    if ((Split-Path -Parent ([IO.Path]::GetFullPath($probeRoot))) -ne [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')) { throw 'Unexpected control cleanup path' }
    Remove-Item -LiteralPath $probeRoot -Recurse -Force -ErrorAction SilentlyContinue
    $global:AffectedProbe = $null
}
exit 0
