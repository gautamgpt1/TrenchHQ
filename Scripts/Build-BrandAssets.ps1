# Resize the owner-supplied PNG without redrawing, cropping or recoloring it.
[CmdletBinding()]
param(
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputPath) { $OutputPath = Join-Path $root 'src\TrenchHQ.App\obj\BrandAssets' }
$output = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory($output) | Out-Null
$sourcePath = Join-Path $root 'src\TrenchHQ.App\Assets\TrenchHQ.png'
$source = [Windows.Media.Imaging.BitmapFrame]::Create([Uri]$sourcePath)

function Write-Asset([string]$name, [byte[]]$bytes) {
    $path = Join-Path $output $name
    if ([IO.File]::Exists($path) -and
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -eq [Convert]::ToBase64String($bytes)) {
        return
    }
    [IO.File]::WriteAllBytes($path, $bytes)
}

function Get-LogoPng([int]$width, [int]$height) {
    $scale = [Math]::Min($width / $source.PixelWidth, $height / $source.PixelHeight)
    $imageWidth = $source.PixelWidth * $scale
    $imageHeight = $source.PixelHeight * $scale
    $visual = [Windows.Media.DrawingVisual]::new()
    [Windows.Media.RenderOptions]::SetBitmapScalingMode($visual, [Windows.Media.BitmapScalingMode]::HighQuality)
    $drawing = $visual.RenderOpen()
    $drawing.DrawRectangle([Windows.Media.Brushes]::Black, $null, [Windows.Rect]::new(0, 0, $width, $height))
    $drawing.DrawImage($source, [Windows.Rect]::new(($width-$imageWidth)/2, ($height-$imageHeight)/2, $imageWidth, $imageHeight))
    $drawing.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($width, $height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        return ,$stream.ToArray()
    } finally { $stream.Dispose() }
}

$sizes = @{
    'StoreLogo.png' = @(50,50)
    'StoreLogo.scale-200.png' = @(100,100)
    'StoreLogo.scale-400.png' = @(200,200)
    'Square44x44Logo.scale-100.png' = @(44,44)
    'Square44x44Logo.scale-200.png' = @(88,88)
    'Square44x44Logo.scale-400.png' = @(176,176)
    'Square44x44Logo.targetsize-24_altform-unplated.png' = @(24,24)
    'Square44x44Logo.targetsize-256.png' = @(256,256)
    'Square44x44Logo.targetsize-256_altform-unplated.png' = @(256,256)
    'Square44x44Logo.targetsize-256_altform-lightunplated.png' = @(256,256)
    'Square150x150Logo.scale-100.png' = @(150,150)
    'Square150x150Logo.scale-200.png' = @(300,300)
    'Square150x150Logo.scale-400.png' = @(600,600)
    'Wide310x150Logo.scale-100.png' = @(310,150)
    'Wide310x150Logo.scale-200.png' = @(620,300)
    'Wide310x150Logo.scale-400.png' = @(1240,600)
    'SplashScreen.scale-100.png' = @(620,300)
    'SplashScreen.scale-200.png' = @(1240,600)
    'SplashScreen.scale-400.png' = @(2480,1200)
}
foreach ($name in $sizes.Keys) {
    $width,$height = $sizes[$name]
    Write-Asset $name (Get-LogoPng $width $height)
    Write-Output "$name ${width}x${height}"
}

# Windows ICO directory followed by PNG frames for each shell/tray size.
$iconSizes = @(16,20,24,32,40,48,64,128,256)
$frames = @($iconSizes | ForEach-Object { Get-LogoPng $_ $_ })
$iconStream = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($iconStream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$iconSizes.Count)
    $offset = 6 + 16 * $iconSizes.Count
    for ($index = 0; $index -lt $iconSizes.Count; $index++) {
        $dimension = $iconSizes[$index] % 256
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    $writer.Flush()
    Write-Asset 'TrenchHQ.ico' ($iconStream.ToArray())
} finally { $writer.Dispose() }
Write-Output 'TrenchHQ.ico (16/20/24/32/40/48/64/128/256px)'
