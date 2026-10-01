# Draws the app icon (assets\watchdog.ico): a teal shield with a check mark, at the sizes Windows uses.
# Only needed when changing the icon; the .ico is checked in.
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function Point($x, $y) { New-Object System.Drawing.PointF ([single]$x), ([single]$y) }

$images = foreach ($size in $sizes) {
    $k = $size / 24.0   # drawn on a 24-unit grid, like the shield in MainWindow.xaml
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

    $shield = New-Object System.Drawing.Drawing2D.GraphicsPath
    $shield.AddLine((Point (12*$k) (1.5*$k)), (Point (21*$k) (4.8*$k)))
    $shield.AddBezier((Point (21*$k) (4.8*$k)), (Point (21*$k) (13*$k)), (Point (17.5*$k) (19*$k)), (Point (12*$k) (22.5*$k)))
    $shield.AddBezier((Point (12*$k) (22.5*$k)), (Point (6.5*$k) (19*$k)), (Point (3*$k) (13*$k)), (Point (3*$k) (4.8*$k)))
    $shield.CloseFigure()

    $navy = [System.Drawing.Color]::FromArgb(255, 0x0B, 0x18, 0x30)
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush((Point 0 0), (Point 0 $size),
        [System.Drawing.Color]::FromArgb(255, 0x5C, 0xF2, 0xC6), [System.Drawing.Color]::FromArgb(255, 0x1F, 0xB8, 0x8E))
    $g.FillPath($fill, $shield)
    $g.DrawPath((New-Object System.Drawing.Pen $navy, ([Math]::Max(1, 1.2 * $k))), $shield)

    $check = New-Object System.Drawing.Pen $navy, ([Math]::Max(1.3, 2.4 * $k))
    $check.StartCap = $check.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $check.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawLines($check, [System.Drawing.PointF[]]@((Point (7.5*$k) (12*$k)), (Point (10.8*$k) (15.2*$k)), (Point (16.5*$k) (8.8*$k))))
    $g.Dispose()

    $png = New-Object System.IO.MemoryStream
    $bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    , $png.ToArray()
}

# ICO file: a header, one directory entry per size, then the PNG images.
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i] % 256   # 256 is written as 0
    $w.Write([byte]$size); $w.Write([byte]$size); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $w.Write($image) }
$w.Flush()

$target = Join-Path $PSScriptRoot '..\assets\watchdog.ico'
New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
[IO.File]::WriteAllBytes([IO.Path]::GetFullPath($target), $ico.ToArray())
"Wrote $([IO.Path]::GetFullPath($target))"
