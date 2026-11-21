#Requires -Version 7
<#
.SYNOPSIS
    A fake local GPU render service for the goal pilot: it takes a while, returns a PNG, can be
    interrupted, and records how many renders ever ran at once.

.DESCRIPTION
    Listens on http://127.0.0.1:<Port>/ until it is killed.
      POST /render?prompt=<text>  waits -RenderSeconds, then answers with a 768x512 PNG that shows the
                                  prompt; answers 409 if an interrupt arrived during the wait.
      POST /interrupt             interrupts the render in progress.
      GET  /stats                 {"renders":n,"interrupts":n,"maxConcurrent":n}
    One request at a time is served, so a second render waits for the first: maxConcurrent above 1
    would mean two callers overlapped inside a render. Fabricated output only; nothing is read.
#>
param(
    [Parameter(Mandatory)][int]$Port,
    [int]$RenderSeconds = 45
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
$stats = @{ renders = 0; interrupts = 0; maxConcurrent = 0 }
$script:interrupted = $false
$script:active = 0

function Send([System.Net.HttpListenerContext]$c, [int]$status, [byte[]]$body, [string]$type) {
    $c.Response.StatusCode = $status
    $c.Response.ContentType = $type
    $c.Response.OutputStream.Write($body, 0, $body.Length)
    $c.Response.Close()
}

function New-Png([string]$prompt) {
    $bmp = [System.Drawing.Bitmap]::new(768, 512)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $rect = [System.Drawing.Rectangle]::new(0, 0, 768, 512)
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect, [System.Drawing.Color]::FromArgb(20, 24, 48), [System.Drawing.Color]::FromArgb(120, 40, 90), 45.0)
    $g.FillRectangle($brush, $rect)
    $font = [System.Drawing.Font]::new('Segoe UI', 28, [System.Drawing.FontStyle]::Bold)
    $format = [System.Drawing.StringFormat]::new(); $format.Alignment = 'Center'; $format.LineAlignment = 'Center'
    $g.DrawString("FAKE RENDER`n$prompt", $font, [System.Drawing.Brushes]::White, [System.Drawing.RectangleF]::new(24, 24, 720, 464), $format)
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    return $ms.ToArray()
}

# The interrupt arrives on a second connection while a render waits, so a render polls for it.
$pending = $listener.GetContextAsync()
while ($listener.IsListening) {
    $c = $pending.GetAwaiter().GetResult()
    $pending = $listener.GetContextAsync()
    $path = $c.Request.Url.AbsolutePath
    if ($path -eq '/stats') { Send $c 200 ([Text.Encoding]::UTF8.GetBytes(($stats | ConvertTo-Json -Compress))) 'application/json'; continue }
    if ($path -eq '/interrupt') { $script:interrupted = $true; $stats.interrupts++; Send $c 200 ([byte[]]@()) 'text/plain'; continue }
    if ($path -ne '/render') { Send $c 404 ([byte[]]@()) 'text/plain'; continue }

    $script:active++
    if ($script:active -gt $stats.maxConcurrent) { $stats.maxConcurrent = $script:active }
    $script:interrupted = $false
    $prompt = [string]$c.Request.QueryString['prompt']
    $until = (Get-Date).AddSeconds($RenderSeconds)
    while ((Get-Date) -lt $until -and -not $script:interrupted) {
        if ($pending.Wait(250)) {
            $other = $pending.GetAwaiter().GetResult()
            $pending = $listener.GetContextAsync()
            switch ($other.Request.Url.AbsolutePath) {
                '/interrupt' { $script:interrupted = $true; $stats.interrupts++; Send $other 200 ([byte[]]@()) 'text/plain' }
                '/stats' { Send $other 200 ([Text.Encoding]::UTF8.GetBytes(($stats | ConvertTo-Json -Compress))) 'application/json' }
                '/render' { $stats.maxConcurrent = [Math]::Max($stats.maxConcurrent, $script:active + 1); Send $other 503 ([Text.Encoding]::UTF8.GetBytes('busy')) 'text/plain' }
                default { Send $other 404 ([byte[]]@()) 'text/plain' }
            }
        }
    }
    $script:active--
    if ($script:interrupted) { Send $c 409 ([Text.Encoding]::UTF8.GetBytes('interrupted')) 'text/plain'; continue }
    $stats.renders++
    Send $c 200 (New-Png $prompt) 'image/png'
}
