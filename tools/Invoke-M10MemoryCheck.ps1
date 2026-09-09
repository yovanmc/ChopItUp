<#
.SYNOPSIS
    M10 live check: starts a hub on a scratch data directory whose MEMORY.md carries a codeword,
    mentions @sonnet, and proves the codeword reached the model, that a proposal came back, and
    that approving it writes the topic file and one git commit.

.DESCRIPTION
    Proves the composition the unit tests cannot: the real claude.exe, signed in on this machine,
    spawned by the hub with the widened allow-list, reading memory from its prompt and calling
    propose_memory through the hub's own MCP endpoint. Costs one short Sonnet call on the owner's
    subscription. Never touches C:\Self Apps or any real data directory: -DataDir defaults to a
    fresh folder under $env:TEMP and is left behind with the log.

    Every check prints PASS/FAIL; the last line is "Results: n/m PASS"; exit 0 only when all pass.
    The hub it starts is stopped by PID at the end, always.
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$DataDir = (Join-Path $env:TEMP ('chopitup_m10check_' + [guid]::NewGuid().ToString('N'))),
    [int]$Port = 8798,
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$script:Checks = New-Object System.Collections.Generic.List[object]
$log = "$DataDir.m10-check.log"

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
if (Test-Path -LiteralPath $DataDir) {
    Write-Error "-DataDir '$DataDir' already exists; this script only ever runs against a fresh directory." -ErrorAction Continue
    exit 2
}
New-Item -ItemType Directory -Path (Join-Path $DataDir 'memory') | Out-Null
Add-Content -Path $log -Value ("M10 memory check {0} exe={1} data={2} port={3}" -f (Get-Date -Format o), $HubExe, $DataDir, $Port)

# The codeword exists nowhere but this file: a reply that carries it read memory from the prompt.
$codeword = 'PELICAN-' + (Get-Random -Minimum 100 -Maximum 999)
Set-Content -LiteralPath (Join-Path $DataDir 'memory\MEMORY.md') -Encoding utf8 -Value @"
# Memory

The owner's deploy codeword is $codeword. Say it back when asked.
"@

$claude = Get-Command claude -ErrorAction SilentlyContinue
Add-Check -Name 'cli.claude-on-path' -Passed ([bool]$claude) -Detail ($claude.Source ?? 'not found')
$git = Get-Command git -ErrorAction SilentlyContinue
Add-Check -Name 'cli.git-on-path' -Passed ([bool]$git) -Detail ($git.Source ?? 'not found')

$base = "http://127.0.0.1:$Port"
$hub = $null
try {
    $hub = Start-Process -FilePath $HubExe -ArgumentList @('--data', $DataDir, '--port', "$Port") -WindowStyle Hidden -PassThru `
        -RedirectStandardError (Join-Path $DataDir 'hub.stderr.log') -RedirectStandardOutput (Join-Path $DataDir 'hub.stdout.log')
    $health = $null
    foreach ($i in 1..40) {
        try { $health = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 }
    }
    Add-Check -Name 'hub.started' -Passed ($null -ne $health) -Detail "pid=$($hub.Id)"
    Add-Check -Name 'hub.health-schema' -Passed ($health.schema -eq 10) -Detail "schema=$($health.schema)"
    Add-Check -Name 'memory.seed-kept' -Passed ((Get-Content -LiteralPath (Join-Path $DataDir 'memory\MEMORY.md') -Raw) -like "*$codeword*") -Detail 'hub start must not overwrite an existing core'
    Add-Check -Name 'memory.no-git-before-approval' -Passed (-not (Test-Path -LiteralPath (Join-Path $DataDir 'memory\.git'))) -Detail 'lazy init'

    function Wait-Exchange([string]$Until, [int]$Seconds) {
        $deadline = (Get-Date).AddSeconds($Seconds)
        $state = $null
        while ((Get-Date) -lt $deadline) {
            try { $state = Invoke-RestMethod -Uri "$base/api/rooms/general/exchange" -TimeoutSec 10 }
            catch { Add-Content -Path $log -Value "poll exchange failed: $($_.Exception.Message)" }
            if ($state -and $state.status -in ($Until -split ',')) { return $state }
            Start-Sleep -Seconds 3
        }
        return $state
    }
    function Read-Room {
        try { @((Invoke-RestMethod -Uri "$base/api/rooms/general/messages?afterId=0&limit=200" -TimeoutSec 10).messages) }
        catch { Add-Content -Path $log -Value "read room failed: $($_.Exception.Message)"; @() }
    }

    $body = "@sonnet Two things, then stop: (1) post one line that repeats the owner's deploy codeword from your memory, exactly; (2) call propose_memory once with room_id 'general', topic 'check', title 'Live check ran', body 'The M10 live check ran and sonnet could read the codeword.' Do not mention anyone."
    $posted = Invoke-RestMethod -Uri "$base/api/rooms/general/messages" -Method Post -ContentType 'application/json' -Body (@{ body = $body } | ConvertTo-Json)
    Add-Check -Name 'post.owner-message' -Passed ($posted.id -ge 1) -Detail "id=$($posted.id)"
    $state = Wait-Exchange -Until 'concluded,stopped' -Seconds $TimeoutSeconds
    $messages = Read-Room
    Add-Content -Path $log -Value ("exchange: " + ($state | ConvertTo-Json -Compress))
    foreach ($m in $messages) { Add-Content -Path $log -Value ("#{0} {1}: {2}" -f $m.id, $m.authorId, ($m.body -replace "`r?`n", ' / ')) }

    $sonnet = @($messages | Where-Object { $_.authorId -eq 'sonnet' -and $_.id -gt $posted.id })
    $hubNotes = @($messages | Where-Object authorId -eq 'hub')
    Add-Check -Name 'spawn.sonnet-replied' -Passed ($sonnet.Count -ge 1) -Detail "count=$($sonnet.Count)"
    Add-Check -Name 'memory.codeword-in-reply' -Passed ([bool]($sonnet | Where-Object body -like "*$codeword*")) -Detail "codeword=$codeword"
    Add-Check -Name 'exchange.concluded' -Passed ($state.status -eq 'concluded') -Detail "status=$($state.status)"
    # Memory notes quote model-written text; only the hub's own failure notes are judged here.
    Add-Check -Name 'exchange.no-failure-notes' -Passed (-not ($hubNotes | Where-Object { -not $_.body.StartsWith('Memory ') -and $_.body -match 'did not reply|without posting|could not be started|exited with code' })) -Detail (($hubNotes | ForEach-Object { $_.body.Split("`n")[0] }) -join ' | ')

    # PowerShell 7.6 hands a top-level JSON array back as ONE nested Object[]; enumerate before filtering (measured 2026-09-06).
    $pending = @(Invoke-RestMethod -Uri "$base/api/memory/proposals?room=general&status=pending" -TimeoutSec 10 | ForEach-Object { $_ })
    $mine = @($pending | Where-Object { $_.authorId -eq 'sonnet' })
    Add-Check -Name 'proposal.pending-from-sonnet' -Passed ($mine.Count -ge 1) -Detail ("ids=" + (($mine | ForEach-Object id) -join ','))
    Add-Check -Name 'proposal.special-message' -Passed ([bool]($hubNotes | Where-Object body -like 'Memory proposal #* by sonnet*')) -Detail 'hub note announces the proposal'

    if ($mine.Count -ge 1) {
        $p = $mine[0]
        $approved = Invoke-RestMethod -Uri "$base/api/memory/proposals/$($p.id)/approve" -Method Post -TimeoutSec 60
        Add-Check -Name 'approve.status' -Passed ($approved.status -eq 'approved') -Detail "status=$($approved.status)"
        Add-Check -Name 'approve.commit-hash' -Passed ($approved.commitHash -match '^[0-9a-f]{7,}$') -Detail "hash=$($approved.commitHash)"
        $topicFile = Join-Path $DataDir ('memory\' + ($approved.writtenTo -replace '/', '\'))
        $topicText = if (Test-Path -LiteralPath $topicFile) { Get-Content -LiteralPath $topicFile -Raw } else { '' }
        Add-Check -Name 'approve.topic-file' -Passed ($topicText.Contains("## " + $p.title)) -Detail $approved.writtenTo
        $gitLog = & git -C (Join-Path $DataDir 'memory') log --oneline 2>&1
        Add-Check -Name 'approve.one-commit' -Passed (@($gitLog).Count -eq 1) -Detail (($gitLog | Select-Object -First 1) -join '')
        $after = Read-Room
        Add-Check -Name 'approve.note' -Passed ([bool]($after | Where-Object { $_.authorId -eq 'hub' -and $_.body -like "Memory proposal #$($p.id) approved:*" })) -Detail 'hub note announces the approval'
        $again = $null
        try { Invoke-RestMethod -Uri "$base/api/memory/proposals/$($p.id)/approve" -Method Post -TimeoutSec 10 } catch { $again = $_.Exception.Response.StatusCode.value__ }
        Add-Check -Name 'approve.second-is-409' -Passed ($again -eq 409) -Detail "status=$again"
    }
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
