#Requires -Version 7
<#
.SYNOPSIS
    Plain-room web check (real Sonnet and real Codex, two calls, spends): a plain-room spawn of each
    host searches the web, and the hub posts one fenced room note listing its calls.

.DESCRIPTION
    Starts the given hub on a fresh scratch data folder under %TEMP% with the real claude and codex
    CLIs on PATH. In the plain general room it then runs two exchanges, one after the other:
      1. @sonnet: WebSearch, a WebFetch of https://example.com, and a WebFetch of the scratch hub's
         own http://127.0.0.1:<port>/health. The note lists the search, the example.com fetch, and
         the loopback fetch marked [denied] (the settings deny list).
      2. @gpt-5.6-sol: one web search. The note lists a WebSearch line. A hub-launched
         Codex searches through its own codex_apps MCP server, which the note reads as a WebSearch.
    Each note must name the participant as the author of the fenced text.

    No real data folder or rooms folder is read or written. The last line is "Results: n/m PASS".
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ChopTokenHelpers.ps1')

$HubExe = (Resolve-Path -LiteralPath $HubExe).Path
$scratch = Join-Path $env:TEMP ('chopitup_plainweb_' + [guid]::NewGuid().ToString('N'))
$dataDir = "$scratch\data"; $roomsRoot = "$scratch\rooms"
New-Item -ItemType Directory -Force $roomsRoot | Out-Null
$l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop()
$base = "http://127.0.0.1:$port"
$ownerToken = (Initialize-ChopScratchTokens -DataDir $dataDir -ParticipantIds @('owner')).owner
$ownerAuth = New-ChopBearerHeaders -Token $ownerToken
$fails = 0
$checks = 0
$hub = $null

function Add-Check([string]$Name, [bool]$Passed, [string]$Detail = '') {
    $script:checks++
    if (-not $Passed) { $script:fails++ }
    Write-Host ("{0,-5} {1}  {2}" -f $(if ($Passed) { 'PASS' } else { 'FAIL' }), $Name, $Detail).TrimEnd()
}

function Invoke-Exchange([string]$Body) {
    $null = Invoke-RestMethod -Uri "$base/api/rooms/general/messages" -Method Post -Headers $ownerAuth -ContentType 'application/json' -Body (@{ body = $Body } | ConvertTo-Json)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $state = $null
        try { $state = Invoke-RestMethod -Uri "$base/api/rooms/general/exchange" -TimeoutSec 10 } catch { }
        if ($state -and $state.status -in 'concluded', 'stopped') { return $state.status }
        Start-Sleep -Seconds 3
    }
    return 'timeout'
}

function Get-CallsNote([string]$Participant) {
    # PowerShell 7.6 hands a nested array back as one Object[]; enumerate before filtering.
    $messages = @((Invoke-RestMethod "$base/api/rooms/general/messages?afterId=0&limit=200" -TimeoutSec 10).messages | ForEach-Object { $_ })
    @($messages | Where-Object { $_.authorId -eq 'hub' -and $_.body.StartsWith("Web and shell calls by $Participant ") }) | Select-Object -Last 1
}

try {
    Add-Check 'cli.claude-on-path' ([bool](Get-Command claude -ErrorAction SilentlyContinue))
    Add-Check 'cli.codex-on-path' ([bool](Get-Command codex -ErrorAction SilentlyContinue))
    $hub = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$dataDir`"", '--port', "$port", '--rooms-root', "`"$roomsRoot`"") `
        -WindowStyle Hidden -PassThru -RedirectStandardError "$scratch\hub.err.log" -RedirectStandardOutput "$scratch\hub.out.log"
    $health = $null
    foreach ($i in 1..60) { try { $health = Invoke-RestMethod "$base/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 } }
    Add-Check 'hub.health' ($null -ne $health) "schema=$($health.schema)"

    $status = Invoke-Exchange "@sonnet Use WebSearch once for `"Model Context Protocol specification`". Then use WebFetch on https://example.com. Then use WebFetch on $base/health. Then post one short line to the room saying which calls worked."
    Add-Check 'claude.exchange-concluded' ($status -eq 'concluded') "status=$status"
    $note = Get-CallsNote 'sonnet'
    Add-Check 'claude.note-posted' ($null -ne $note)
    if ($note) {
        $b = $note.body
        Add-Check 'claude.note-names-author' ($b.Contains("The text below was written by sonnet's calls, not by the hub.") -and $b.Contains('```text'))
        Add-Check 'claude.note-lists-search' ($b -match '\d+\. WebSearch "') "body=$b"
        Add-Check 'claude.note-lists-example-fetch' ($b -match '\d+\. WebFetch "https://example\.com/?"')
        Add-Check 'claude.note-marks-loopback-denied' ($b -match ('\d+\. WebFetch "' + [regex]::Escape("$base/health") + '" \[denied\]'))
    }

    $status = Invoke-Exchange '@gpt-5.6-sol Search the web for "Model Context Protocol specification latest revision", then post one short line to the room with the revision date.'
    Add-Check 'codex.exchange-concluded' ($status -eq 'concluded') "status=$status"
    $note = Get-CallsNote 'gpt-5.6-sol'
    Add-Check 'codex.note-posted' ($null -ne $note)
    if ($note) {
        Add-Check 'codex.note-names-author' ($note.body.Contains("The text below was written by gpt-5.6-sol's calls, not by the hub."))
        Add-Check 'codex.note-lists-search' ($note.body -match '\d+\. WebSearch "') "body=$($note.body)"
    }
    if ($script:fails -gt 0) {
        # The room transcript on a failure, so a model that skipped a call is told apart from a missed parse.
        $messages = @((Invoke-RestMethod "$base/api/rooms/general/messages?afterId=0&limit=200" -TimeoutSec 10).messages | ForEach-Object { $_ })
        $messages | ForEach-Object { Write-Host ("--- {0}: {1}" -f $_.authorId, $_.body) }
    }
}
catch {
    Add-Check 'run' $false $_.Exception.Message
}
finally {
    if ($hub -and -not $hub.HasExited) { $hub.Kill($true); $hub.WaitForExit(10000) | Out-Null }
    Start-Sleep -Milliseconds 500
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Results: $($checks - $fails)/$checks PASS"
exit ($(if ($fails -gt 0) { 1 } else { 0 }))
