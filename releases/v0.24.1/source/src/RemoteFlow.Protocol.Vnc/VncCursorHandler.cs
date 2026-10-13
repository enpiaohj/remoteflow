using MarcusW.VncClient;
using MarcusW.VncClient.Rendering;

namespace RemoteFlow.Protocol.Vnc;

/// <summary>远端光标形状。<see cref="VncSession.CursorChanged"/> 携带；null = 隐藏。</summary>
/// <param name="Rgba">预乘 alpha 的 RGBA32（宽 × 高 × 4）。</param>
public sealed record VncCursorShape(byte[] Rgba, int Width, int Height, int HotX, int HotY);

/// <summary>
/// 把 RFB 的光标伪编码（RGBA / RichCursor / XCursor）统一转成预乘 RGBA32，
/// 交给 UI 层本地画 —— 光标零延迟，也不再被库合成进帧缓冲。
/// </summary>
internal sealed class VncCursorHandler(Action<VncCursorShape?> onCursor) : ICursorHandler
{
    public void HideCursor() => onCursor(null);

    /// <summary>RGBA（已预乘 alpha），直接透传。</summary>
    public void UpdateCursorWithAlpha(int hotX, int hotY, int width, int height, byte[] rgba)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
        {
            onCursor(null);
            return;
        }

        onCursor(new VncCursorShape((byte[])rgba.Clone(), width, height, hotX, hotY));
    }

    /// <summary>RichCursor：像素按 <paramref name="format"/> 解，AND 位掩码定透明。</summary>
    public void UpdateCursor(int hotX, int hotY, int width, int height,
        byte[] pixels, byte[] bitmask, PixelFormat format)
    {
        if (width <= 0 || height <= 0)
        {
            onCursor(null);
            return;
        }

        var rgba = new byte[width * height * 4];
        var bpp = Math.Max(1, (int)format.BytesPerPixel);
        var maskStride = (width + 7) / 8;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pi = (y * width + x) * bpp;
                uint raw = 0;
                for (var b = 0; b < bpp && pi + b < pixels.Length; b++)
                {
                    var shift = 8 * (format.BigEndian ? bpp - 1 - b : b);
                    raw |= (uint)pixels[pi + b] << shift;
                }

                var r = Scale((raw >> format.RedShift) & format.RedMax, format.RedMax);
                var g = Scale((raw >> format.GreenShift) & format.GreenMax, format.GreenMax);
                var bl = Scale((raw >> format.BlueShift) & format.BlueMax, format.BlueMax);

                var mi = y * maskStride + (x >> 3);
                var opaque = mi < bitmask.Length && (bitmask[mi] & (0x80 >> (x & 7))) != 0;
                Write(rgba, (y * width + x) * 4, r, g, bl, opaque);
            }
        }

        onCursor(new VncCursorShape(rgba, width, height, hotX, hotY));
    }

    /// <summary>XCursor：两色（fg/bg 各 3 字节 RGB）+ 位图选色 + 掩码定透明。</summary>
    public void UpdateXCursor(int hotX, int hotY, int width, int height,
        byte[] foregroundColor, byte[] backgroundColor, byte[] bitmap, byte[] bitmask)
    {
        if (width <= 0 || height <= 0)
        {
            onCursor(null);
            return;
        }

        var rgba = new byte[width * height * 4];
        var stride = (width + 7) / 8;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var bit = 0x80 >> (x & 7);
                var idx = y * stride + (x >> 3);
                var opaque = idx < bitmask.Length && (bitmask[idx] & bit) != 0;
                var isFg = idx < bitmap.Length && (bitmap[idx] & bit) != 0;
                var c = isFg ? foregroundColor : backgroundColor;
                Write(rgba, (y * width + x) * 4,
                    c.Length > 0 ? c[0] : (byte)0,
                    c.Length > 1 ? c[1] : (byte)0,
                    c.Length > 2 ? c[2] : (byte)0,
                    opaque);
            }
        }

        onCursor(new VncCursorShape(rgba, width, height, hotX, hotY));
    }

    private static void Write(byte[] dst, int o, byte r, byte g, byte b, bool opaque)
    {
        // 预乘：不透明像素原样，透明像素全 0。
        var a = opaque ? (byte)255 : (byte)0;
        dst[o] = opaque ? r : (byte)0;
        dst[o + 1] = opaque ? g : (byte)0;
        dst[o + 2] = opaque ? b : (byte)0;
        dst[o + 3] = a;
    }

    private static byte Scale(uint value, ushort max)
        => max == 0 ? (byte)0 : (byte)Math.Min(255, value * 255 / max);
}
