# Exercise the runner at its process boundary. No build, test host or npm process.
$ErrorActionPreference = 'Stop'
$probeRoot = Join-Path $env:TEMP ('chop-runner-control-' + [guid]::NewGuid().ToString('N'))
$global:AffectedProbe = @{ Mode = 'Success'; Commands = @(); TestArguments = @() }
function node {
    $global:LASTEXITCODE = 0
    '{"mode":"affected","suites":["ChopItUp.Desktop.Tests"],"client":true,"noProductTests":false,"files":["synthetic"],"reasons":["runner control"]}'
}
function dotnet {
    $a = @($args)
    $global:AffectedProbe.Commands += $a[0]
    if ($a[0] -eq 'test') { $global:AffectedProbe.TestArguments += , $a }
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
        $global:AffectedProbe = @{ Mode = $mode; Commands = @(); TestArguments = @() }
        $failure = $null
        try { & (Join-Path $PSScriptRoot 'Invoke-AffectedTests.ps1') -LogDir (Join-Path $probeRoot $mode) }
        catch { $failure = $_ }
        if (($mode -eq 'Success') -eq [bool]$failure) { throw "Wrong runner outcome for $mode : $failure" }
        if ($mode -eq 'Success' -and ($global:AffectedProbe.Commands -join ',') -ne 'build,test,npm') { throw 'Selected build/test/client commands were omitted or repeated' }
        if ($mode -eq 'FailedBuild' -and $global:AffectedProbe.Commands.Count -ne 1) { throw 'Tests ran after a failed build' }
        Write-Host "PASS: runner control $mode"
    }
    # Tests that wait out a production timeout on purpose carry Category=RealDuration and stay out of
    # the ordinary run; -IncludeRealDuration (the weekly full run) is the only thing that admits them.
    foreach ($includeRealDuration in @($false, $true)) {
        $global:AffectedProbe = @{ Mode = 'Success'; Commands = @(); TestArguments = @() }
        $arguments = @{ LogDir = (Join-Path $probeRoot "RealDuration-$includeRealDuration") }
        if ($includeRealDuration) { $arguments.IncludeRealDuration = $true }
        & (Join-Path $PSScriptRoot 'Invoke-AffectedTests.ps1') @arguments
        $filters = @($global:AffectedProbe.TestArguments | ForEach-Object {
            $at = [array]::IndexOf($_, '--filter')
            if ($at -ge 0) { $_[$at + 1] }
        })
        if ($global:AffectedProbe.TestArguments.Count -ne 1) { throw 'The RealDuration control expected exactly one test invocation' }
        if ($includeRealDuration -and $filters.Count -ne 0) { throw '-IncludeRealDuration still filtered the test run' }
        if (-not $includeRealDuration -and ($filters -join ',') -ne 'Category!=RealDuration') { throw "The ordinary run did not exclude Category=RealDuration (filters: $($filters -join ','))" }
        Write-Host "PASS: runner control RealDuration include=$includeRealDuration"
    }
} finally {
    # Only remove this control's freshly generated directory beneath TEMP.
    if ((Split-Path -Parent ([IO.Path]::GetFullPath($probeRoot))) -ne [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')) { throw 'Unexpected control cleanup path' }
    Remove-Item -LiteralPath $probeRoot -Recurse -Force -ErrorAction SilentlyContinue
    $global:AffectedProbe = $null
}
exit 0
