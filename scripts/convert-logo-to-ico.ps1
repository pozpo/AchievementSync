param(
    [Parameter(Mandatory = $true)]
    [string]$Source,
    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationCore, WindowsBase

$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$destinationDirectory = [System.IO.Path]::GetDirectoryName($destinationPath)
[System.IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null

$sourceStream = [System.IO.File]::OpenRead($sourcePath)
try {
    $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create(
        $sourceStream,
        [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
        [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    $sourceFrame = $decoder.Frames[0]
}
finally {
    $sourceStream.Dispose()
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = [System.Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $scale = $size / [double][Math]::Max($sourceFrame.PixelWidth, $sourceFrame.PixelHeight)
    $transform = [System.Windows.Media.ScaleTransform]::new($scale, $scale)
    $scaledFrame = [System.Windows.Media.Imaging.TransformedBitmap]::new($sourceFrame, $transform)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($scaledFrame))
    $memory = [System.IO.MemoryStream]::new()
    try {
        $encoder.Save($memory)
        $images.Add($memory.ToArray())
    }
    finally {
        $memory.Dispose()
    }
}

$output = [System.IO.File]::Create($destinationPath)
$writer = [System.IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    for ($index = 0; $index -lt $images.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $images[$index].Length
    }
    foreach ($image in $images) { $writer.Write($image) }
}
finally {
    $writer.Dispose()
}

Write-Output "Created $destinationPath with $($sizes.Count) PNG-backed icon sizes."
