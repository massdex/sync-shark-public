[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$assetRoot = Join-Path $repositoryRoot 'apps\SyncShark\Assets'
$svgPath = Join-Path $assetRoot 'sync-shark-mark.svg'
$iconPath = Join-Path $assetRoot 'SyncShark.ico'
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)

[xml]$document = Get-Content -Raw -LiteralPath $svgPath
$manager = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
$manager.AddNamespace('svg', 'http://www.w3.org/2000/svg')
$paths = $document.SelectNodes('/svg:svg/svg:path', $manager)
if ($paths.Count -eq 0) { throw 'The Sync Shark SVG contains no paths.' }
$viewBox = $document.svg.viewBox -split '\s+' | ForEach-Object { [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) }
if ($viewBox.Count -ne 4 -or $viewBox[2] -le 0 -or $viewBox[3] -le 0) {
    throw 'The Sync Shark SVG viewBox is invalid.'
}

$encodedImages = [System.Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    try {
        $context.PushTransform([System.Windows.Media.ScaleTransform]::new($size / $viewBox[2], $size / $viewBox[3]))
        foreach ($path in $paths) {
            $geometry = [System.Windows.Media.Geometry]::Parse($path.d)
            $brush = [System.Windows.Media.SolidColorBrush]::new(
                [System.Windows.Media.ColorConverter]::ConvertFromString($path.fill))
            $context.DrawGeometry($brush, $null, $geometry)
        }
        $context.Pop()
    }
    finally { $context.Close() }

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $memory = [System.IO.MemoryStream]::new()
    try {
        $encoder.Save($memory)
        $bytes = $memory.ToArray()
        # Keep the PNG assets unchanged. Native icon consumers, including
        # secure-desktop prompts, also need ordinary 32-bit DIB icon frames.
        # Only the 256px frame is PNG-compressed inside the ICO container.
        if ($size -eq 256) { $encodedImages.Add($bytes) }
        else {
            $straight = [System.Windows.Media.Imaging.FormatConvertedBitmap]::new(
                $bitmap, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
            $stride = $size * 4
            $pixels = [byte[]]::new($stride * $size)
            $straight.CopyPixels($pixels, $stride, 0)
            $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
            $mask = [byte[]]::new($maskStride * $size)
            $dib = [System.IO.MemoryStream]::new()
            $dibWriter = [System.IO.BinaryWriter]::new($dib)
            try {
                $dibWriter.Write([uint32]40)
                $dibWriter.Write([int32]$size)
                $dibWriter.Write([int32]($size * 2))
                $dibWriter.Write([uint16]1)
                $dibWriter.Write([uint16]32)
                $dibWriter.Write([uint32]0)
                $dibWriter.Write([uint32]($pixels.Length + $mask.Length))
                foreach ($unused in 1..4) { $dibWriter.Write([uint32]0) }
                for ($row = $size - 1; $row -ge 0; $row--) {
                    $dibWriter.Write($pixels, $row * $stride, $stride)
                    for ($column = 0; $column -lt $size; $column++) {
                        if ($pixels[$row * $stride + $column * 4 + 3] -eq 0) {
                            $maskAt = ($size - 1 - $row) * $maskStride + [int][Math]::Floor($column / 8.0)
                            $mask[$maskAt] = $mask[$maskAt] -bor (0x80 -shr ($column % 8))
                        }
                    }
                }
                $dibWriter.Write($mask)
                $dibWriter.Flush()
                $encodedImages.Add($dib.ToArray())
            }
            finally { $dibWriter.Dispose(); $dib.Dispose() }
        }
        [System.IO.File]::WriteAllBytes((Join-Path $assetRoot ("SyncShark-{0}.png" -f $size)), $bytes)
    }
    finally { $memory.Dispose() }
}

$stream = [System.IO.FileStream]::new($iconPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + (16 * $sizes.Count)
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $size = $sizes[$index]
        $bytes = $encodedImages[$index]
        $dimension = if ($size -eq 256) { 0 } else { $size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $bytes.Length
    }
    foreach ($bytes in $encodedImages) { $writer.Write($bytes) }
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}

Get-Item -LiteralPath $iconPath, (Join-Path $assetRoot 'SyncShark-64.png') |
    Select-Object FullName, Length
