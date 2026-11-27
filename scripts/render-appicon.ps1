<#
render-appicon.ps1 —— 把 scripts/appicon/*.xaml 矢量图标渲染为各尺寸 PNG（仅 Windows：用 WPF 渲染渐变、高光与投影）。
每个尺寸都从矢量直接渲染，不是由大图缩小；16 / 24 / 32 px 使用 AppIcon-small.xaml 的简化图形。

输出（-OutDir）：
  mac-<n>.png  macOS 用，保留 1024 网格的四周留白（n = 16 … 1024）
  win-<n>.png  Windows 用，裁去四周 60 的留白，让图标在任务栏 / 资源管理器里不显得过小（n = 16 … 256）
  logo-small-64.png  界面内小 Logo（简化字形）

用法：pwsh -NoProfile -STA -File scripts/render-appicon.ps1 -OutDir <目录>
通常由 scripts/build-appicon.py 调用。
#>
param([Parameter(Mandatory)][string]$OutDir)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
New-Item -ItemType Directory -Force $OutDir | Out-Null

$fullXaml = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'appicon/AppIcon.xaml'))
$smallXaml = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'appicon/AppIcon-small.xaml'))
$winCrop = 60
$smallLimit = 32

function Save-Png($element, [int]$size, [string]$path) {
    $box = [System.Windows.Controls.Viewbox]::new()
    $box.Width = $size; $box.Height = $size
    $box.Stretch = [System.Windows.Media.Stretch]::Uniform
    $box.Child = $element
    $box.Measure([System.Windows.Size]::new($size, $size))
    $box.Arrange([System.Windows.Rect]::new(0, 0, $size, $size))
    $box.UpdateLayout()
    $bmp = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($box)
    $enc = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $fs = [IO.File]::Create($path); try { $enc.Save($fs) } finally { $fs.Dispose() }
}

function New-Full([int]$crop) {
    $el = [System.Windows.Markup.XamlReader]::Parse($fullXaml)
    if ($crop -eq 0) { return $el }
    # 只取中间 (1024 - 2·crop) 的区域：放进裁剪容器并整体左上平移
    $inner = 1024 - 2 * $crop
    $canvas = [System.Windows.Controls.Canvas]::new()
    $canvas.Width = $inner; $canvas.Height = $inner; $canvas.ClipToBounds = $true
    [System.Windows.Controls.Canvas]::SetLeft($el, -$crop); [System.Windows.Controls.Canvas]::SetTop($el, -$crop)
    [void]$canvas.Children.Add($el)
    return $canvas
}

foreach ($n in 16, 24, 32, 48, 64, 128, 256, 512, 1024) {
    if ($n -le $smallLimit) {
        Save-Png ([System.Windows.Markup.XamlReader]::Parse($smallXaml)) $n (Join-Path $OutDir "mac-$n.png")
        Save-Png ([System.Windows.Markup.XamlReader]::Parse($smallXaml)) $n (Join-Path $OutDir "win-$n.png")
    }
    else {
        Save-Png (New-Full 0) $n (Join-Path $OutDir "mac-$n.png")
        if ($n -le 256) { Save-Png (New-Full $winCrop) $n (Join-Path $OutDir "win-$n.png") }
    }
}
# 界面里 20–32 DIP 的小 Logo（标题栏、侧栏、设置页）：简化字形，按 64 px 渲染以兼顾高分屏
Save-Png ([System.Windows.Markup.XamlReader]::Parse($smallXaml)) 64 (Join-Path $OutDir "logo-small-64.png")
"已渲染到 $OutDir"
