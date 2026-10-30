# Draws the Resize Run ribbon icons: a pipe turning through an elbow, with arrows pushing its diameter outward.
# Run from Windows PowerShell: .\tools\make-icons.ps1  (writes RunResize\Resources\ResizeRun16.png and ResizeRun32.png)
param([string]$OutDir = (Join-Path $PSScriptRoot '..\RunResize\Resources'))

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

$outline = [System.Drawing.Color]::FromArgb(255, 30, 78, 140)
$body = [System.Drawing.Color]::FromArgb(255, 74, 144, 217)
$arrow = [System.Drawing.Color]::FromArgb(255, 236, 128, 24)

function Point([double]$x, [double]$y) { New-Object System.Drawing.PointF ([single]$x), ([single]$y) }

function New-Canvas([int]$size) {
    New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
}

# WPF sizes an image by its DPI. GDI+ stores 96 dpi as 3779 pixels per metre, which reads back as 95.99 dpi and makes
# the icon a fraction larger than its pixels, so the DPI chunk (pHYs) is dropped and WPF assumes exactly 96.
function Save-Png([System.Drawing.Bitmap]$bitmap, [string]$name) {
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    $bytes = $stream.ToArray()
    $png = New-Object System.IO.MemoryStream
    $png.Write($bytes, 0, 8)
    for ($i = 8; $i -lt $bytes.Length; $i += 12 + $length) {
        $length = ([int]$bytes[$i] -shl 24) -bor ([int]$bytes[$i + 1] -shl 16) -bor ([int]$bytes[$i + 2] -shl 8) -bor $bytes[$i + 3]
        if ([System.Text.Encoding]::ASCII.GetString($bytes, $i + 4, 4) -ne 'pHYs') { $png.Write($bytes, $i, 12 + $length) }
    }
    [System.IO.File]::WriteAllBytes((Join-Path $OutDir $name), $png.ToArray())
}

# 32 px and up: drawn on a 32-unit grid and scaled. At 32 px the pipe's edges fall on whole pixels.
function Save-Drawn([int]$size, [string]$name) {
    $bitmap = New-Canvas $size
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'Half' # pixel n covers n to n+1, so whole-number edges come out sharp
    $g.ScaleTransform($size / 32.0, $size / 32.0)

    $run = [System.Drawing.PointF[]]@((Point (-1) 15), (Point 25 15), (Point 25 33))
    foreach ($stroke in @(@($outline, 10), @($body, 6))) {
        $pen = New-Object System.Drawing.Pen $stroke[0], $stroke[1]
        $pen.LineJoin = 'Round'
        $g.DrawLines($pen, $run)
        $pen.Dispose()
    }

    $brush = New-Object System.Drawing.SolidBrush $arrow
    $g.FillPolygon($brush, [System.Drawing.PointF[]]@((Point 10 1), (Point 17 8), (Point 3 8)))
    $g.FillPolygon($brush, [System.Drawing.PointF[]]@((Point 10 29), (Point 17 22), (Point 3 22)))
    $brush.Dispose()

    $g.Dispose()
    Save-Png $bitmap $name
}

# 16 px: set pixel by pixel, because anti-aliasing blurs a drawing this small.
# o = pipe outline, b = pipe body, a = arrow, . = transparent.
function Save-Pixels([string[]]$rows, [string]$name) {
    $colors = @{ 'o' = $outline; 'b' = $body; 'a' = $arrow }
    $bitmap = New-Canvas $rows.Count
    for ($y = 0; $y -lt $rows.Count; $y++) {
        for ($x = 0; $x -lt $rows[$y].Length; $x++) {
            $c = [string]$rows[$y][$x]
            if ($colors.ContainsKey($c)) { $bitmap.SetPixel($x, $y, $colors[$c]) }
        }
    }
    Save-Png $bitmap $name
}

Save-Pixels @(
    '....aa..........'
    '...aaaa.........'
    '..aaaaaa........'
    '.aaaaaaaa.......'
    '................'
    'oooooooooooooo..'
    'bbbbbbbbbbbbbbo.'
    'bbbbbbbbbbbbbbo.'
    'bbbbbbbbbbbbbbo.'
    'ooooooooooobbbo.'
    '..........obbbo.'
    '.aaaaaaaa.obbbo.'
    '..aaaaaa..obbbo.'
    '...aaaa...obbbo.'
    '....aa....obbbo.'
    '..........obbbo.'
) 'ResizeRun16.png'
Save-Drawn 32 'ResizeRun32.png'
