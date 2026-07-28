# make-icons.ps1
# Generates original Go-themed icon PNGs using System.Drawing (GDI+).
# No downloaded artwork: the mark is an original rounded-rect badge in Go cyan
# (#00ADD8) with bold white "GO" text; the file variant adds a document-page
# outline with a folded corner. Re-run to regenerate deterministically.
#
# Outputs (in this script's directory):
#   go-project-16.png, go-project-32.png  - badge only
#   go-file-16.png,    go-file-32.png     - badge + document page glyph
#   template-icon-32.png                  - badge (32px, template dialog icon)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$outDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$goCyan = [System.Drawing.Color]::FromArgb(255, 0x00, 0xAD, 0xD8)
$white  = [System.Drawing.Color]::White

function New-RoundedRectPath {
    param([float]$X, [float]$Y, [float]$W, [float]$H, [float]$R)
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $R * 2
    $p.AddArc($X, $Y, $d, $d, 180, 90)
    $p.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $p.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $p.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-GoBadge {
    # Draws the rounded-rect GO badge into graphics context $g covering the
    # square region (x, y, size, size).
    param(
        [System.Drawing.Graphics]$G,
        [float]$X, [float]$Y, [float]$Size
    )
    $radius = [Math]::Max(2.0, $Size * 0.2)
    $path = New-RoundedRectPath -X $X -Y $Y -W $Size -H $Size -R $radius
    $brush = New-Object System.Drawing.SolidBrush $goCyan
    $G.FillPath($brush, $path)
    $brush.Dispose(); $path.Dispose()

    # Bold white "GO", centered. Use GenericTypographic-ish centering via
    # StringFormat; pick font size relative to badge size.
    $fontSize = $Size * 0.42
    $font = New-Object System.Drawing.Font('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $textBrush = New-Object System.Drawing.SolidBrush $white
    $rect = New-Object System.Drawing.RectangleF($X, $Y, $Size, $Size)
    $G.DrawString('GO', $font, $textBrush, $rect, $sf)
    $textBrush.Dispose(); $sf.Dispose(); $font.Dispose()
}

function New-Icon {
    param(
        [int]$Px,
        [string]$OutFile,
        [switch]$FileVariant
    )
    $bmp = New-Object System.Drawing.Bitmap($Px, $Px, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    if ($FileVariant) {
        # Document page outline (upper-left) with folded corner, plus a smaller
        # GO badge anchored bottom-right.
        $penW = [Math]::Max(1.0, $Px / 16.0)
        $pagePen = New-Object System.Drawing.Pen($goCyan, $penW)
        $pagePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

        $m    = $Px * 0.06                 # margin
        $pw   = $Px * 0.56                 # page width
        $ph   = $Px * 0.78                 # page height
        $fold = $pw * 0.32                 # folded corner size
        $x0 = $m; $y0 = $m

        # Page outline with cut corner (top-right fold)
        [System.Drawing.PointF[]]$pts = @(
            (New-Object System.Drawing.PointF($x0, $y0)),
            (New-Object System.Drawing.PointF(($x0 + $pw - $fold), $y0)),
            (New-Object System.Drawing.PointF(($x0 + $pw), ($y0 + $fold))),
            (New-Object System.Drawing.PointF(($x0 + $pw), ($y0 + $ph))),
            (New-Object System.Drawing.PointF($x0, ($y0 + $ph)))
        )
        $g.DrawPolygon($pagePen, $pts)
        # Fold crease
        $g.DrawLine($pagePen,
            ($x0 + $pw - $fold), $y0,
            ($x0 + $pw - $fold), ($y0 + $fold))
        $g.DrawLine($pagePen,
            ($x0 + $pw - $fold), ($y0 + $fold),
            ($x0 + $pw), ($y0 + $fold))
        $pagePen.Dispose()

        # Small text lines on the page for legibility at 32px (skip detail at 16px)
        if ($Px -ge 32) {
            $linePen = New-Object System.Drawing.Pen($goCyan, [Math]::Max(1.0, $penW * 0.75))
            $lx = $x0 + $pw * 0.18
            $lw = $pw * 0.55
            foreach ($fy in 0.38, 0.54) {
                $ly = $y0 + $ph * $fy
                $g.DrawLine($linePen, $lx, $ly, ($lx + $lw), $ly)
            }
            $linePen.Dispose()
        }

        # Badge bottom-right. At tiny sizes the badge must dominate so the
        # "GO" text stays readable; at 32px+ leave more of the page visible.
        $frac = if ($Px -lt 24) { 0.78 } else { 0.62 }
        $bs = $Px * $frac
        Draw-GoBadge -G $g -X ($Px - $bs) -Y ($Px - $bs) -Size $bs
    }
    else {
        # Full-canvas badge with tiny margin
        $m = [Math]::Max(0.0, $Px * 0.03)
        Draw-GoBadge -G $g -X $m -Y $m -Size ($Px - 2 * $m)
    }

    $g.Dispose()
    $bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "Wrote $OutFile"
}

New-Icon -Px 16 -OutFile (Join-Path $outDir 'go-project-16.png')
New-Icon -Px 32 -OutFile (Join-Path $outDir 'go-project-32.png')
New-Icon -Px 16 -OutFile (Join-Path $outDir 'go-file-16.png') -FileVariant
New-Icon -Px 32 -OutFile (Join-Path $outDir 'go-file-32.png') -FileVariant
New-Icon -Px 32 -OutFile (Join-Path $outDir 'template-icon-32.png')
