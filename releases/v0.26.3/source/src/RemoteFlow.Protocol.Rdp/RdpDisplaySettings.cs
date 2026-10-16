using RemoteFlow.Core.Models;

namespace RemoteFlow.Protocol.Rdp;

/// <summary>首次握手与动态调整共用的像素、毫米尺寸及远端 DPI 参数。</summary>
internal readonly record struct RdpDisplaySettings(
    int Width, int Height, uint PhysicalWidth, uint PhysicalHeight,
    uint DesktopScaleFactor, uint DeviceScaleFactor)
{
    public static RdpDisplaySettings Create(RdpOptions options, int width, int height, double dpiScale)
    {
        dpiScale = double.IsFinite(dpiScale) ? Math.Clamp(dpiScale, 1, 5) : 1;
        var desktopScale = (uint)Math.Round(dpiScale * 100);
        // device scale 只接受 100 / 140 / 180，取最接近的合法值。
        var deviceScale = desktopScale <= 120 ? 100u : desktopScale <= 160 ? 140u : 180u;
        if (options.DisplayMode == RdpDisplayMode.FixedResolution)
        {
            width = options.DesktopWidth;
            height = options.DesktopHeight;
        }
        else
        {
            // DISPLAYCONTROL_MONITOR_LAYOUT：宽高 200..8192，宽度必须为偶数。
            width = Math.Clamp(width > 0 ? width : 1024, 200, 8192) & ~1;
            height = Math.Clamp(height > 0 ? height : 768, 200, 8192);
        }

        // 没有 EDID 时按有效 DPI 推算名义尺寸（mm），不能把像素当作毫米。
        var dpi = 96 * dpiScale;
        return new(width, height,
            (uint)Math.Clamp(Math.Round(width * 25.4 / dpi), 10, 10_000),
            (uint)Math.Clamp(Math.Round(height * 25.4 / dpi), 10, 10_000),
            desktopScale, deviceScale);
    }
}
