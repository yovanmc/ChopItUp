<#
.SYNOPSIS
    M9 live check: starts a hub on a scratch data directory with a scratch rooms root, creates a room,
    proves a refused path is refused, mentions @sonnet in the room, and proves the model wrote a file
    inside the room's tree, that the hub committed the owner's edit and then the model's turn under
    the model's name with a shell log, and that unread, mark-read, archive and the trail endpoint work.

.DESCRIPTION
    Costs one short Sonnet call on the owner's subscription (two with -IncludeCodex). Never touches
    C:\Self Apps, %USERPROFILE%\ChopItUp or any real data directory: -DataDir and -RoomsRoot default
    to fresh folders under $env:TEMP and are left behind with the log. Every check prints PASS/FAIL;
    the last line is "Results: n/m PASS"; exit 0 only when all pass. The hub is stopped by PID, always.
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$DataDir = (Join-Path $env:TEMP ('chopitup_m9check_' + [guid]::NewGuid().ToString('N'))),
    [string]$RoomsRoot = '',
    [int]$Port = 8799,
    [int]$TimeoutSeconds = 240,
    [switch]$IncludeCodex
)

$ErrorActionPreference = 'Stop'
if (-not $RoomsRoot) { $RoomsRoot = "$DataDir.rooms" }   # a sibling: anything under the data dir is refused by design
$script:Checks = New-Object System.Collections.Generic.List[object]
$log = "$DataDir.m9-check.log"

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
foreach ($d in @($DataDir, $RoomsRoot)) {
    if (Test-Path -LiteralPath $d) {
        Write-Error "'$d' already exists; this script only ever runs against fresh directories." -ErrorAction Continue
        exit 2
    }
}
New-Item -ItemType Directory -Path $DataDir | Out-Null
Add-Content -Path $log -Value ("M9 room check {0} exe={1} data={2} rooms={3} port={4}" -f (Get-Date -Format o), $HubExe, $DataDir, $RoomsRoot, $Port)

$claude = Get-Command claude -ErrorAction SilentlyContinue
Add-Check -Name 'cli.claude-on-path' -Passed ([bool]$claude) -Detail ($claude.Source ?? 'not found')
$git = Get-Command git -ErrorAction SilentlyContinue
Add-Check -Name 'cli.git-on-path' -Passed ([bool]$git) -Detail ($git.Source ?? 'not found')

$base = "http://127.0.0.1:$Port"
$hub = $null
function Invoke-Api([string]$Method, [string]$Path, $Body = $null) {
    $args = @{ Uri = "$base$Path"; Method = $Method; TimeoutSec = 30 }
    if ($null -ne $Body) { $args.ContentType = 'application/json'; $args.Body = ($Body | ConvertTo-Json -Compress) }
    Invoke-RestMethod @args
}
function Get-StatusOf([scriptblock]$Call) {
    try { & $Call | Out-Null; 200 } catch { $_.Exception.Response.StatusCode.value__ }
}
try {
    $hub = Start-Process -FilePath $HubExe -ArgumentList @('--data', $DataDir, '--port', "$Port", '--rooms-root', $RoomsRoot) -WindowStyle Hidden -PassThru `
        -RedirectStandardError (Join-Path $DataDir 'hub.stderr.log') -RedirectStandardOutput (Join-Path $DataDir 'hub.stdout.log')
    $health = $null
    foreach ($i in 1..40) {
        try { $health = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 }
    }
    Add-Check -Name 'hub.started' -Passed ($null -ne $health) -Detail "pid=$($hub.Id)"
    Add-Check -Name 'hub.health-schema' -Passed ($health.schema -eq 10) -Detail "schema=$($health.schema)"

    # Rooms: create, refuse, list.
    $room = Invoke-Api POST '/api/rooms' @{ name = 'Live check' }
    $roomDir = Join-Path $RoomsRoot 'live-check'
    Add-Check -Name 'room.created' -Passed ($room.id -eq 'live-check' -and $room.directory -eq $roomDir) -Detail "id=$($room.id) dir=$($room.directory)"
    Add-Check -Name 'room.git-initialised' -Passed (Test-Path -LiteralPath (Join-Path $roomDir '.git') -PathType Container) -Detail $roomDir
    Add-Check -Name 'room.refuses-drive-root' -Passed ((Get-StatusOf { Invoke-Api POST '/api/rooms' @{ name = 'Nope'; directory = 'C:\' } }) -eq 400) -Detail 'C:\ is 400'
    Add-Check -Name 'room.refuses-data-dir' -Passed ((Get-StatusOf { Invoke-Api POST '/api/rooms' @{ name = 'Nope'; directory = (Join-Path $DataDir 'x') } }) -eq 400) -Detail 'inside the data dir is 400'
    Add-Check -Name 'room.refuses-self-apps' -Passed ((Get-StatusOf { Invoke-Api POST '/api/rooms' @{ name = 'Nope'; directory = 'C:\Self Apps\ChopItUp\rooms' } }) -eq 400) -Detail 'C:\Self Apps is 400'
    # PowerShell 7.6 hands a top-level JSON array back as ONE nested Object[]; enumerate before filtering (measured 2026-09-06).
    $rooms = @(Invoke-Api GET '/api/rooms' | ForEach-Object { $_ })
    Add-Check -Name 'room.listed-first' -Passed ($rooms.Count -eq 2 -and $rooms[0].id -eq 'live-check') -Detail (($rooms | ForEach-Object id) -join ',')

    # The owner edits before the spawn: that edit must become its own commit, authored as the owner.
    Set-Content -LiteralPath (Join-Path $roomDir 'owner.md') -Encoding utf8 -Value "# Owner notes`n`nWritten before the spawn.`n"

    function Wait-Exchange([string]$RoomId, [string]$Until, [int]$Seconds) {
        $deadline = (Get-Date).AddSeconds($Seconds)
        $state = $null
        while ((Get-Date) -lt $deadline) {
            try { $state = Invoke-RestMethod -Uri "$base/api/rooms/$RoomId/exchange" -TimeoutSec 10 }
            catch { Add-Content -Path $log -Value "poll exchange failed: $($_.Exception.Message)" }
            if ($state -and $state.status -in ($Until -split ',')) { return $state }
            Start-Sleep -Seconds 3
        }
        return $state
    }
    function Read-Room([string]$RoomId) {
        try { @((Invoke-RestMethod -Uri "$base/api/rooms/$RoomId/messages?afterId=0&limit=200" -TimeoutSec 10).messages) }
        catch { Add-Content -Path $log -Value "read room failed: $($_.Exception.Message)"; @() }
    }
    function Test-Spawn([string]$Participant, [string]$FileName, [string]$ExpectAuthor, [string]$Prefix) {
        # The codeword exists nowhere but this prompt: a file that carries it was written by the model, in the room.
        $codeword = 'HERON-' + (Get-Random -Minimum 100 -Maximum 999)
        $body = "@$Participant Three things, then stop: (1) create a file named $FileName in your working directory whose only content is the line $codeword; (2) run one shell command that lists the files in your working directory; (3) post one line to the room saying done. Do not mention anyone."
        $posted = Invoke-Api POST "/api/rooms/live-check/messages" @{ body = $body }
        Add-Check -Name "$Prefix.post.owner-message" -Passed ($posted.id -ge 1) -Detail "id=$($posted.id)"
        $state = Wait-Exchange -RoomId 'live-check' -Until 'concluded,stopped' -Seconds $TimeoutSeconds
        $messages = Read-Room 'live-check'
        Add-Content -Path $log -Value ("exchange: " + ($state | ConvertTo-Json -Compress))
        foreach ($m in $messages) { Add-Content -Path $log -Value ("#{0} {1}: {2}" -f $m.id, $m.authorId, ($m.body -replace "`r?`n", ' / ')) }
        $replies = @($messages | Where-Object { $_.authorId -eq $Participant -and $_.id -gt $posted.id })
        $hubNotes = @($messages | Where-Object { $_.authorId -eq 'hub' -and $_.id -gt $posted.id })
        Add-Check -Name "$Prefix.spawn.replied" -Passed ($replies.Count -ge 1) -Detail "count=$($replies.Count)"
        Add-Check -Name "$Prefix.exchange.concluded" -Passed ($state.status -eq 'concluded') -Detail "status=$($state.status)"
        Add-Check -Name "$Prefix.exchange.no-failure-notes" -Passed (-not ($hubNotes | Where-Object { $_.body -match 'did not reply|without posting|could not be started|exited with code|^Not committed' })) -Detail (($hubNotes | ForEach-Object { $_.body.Split("`n")[0] }) -join ' | ')
        $file = Join-Path $roomDir $FileName
        $text = if (Test-Path -LiteralPath $file) { (Get-Content -LiteralPath $file -Raw) } else { '' }
        Add-Check -Name "$Prefix.file.created-in-room" -Passed (Test-Path -LiteralPath $file) -Detail $file
        Add-Check -Name "$Prefix.file.carries-codeword" -Passed ($text -like "*$codeword*") -Detail "codeword=$codeword"
        $trailNote = @($hubNotes | Where-Object body -like 'Committed *')
        Add-Check -Name "$Prefix.trail.note" -Passed ($trailNote.Count -eq 1 -and $trailNote[0].body -like "*as $Participant*") -Detail ($trailNote | ForEach-Object body | Select-Object -First 1)
        $top = & git -C $roomDir log -1 --format='%an|%cn|%s' 2>&1
        Add-Check -Name "$Prefix.git.author-is-model" -Passed ("$top" -like "$ExpectAuthor|ChopItUp hub|$Participant`: turn *") -Detail "$top"
        $bodyText = (& git -C $roomDir log -1 --format=%B 2>&1) -join "`n"
        Add-Check -Name "$Prefix.git.shell-log" -Passed ($bodyText -match 'Shell commands run \(\d+\):') -Detail (($bodyText -split "`n" | Select-Object -Skip 2 -First 2) -join ' / ')
        $tracked = & git -C $roomDir ls-files 2>&1
        Add-Check -Name "$Prefix.git.file-committed" -Passed (@($tracked) -contains $FileName) -Detail (@($tracked) -join ',')
    }

    Test-Spawn -Participant 'sonnet' -FileName 'hello.txt' -ExpectAuthor 'Sonnet' -Prefix 'claude'
    $authors = @(& git -C $roomDir log --format='%an' 2>&1)
    Add-Check -Name 'git.owner-commit-first' -Passed ($authors.Count -eq 2 -and $authors[1] -eq 'Owner') -Detail ($authors -join ',')
    Add-Check -Name 'git.owner-file-committed' -Passed ((& git -C $roomDir show --name-only --format= HEAD~1 2>&1) -contains 'owner.md') -Detail 'owner.md in the owner commit'
    if ($IncludeCodex) {
        Test-Spawn -Participant 'gpt-6-astra' -FileName 'hello-codex.txt' -ExpectAuthor 'GPT-6 Astra' -Prefix 'codex'
    }

    # Trail endpoint, unread, mark read, archive.
    $trail = Invoke-Api GET '/api/rooms/live-check/trail'
    $commits = @($trail.commits | ForEach-Object { $_ })
    $expectedCommits = if ($IncludeCodex) { 3 } else { 2 }
    Add-Check -Name 'trail.endpoint' -Passed (($trail.directory -eq $roomDir) -and ($commits.Count -ge $expectedCommits) -and ($commits[0].author -like '* <*@chopitup.local>')) -Detail "commits=$($commits.Count) top=$($commits[0].author)"
    $before = @(Invoke-Api GET '/api/rooms' | ForEach-Object { $_ }) | Where-Object id -eq 'live-check'
    Add-Check -Name 'unread.counts-model-and-hub-messages' -Passed ($before.unread -ge 2) -Detail "unread=$($before.unread)"
    Invoke-Api POST '/api/rooms/live-check/read' | Out-Null
    $after = @(Invoke-Api GET '/api/rooms' | ForEach-Object { $_ }) | Where-Object id -eq 'live-check'
    Add-Check -Name 'unread.zero-after-read' -Passed ($after.unread -eq 0) -Detail "unread=$($after.unread)"
    Invoke-Api POST '/api/rooms/live-check/archive' | Out-Null
    $visible = @(Invoke-Api GET '/api/rooms' | ForEach-Object { $_ })
    $all = @(Invoke-Api GET '/api/rooms?archived=true' | ForEach-Object { $_ })
    Add-Check -Name 'archive.hides' -Passed (($visible | ForEach-Object id) -notcontains 'live-check' -and ($all | ForEach-Object id) -contains 'live-check') -Detail ("visible=" + (($visible | ForEach-Object id) -join ','))
    Add-Check -Name 'archive.keeps-directory' -Passed (Test-Path -LiteralPath (Join-Path $roomDir '.git')) -Detail 'nothing deleted'
    Invoke-Api POST '/api/rooms/live-check/unarchive' | Out-Null
    $restored = @(Invoke-Api GET '/api/rooms' | ForEach-Object { $_ })
    Add-Check -Name 'archive.unarchive-restores' -Passed (($restored | ForEach-Object id) -contains 'live-check') -Detail ''
    Add-Check -Name 'archive.general-refused' -Passed ((Get-StatusOf { Invoke-Api POST '/api/rooms/general/archive' }) -eq 400) -Detail 'general stays'
}
finally {
    if ($hub -and -not $hub.HasExited) { Stop-Process -Id $hub.Id -Force -ErrorAction SilentlyContinue }
    $passed = @($script:Checks | Where-Object Passed).Count
    $total = $script:Checks.Count
    $line = "Results: $passed/$total PASS"
    Write-Host $line
    Add-Content -Path $log -Value $line
    Write-Host "Log: $log"
    Write-Host "Room dir: $roomDir"
}
exit $(if ($passed -eq $total -and $total -gt 0) { 0 } else { 1 })
