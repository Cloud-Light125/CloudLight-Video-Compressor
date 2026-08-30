[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourcePath,

    [Parameter(Mandatory)]
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$requiredSizes = @(16, 24, 32, 48, 64, 128, 256)
$sourceFullPath = [System.IO.Path]::GetFullPath($SourcePath)
$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)

if (-not (Test-Path -LiteralPath $sourceFullPath -PathType Leaf)) {
    throw "Source PNG was not found: $sourceFullPath"
}

$outputDirectory = Split-Path -Parent $outputFullPath
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$sourceImage = [System.Drawing.Image]::FromFile($sourceFullPath)
$entries = [System.Collections.Generic.List[object]]::new()

try {
    foreach ($size in $requiredSizes) {
        $bitmap = [System.Drawing.Bitmap]::new(
            $size,
            $size,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $stream = [System.IO.MemoryStream]::new()

        try {
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

            $scale = [Math]::Min($size / $sourceImage.Width, $size / $sourceImage.Height)
            $width = [int][Math]::Round($sourceImage.Width * $scale)
            $height = [int][Math]::Round($sourceImage.Height * $scale)
            $x = [int][Math]::Floor(($size - $width) / 2)
            $y = [int][Math]::Floor(($size - $height) / 2)
            $graphics.DrawImage($sourceImage, [System.Drawing.Rectangle]::new($x, $y, $width, $height))
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $entries.Add([PSCustomObject]@{
                    Size = $size
                    Data = $stream.ToArray()
                })
        }
        finally {
            $stream.Dispose()
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }

    $fileStream = [System.IO.File]::Open(
        $outputFullPath,
        [System.IO.FileMode]::Create,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    $writer = [System.IO.BinaryWriter]::new($fileStream)
    try {
        $writer.Write([UInt16]0) # Reserved
        $writer.Write([UInt16]1) # ICO image type
        $writer.Write([UInt16]$entries.Count)

        [uint32]$offset = 6 + (16 * $entries.Count)
        foreach ($entry in $entries) {
            $encodedSize = if ($entry.Size -eq 256) { 0 } else { $entry.Size }
            $writer.Write([byte]$encodedSize)
            $writer.Write([byte]$encodedSize)
            $writer.Write([byte]0) # Palette colors
            $writer.Write([byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$entry.Data.Length)
            $writer.Write($offset)
            $offset += [uint32]$entry.Data.Length
        }

        foreach ($entry in $entries) {
            $writer.Write([byte[]]$entry.Data)
        }
    }
    finally {
        $writer.Dispose()
        $fileStream.Dispose()
    }
}
finally {
    $sourceImage.Dispose()
}

Write-Host "Generated multi-size icon: $outputFullPath ($($requiredSizes -join ', ') px)"
