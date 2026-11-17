<#
.SYNOPSIS
  Regenerates assets/WinWallpaper.ico (rounded gradient tile, white mountains + sun).
  Only needed when the icon design changes; the .ico is committed.
#>
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$sizes = 16, 20, 24, 32, 40, 48, 64, 256

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"
    $r = $size * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($size - $r * 2 - 1, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($size - $r * 2 - 1, $size - $r * 2 - 1, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $size - $r * 2 - 1, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($size, $size)), ([System.Drawing.Color]::FromArgb(0x4F, 0x7C, 0xFF)), ([System.Drawing.Color]::FromArgb(0xB4, 0x4C, 0xFF))
    $g.FillPath($brush, $path)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(240, 255, 255, 255))
    $g.FillEllipse($white, $size * 0.62, $size * 0.2, $size * 0.17, $size * 0.17)
    $pts = @(@(0.14, 0.80), @(0.40, 0.42), @(0.56, 0.64), @(0.66, 0.52), @(0.86, 0.80)) | ForEach-Object { [System.Drawing.PointF]::new($size * $_[0], $size * $_[1]) }
    $g.FillPolygon($white, [System.Drawing.PointF[]]$pts)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO = header + directory + PNG-compressed images (supported since Windows Vista).
$images = $sizes | ForEach-Object { , (Draw $_) }
$ms = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ms
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256))   # 256 is stored as 0
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset); $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
New-Item -ItemType Directory -Force "$root/assets" | Out-Null
[System.IO.File]::WriteAllBytes("$root/assets/WinWallpaper.ico", $ms.ToArray())
"wrote assets/WinWallpaper.ico ($($ms.Length) bytes, sizes $($sizes -join ', '))"
