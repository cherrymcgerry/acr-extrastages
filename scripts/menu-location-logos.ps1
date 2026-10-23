<#
.SYNOPSIS
  Draws the neutral rally-plate logo (485x218 PNG, transparent) of every new location whose spec has Location.GenLogo:
  ACR's plate shape (rounded body with a raised centre arch) in the location's colours, "RALLY" over the location name,
  diagonal stripes on the right. No third-party marks. Read by `acr-additive menu-locations --logo-dir` (docs/menu-images.md).
#>
param(
    [Parameter(Mandatory)][string[]]$Specs,     # stage spec files or directories (also one ';'-separated string, for pwsh -File)
    [Parameter(Mandatory)][string]$Out
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $Out | Out-Null

function Color([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

function Plate([float]$grow) {
    # body 6..479 x 58..212 (corner 22) plus a centre arch rising to y 6, grown by $grow px for the outline layers
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $x0 = 6 - $grow; $x1 = 479 + $grow; $y0 = 58 - $grow; $y1 = 212 + $grow; $r = 22 + $grow
    $p.AddArc($x0, $y0, 2 * $r, 2 * $r, 180, 90)
    $p.AddLine($x0 + $r, $y0, 96 - $grow, $y0)
    $p.AddBezier(96 - $grow, $y0, 140, 6 - $grow, 345, 6 - $grow, 389 + $grow, $y0)
    $p.AddLine(389 + $grow, $y0, $x1 - $r, $y0)
    $p.AddArc($x1 - 2 * $r, $y0, 2 * $r, 2 * $r, 270, 90)
    $p.AddArc($x1 - 2 * $r, $y1 - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $p.AddArc($x0, $y1 - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $p.CloseFigure()
    return $p
}

function Font([float]$size) {
    foreach ($name in 'Bahnschrift SemiBold', 'Bahnschrift', 'Segoe UI Semibold', 'Segoe UI') {
        $f = New-Object System.Drawing.Font $name, $size, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        if ($f.Name -eq $name -or $f.Name -eq $name.Split(' ')[0]) { return $f }
    }
    return $f
}

$files = @($Specs | % { $_ -split ';' } | ? { $_ } | % { if (Test-Path $_ -PathType Container) { Get-ChildItem $_ -Filter *.json | % FullName } else { $_ } })
$done = @{}
foreach ($f in $files) {
    $loc = (Get-Content $f -Raw | ConvertFrom-Json).Location
    if (-not $loc.GenLogo -or $done[$loc.Row]) { continue }
    $done[$loc.Row] = $true
    $colors = @($loc.LogoColors -split ',' | % { Color $_.Trim() })
    $bmp = New-Object System.Drawing.Bitmap 485, 218, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.Clear([System.Drawing.Color]::Transparent)
    $g.FillPath((New-Object System.Drawing.SolidBrush (Color '#9AA0A6')), (Plate 0))
    $g.FillPath([System.Drawing.Brushes]::White, (Plate -2))
    $inner = Plate -8
    $g.FillPath((New-Object System.Drawing.SolidBrush $colors[0]), $inner)
    # stripes: diagonal bands at the right end, clipped to the inner plate
    $stripes = @($colors | Select-Object -Skip 1)
    $textRight = 470
    if ($stripes.Count) {
        $g.SetClip($inner)
        $w = 18; $x = 440 - $w * $stripes.Count
        for ($i = 0; $i -lt $stripes.Count; $i++) {
            $c = $stripes[$i]
            # the last band runs to the plate's right edge
            $r = if ($i -eq $stripes.Count - 1) { 520 } else { $x + $w }
            $pts = [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF ($x + 36), 0), (New-Object System.Drawing.PointF ($r + 36), 0),
                (New-Object System.Drawing.PointF $r, 218), (New-Object System.Drawing.PointF $x, 218))
            $g.FillPolygon((New-Object System.Drawing.SolidBrush $c), $pts)
            $x += $w
        }
        $g.ResetClip()
        $textRight = 440 - $w * $stripes.Count - 8
    }
    $cx = (16 + $textRight) / 2
    $fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $white = [System.Drawing.Brushes]::White
    $g.DrawString('RALLY', (Font 34), $white, (New-Object System.Drawing.RectangleF ($cx - 150), 66, 300, 44), $fmt)
    $name = $loc.Name.ToUpperInvariant()
    $size = 76
    do { $font = Font $size; $sz = $g.MeasureString($name, $font); $size -= 2 } while ($sz.Width -gt ($textRight - 26) -and $size -gt 20)
    $g.DrawString($name, $font, $white, (New-Object System.Drawing.RectangleF 16, 106, ($textRight - 16), 96), $fmt)
    $png = Join-Path $Out "$($loc.Row).png"
    $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "logo $($loc.Row) -> $png"
}
