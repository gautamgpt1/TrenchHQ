# Rasterize the original vector mark for Windows manifest asset sizes.
# Assets/TrenchHQ.svg is the editable source; this renderer supports its rect/path shapes.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$svg = [xml](Get-Content -Raw -LiteralPath (Join-Path $root 'Assets\TrenchHQ.svg'))
$sizes = @{
    'StoreLogo.png' = @(50,50)
    'Square44x44Logo.scale-200.png' = @(88,88)
    'Square44x44Logo.targetsize-24_altform-unplated.png' = @(24,24)
    'Square150x150Logo.scale-200.png' = @(300,300)
    'Wide310x150Logo.scale-200.png' = @(620,300)
    'SplashScreen.scale-200.png' = @(1240,600)
    'LockScreenLogo.scale-200.png' = @(48,48)
}
foreach ($name in $sizes.Keys) {
    $width,$height = $sizes[$name]
    $edge = [Math]::Min($width,$height)
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $drawing.PushTransform([Windows.Media.TranslateTransform]::new(($width-$edge)/2,($height-$edge)/2))
    $drawing.PushTransform([Windows.Media.ScaleTransform]::new($edge/64,$edge/64))
    foreach ($shape in $svg.DocumentElement.ChildNodes) {
        $brush = [Windows.Media.BrushConverter]::new().ConvertFromInvariantString($shape.fill)
        if ($shape.LocalName -eq 'rect') {
            $drawing.DrawRoundedRectangle($brush,$null,
                [Windows.Rect]::new([double]$shape.x,[double]$shape.y,[double]$shape.width,[double]$shape.height),
                [double]$shape.rx,[double]$shape.rx)
        } elseif ($shape.LocalName -eq 'path') {
            $drawing.DrawGeometry($brush,$null,[Windows.Media.Geometry]::Parse($shape.d))
        } else { throw "Unsupported vector shape: $($shape.LocalName)" }
    }
    $drawing.Pop(); $drawing.Pop(); $drawing.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($width,$height,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create((Join-Path $root ('Assets\' + $name)))
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
    Write-Output "$name ${width}x${height}"
}
