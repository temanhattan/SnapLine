Add-Type -AssemblyName System.Drawing

$size = 256
$bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$graphics.Clear([System.Drawing.Color]::Transparent)

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

$backgroundPath = New-RoundedPath 12 12 232 232 56
$backgroundBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 23, 31, 54))
$graphics.FillPath($backgroundBrush, $backgroundPath)

# Bright, taut line and two distinctive photo tiles.
$linePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 67, 220, 219), 8)
$linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$linePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$graphics.DrawLine($linePen, 42, 75, 214, 75)

$cards = @(
    @{ X = 56; Y = 88; W = 68; H = 112; Angle = -6; Top = [System.Drawing.Color]::FromArgb(255, 255, 179, 92); Bottom = [System.Drawing.Color]::FromArgb(255, 250, 92, 127) },
    @{ X = 133; Y = 92; W = 68; H = 106; Angle = 5; Top = [System.Drawing.Color]::FromArgb(255, 120, 167, 255); Bottom = [System.Drawing.Color]::FromArgb(255, 151, 107, 231) }
)
foreach ($card in $cards) {
    $graphicsState = $graphics.Save()
    $graphics.TranslateTransform($card.X + $card.W / 2, $card.Y + 10)
    $graphics.RotateTransform($card.Angle)
    $frame = New-RoundedPath (-$card.W / 2) 0 $card.W $card.H 12
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.RectangleF]::new(-$card.W / 2, 0, $card.W, $card.H), $card.Top, $card.Bottom, 90)
    $graphics.FillPath($brush, $frame)
    $inner = New-RoundedPath (-$card.W / 2 + 7) 7 ($card.W - 14) ($card.H - 14) 8
    $photo = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 245, 246, 250))
    $graphics.FillPath($photo, $inner)
    $accent = [System.Drawing.SolidBrush]::new($card.Bottom)
    $graphics.FillEllipse($accent, -8, 25, 16, 16)
    $graphics.FillRectangle($accent, -$card.W / 2 + 13, 52, $card.W - 26, 5)
    $graphics.FillRectangle($accent, -$card.W / 2 + 13, 64, $card.W - 35, 4)
    $clip = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 238, 243, 255))
    $clipPath = New-RoundedPath -6 -11 12 27 4
    $graphics.FillPath($clip, $clipPath)
    $graphics.Restore($graphicsState)
}

$outputDirectory = Join-Path $PSScriptRoot '..\SnapLine\Assets'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$pngStream = [System.IO.MemoryStream]::new()
$bitmap.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $pngStream.ToArray()

# A standards-compliant ICO containing a PNG-compressed 256×256 image.
$ico = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($ico)
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]1)
$writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0)
$writer.Write([UInt16]1); $writer.Write([UInt16]32)
$writer.Write([UInt32]$png.Length); $writer.Write([UInt32]22)
$writer.Write($png)
[System.IO.File]::WriteAllBytes((Join-Path $outputDirectory 'SnapLine.ico'), $ico.ToArray())

$writer.Dispose(); $ico.Dispose(); $pngStream.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
