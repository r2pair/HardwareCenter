# Draws the HardwareCenter icon (a gauge arc on a dark tile) at every size Windows asks for
# and packs the PNGs into Assets\app.ico. Run again only if the icon design changes.

Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$output = Join-Path $PSScriptRoot '..\Assets\app.ico'

function New-IconPng([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Everything below is drawn on a 256-unit canvas and scaled.
    $scale = $size / 256.0
    $g.ScaleTransform($scale, $scale)

    # Tile
    $radius = 58
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $tile.AddArc(4, 4, $d, $d, 180, 90)
    $tile.AddArc(252 - $d, 4, $d, $d, 270, 90)
    $tile.AddArc(252 - $d, 252 - $d, $d, $d, 0, 90)
    $tile.AddArc(4, 252 - $d, $d, $d, 90, 90)
    $tile.CloseFigure()

    $top = [System.Drawing.Color]::FromArgb(255, 30, 37, 52)
    $bottom = [System.Drawing.Color]::FromArgb(255, 12, 15, 21)
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point 0, 256), $top, $bottom
    $g.FillPath($fill, $tile)

    # Small sizes get a heavier arc so it survives downscaling.
    $stroke = if ($size -le 24) { 40 } elseif ($size -le 48) { 32 } else { 26 }
    $arc = New-Object System.Drawing.RectangleF 52, 62, 152, 152

    $trackPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 44, 54, 74)), $stroke
    $trackPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $trackPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($trackPen, $arc, 135, 270)

    $from = [System.Drawing.Color]::FromArgb(255, 57, 135, 229)
    $to = [System.Drawing.Color]::FromArgb(255, 120, 180, 255)
    $valueBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 40, 0), (New-Object System.Drawing.Point 216, 0), $from, $to
    $valuePen = New-Object System.Drawing.Pen $valueBrush, $stroke
    $valuePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $valuePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($valuePen, $arc, 135, 195)

    $g.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    return , $stream.ToArray()
}

$images = foreach ($size in $sizes) { , (New-IconPng $size) }

$file = [System.IO.File]::Create($output)
$writer = New-Object System.IO.BinaryWriter $file

# ICONDIR
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)

# ICONDIRENTRY for each image
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length)
    $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}

foreach ($image in $images) { $writer.Write([byte[]]$image) }

$writer.Dispose()
$file.Dispose()
Write-Output "Wrote $((Resolve-Path $output).Path) ($((Get-Item $output).Length) bytes, $($sizes.Count) sizes)"
