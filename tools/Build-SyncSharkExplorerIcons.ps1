[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
$sizes = @(16, 20, 24, 32, 48, 64)
# Distinct shapes remain meaningful without colour; these are Explorer status-column icons,
# not a second icon-overlay handler competing for Windows' limited overlay slots.
$icons = @(
    @{ Id = 101; Colour = '#279B69'; Path = 'M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 M6,12 L10,16 18,8' },
    @{ Id = 102; Colour = '#267AF5'; Path = 'M3,10 A9,9 0 0 1 19,5 L21,8 M21,2 V8 H15 M21,14 A9,9 0 0 1 5,19 L3,16 M3,22 V16 H9' },
    @{ Id = 103; Colour = '#C68A20'; Path = 'M7,4 V20 M17,4 V20' },
    @{ Id = 104; Colour = '#DA4545'; Path = 'M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 M12,6 V13 M12,17 V18' },
    @{ Id = 105; Colour = '#858A93'; Path = 'M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 M6,18 L18,6' }
)
$resources = [Collections.Generic.List[string]]::new()
foreach ($icon in $icons) {
    $images = [Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) {
        $visual = [Windows.Media.DrawingVisual]::new()
        $drawing = $visual.RenderOpen()
        try {
            $drawing.PushTransform([Windows.Media.ScaleTransform]::new($size / 24.0, $size / 24.0))
            $brush = [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($icon.Colour))
            $width = if ($icon.Id -eq 103) { 4.5 } else { 2.2 }
            $pen = [Windows.Media.Pen]::new($brush, $width)
            $pen.StartLineCap = $pen.EndLineCap = [Windows.Media.PenLineCap]::Round
            $pen.LineJoin = [Windows.Media.PenLineJoin]::Round
            $drawing.DrawGeometry($null, $pen, [Windows.Media.Geometry]::Parse($icon.Path))
            $drawing.Pop()
        } finally { $drawing.Close() }
        $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($visual)
        $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $memory = [IO.MemoryStream]::new()
        try { $encoder.Save($memory); $images.Add($memory.ToArray()) } finally { $memory.Dispose() }
    }
    $path = Join-Path $output ('status-{0}.ico' -f $icon.Id)
    $stream = [IO.File]::Open($path, [IO.FileMode]::Create)
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $writer.Write([byte]$sizes[$i]); $writer.Write([byte]$sizes[$i])
            $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
            $offset += $images[$i].Length
        }
        foreach ($bytes in $images) { $writer.Write($bytes) }
    } finally { $writer.Dispose(); $stream.Dispose() }
    $resources.Add(('{0} ICON "{1}"' -f $icon.Id, $path.Replace('\', '\\')))
}
[IO.File]::WriteAllLines((Join-Path $output 'status-icons.rc'), $resources, [Text.UTF8Encoding]::new($false))
