#requires -Version 7
# Gate: Check-PlanClaims on every plan a 📝 or 🔨 row names. No such row = nothing to check, exit 0.
[CmdletBinding()] param()
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false   # native exit codes are read from $LASTEXITCODE; 'Stop' must not throw on them (pwsh 7.6.5 default False, measured 2026-09-07)
$root = (Get-Location).Path
$roadmap = Join-Path $root 'ROADMAP.md'
if (-not (Test-Path -LiteralPath $roadmap -PathType Leaf)) { Write-Host "plan-claims: no ROADMAP.md in $root"; exit 2 }
$gate = Join-Path $PSScriptRoot '..\preflight\Check-PlanClaims.ps1'
if (-not (Test-Path -LiteralPath $gate -PathType Leaf)) { Write-Host "plan-claims: preflight script missing at $gate"; exit 2 }
$worst = 0; $checked = 0
foreach ($line in Get-Content -LiteralPath $roadmap) {
    if ($line -notmatch '^\|\s*\d+\s*\|') { continue }
    $cells = ($line.Trim() -replace '^\||\|$', '') -split '\|' | ForEach-Object { $_.Trim() }
    if ($cells.Count -lt 5) { continue }
    if ($cells[2] -notmatch '^(📝|🔨)') { continue }
    $planCell = $cells[4] -replace '^\[.*?\]\((.*?)\).*$', '$1'
    if (-not $planCell -or $planCell -eq '—') { continue }
    $plan = Join-Path $root $planCell
    if (-not (Test-Path -LiteralPath $plan -PathType Leaf)) { Write-Host "plan-claims: row $($cells[0]) names '$planCell' which does not exist"; $worst = [Math]::Max($worst, 1); continue }
    $checked++
    & pwsh -NoProfile -NonInteractive -File $gate -PlanPath $plan -RepoPath $root
    $worst = [Math]::Max($worst, $LASTEXITCODE)
}
Write-Host "plan-claims: $checked plan(s) checked, worst exit $worst"
exit $worst
