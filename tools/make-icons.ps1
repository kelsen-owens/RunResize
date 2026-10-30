# Draws the Resize Run icons: a pipe turning through an elbow, with arrows pushing its diameter outward.
# Run from Windows PowerShell: .\tools\make-icons.ps1
# Writes the ribbon icons RunResize\Resources\ResizeRun16.png and ResizeRun32.png, and the store's listing icon store\icon-120.png.
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\RunResize\Resources'),
    [string]$StoreDir = (Join-Path $PSScriptRoot '..\store')
)

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir, $StoreDir | Out-Null

$outline = [System.Drawing.Color]::FromArgb(255, 30, 78, 140)
$body = [System.Drawing.Color]::FromArgb(255, 74, 144, 217)
$arrow = [System.Drawing.Color]::FromArgb(255, 236, 128, 24)

function Point([double]$x, [double]$y) { New-Object System.Drawing.PointF ([single]$x), ([single]$y) }

function New-Canvas([int]$size) {
    New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
}

function New-Graphics([System.Drawing.Bitmap]$bitmap) {
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'Half' # pixel n covers n to n+1, so whole-number edges come out sharp
    $g
}

# WPF sizes an image by its DPI. GDI+ stores 96 dpi as 3779 pixels per metre, which reads back as 95.99 dpi and makes
# the icon a fraction larger than its pixels, so the DPI chunk (pHYs) is dropped and WPF assumes exactly 96.
function Save-Png([System.Drawing.Bitmap]$bitmap, [string]$path) {
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
    [System.IO.File]::WriteAllBytes($path, $png.ToArray())
}

# The drawing, on a 32-unit grid. $reach runs the pipe further past the grid's edges, for a drawing clipped by a frame.
function Draw-Icon([System.Drawing.Graphics]$g, [double]$reach) {
    $run = [System.Drawing.PointF[]]@((Point (-1 - $reach) 15), (Point 25 15), (Point 25 (33 + $reach)))
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
}

# 32 px: the drawing at full size. At 32 px the pipe's edges fall on whole pixels.
function Save-Drawn([int]$size, [string]$path) {
    $bitmap = New-Canvas $size
    $g = New-Graphics $bitmap
    $g.ScaleTransform($size / 32.0, $size / 32.0)
    Draw-Icon $g 0
    $g.Dispose()
    Save-Png $bitmap $path
}

# The store's listing icon: the drawing on a framed tile, since the store turns down icons without a border.
function Save-StoreIcon([int]$size, [string]$path) {
    $k = $size / 120.0
    $bitmap = New-Canvas $size
    $g = New-Graphics $bitmap
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $x = [single](1 * $k); $far = [single]($size - 1 * $k); $d = [single](36 * $k)
    $tile.AddArc($x, $x, $d, $d, 180, 90)
    $tile.AddArc([single]($far - $d), $x, $d, $d, 270, 90)
    $tile.AddArc([single]($far - $d), [single]($far - $d), $d, $d, 0, 90)
    $tile.AddArc($x, [single]($far - $d), $d, $d, 90, 90)
    $tile.CloseFigure()
    $g.FillPath([System.Drawing.Brushes]::White, $tile)
    $g.SetClip($tile)
    $g.TranslateTransform(12 * $k, 12 * $k)
    $g.ScaleTransform(3 * $k, 3 * $k)
    Draw-Icon $g 8
    $g.ResetTransform()
    $g.ResetClip()
    $frame = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 169, 184, 202)), ([single](2 * $k))
    $g.DrawPath($frame, $tile)
    $frame.Dispose()
    $g.Dispose()
    Save-Png $bitmap $path
}

# 16 px: set pixel by pixel, because anti-aliasing blurs a drawing this small.
# o = pipe outline, b = pipe body, a = arrow, . = transparent.
function Save-Pixels([string[]]$rows, [string]$path) {
    $colors = @{ 'o' = $outline; 'b' = $body; 'a' = $arrow }
    $bitmap = New-Canvas $rows.Count
    for ($y = 0; $y -lt $rows.Count; $y++) {
        for ($x = 0; $x -lt $rows[$y].Length; $x++) {
            $c = [string]$rows[$y][$x]
            if ($colors.ContainsKey($c)) { $bitmap.SetPixel($x, $y, $colors[$c]) }
        }
    }
    Save-Png $bitmap $path
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
) (Join-Path $OutDir 'ResizeRun16.png')
Save-Drawn 32 (Join-Path $OutDir 'ResizeRun32.png')
Save-StoreIcon 120 (Join-Path $StoreDir 'icon-120.png')
