#Requires -Version 7
<#
.SYNOPSIS
    Live probe: can a Claude spawn and a Codex spawn in a directory room read a room image?

.DESCRIPTION
    Not automatable without spend: each leg makes one model call on the owner's subscriptions.
    Starts the given hub exe against fresh folders under %TEMP% (never a real data directory),
    creates a directory room, writes a PNG holding random text into its artifacts folder, and posts
    one owner message per leg with a leading mention, so only that participant spawns. A leg passes
    when the participant's reply contains the text. The text appears only inside the image.

    Exit 0 when every leg passes, 1 otherwise.
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string[]]$Participants = @('opus', 'gpt-5.6-sol'),
    [int]$TimeoutSeconds = 420
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
. (Join-Path $PSScriptRoot 'ChopTokenHelpers.ps1')

$scratch = Join-Path $env:TEMP ('chopitup_imageprobe_' + [guid]::NewGuid().ToString('N'))
$dataDir = "$scratch\data"; $roomsRoot = "$scratch\rooms"
$l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop()
$base = "http://127.0.0.1:$port"
$tokens = Initialize-ChopScratchTokens -DataDir $dataDir -ParticipantIds @('owner')
$owner = New-ChopBearerHeaders -Token $tokens.owner
$results = [ordered]@{}
$hub = $null

function New-ProbeImage([string]$Path, [string]$Text) {
    $bmp = New-Object System.Drawing.Bitmap(900, 300)
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $gfx.Clear([System.Drawing.Color]::White)
    $gfx.TextRenderingHint = 'AntiAliasGridFit'
    $font = New-Object System.Drawing.Font('Consolas', 72, [System.Drawing.FontStyle]::Bold)
    $gfx.DrawString($Text, $font, [System.Drawing.Brushes]::Black, 40, 90)
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $gfx.Dispose(); $bmp.Dispose()
}

function New-ProbeText {
    # No 0/O or 1/I/L, so a correct reading is never ambiguous.
    $alphabet = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789'
    -join (1..8 | ForEach-Object { $alphabet[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($alphabet.Length)] })
}

function Wait-Reply([string]$RoomId, [string]$Author, [long]$AfterId) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $page = Invoke-RestMethod -Uri "$base/api/rooms/$RoomId/messages?afterId=$AfterId&limit=200" -TimeoutSec 10
        $reply = @($page.messages | Where-Object { $_.authorId -eq $Author }) | Select-Object -First 1
        if ($reply) { return $reply.body }
        $failed = @($page.messages | Where-Object { $_.authorId -eq 'hub' -and $_.body -match "@$([regex]::Escape($Author)) (could not be started|timed out|failed)" })
        if ($failed) { return "HUB: $($failed[0].body)" }
        Start-Sleep -Seconds 3
    }
    return $null
}

function Post([string]$RoomId, [hashtable]$Auth, [string]$Body) {
    Invoke-RestMethod -Method Post -Uri "$base/api/rooms/$RoomId/messages" -Headers $Auth -ContentType 'application/json' -Body (@{ body = $Body } | ConvertTo-Json) -TimeoutSec 15
}

try {
    New-Item -ItemType Directory -Force $roomsRoot | Out-Null
    $hub = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$dataDir`"", '--port', "$port", '--rooms-root', "`"$roomsRoot`"") `
        -WindowStyle Hidden -PassThru -RedirectStandardError "$scratch\hub.err.log" -RedirectStandardOutput "$scratch\hub.out.log"
    $up = $false
    foreach ($i in 1..60) { try { Invoke-RestMethod "$base/health" -TimeoutSec 2 | Out-Null; $up = $true; break } catch { Start-Sleep -Milliseconds 500 } }
    if (-not $up) { throw 'scratch hub never answered /health' }

    $room = Invoke-RestMethod -Method Post -Uri "$base/api/rooms" -Headers $owner -ContentType 'application/json' -Body '{"name":"Image Probe"}' -TimeoutSec 60
    $artifacts = "$($room.directory).artifacts"
    New-Item -ItemType Directory -Force $artifacts | Out-Null

    foreach ($id in $Participants) {
        $text = New-ProbeText
        $file = "probe-$($id -replace '[^a-z0-9]', '').png"
        New-ProbeImage -Path (Join-Path $artifacts $file) -Text $text
        $posted = Post $room.id $owner "@$id Open the image file artifact:$file (it is in the room's artifacts folder) and reply with only the text it shows, exactly as written."
        $reply = Wait-Reply $room.id $id $posted.id
        $ok = $null -ne $reply -and $reply.Contains($text)
        $results["read.$id"] = $ok
        Write-Host ("{0,-5} read.{1}  expected={2} reply={3}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $id, $text, $(if ($reply) { ($reply -replace '\s+', ' ').Substring(0, [Math]::Min(160, ($reply -replace '\s+', ' ').Length)) } else { '<none>' }))
    }
}
catch {
    $results['run'] = $false
    Write-Host "FAIL  run  $($_.Exception.Message)"
}
finally {
    if ($hub -and -not $hub.HasExited) { $hub.Kill($true); $hub.WaitForExit(10000) | Out-Null }
    Start-Sleep -Milliseconds 500
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

$failedLegs = @($results.Keys | Where-Object { -not $results[$_] })
Write-Host ("Row 82 image probe: {0} PASS, {1} FAIL" -f ($results.Count - $failedLegs.Count), $failedLegs.Count)
exit ($(if ($failedLegs.Count -gt 0) { 1 } else { 0 }))
