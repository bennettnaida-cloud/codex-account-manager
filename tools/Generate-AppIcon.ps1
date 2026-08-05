param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath {
    param(
        [System.Drawing.RectangleF]$Bounds,
        [float]$Radius
    )

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = [Math]::Min([Math]::Min($Radius * 2.0, $Bounds.Width), $Bounds.Height)
    $arc = [System.Drawing.RectangleF]::new($Bounds.X, $Bounds.Y, $diameter, $diameter)
    $path.AddArc($arc, 180, 90)
    $arc.X = $Bounds.Right - $diameter
    $path.AddArc($arc, 270, 90)
    $arc.Y = $Bounds.Bottom - $diameter
    $path.AddArc($arc, 0, 90)
    $arc.X = $Bounds.Left
    $path.AddArc($arc, 90, 90)
    $path.CloseFigure()
    return $path
}

function Add-ArrowHead {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.PointF]$Tip,
        [float]$DirectionDegrees,
        [float]$Length,
        [float]$Width,
        [System.Drawing.Color]$Color
    )

    $angle = $DirectionDegrees * [Math]::PI / 180.0
    $backX = $Tip.X - ([Math]::Cos($angle) * $Length)
    $backY = $Tip.Y - ([Math]::Sin($angle) * $Length)
    $normalX = -[Math]::Sin($angle) * $Width / 2.0
    $normalY = [Math]::Cos($angle) * $Width / 2.0
    $points = [System.Drawing.PointF[]]@(
        $Tip,
        [System.Drawing.PointF]::new([float]($backX + $normalX), [float]($backY + $normalY)),
        [System.Drawing.PointF]::new([float]($backX - $normalX), [float]($backY - $normalY))
    )
    $brush = [System.Drawing.SolidBrush]::new($Color)
    try {
        $Graphics.FillPolygon($brush, $points)
    }
    finally {
        $brush.Dispose()
    }
}

function New-AppIconBitmap {
    param(
        [int]$Size,
        [int]$Supersample = 4
    )

    $renderSize = $Size * $Supersample
    $scale = $renderSize / 256.0
    $bitmap = [System.Drawing.Bitmap]::new(
        $renderSize,
        $renderSize,
        [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

        $tileBounds = [System.Drawing.RectangleF]::new(
            [float](8 * $scale),
            [float](8 * $scale),
            [float](240 * $scale),
            [float](240 * $scale))
        $tilePath = New-RoundedRectanglePath $tileBounds ([float](48 * $scale))
        try {
            $navy = [System.Drawing.ColorTranslator]::FromHtml('#071426')
            $navyLift = [System.Drawing.ColorTranslator]::FromHtml('#12325D')
            $tileBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                $tileBounds,
                $navyLift,
                $navy,
                132.0)
            try {
                $graphics.FillPath($tileBrush, $tilePath)
            }
            finally {
                $tileBrush.Dispose()
            }

            $borderPen = [System.Drawing.Pen]::new(
                [System.Drawing.ColorTranslator]::FromHtml('#1B4D7A'),
                [float](2.2 * $scale))
            try {
                $graphics.DrawPath($borderPen, $tilePath)
            }
            finally {
                $borderPen.Dispose()
            }
        }
        finally {
            $tilePath.Dispose()
        }

        # A restrained cyan halo ties the mark to the application's selected-state accent.
        $haloPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $haloPath.AddEllipse([System.Drawing.RectangleF]::new(
            [float](48 * $scale), [float](46 * $scale),
            [float](160 * $scale), [float](160 * $scale)))
        $haloBrush = [System.Drawing.Drawing2D.PathGradientBrush]::new($haloPath)
        try {
            $haloBrush.CenterColor = [System.Drawing.Color]::FromArgb(34, 34, 211, 238)
            $haloBrush.SurroundColors = [System.Drawing.Color[]]@(
                [System.Drawing.Color]::FromArgb(0, 34, 211, 238))
            $graphics.FillPath($haloBrush, $haloPath)
        }
        finally {
            $haloBrush.Dispose()
            $haloPath.Dispose()
        }

        $azure = [System.Drawing.ColorTranslator]::FromHtml('#2F6BFF')
        $cyan = [System.Drawing.ColorTranslator]::FromHtml('#22D3EE')
        $mint = [System.Drawing.ColorTranslator]::FromHtml('#34D399')
        $ice = [System.Drawing.ColorTranslator]::FromHtml('#EAF7FF')
        $deep = [System.Drawing.ColorTranslator]::FromHtml('#08182C')

        $outerRect = [System.Drawing.RectangleF]::new(
            [float](49 * $scale), [float](48 * $scale),
            [float](158 * $scale), [float](158 * $scale))
        $outerPen = [System.Drawing.Pen]::new($azure, [float](17 * $scale))
        try {
            $outerPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $outerPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $graphics.DrawArc($outerPen, $outerRect, 138, 226)
        }
        finally {
            $outerPen.Dispose()
        }
        Add-ArrowHead `
            -Graphics $graphics `
            -Tip ([System.Drawing.PointF]::new([float](205 * $scale), [float](130 * $scale))) `
            -DirectionDegrees -84 `
            -Length ([float](26 * $scale)) `
            -Width ([float](28 * $scale)) `
            -Color $azure

        $innerRect = [System.Drawing.RectangleF]::new(
            [float](69 * $scale), [float](69 * $scale),
            [float](118 * $scale), [float](118 * $scale))
        $innerPen = [System.Drawing.Pen]::new($cyan, [float](14 * $scale))
        try {
            $innerPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $innerPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $graphics.DrawArc($innerPen, $innerRect, -44, 218)
        }
        finally {
            $innerPen.Dispose()
        }
        Add-ArrowHead `
            -Graphics $graphics `
            -Tip ([System.Drawing.PointF]::new([float](70 * $scale), [float](129 * $scale))) `
            -DirectionDegrees 96 `
            -Length ([float](22 * $scale)) `
            -Width ([float](24 * $scale)) `
            -Color $cyan

        # Central credential diamond and keyhole.
        $diamond = [System.Drawing.PointF[]]@(
            [System.Drawing.PointF]::new([float](128 * $scale), [float](92 * $scale)),
            [System.Drawing.PointF]::new([float](164 * $scale), [float](128 * $scale)),
            [System.Drawing.PointF]::new([float](128 * $scale), [float](164 * $scale)),
            [System.Drawing.PointF]::new([float](92 * $scale), [float](128 * $scale))
        )
        $diamondBrush = [System.Drawing.SolidBrush]::new($ice)
        $diamondPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(210, $cyan), [float](3 * $scale))
        try {
            $graphics.FillPolygon($diamondBrush, $diamond)
            $graphics.DrawPolygon($diamondPen, $diamond)
        }
        finally {
            $diamondBrush.Dispose()
            $diamondPen.Dispose()
        }

        $keyBrush = [System.Drawing.SolidBrush]::new($deep)
        try {
            $graphics.FillEllipse(
                $keyBrush,
                [float](119 * $scale), [float](112 * $scale),
                [float](18 * $scale), [float](18 * $scale))
            $graphics.FillRectangle(
                $keyBrush,
                [float](123 * $scale), [float](125 * $scale),
                [float](10 * $scale), [float](21 * $scale))
        }
        finally {
            $keyBrush.Dispose()
        }

        # Small status light; the dark outline keeps it legible on every taskbar background.
        $statusOutline = [System.Drawing.SolidBrush]::new($deep)
        $statusBrush = [System.Drawing.SolidBrush]::new($mint)
        try {
            $graphics.FillEllipse(
                $statusOutline,
                [float](187 * $scale), [float](187 * $scale),
                [float](38 * $scale), [float](38 * $scale))
            $graphics.FillEllipse(
                $statusBrush,
                [float](194 * $scale), [float](194 * $scale),
                [float](24 * $scale), [float](24 * $scale))
        }
        finally {
            $statusOutline.Dispose()
            $statusBrush.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
    }

    if ($Supersample -eq 1) {
        return $bitmap
    }

    $result = [System.Drawing.Bitmap]::new(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $downsample = [System.Drawing.Graphics]::FromImage($result)
    try {
        $downsample.Clear([System.Drawing.Color]::Transparent)
        $downsample.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $downsample.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $downsample.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $downsample.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $downsample.DrawImage(
            $bitmap,
            [System.Drawing.Rectangle]::new(0, 0, $Size, $Size),
            0,
            0,
            $renderSize,
            $renderSize,
            [System.Drawing.GraphicsUnit]::Pixel)
    }
    finally {
        $downsample.Dispose()
        $bitmap.Dispose()
    }
    return $result
}

function Convert-BitmapToPngBytes {
    param([System.Drawing.Bitmap]$Bitmap)
    $stream = [System.IO.MemoryStream]::new()
    try {
        $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return $stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function Write-MultiSizeIcon {
    param(
        [string]$Path,
        [int[]]$Sizes
    )

    $images = @(foreach ($size in $Sizes) {
        $bitmap = New-AppIconBitmap -Size $size -Supersample $(if ($size -le 64) { 5 } else { 3 })
        try {
            [pscustomobject]@{
                Size = $size
                Bytes = Convert-BitmapToPngBytes $bitmap
            }
        }
        finally {
            $bitmap.Dispose()
        }
    })

    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$images.Count)
        $offset = 6 + (16 * $images.Count)
        foreach ($image in $images) {
            $dimension = if ($image.Size -ge 256) { [byte]0 } else { [byte]$image.Size }
            $writer.Write($dimension)
            $writer.Write($dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$image.Bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $image.Bytes.Length
        }
        foreach ($image in $images) {
            $writer.Write([byte[]]$image.Bytes)
        }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null
$pngPath = Join-Path $resolvedOutput 'CodexAccountManager.png'
$icoPath = Join-Path $resolvedOutput 'CodexAccountManager.ico'

$pngBitmap = New-AppIconBitmap -Size 512 -Supersample 3
try {
    $pngBitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $pngBitmap.Dispose()
}

Write-MultiSizeIcon -Path $icoPath -Sizes @(16, 20, 24, 32, 40, 48, 64, 128, 256)
Write-Host "Generated $pngPath"
Write-Host "Generated $icoPath"
