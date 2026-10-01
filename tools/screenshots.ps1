# Renders the README images into docs\ from the built exe, using made-up data (run build.ps1 first).
#   powershell -ExecutionPolicy Bypass -File tools\screenshots.ps1
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot
$exe = Join-Path $root 'bin\WindscribeWatchdog.exe'
$docs = Join-Path $root 'docs'
New-Item -ItemType Directory -Force $docs | Out-Null

$states = 'connected', 'fixing', 'paused', 'problem'
foreach ($state in $states) {
    Start-Process $exe -ArgumentList '--preview', "`"$(Join-Path $docs "$state.png")`"", $state -Wait
}

# All four side by side, at half size.
$images = $states | ForEach-Object { [System.Drawing.Image]::FromFile((Join-Path $docs "$_.png")) }
$width = [int]($images[0].Width / 2)
$height = [int](($images | Measure-Object -Property Height -Maximum).Maximum / 2)
$sheet = New-Object System.Drawing.Bitmap ($width * $images.Count), $height
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
for ($i = 0; $i -lt $images.Count; $i++) {
    $g.DrawImage($images[$i], $i * $width, 0, $width, [int]($images[$i].Height / 2))
}
$g.Dispose()
$images | ForEach-Object { $_.Dispose() }
$sheet.Save((Join-Path $docs 'states.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$sheet.Dispose()
Get-ChildItem $docs -Filter *.png | ForEach-Object { $_.Name }
