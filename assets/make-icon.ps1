# Draws the app icon (a sound line that splits in two) and writes:
#   assets\app.ico      - Windows icon, sizes 16..256, drawn per size so small sizes stay crisp
#   assets\AppIcon.png  - 1024 px master for the macOS .app icon
# Run once:  powershell -ExecutionPolicy Bypass -File assets\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function New-IconBitmap([int]$size, [double]$inset) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)

    # rounded square, deep blue gradient
    $x = $size * $inset; $w = $size - 2 * $x; $r = $w * 0.225
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $x, 2*$r, 2*$r, 180, 90)
    $path.AddArc($x + $w - 2*$r, $x, 2*$r, 2*$r, 270, 90)
    $path.AddArc($x + $w - 2*$r, $x + $w - 2*$r, 2*$r, 2*$r, 0, 90)
    $path.AddArc($x, $x + $w - 2*$r, 2*$r, 2*$r, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF ([single]$x), ([single]$x), ([single]$w), ([single]$w)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 0x3B, 0xA9, 0xFF)), ([System.Drawing.Color]::FromArgb(255, 0x00, 0x4E, 0xA8)), 90
    $g.FillPath($bg, $path)

    # glyph: one line in, two lines out
    $u = $w / 100.0
    function P([double]$px, [double]$py) { New-Object System.Drawing.PointF ([single]($x + $px * $u)), ([single]($x + $py * $u)) }
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single]([Math]::Max(1.6, 8.5 * $u)))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $g.DrawLine($pen, (P 20 50), (P 44 50))
    $g.DrawBezier($pen, (P 44 50), (P 58 50), (P 60 30), (P 76 30))
    $g.DrawBezier($pen, (P 44 50), (P 58 50), (P 60 70), (P 76 70))
    $dot = [Math]::Max(1.2, 6.5 * $u)
    $g.FillEllipse([System.Drawing.Brushes]::White, [single]($x + 78*$u - $dot), [single]($x + 30*$u - $dot), [single](2*$dot), [single](2*$dot))
    $g.FillEllipse([System.Drawing.Brushes]::White, [single]($x + 78*$u - $dot), [single]($x + 70*$u - $dot), [single](2*$dot), [single](2*$dot))
    $g.Dispose()
    return $bmp
}

# --- Windows .ico (PNG-compressed entries)
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $b = New-IconBitmap $s ($(if ($s -le 24) { 0.02 } else { 0.05 }))
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
    ,$ms.ToArray()
}
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$d.Length); $bw.Write([UInt32]$offset)
    $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
[System.IO.File]::WriteAllBytes((Join-Path $here 'app.ico'), $out.ToArray())

# --- macOS master (Apple grid: artwork inside ~80% of the canvas)
$m = New-IconBitmap 1024 0.1
$m.Save((Join-Path $here 'AppIcon.png'), [System.Drawing.Imaging.ImageFormat]::Png); $m.Dispose()
"Wrote app.ico and AppIcon.png"
