<#
.SYNOPSIS
    Builds the room's /roadmap skill folder from the owner's shared delivery texts, ready for
    `ChopItUp.Hub.exe --import-skill <Out> --overlay tools\skills\roadmap-hub`.

.DESCRIPTION
    The room skill is the host-neutral delivery core plus the shared Risk and review rules, with the
    two preflight scripts the board-gate and plan-claims gates call. Those texts live on the owner's
    machine and are never committed here, so this script assembles them at install time:

      <Out>\SKILL.md                         frontmatter, core.md, then the "## Risk and review"
                                             section of engineering.md up to its next "## " heading
      <Out>\preflight\Check-RoadmapBudget.ps1  copied byte for byte
      <Out>\preflight\Check-PlanClaims.ps1     copied byte for byte

    It prints the commit each source was last changed in and the character counts, and refuses
    (exit 1, nothing written) when a source or the section is missing, when <Out>'s folder name is
    not "roadmap" (the import names the skill after its folder), when <Out> holds anything else, or
    when SKILL.md plus the overlay passes the budget. It only writes <Out>. It never imports: the
    import runs with the hub stopped, from the owner's shell.

.PARAMETER ClaudeRoot
    The Claude Code home holding skills\roadmap. Defaults to $HOME\.claude.

.PARAMETER CodexRoot
    The Codex home holding guidance\engineering.md. Defaults to $HOME\.codex.

.PARAMETER Out
    The folder to build, whose name must be "roadmap". Defaults to a folder under the temp directory.

.PARAMETER Overlay
    The overlay's OVERLAY.md, counted against the budget. Defaults to the one beside this script.

.PARAMETER Budget
    The most characters SKILL.md and OVERLAY.md may hold together, since every spawn of a run is
    given both. Defaults to 20000.

.EXAMPLE
    & .\tools\Build-RoomSkill.ps1 -Out "$env:TEMP\chopitup-room-skill\roadmap"
#>
[CmdletBinding()]
param(
    [string]$ClaudeRoot = (Join-Path $HOME '.claude'),
    [string]$CodexRoot = (Join-Path $HOME '.codex'),
    [string]$Out = (Join-Path ([IO.Path]::GetTempPath()) 'chopitup-room-skill\roadmap'),
    [string]$Overlay = (Join-Path $PSScriptRoot 'skills\roadmap-hub\OVERLAY.md'),
    [int]$Budget = 20000
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Stop-Build([string]$Why) { Write-Host "Build-RoomSkill: $Why"; exit 1 }

$core = Join-Path $ClaudeRoot 'skills\roadmap\references\core.md'
$engineering = Join-Path $CodexRoot 'guidance\engineering.md'
$preflight = Join-Path $ClaudeRoot 'skills\roadmap\preflight'
$scripts = @('Check-RoadmapBudget.ps1', 'Check-PlanClaims.ps1')

if ((Split-Path -Leaf $Out) -cne 'roadmap') { Stop-Build "the output folder must be named 'roadmap', not '$(Split-Path -Leaf $Out)'" }
foreach ($source in @($core, $engineering, $Overlay) + ($scripts | ForEach-Object { Join-Path $preflight $_ })) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { Stop-Build "missing source $source" }
}
if (Test-Path -LiteralPath $Out) {
    $expected = @('SKILL.md') + ($scripts | ForEach-Object { "preflight\$_" })
    $present = Get-ChildItem -LiteralPath $Out -Recurse -File | ForEach-Object { $_.FullName.Substring((Resolve-Path -LiteralPath $Out).Path.Length + 1) }
    $foreign = $present | Where-Object { $_ -notin $expected }
    if ($foreign) { Stop-Build "$Out holds files this script did not build: $($foreign -join ', ')" }
}

$lines = (Get-Content -LiteralPath $engineering -Raw) -replace "`r`n", "`n" -split "`n"
$start = [Array]::FindIndex($lines, [Predicate[string]] { param($l) $l.TrimEnd() -eq '## Risk and review' })
if ($start -lt 0) { Stop-Build "no '## Risk and review' section in $engineering" }
$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^## ') { $end = $i; break } }
$section = ($lines[$start..($end - 1)] -join "`n").TrimEnd()

$frontmatter = "---`nname: roadmap`ndescription: Deliver one board row in this room to a reviewed room branch that a native session merges and deploys.`n---`n"
$body = ((Get-Content -LiteralPath $core -Raw) -replace "`r`n", "`n").TrimEnd()
$skill = $frontmatter + $body + "`n`n" + $section + "`n"
$overlayChars = (Get-Content -LiteralPath $Overlay -Raw).Length
$total = $skill.Length + $overlayChars
if ($total -gt $Budget) { Stop-Build "SKILL.md ($($skill.Length)) plus OVERLAY.md ($overlayChars) is $total characters, over the $Budget-character budget" }

New-Item -ItemType Directory -Force -Path (Join-Path $Out 'preflight') | Out-Null
[IO.File]::WriteAllText((Join-Path $Out 'SKILL.md'), $skill, [Text.UTF8Encoding]::new($false))
foreach ($s in $scripts) { Copy-Item -LiteralPath (Join-Path $preflight $s) -Destination (Join-Path $Out "preflight\$s") -Force }

foreach ($pair in @(@($ClaudeRoot, 'skills/roadmap'), @($CodexRoot, 'guidance/engineering.md'))) {
    $commit = & git -C $pair[0] log -1 --format=%h -- $pair[1] 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $commit) { $commit = 'not in a git repository' }
    Write-Host "Build-RoomSkill: $($pair[1]) at $commit"
}
Write-Host "Build-RoomSkill: built $Out (SKILL.md $($skill.Length) characters, OVERLAY.md $overlayChars, $total of $Budget)"
exit 0
