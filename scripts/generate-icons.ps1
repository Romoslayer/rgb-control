# Generates the app icons (a rainbow ring, and a gray ring for "lighting off") as multi-size .ico files.
# Output: src/RgbControl.App/Assets/RgbControl.ico and RgbControl-off.ico
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$root   = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'src\RgbControl.App\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function New-RingBitmap([int]$size, [bool]$lit) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $thickness = [Math]::Max(2.0, $size * 0.2)
    $inset = $thickness / 2 + [Math]::Max(0.5, $size * 0.04)
    $rect = New-Object System.Drawing.RectangleF $inset, $inset, ($size - 2 * $inset), ($size - 2 * $inset)

    $segments = 48
    for ($i = 0; $i -lt $segments; $i++) {
        if ($lit) {
            $color = Convert-Hue ($i * 360.0 / $segments)
        } else {
            $color = [System.Drawing.Color]::FromArgb(255, 128, 128, 128)
        }
        $pen = New-Object System.Drawing.Pen $color, $thickness
        # Overlap segments slightly so no seams show.
        $g.DrawArc($pen, $rect, [single](-90 + $i * 360.0 / $segments), [single](360.0 / $segments + 1.5))
        $pen.Dispose()
    }

    $g.Dispose()
    return $bitmap
}

function Convert-Hue([double]$hue) {
    $x = 1 - [Math]::Abs((($hue / 60) % 2) - 1)
    switch ([Math]::Floor($hue / 60)) {
        0 { $r, $gr, $b = 1, $x, 0 }
        1 { $r, $gr, $b = $x, 1, 0 }
        2 { $r, $gr, $b = 0, 1, $x }
        3 { $r, $gr, $b = 0, $x, 1 }
        4 { $r, $gr, $b = $x, 0, 1 }
        default { $r, $gr, $b = 1, 0, $x }
    }
    return [System.Drawing.Color]::FromArgb(255, [int]($r * 255), [int]($gr * 255), [int]($b * 255))
}

# Uncompressed 32bpp DIB entry (bottom-up BGRA + empty AND mask); best compatibility for small sizes.
function Get-DibBytes([System.Drawing.Bitmap]$bitmap) {
    $size = $bitmap.Width
    $maskRow = [int]([Math]::Ceiling($size / 32.0) * 4)
    $stream = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $stream
    $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2))
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]0)
    $w.Write([int]($size * $size * 4 + $maskRow * $size))
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $p = $bitmap.GetPixel($x, $y)
            $w.Write([byte]$p.B); $w.Write([byte]$p.G); $w.Write([byte]$p.R); $w.Write([byte]$p.A)
        }
    }
    $w.Write((New-Object byte[] ($maskRow * $size)))
    $w.Flush()
    return , $stream.ToArray()
}

function Write-Icon([string]$path, [bool]$lit) {
    $sizes = 16, 20, 24, 32, 40, 48, 64, 256
    $images = New-Object 'System.Collections.Generic.List[byte[]]'
    foreach ($size in $sizes) {
        $bitmap = New-RingBitmap $size $lit
        if ($size -eq 256) {
            $png = New-Object System.IO.MemoryStream
            $bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
            $images.Add($png.ToArray())
        } else {
            $images.Add([byte[]](Get-DibBytes $bitmap))
        }
        $bitmap.Dispose()
    }

    $file = [System.IO.File]::Create($path)
    $w = New-Object System.IO.BinaryWriter $file
    $w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([int16]1); $w.Write([int16]32)
        $w.Write([int]$images[$i].Length); $w.Write([int]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $w.Write($image) }
    $w.Dispose()
    Write-Host "Wrote $path"
}

Write-Icon (Join-Path $assets 'RgbControl.ico') $true
Write-Icon (Join-Path $assets 'RgbControl-off.ico') $false
