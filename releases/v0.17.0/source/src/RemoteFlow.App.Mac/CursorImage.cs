using System.Runtime.InteropServices;
using AppKit;
using CoreGraphics;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 从远端下发的 RGBA32 光标位图构造 <see cref="NSCursor"/>。RDP / VNC 会话视图共用
/// （本地渲染光标 → 零延迟，且能反映 I 型 / 手型 / 忙等待等形状）。
/// </summary>
internal static class CursorImage
{
    /// <summary>隐藏光标：全透明 1×1。macOS 的 AddCursorRect 不接受 null。</summary>
    public static readonly NSCursor Hidden = MakeHidden();

    private static NSCursor MakeHidden()
    {
        var rep = new NSBitmapImageRep(nint.Zero, 1, 1, 8, 4, true, false,
            NSColorSpace.DeviceRGB, 4, 32);
        Marshal.Copy(new byte[4], 0, rep.BitmapData, 4);
        var img = new NSImage(new CGSize(1, 1));
        img.AddRepresentation(rep);
        return new NSCursor(img, new CGPoint(0, 0));
    }

    /// <summary>rgba：宽 × 高 × 4 的 RGBA32（预乘 alpha）。热点越界会被夹回范围内。</summary>
    public static NSCursor? FromRgba(byte[]? rgba, int width, int height, int hotX, int hotY)
    {
        if (rgba is null || width <= 0 || height <= 0 || rgba.Length < width * height * 4)
        {
            return null;
        }

        var rep = new NSBitmapImageRep(nint.Zero, width, height, 8, 4, true, false,
            NSColorSpace.DeviceRGB, width * 4, 32);
        Marshal.Copy(rgba, 0, rep.BitmapData, width * height * 4);

        var img = new NSImage(new CGSize(width, height));
        img.AddRepresentation(rep);

        var hot = new CGPoint(
            Math.Clamp(hotX, 0, Math.Max(0, width - 1)),
            Math.Clamp(hotY, 0, Math.Max(0, height - 1)));
        return new NSCursor(img, hot);
    }
}
