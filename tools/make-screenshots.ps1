# Composes the store screenshots (1600x900: headline, subline, framed crop) from raw Revit captures
# taken with tools\capture-revit.ps1. Raw captures and output live in dist\ (gitignored).
#   .\tools\make-screenshots.ps1
param(
    [string]$Raw = "$PSScriptRoot\..\dist\screenshots\v2",
    [string]$Out = "$PSScriptRoot\..\dist\screenshots"
)
Add-Type -AssemblyName System.Drawing
$Raw = (Resolve-Path $Raw).Path
$Out = (Resolve-Path $Out).Path

$W = 1600; $H = 900
$bg     = [Drawing.Color]::FromArgb(240, 244, 249)
$ink    = [Drawing.Color]::FromArgb(22, 41, 69)
$muted  = [Drawing.Color]::FromArgb(84, 99, 120)
$orange = [Drawing.Color]::FromArgb(236, 128, 24)
$slate  = [Drawing.Color]::FromArgb(100, 116, 139)
$border = [Drawing.Color]::FromArgb(203, 213, 225)

function Rect($x, $y, $w, $h) { New-Object Drawing.Rectangle $x, $y, $w, $h }

function Rounded([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function Font([float]$px, [switch]$Bold) {
    New-Object Drawing.Font ($(if ($Bold) { 'Segoe UI Semibold' } else { 'Segoe UI' })), $px, ([Drawing.FontStyle]::Regular), ([Drawing.GraphicsUnit]::Pixel)
}

# Centered headline and subline starting at $top.
function Title($g, [string]$headline, [string]$subline, [float]$top) {
    $fmt = New-Object Drawing.StringFormat
    $fmt.Alignment = [Drawing.StringAlignment]::Center
    $g.DrawString($headline, (Font 46 -Bold), (New-Object Drawing.SolidBrush $ink), (New-Object Drawing.RectangleF 0, $top, $W, 70), $fmt)
    $g.DrawString($subline, (Font 25), (New-Object Drawing.SolidBrush $muted), (New-Object Drawing.RectangleF 0, ($top + 66), $W, 40), $fmt)
}

# Draws $src of $img into a rounded frame with a soft shadow. 1:1 when the sizes match.
function Framed($g, [Drawing.Bitmap]$img, [Drawing.Rectangle]$src, [int]$x, [int]$y, [int]$w, [int]$h) {
    for ($i = 10; $i -ge 1; $i--) {
        $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(5, 15, 23, 42))), (Rounded ($x - $i) ($y - $i + 5) ($w + 2 * $i) ($h + 2 * $i) (12 + $i)))
    }
    $tile = New-Object Drawing.Bitmap $w, $h
    $t = [Drawing.Graphics]::FromImage($tile)
    $t.InterpolationMode = if ($w -eq $src.Width -and $h -eq $src.Height) { 'NearestNeighbor' } else { 'HighQualityBicubic' }
    $t.PixelOffsetMode = 'Half'
    $t.DrawImage($img, (Rect 0 0 $w $h), $src, [Drawing.GraphicsUnit]::Pixel)
    $t.Dispose()
    $brush = New-Object Drawing.TextureBrush $tile
    $brush.TranslateTransform($x, $y)
    $frame = Rounded $x $y $w $h 12
    $g.FillPath($brush, $frame)
    $g.DrawPath((New-Object Drawing.Pen $border, 1), $frame)
    $tile.Dispose()
}

# A label pill whose right edge is at $right.
function Pill($g, [string]$text, [Drawing.Color]$color, [float]$right, [float]$y) {
    $font = Font 19 -Bold
    $w = [math]::Ceiling($g.MeasureString($text, $font).Width) + 26
    $x = $right - $w
    $g.FillPath((New-Object Drawing.SolidBrush $color), (Rounded $x $y $w 34 17))
    $fmt = New-Object Drawing.StringFormat
    $fmt.Alignment = [Drawing.StringAlignment]::Center
    $fmt.LineAlignment = [Drawing.StringAlignment]::Center
    $g.DrawString($text, $font, [Drawing.Brushes]::White, (New-Object Drawing.RectangleF $x, ($y + 1), $w, 34), $fmt)
}

function Canvas {
    $b = New-Object Drawing.Bitmap $W, $H
    $g = [Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear($bg)
    , @($b, $g)
}

function Save($canvas, [string]$name) {
    $canvas[1].Dispose()
    $canvas[0].Save("$Out\$name", [Drawing.Imaging.ImageFormat]::Png)
    $canvas[0].Dispose()
    "$name  $([math]::Round((Get-Item "$Out\$name").Length / 1KB)) KB"
}

function Load([string]$name) { [Drawing.Bitmap]::FromFile("$Raw\$name") }

# 1. Before and after, side by side, scaled to fit.
$before = Load 'raw-before.png'; $after = Load 'raw-after.png'
$src = Rect 630 330 900 600
$pw = 724; $ph = [int]($pw * $src.Height / $src.Width); $gap = 56
$top = [int](($H - (130 + $ph)) / 2)
$c = Canvas; $g = $c[1]
Title $g 'Resize a whole run in one step' 'Pipes, fittings and valves change size together, here from 6" to 8"' $top
$x1 = [int](($W - 2 * $pw - $gap) / 2); $x2 = $x1 + $pw + $gap; $y = $top + 130
Framed $g $before $src $x1 $y $pw $ph
Framed $g $after $src $x2 $y $pw $ph
Pill $g 'BEFORE' $slate ($x1 + $pw - 16) ($y + 16)
Pill $g 'AFTER' $orange ($x2 + $pw - 16) ($y + 16)
$cx = $x1 + $pw + $gap / 2; $cy = $y + $ph / 2
$g.FillEllipse((New-Object Drawing.SolidBrush $orange), ($cx - 26), ($cy - 26), 52, 52)
$pen = New-Object Drawing.Pen ([Drawing.Color]::White), 4
$pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
$g.DrawLine($pen, ($cx - 11), $cy, ($cx + 10), $cy)
$g.DrawLines($pen, [Drawing.PointF[]]@((New-Object Drawing.PointF ($cx + 1), ($cy - 9)), (New-Object Drawing.PointF ($cx + 10), $cy), (New-Object Drawing.PointF ($cx + 1), ($cy + 9))))
Save $c 'RunResize-1-before-after.png'
$before.Dispose(); $after.Dispose()

# 2-4. One 1:1 crop each.
function Single([string]$rawName, [Drawing.Rectangle]$src, [string]$headline, [string]$subline, [string]$name, [scriptblock]$prepare) {
    $img = Load $rawName
    if ($prepare) { & $prepare $img }
    $c = Canvas; $g = $c[1]
    $top = [int](($H - (130 + $src.Height)) / 2)
    Title $g $headline $subline $top
    Framed $g $img $src ([int](($W - $src.Width) / 2)) ($top + 130) $src.Width $src.Height
    Save $c $name
    $img.Dispose()
}

$runArea = Rect 450 315 1120 630
Single 'raw-dialog.png' $runArea 'Select the run, pick a size' 'Valves and other accessories in the selection are resized too' 'RunResize-2-pick-size.png' $null

# The dev build labels its dialogs "RunResize (dev host)". The summary's dialog is taken from the store build's
# capture of the same resize (same size and text), so the title reads as users see it.
Single 'raw-summary-devhost.png' $runArea 'One summary, one undo' 'It counts the transitions Revit adds where the run meets the old size' 'RunResize-3-summary.png' {
    param($img)
    $store = [Drawing.Bitmap]::FromFile("$Out\v1\raw-2-summary.png")
    $g = [Drawing.Graphics]::FromImage($img)
    $g.DrawImage($store, (Rect 526 712 352 164), (Rect 776 399 352 164), [Drawing.GraphicsUnit]::Pixel)
    $g.Dispose(); $store.Dispose()
}

# PrintWindow draws the layered tooltip window (340,96 355x177) with a black frame, so that area comes from a
# screen capture. On screen, the computer-use glow tints everything below the topmost tooltip box near the top
# edge; each row's tint is measured at x=900, where both captures show plain ribbon, and removed.
Single 'raw-tooltip.png' (Rect 0 26 1120 630) 'On the Add-Ins tab' 'Press F1 on the button to open the help page' 'RunResize-4-add-ins-tab.png' {
    param($img)
    $screen = [Drawing.Bitmap]::FromFile("$Raw\raw-tooltip-screen.png")
    for ($y = 96; $y -le 272; $y++) {
        $w = $img.GetPixel(900, $y); $s = $screen.GetPixel(900, $y)
        $dr = $w.R - $s.R; $dg = $w.G - $s.G; $db = $w.B - $s.B
        for ($x = 340; $x -le 694; $x++) {
            $c = $screen.GetPixel($x, $y)
            if (-not ($x -ge 344 -and $x -le 686 -and $y -ge 104 -and $y -le 268)) {
                $c = [Drawing.Color]::FromArgb([math]::Min(255, [math]::Max(0, $c.R + $dr)), [math]::Min(255, [math]::Max(0, $c.G + $dg)), [math]::Min(255, [math]::Max(0, $c.B + $db)))
            }
            $img.SetPixel($x, $y, $c)
        }
    }
    $screen.Dispose()
}
