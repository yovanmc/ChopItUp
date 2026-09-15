<#
.SYNOPSIS
    Renders src\ChopItUp.Desktop\chopitup.ico — the desktop shell's application, window and tray
    icon (row 12, task 6).

.DESCRIPTION
    The mark is a dark rounded plate (#0e1013, the client's --bg) with a hairline #242a34 edge
    (--line) carrying a speech-bubble "C" in the owner accent #6fb2ff (--accent-owner): a thick
    round-capped ring open to the right, with a bubble tail off the lower left. The C is drawn as
    geometry, never as text, so no font has to be installed and no font substitution can change the
    result on another machine.

    Each layer is drawn at 4x and downsampled bicubically (with TileFlipXY wrapping, which is what
    keeps a transparent halo off the plate's edge), then PNG-encoded. The four layers (256, 48, 32,
    16) are packed into an ICONDIR / ICONDIRENTRY header with PNG payloads.

    The output is byte-stable: the geometry is fixed, GDI+'s PNG encoder writes no tIME chunk, and
    nothing here reads the clock or the environment. Running it twice must yield the same SHA-256;
    that is the whole point of committing the script beside the .ico.

    Re-run it after changing the palette or the mark, then commit the regenerated .ico.

    Exit 0 when the file is written and reloads with all four sizes; 1 when verification fails.

.PARAMETER OutPath
    Where to write the .ico. Defaults to src\ChopItUp.Desktop\chopitup.ico beside this script's repo.

.PARAMETER DumpLargestPng
    Optional path to also write the 256 px layer as a plain .png, for eyeballing the mark.
#>
[CmdletBinding()]
param(
    [string]$OutPath,
    [string]$DumpLargestPng
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not $OutPath) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $OutPath = Join-Path $repoRoot 'src\ChopItUp.Desktop\chopitup.ico'
}
$OutPath = [System.IO.Path]::GetFullPath($OutPath)

# The client palette (src\ChopItUp.Hub\client\src\styles.css, dark theme): --bg, --line,
# --accent-owner. Kept as literals so this script has no build-time dependency on the client.
$Bg = [System.Drawing.Color]::FromArgb(255, 0x0E, 0x10, 0x13)
$Edge = [System.Drawing.Color]::FromArgb(255, 0x24, 0x2A, 0x34)
$Accent = [System.Drawing.Color]::FromArgb(255, 0x6F, 0xB2, 0xFF)

# Largest first: a consumer that reads only the first entry then gets the sharpest one.
$Sizes = @(256, 48, 32, 16)
$Super = 4

function New-RoundedRectPath {
    param([single]$X, [single]$Y, [single]$W, [single]$H, [single]$Radius)
    $d = $Radius * 2
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $p.AddArc($X, $Y, $d, $d, 180, 90)
    $p.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $p.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $p.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Get-PolarPoint {
    param([single]$Cx, [single]$Cy, [single]$R, [double]$Degrees)
    # GDI+ angles: 0 deg at 3 o'clock, growing clockwise because y grows downward.
    $rad = $Degrees * [Math]::PI / 180.0
    return [System.Drawing.PointF]::new([single]($Cx + $R * [Math]::Cos($rad)), [single]($Cy + $R * [Math]::Sin($rad)))
}

function New-IconLayer {
    param([int]$Size)

    $n = $Size * $Super
    $work = [System.Drawing.Bitmap]::new($n, $n, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($work)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # The plate. The edge is a hairline at tray/taskbar sizes and 2 px on the 256 layer, where a
    # single pixel would vanish; it is stroked centred on the path, so the path insets by half of it.
    $edgeW = [single]([Math]::Max(1.0, $Size / 128.0) * $Super)
    $plate = New-RoundedRectPath ($edgeW / 2) ($edgeW / 2) ($n - $edgeW) ($n - $edgeW) ($n * 0.2195)
    $bgBrush = [System.Drawing.SolidBrush]::new($Bg)
    $edgePen = [System.Drawing.Pen]::new($Edge, $edgeW)
    $edgePen.Alignment = [System.Drawing.Drawing2D.PenAlignment]::Center
    $g.FillPath($bgBrush, $plate)
    $g.DrawPath($edgePen, $plate)

    # The speech-bubble C: a ring centred slightly above the plate's middle so the tail's weight
    # does not drag the mark low, open from 322 deg round to 38 deg (the C's mouth, facing right).
    $cx = [single]($n * 0.5)
    $cy = [single]($n * 0.44)
    $rMid = [single]($n * 0.225)
    $stroke = [single]($n * 0.105)
    $accentBrush = [System.Drawing.SolidBrush]::new($Accent)
    $accentPen = [System.Drawing.Pen]::new($Accent, $stroke)
    $accentPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $accentPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($accentPen, ($cx - $rMid), ($cy - $rMid), ($rMid * 2), ($rMid * 2), 38, 284)

    # The tail. Its base sits on the ring's centre line, so the stroke swallows the seam.
    $tail = [System.Drawing.PointF[]]@(
        (Get-PolarPoint $cx $cy $rMid 112),
        (Get-PolarPoint $cx $cy ($rMid * 1.62) 133),
        (Get-PolarPoint $cx $cy $rMid 146)
    )
    $g.FillPolygon($accentBrush, $tail)

    # Downsample. WrapMode=TileFlipXY stops the bicubic kernel from sampling transparent pixels
    # beyond the edges, which otherwise leaves a faint halo around the plate.
    $final = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $fg = [System.Drawing.Graphics]::FromImage($final)
    $fg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $fg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $fg.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $fg.Clear([System.Drawing.Color]::Transparent)
    $attr = [System.Drawing.Imaging.ImageAttributes]::new()
    $attr.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
    $fg.DrawImage($work, [System.Drawing.Rectangle]::new(0, 0, $Size, $Size), 0, 0, $n, $n,
        [System.Drawing.GraphicsUnit]::Pixel, $attr)

    $ms = [System.IO.MemoryStream]::new()
    $final.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()

    $ms.Dispose(); $attr.Dispose(); $fg.Dispose(); $final.Dispose()
    $accentPen.Dispose(); $accentBrush.Dispose(); $edgePen.Dispose(); $bgBrush.Dispose()
    $plate.Dispose(); $g.Dispose(); $work.Dispose()

    return [pscustomobject]@{ Size = $Size; Bytes = $bytes }
}

$layers = @(foreach ($s in $Sizes) { New-IconLayer -Size $s })

# ICONDIR, then one ICONDIRENTRY per layer, then the PNG payloads. bWidth/bHeight are 0 for 256
# (the field is a byte); bColorCount 0 and wBitCount 32 say "truecolour + alpha, no palette".
$out = [System.IO.MemoryStream]::new()
$w = [System.IO.BinaryWriter]::new($out)
$w.Write([uint16]0)
$w.Write([uint16]1)
$w.Write([uint16]$layers.Count)
$offset = 6 + 16 * $layers.Count
foreach ($l in $layers) {
    $w.Write([byte]($l.Size % 256))
    $w.Write([byte]($l.Size % 256))
    $w.Write([byte]0)
    $w.Write([byte]0)
    $w.Write([uint16]1)
    $w.Write([uint16]32)
    $w.Write([uint32]$l.Bytes.Length)
    $w.Write([uint32]$offset)
    $offset += $l.Bytes.Length
}
foreach ($l in $layers) { $w.Write($l.Bytes) }
$w.Flush()
$ico = $out.ToArray()
$w.Dispose(); $out.Dispose()

$dir = Split-Path -Parent $OutPath
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
[System.IO.File]::WriteAllBytes($OutPath, $ico)

if ($DumpLargestPng) {
    [System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($DumpLargestPng), $layers[0].Bytes)
}

# Verify the file that was just written by reparsing it from disk: the directory must describe the
# four layers, and each payload must be a PNG that decodes at exactly the size its entry declares.
#
# The 256 layer is checked by decoding its payload rather than by asking Icon for it:
# System.Drawing.Icon's managed best-fit search does not map an entry's bWidth of 0 back to 256, so
# Icon(path, 256, 256) hands back the 48 layer (measured here, 2026-09-15). That is a limitation of
# that class; the consumers that matter read the entry correctly (the shell reads the exe's Win32
# icon resource, WPF's Window.Icon goes through WIC, and the tray asks for the small size).
$problems = @()
$raw = [System.IO.File]::ReadAllBytes($OutPath)
if ([BitConverter]::ToUInt16($raw, 0) -ne 0 -or [BitConverter]::ToUInt16($raw, 2) -ne 1) {
    $problems += 'ICONDIR reserved/type is not 0/1.'
}
$count = [int][BitConverter]::ToUInt16($raw, 4)
if ($count -ne $Sizes.Count) { $problems += "ICONDIR declares $count entries, expected $($Sizes.Count)." }

$declaredSizes = @()
for ($k = 0; $k -lt $count; $k++) {
    $o = 6 + 16 * $k
    $declared = if ($raw[$o] -eq 0) { 256 } else { [int]$raw[$o] }
    $declaredSizes += $declared
    $len = [int][BitConverter]::ToUInt32($raw, $o + 8)
    $off = [int][BitConverter]::ToUInt32($raw, $o + 12)
    if ($off + $len -gt $raw.Length) { $problems += "entry $k ($declared) runs past the end of the file."; continue }
    $payload = [byte[]]::new($len)
    [Array]::Copy($raw, $off, $payload, 0, $len)
    if ($payload[0] -ne 0x89 -or $payload[1] -ne 0x50 -or $payload[2] -ne 0x4E -or $payload[3] -ne 0x47) {
        $problems += "entry $k ($declared) is not PNG-encoded."
        continue
    }
    $ms = [System.IO.MemoryStream]::new($payload)
    $bmp = [System.Drawing.Bitmap]::FromStream($ms)
    if ($bmp.Width -ne $declared -or $bmp.Height -ne $declared) {
        $problems += "entry $k declares $declared but decodes to $($bmp.Width)x$($bmp.Height)."
    }
    $bmp.Dispose(); $ms.Dispose()
}

# And the whole file through the icon loader the tray will use.
$loaded = [System.Drawing.Icon]::new($OutPath)
$defaultSize = "$($loaded.Width)x$($loaded.Height)"
$loaded.Dispose()
$small = [System.Drawing.Icon]::new($OutPath, 16, 16)
if ($small.Width -ne 16 -or $small.Height -ne 16) { $problems += "the tray's 16x16 request came back as $($small.Width)x$($small.Height)." }
$small.Dispose()

$hash = (Get-FileHash -LiteralPath $OutPath -Algorithm SHA256).Hash
Write-Host "wrote   $OutPath"
Write-Host "layers  $(($layers | ForEach-Object { "$($_.Size)=$($_.Bytes.Length)B" }) -join ' ')"
Write-Host "entries $($declaredSizes -join ', ') (Icon(path) picks $defaultSize)"
Write-Host "bytes   $($ico.Length)"
Write-Host "sha256  $hash"

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Warning $_ }
    Write-Host 'FAILED verification'
    exit 1
}
exit 0
