<#
.SYNOPSIS
    Row 23 (T9) self-check: proves the consolidate-memory skill is installed and reads clean, the
    schema is still what it was, and names the data directory it actually read — the check T10 runs
    against the deployed build after import, and that this script proves out here against a scratch
    stand-in first.

.DESCRIPTION
    Ticket 09 / plan lens 195: a desk-check harness modelled on the row 20 template
    (.scratch\desk-checks\m20.ps1 — there is no references\desk-check-template.ps1 in this repo; see
    the STOP note in this row's builder report) has a documented defect: its shape-checking wrapper
    can record the literal string 'FAIL' as a passing check, so every automated row would be
    unconditionally green. This script does not use that wrapper at all — every check here asserts a
    real .NET boolean or an explicit process exit code, never a string a later branch has to
    re-interpret, which is the "assert exit codes directly" alternative the ticket names.

    -ExpectedSchema and -SkillName exist so this script's own FAIL path can be exercised on demand
    (pass a wrong value and one check goes red) without wiring a fake check permanently into the
    real self-check. See the builder report for the forced-RED run's actual output.

    Never touches C:\Self Apps or any real data directory: -DataDir defaults to a fresh folder under
    $env:TEMP. Pointed at the deployed exe and its real data directory (T10), it is the same check,
    run for real.
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$SkillDir = (Join-Path $PSScriptRoot 'skills\consolidate-memory'),
    [string]$DataDir = (Join-Path $env:TEMP ('chopitup_m23selfcheck_' + [guid]::NewGuid().ToString('N'))),
    [int]$Port = 8805,
    [int]$TimeoutSeconds = 30,
    [int]$ExpectedSchema = 9,          # pass 999 to force that row RED on demand
    [string]$SkillName = 'consolidate-memory'   # pass a bogus name to force that row RED on demand
)

$ErrorActionPreference = 'Stop'
$script:Checks = New-Object System.Collections.Generic.List[object]
$log = "$DataDir.m23-selfcheck.log"

# No $shapeOk wrapper: every check below hands Add-Check an already-computed [bool] or compares a
# process's real exit code to 0 itself. There is no string value that a later branch reinterprets,
# so there is nothing for the lens-195 defect (a 'FAIL' string read as a shape-conforming PASS) to
# hide inside.
function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail = '')
    $script:Checks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail })
    $line = ('{0}  {1}  {2}' -f ($(if ($Passed) { 'PASS' } else { 'FAIL' }), $Name, $Detail))
    Write-Host $line
    Add-Content -Path $log -Value $line
}

if (-not (Test-Path -LiteralPath $HubExe -PathType Leaf)) {
    Write-Error "Hub exe not found at '$HubExe'. Build first, or pass -HubExe." -ErrorAction Continue
    exit 2
}
if (-not (Test-Path -LiteralPath $SkillDir -PathType Container)) {
    Write-Error "Skill dir not found at '$SkillDir'." -ErrorAction Continue
    exit 2
}
if (Test-Path -LiteralPath $DataDir) {
    Write-Error "-DataDir '$DataDir' already exists; this script only ever runs against a fresh directory." -ErrorAction Continue
    exit 2
}
New-Item -ItemType Directory -Path $DataDir -Force | Out-Null

# The data directory this check actually read: printed AND logged, never only one or the other -
# under MSIX virtualization an agent-launched exe and an owner-launched one can see different
# stores (LESSONS), so this line is what lets a human catch that mismatch later.
$dataDirLine = "data-directory-read: $DataDir"
Write-Host $dataDirLine
Add-Content -Path $log -Value ("M23 self-check {0} exe={1} data={2} port={3}" -f (Get-Date -Format o), $HubExe, $DataDir, $Port)
Add-Content -Path $log -Value $dataDirLine

$base = "http://127.0.0.1:$Port"
$hub = $null
try {
    # Import with the hub stopped (docs/verification.md's own runbook line for this skill). Every
    # path element is wrapped in its own literal double quotes (2026-09-07 lesson, hit for real
    # while writing this script: Start-Process -ArgumentList joins its array with plain spaces
    # rather than using ProcessStartInfo.ArgumentList, so an UNquoted path containing a space -
    # this repo's own clone root, "C:\Agent Projects\ChopItUp", is one - gets word-split by the
    # child process's argv parser. Unquoted, --import-skill silently received only "C:\Agent" and
    # SkillImport failed on the derived name "Agent"; quoting fixed it).
    $import = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$DataDir`"", '--import-skill', "`"$SkillDir`"") -PassThru -Wait -NoNewWindow `
        -RedirectStandardError (Join-Path $DataDir 'import.stderr.log') -RedirectStandardOutput (Join-Path $DataDir 'import.stdout.log')
    Add-Check -Name 'skill.import-exit-0' -Passed ($import.ExitCode -eq 0) -Detail "exit=$($import.ExitCode)"

    $hub = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$DataDir`"", '--port', "$Port") -WindowStyle Hidden -PassThru `
        -RedirectStandardError (Join-Path $DataDir 'hub.stderr.log') -RedirectStandardOutput (Join-Path $DataDir 'hub.stdout.log')
    $health = $null
    foreach ($i in 1..40) {
        try { $health = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 }
    }
    Add-Check -Name 'hub.started' -Passed ($null -ne $health) -Detail "pid=$($hub.Id)"
    Add-Check -Name 'health.schema' -Passed ($null -ne $health -and $health.schema -eq $ExpectedSchema) -Detail "schema=$($health.schema) expected=$ExpectedSchema"

    # /api/skills already omits anything Tampered or Missing (SkillsApi.cs doc comment: SkillStore.List
    # skips both), so the skill's mere presence here IS "reads Ok, not Tampered" - there is no separate
    # status field to read. LESSONS M10: enumerate the array before filtering.
    $skills = @(Invoke-RestMethod -Uri "$base/api/skills" -TimeoutSec 10 | ForEach-Object { $_ })
    $row = $skills | Where-Object name -eq $SkillName
    Add-Check -Name 'skill.installed-and-ok' -Passed (@($row).Count -eq 1) -Detail "looked for '$SkillName' among: $(($skills | ForEach-Object name) -join ', ')"
    Add-Check -Name 'skill.not-a-run-skill' -Passed (@($row).Count -eq 1 -and $row.isRun -eq $false) -Detail "isRun=$($row.isRun)"
}
finally {
    if ($hub -and -not $hub.HasExited) { Stop-Process -Id $hub.Id -Force -ErrorAction SilentlyContinue }
    $passed = @($script:Checks | Where-Object Passed).Count
    $total = $script:Checks.Count
    $line = "Results: $passed/$total PASS"
    Write-Host $line
    Add-Content -Path $log -Value $line
    Write-Host "Log: $log"
}
exit $(if ($passed -eq $total -and $total -gt 0) { 0 } else { 1 })
