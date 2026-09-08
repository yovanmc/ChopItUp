#requires -Version 7
# Gate: Check-RoadmapBudget on the room's ROADMAP.md. cwd = room directory (run_gate sets it).
[CmdletBinding()] param()
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false   # native exit codes are read from $LASTEXITCODE; 'Stop' must not throw on them (pwsh 7.6.5 default False, measured 2026-09-07)
$root = (Get-Location).Path
$roadmap = Join-Path $root 'ROADMAP.md'
if (-not (Test-Path -LiteralPath $roadmap -PathType Leaf)) { Write-Host "board-gate: no ROADMAP.md in $root"; exit 2 }
$gate = Join-Path $PSScriptRoot '..\preflight\Check-RoadmapBudget.ps1'
if (-not (Test-Path -LiteralPath $gate -PathType Leaf)) { Write-Host "board-gate: preflight script missing at $gate"; exit 2 }
if ($env:ROADMAP_GATE_BASELINE) { New-Item -ItemType Directory -Force -Path (Split-Path -Parent $env:ROADMAP_GATE_BASELINE) | Out-Null }
& pwsh -NoProfile -NonInteractive -File $gate -RoadmapPath $roadmap -RequireSchema -RepoRoot $root
exit $LASTEXITCODE
