<#
.SYNOPSIS
    Generates crisp multi-resolution SolidWorks-style PNG icon strips and FeatureManager tree icons
    for SolidWorks3DPrintDFMAddin and SolidWorksJoineryAddin.
#>
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = Split-Path -Parent $scriptDir

$dfmIconsDir = Join-Path $repoRoot "src\SolidWorks3DPrintDFMAddin\Resources\Icons"
$joineryIconsDir = Join-Path $repoRoot "src\SolidWorksJoineryAddin\Resources\Icons"

New-Item -ItemType Directory -Path $dfmIconsDir -Force | Out-Null
New-Item -ItemType Directory -Path $joineryIconsDir -Force | Out-Null

function Get-Palette([string]$mode) {
    switch ($mode) {
        "suppressed" {
            return @{
                Outline    = [System.Drawing.Color]::FromArgb(255, 130, 142, 158)
                CyanFill   = [System.Drawing.Color]::FromArgb(255, 203, 213, 225)
                CyanLight  = [System.Drawing.Color]::FromArgb(255, 226, 232, 240)
                AmberFill  = [System.Drawing.Color]::FromArgb(255, 180, 190, 204)
                AmberDark  = [System.Drawing.Color]::FromArgb(255, 140, 152, 168)
                Cutout     = [System.Drawing.Color]::FromArgb(255, 248, 250, 252)
                Highlight  = [System.Drawing.Color]::Transparent
            }
        }
        "highlight" {
            return @{
                Outline    = [System.Drawing.Color]::FromArgb(255, 3, 105, 161)
                CyanFill   = [System.Drawing.Color]::FromArgb(255, 56, 189, 248)
                CyanLight  = [System.Drawing.Color]::FromArgb(255, 186, 230, 253)
                AmberFill  = [System.Drawing.Color]::FromArgb(255, 251, 191, 36)
                AmberDark  = [System.Drawing.Color]::FromArgb(255, 180, 83, 9)
                Cutout     = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
                Highlight  = [System.Drawing.Color]::FromArgb(90, 56, 189, 248)
            }
        }
        default {
            return @{
                Outline    = [System.Drawing.Color]::FromArgb(255, 45, 55, 72)
                CyanFill   = [System.Drawing.Color]::FromArgb(255, 56, 189, 248)
                CyanLight  = [System.Drawing.Color]::FromArgb(255, 186, 230, 253)
                AmberFill  = [System.Drawing.Color]::FromArgb(255, 245, 158, 11)
                AmberDark  = [System.Drawing.Color]::FromArgb(255, 180, 83, 9)
                Cutout     = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
                Highlight  = [System.Drawing.Color]::Transparent
            }
        }
    }
}

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Max(1.0, 2.0 * $r)
    $gp.AddArc($x, $y, $d, $d, 180, 90)
    $gp.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $gp.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $gp.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $gp.CloseFigure()
    return $gp
}

function Get-StrokeWidth([int]$size) {
    return [float][Math]::Max(1.1, $size * 0.062)
}

function Draw-HighlightHalo([System.Drawing.Graphics]$g, [float]$xOff, [int]$size, $pal) {
    if ($pal.Highlight.A -eq 0) { return }
    $brush = New-Object System.Drawing.SolidBrush($pal.Highlight)
    $gp = New-RoundedRectPath ($xOff + $size * 0.04) ($size * 0.04) ($size * 0.92) ($size * 0.92) ($size * 0.14)
    $g.FillPath($brush, $gp)
    $gp.Dispose()
    $brush.Dispose()
}

function Draw-TeardropIcon([System.Drawing.Graphics]$g, [float]$xOff, [int]$size, [string]$mode) {
    $pal = Get-Palette $mode
    Draw-HighlightHalo $g $xOff $size $pal
    $sw = Get-StrokeWidth $size
    $pen = New-Object System.Drawing.Pen($pal.Outline, $sw)
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    # Outer cyan block
    $block = New-RoundedRectPath ($xOff + $size * 0.12) ($size * 0.14) ($size * 0.76) ($size * 0.74) ($size * 0.09)
    $cyanBrush = New-Object System.Drawing.SolidBrush($pal.CyanFill)
    $g.FillPath($cyanBrush, $block)
    $g.DrawPath($pen, $block)
    $block.Dispose()
    $cyanBrush.Dispose()

    # Teardrop cutout + amber crown relief
    $cx = $xOff + $size * 0.50
    $cy = $size * 0.60
    $r = $size * 0.21
    $alpha = 40.0 * [Math]::PI / 180.0
    $dx = $r * [Math]::Sin($alpha)
    $dy = $r * [Math]::Cos($alpha)
    $capY = $cy - $r * 1.55
    $capHalfW = $dx * 0.40

    # 1. White circular bore
    $cutBrush = New-Object System.Drawing.SolidBrush($pal.Cutout)
    $g.FillEllipse($cutBrush, [float]($cx - $r), [float]($cy - $r), [float](2 * $r), [float](2 * $r))
    $cutBrush.Dispose()

    # 2. Amber crown relief above circle
    $crown = New-Object System.Drawing.Drawing2D.GraphicsPath
    $crown.AddLine([float]($cx - $dx), [float]($cy - $dy), [float]($cx - $capHalfW), [float]$capY)
    $crown.AddLine([float]($cx - $capHalfW), [float]$capY, [float]($cx + $capHalfW), [float]$capY)
    $crown.AddLine([float]($cx + $capHalfW), [float]$capY, [float]($cx + $dx), [float]($cy - $dy))
    $crown.CloseFigure()

    $amberBrush = New-Object System.Drawing.SolidBrush($pal.AmberFill)
    $g.FillPath($amberBrush, $crown)
    $amberBrush.Dispose()

    # 3. Single outer teardrop outline (lower circular arc + crown roof, no thick horizontal bar)
    $startDeg = 40.0 - 90.0
    $sweepDeg = 360.0 - 80.0
    $outlinePath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $outlinePath.AddArc([float]($cx - $r), [float]($cy - $r), [float](2 * $r), [float](2 * $r), [float]$startDeg, [float]$sweepDeg)
    $outlinePath.AddLine([float]($cx - $dx), [float]($cy - $dy), [float]($cx - $capHalfW), [float]$capY)
    $outlinePath.AddLine([float]($cx - $capHalfW), [float]$capY, [float]($cx + $capHalfW), [float]$capY)
    $outlinePath.AddLine([float]($cx + $capHalfW), [float]$capY, [float]($cx + $dx), [float]($cy - $dy))
    $outlinePath.CloseFigure()
    $g.DrawPath($pen, $outlinePath)
    $outlinePath.Dispose()
    $crown.Dispose()
    $pen.Dispose()
}

function Draw-BridgeIcon([System.Drawing.Graphics]$g, [float]$xOff, [int]$size, [string]$mode) {
    $pal = Get-Palette $mode
    Draw-HighlightHalo $g $xOff $size $pal
    $sw = Get-StrokeWidth $size
    $pen = New-Object System.Drawing.Pen($pal.Outline, $sw)
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $cx = $xOff + $size * 0.50
    $cy = $size * 0.50
    $rOut = $size * 0.40
    $rRecess = $size * 0.30

    # Outer cyan counterbore ring
    $cyanBrush = New-Object System.Drawing.SolidBrush($pal.CyanFill)
    $g.FillEllipse($cyanBrush, [float]($cx - $rOut), [float]($cy - $rOut), [float](2 * $rOut), [float](2 * $rOut))
    $g.DrawEllipse($pen, [float]($cx - $rOut), [float]($cy - $rOut), [float](2 * $rOut), [float](2 * $rOut))
    $cyanBrush.Dispose()

    # White recessed pocket interior
    $cutBrush = New-Object System.Drawing.SolidBrush($pal.Cutout)
    $g.FillEllipse($cutBrush, [float]($cx - $rRecess), [float]($cy - $rRecess), [float](2 * $rRecess), [float](2 * $rRecess))
    $g.DrawEllipse($pen, [float]($cx - $rRecess), [float]($cy - $rRecess), [float](2 * $rRecess), [float](2 * $rRecess))

    # Tier 1 vertical cyan bridge chords (spanning across recess)
    $tier1Brush = New-Object System.Drawing.SolidBrush($pal.CyanLight)
    $barW = $size * 0.22
    $barH = $size * 0.54
    $g.FillRectangle($tier1Brush, [float]($cx - $barW * 0.5), [float]($cy - $barH * 0.5), [float]$barW, [float]$barH)
    $g.DrawRectangle($pen, [float]($cx - $barW * 0.5), [float]($cy - $barH * 0.5), [float]$barW, [float]$barH)
    $tier1Brush.Dispose()

    # Tier 2 horizontal amber bridge bars
    $amberBrush = New-Object System.Drawing.SolidBrush($pal.AmberFill)
    $g.FillRectangle($amberBrush, [float]($cx - $barH * 0.5), [float]($cy - $barW * 0.5), [float]$barH, [float]$barW)
    $g.DrawRectangle($pen, [float]($cx - $barH * 0.5), [float]($cy - $barW * 0.5), [float]$barH, [float]$barW)
    $amberBrush.Dispose()

    # Inner shaft hole
    $rIn = $size * 0.095
    $g.FillEllipse($cutBrush, [float]($cx - $rIn), [float]($cy - $rIn), [float](2 * $rIn), [float](2 * $rIn))
    $g.DrawEllipse($pen, [float]($cx - $rIn), [float]($cy - $rIn), [float](2 * $rIn), [float](2 * $rIn))

    $cutBrush.Dispose()
    $pen.Dispose()
}

function Draw-DfmMainIcon([System.Drawing.Graphics]$g, [float]$xOff, [int]$size, [string]$mode) {
    $pal = Get-Palette $mode
    Draw-HighlightHalo $g $xOff $size $pal
    $sw = Get-StrokeWidth $size
    $pen = New-Object System.Drawing.Pen($pal.Outline, $sw)
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    # Two printed cyan layers at bottom
    $cyanBrush = New-Object System.Drawing.SolidBrush($pal.CyanFill)
    $l1 = New-RoundedRectPath ($xOff + $size * 0.12) ($size * 0.52) ($size * 0.76) ($size * 0.16) ($size * 0.04)
    $l2 = New-RoundedRectPath ($xOff + $size * 0.12) ($size * 0.71) ($size * 0.76) ($size * 0.16) ($size * 0.04)
    $g.FillPath($cyanBrush, $l1)
    $g.DrawPath($pen, $l1)
    $g.FillPath($cyanBrush, $l2)
    $g.DrawPath($pen, $l2)
    $l1.Dispose()
    $l2.Dispose()
    $cyanBrush.Dispose()

    # Amber 3D printer nozzle pointing down onto layers
    $nozzle = New-Object System.Drawing.Drawing2D.GraphicsPath
    $nozzle.AddPolygon([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF([float]($xOff + $size * 0.30), [float]($size * 0.12))),
        (New-Object System.Drawing.PointF([float]($xOff + $size * 0.70), [float]($size * 0.12))),
        (New-Object System.Drawing.PointF([float]($xOff + $size * 0.70), [float]($size * 0.27))),
        (New-Object System.Drawing.PointF([float]($xOff + $size * 0.54), [float]($size * 0.46))),
        (New-Object System.Drawing.PointF([float]($xOff + $size * 0.46), [float]($size * 0.46))),
        (New-Object System.Drawing.PointF([float]($xOff + $size * 0.30), [float]($size * 0.27)))
    ))
    $amberBrush = New-Object System.Drawing.SolidBrush($pal.AmberFill)
    $g.FillPath($amberBrush, $nozzle)
    $g.DrawPath($pen, $nozzle)
    $nozzle.Dispose()
    $amberBrush.Dispose()
    $pen.Dispose()
}

function Draw-JoineryIcon([System.Drawing.Graphics]$g, [float]$xOff, [int]$size, [string]$mode) {
    $pal = Get-Palette $mode
    Draw-HighlightHalo $g $xOff $size $pal
    $sw = Get-StrokeWidth $size
    $pen = New-Object System.Drawing.Pen($pal.Outline, $sw)
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    # Left interlocking body (cyan) with central tab protruding right
    $leftBody = New-Object System.Drawing.Drawing2D.GraphicsPath
    $leftBody.AddPolygon([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF([float]($xOff + $size * 0.11), [float]($size * 0.20))),
        (New-Object System.Drawing.PointF([float]($xOff + $size * 0.44), [float]($size * 0.20))),
        (New-Object System.Drawing.PointF([float]($xOff + $size * 0.44), [float]($size * 0.38))),
        (New-Object System.Drawing.PointF([float]($xOff + $size * 0.60), [float]($size * 0.38))),
        (New-Object System.Drawing.PointF([float]($xOff + $size * 0.60), [float]($size * 0.62))),
        (New-Object System.Drawing.PointF([float]($xOff + $size * 0.44), [float]($size * 0.62))),
        (New-Object System.Drawing.PointF([float]($xOff + $size * 0.44), [float]($size * 0.80))),
        (New-Object System.Drawing.PointF([float]($xOff + $size * 0.11), [float]($size * 0.80)))
    ))

    # Right interlocking body (amber) with matching slot
    $rightBody = New-Object System.Drawing.Drawing2D.GraphicsPath
    $rightBody.AddPolygon([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF([float]($xOff + $size * 0.44), [float]($size * 0.20))),
        (New-Object System.Drawing.PointF([float]($xOff + $size * 0.89), [float]($size * 0.20))),
        (New-Object System.Drawing.PointF([float]($xOff + $size * 0.89), [float]($size * 0.80))),
        (New-Object System.Drawing.PointF([float]($xOff + $size * 0.44), [float]($size * 0.80))),
        (New-Object System.Drawing.PointF([float]($xOff + $size * 0.44), [float]($size * 0.62))),
        (New-Object System.Drawing.PointF([float]($xOff + $size * 0.60), [float]($size * 0.62))),
        (New-Object System.Drawing.PointF([float]($xOff + $size * 0.60), [float]($size * 0.38))),
        (New-Object System.Drawing.PointF([float]($xOff + $size * 0.44), [float]($size * 0.38)))
    ))

    $cyanBrush = New-Object System.Drawing.SolidBrush($pal.CyanFill)
    $amberBrush = New-Object System.Drawing.SolidBrush($pal.AmberFill)

    $g.FillPath($cyanBrush, $leftBody)
    $g.FillPath($amberBrush, $rightBody)
    $g.DrawPath($pen, $leftBody)
    $g.DrawPath($pen, $rightBody)

    $leftBody.Dispose()
    $rightBody.Dispose()
    $cyanBrush.Dispose()
    $amberBrush.Dispose()
    $pen.Dispose()
}

function Save-IconStrip([string]$outPath, [int]$size, [scriptblock[]]$drawCallbacks) {
    $bmp = New-Object System.Drawing.Bitmap(($size * $drawCallbacks.Count), $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    for ($i = 0; $i -lt $drawCallbacks.Count; $i++) {
        & $drawCallbacks[$i] $g ($i * $size) $size "regular"
    }

    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
}

function Save-TreeIcon([string]$outPath, [int]$size, [string]$mode, [scriptblock]$drawCallback) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    & $drawCallback $g 0 $size $mode

    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
}

$sizes = @(20, 32, 40, 64, 96, 128)

foreach ($sz in $sizes) {
    Save-IconStrip (Join-Path $dfmIconsDir "dfm_cmd_strip_$sz.png") $sz @(
        { param($g, $x, $s, $m) Draw-TeardropIcon $g $x $s $m },
        { param($g, $x, $s, $m) Draw-BridgeIcon $g $x $s $m }
    )
    Save-IconStrip (Join-Path $dfmIconsDir "dfm_main_$sz.png") $sz @(
        { param($g, $x, $s, $m) Draw-DfmMainIcon $g $x $s $m }
    )

    Save-IconStrip (Join-Path $joineryIconsDir "joinery_cmd_strip_$sz.png") $sz @(
        { param($g, $x, $s, $m) Draw-JoineryIcon $g $x $s $m }
    )
    Save-IconStrip (Join-Path $joineryIconsDir "joinery_main_$sz.png") $sz @(
        { param($g, $x, $s, $m) Draw-JoineryIcon $g $x $s $m }
    )
}

foreach ($mode in @("regular", "suppressed", "highlight")) {
    Save-TreeIcon (Join-Path $dfmIconsDir "teardrop_tree_$mode.png") 20 $mode { param($g, $x, $s, $m) Draw-TeardropIcon $g $x $s $m }
    Save-TreeIcon (Join-Path $dfmIconsDir "bridge_tree_$mode.png") 20 $mode { param($g, $x, $s, $m) Draw-BridgeIcon $g $x $s $m }
    Save-TreeIcon (Join-Path $joineryIconsDir "joinery_tree_$mode.png") 20 $mode { param($g, $x, $s, $m) Draw-JoineryIcon $g $x $s $m }
}

Write-Host "Generated all icon strips and FeatureManager tree icons successfully." -ForegroundColor Green
