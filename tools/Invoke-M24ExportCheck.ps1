<#
.SYNOPSIS
    Row 24 (M24) self-check gate: a PASS/FAIL row per acceptance-relevant claim about the export
    verb's shape and this row's documentation, plus one row for the synthetic-corpus dry run
    (`tools\Invoke-M24DryRun.ps1`) actually passing. Every row's boolean comes from a real measurement
    -- a `Select-String` match, a `Test-Path`, or the dry run's own exit code -- never a string a
    later branch could reinterpret as truthy.

.DESCRIPTION
    See docs/superpowers/plans/m24-memory-v1-1c-export.md, "T6 -- dry run, self-check, docs", and
    .scratch/m24-memory-v1-1c-export/issues/06-dry-run-and-the-owner-probe.md: "The self-check must
    be capable of failing. A harness whose rows cannot go red is worse than no harness, and this repo
    has already shipped one that recorded a failure as a pass."

    The shipped `~/.claude/skills/roadmap/references/desk-check-template.ps1` records a check's
    result through a scriptblock whose return value is later re-interpreted by a `$shapeOk` matcher
    (a string, an int, a bare 'OK'/'FAIL' token) -- a shape a future check can satisfy by accident
    without the check itself ever having compared anything. This script never does that: `Add-Check`
    takes `[bool]$Passed` as a MANDATORY, typed parameter (the M2/M4 idiom, `tools\Invoke-M2DryRun.ps1`
    / `tools\Invoke-M4SelfCheck.ps1`), so every row's status is a boolean the calling code computed
    itself, not a string this function goes on to interpret.

    This script was run once with a deliberately wrong expectation on one row (a canary added and
    then removed before this file was committed) to confirm `Add-Check` actually records FAIL and
    the script actually exits non-zero when a row is false -- see the builder's report for that run's
    real console output. Nothing in the delivered script is designed to fail; every row here should
    read PASS against a correct checkout.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$dryRunScript = Join-Path $PSScriptRoot 'Invoke-M24DryRun.ps1'
$verificationDoc = Join-Path $repoRoot 'docs\verification.md'

$checkLines = New-Object System.Collections.Generic.List[string]
$failCount = 0

function Add-Check {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][bool]$Passed, [string]$Detail = '')
    $status = if ($Passed) { 'PASS' } else { 'FAIL' }
    $line = "$status  $Name  $Detail".TrimEnd()
    $checkLines.Add($line)
    if (-not $Passed) { $script:failCount++ }
    Write-Host $line
}

function Test-FileContains {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Pattern)
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    return [bool](Select-String -LiteralPath $Path -Pattern $Pattern -Quiet)
}

$exitCode = 1

try {
    # === Source shape: the verb, its exit codes, the guard's states ================================
    Add-Check -Name 'shape.hub-command-has-export-memory' `
        -Passed (Test-FileContains -Path "$repoRoot\src\ChopItUp.Hub\Hosting\HubOptions.cs" -Pattern 'enum HubCommand \{ Serve, RotateToken, PrintConfig, ImportSkill, SetClasses, ExportMemory \}') `
        -Detail 'HubOptions.cs: HubCommand enum'

    Add-Check -Name 'shape.host-commands-routes-export-memory' `
        -Passed (Test-FileContains -Path "$repoRoot\src\ChopItUp.Hub\Hosting\HostCommands.cs" -Pattern 'HubCommand\.ExportMemory => ExportMemory\(options, output, error\)') `
        -Detail 'HostCommands.cs: Run switch'

    Add-Check -Name 'shape.export-memory-gates-on-hub-lock' `
        -Passed (Test-FileContains -Path "$repoRoot\src\ChopItUp.Hub\Hosting\HostCommands.cs" -Pattern 'HubLock\.IsHeld\(options\.DataDir\)') `
        -Detail 'D11: exit 5 while a hub is running'

    Add-Check -Name 'shape.export-memory-fences-on-missing-memory-dir' `
        -Passed (Test-FileContains -Path "$repoRoot\src\ChopItUp.Hub\Hosting\HostCommands.cs" -Pattern 'if \(!Directory\.Exists\(memoryDir\)\)') `
        -Detail 'exit 4: fenced before the store is constructed (pass 2 M11)'

    Add-Check -Name 'shape.export-refused-exception-is-not-invalid-operation' `
        -Passed (Test-FileContains -Path "$repoRoot\src\ChopItUp.Hub\Memory\MemoryExport.cs" -Pattern 'class ExportRefusedException\(string message\) : Exception\(message\)') `
        -Detail 'claim 21, pass 2 M5: over-cap must map to exit 6, never the shared exit-3 mapping'

    Add-Check -Name 'shape.target-state-enumerates-all-six' `
        -Passed (Test-FileContains -Path "$repoRoot\src\ChopItUp.Hub\Memory\ExportManifest.cs" -Pattern 'enum TargetState \{ Absent, Foreign, Unreadable, DifferentSource, Drifted, Clean \}') `
        -Detail 'D5: Foreign is never conflated with Absent'

    Add-Check -Name 'shape.retention-is-a-superset-test-not-clean-alone' `
        -Passed (Test-FileContains -Path "$repoRoot\src\ChopItUp.Hub\Memory\MemoryExportWriter.cs" -Pattern 'private static bool IsSuperset') `
        -Detail 'D8, pass 2 B3: a shrunk store must not land in the reusable slot'

    Add-Check -Name 'shape.differentsource-not-overridable-by-force' `
        -Passed (Test-FileContains -Path "$repoRoot\src\ChopItUp.Hub\Memory\MemoryExportWriter.cs" -Pattern '--force cannot override this \(D9\)') `
        -Detail 'D9: only --accept-new-source proceeds past DifferentSource'

    # === Documentation: docs/verification.md carries the runbook T6 requires =======================
    Add-Check -Name 'docs.exit-code-4-documented' -Passed (Test-FileContains -Path $verificationDoc -Pattern '\b4\b.*[Nn]o memory') -Detail 'exit 4'
    Add-Check -Name 'docs.exit-code-6-documented' -Passed (Test-FileContains -Path $verificationDoc -Pattern '\b6\b.*[Rr]efused') -Detail 'exit 6'
    Add-Check -Name 'docs.d1-operating-rule-stated' -Passed (Test-FileContains -Path $verificationDoc -Pattern 'D1') -Detail 'the export owns its directory'
    Add-Check -Name 'docs.reusable-previous-name-stated' -Passed (Test-FileContains -Path $verificationDoc -Pattern '\.chopitup-export-previous(?!-)') -Detail 'the plain, reusable recovery name'
    Add-Check -Name 'docs.timestamped-previous-name-stated' -Passed (Test-FileContains -Path $verificationDoc -Pattern '\.chopitup-export-previous-') -Detail 'the timestamped recovery name'
    Add-Check -Name 'docs.staging-tmp-disposal-stated' -Passed (Test-FileContains -Path $verificationDoc -Pattern '\.chopitup-export-tmp-') -Detail "the owner's sanctioned cleanup"
    Add-Check -Name 'docs.accept-new-source-guidance-stated' -Passed (Test-FileContains -Path $verificationDoc -Pattern '--accept-new-source') -Detail 'when it is (and is not) the right answer'
    Add-Check -Name 'docs.manifest-source-root-sentence-stated' -Passed (Test-FileContains -Path $verificationDoc -Pattern '(?i)manifest.*(records|holds).*(absolute|source)') -Detail 'the manifest records the absolute source path'
    Add-Check -Name 'docs.owner-probe-stated' -Passed (Test-FileContains -Path $verificationDoc -Pattern '(?i)owner probe') -Detail 'the not-machine-checkable question'
    Add-Check -Name 'docs.owner-probe-type-discriminator-stated' -Passed (Test-FileContains -Path $verificationDoc -Pattern 'room-general') -Detail 'an out-of-enum metadata.type'
    Add-Check -Name 'docs.owner-probe-index-discriminators-stated' -Passed ((Test-FileContains -Path $verificationDoc -Pattern '198') -and (Test-FileContains -Path $verificationDoc -Pattern '199')) -Detail 'the 198/199-entry boundary'

    # === The dry run itself: T6's required synthetic-corpus run, driven end to end ==================
    if (Test-Path -LiteralPath $dryRunScript) {
        Write-Host ""
        Write-Host "Running $dryRunScript ..."
        $dryRunLog = Join-Path $env:TEMP "chopitup_m24exportcheck_$([guid]::NewGuid().ToString('N')).dryrun.log"
        & pwsh -NoProfile -File $dryRunScript *>&1 | Tee-Object -FilePath $dryRunLog | Out-Null
        $dryRunExit = $LASTEXITCODE
        Add-Check -Name 'dry-run.exit-zero' -Passed ($dryRunExit -eq 0) -Detail "exit=$dryRunExit (full output: $dryRunLog)"
    }
    else {
        Add-Check -Name 'dry-run.exit-zero' -Passed $false -Detail "missing: $dryRunScript"
    }

    $exitCode = if ($failCount -eq 0) { 0 } else { 1 }
}
catch {
    Add-Check -Name 'unhandled-error' -Passed $false -Detail "$($_.Exception.Message) [line $($_.InvocationInfo.ScriptLineNumber)]"
    $exitCode = 1
}
finally {
    $passCount = ($checkLines | Where-Object { $_.StartsWith('PASS') }).Count
    $totalCount = $checkLines.Count
    Write-Host ""
    Write-Host "Results: $passCount/$totalCount PASS"
}

exit $exitCode
