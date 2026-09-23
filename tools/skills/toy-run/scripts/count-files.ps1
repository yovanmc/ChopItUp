<#
.SYNOPSIS
    The toy-run skill's one gate. Counts files in the current directory, which run_gate sets to the
    room's bound directory, and always exits 0: the run live check only needs a recorded, successful
    gate run showing up in run_gate_runs, never a pass/fail verdict of its own.
#>
[CmdletBinding()]
param()

$count = @(Get-ChildItem -File -ErrorAction SilentlyContinue).Count
Write-Host "count-files: $count file(s) in $((Get-Location).Path)"
exit 0
