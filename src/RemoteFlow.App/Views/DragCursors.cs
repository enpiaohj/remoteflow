using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RemoteFlow.App.Views;

/// <summary>
/// 拖动用的手掌光标：悬停在拖动手柄上是<b>张开的手掌</b>，拖动中是<b>抓握的手</b>。
/// <para>
/// Windows / WPF 没有内置的抓握手掌（<see cref="Cursors.Hand"/> 是食指指向的手形，用于「可点击」），
/// 所以自绘：白色填充 + 黑色描边，与系统光标风格一致。绘制成 32 / 48 / 64 三个尺寸打进同一个 .cur，
/// 由系统按当前光标大小与 DPI 挑选，避免高分屏上发虚。
/// </para>
/// </summary>
public static class DragCursors
{
    private static Cursor? _open;
    private static Cursor? _closed;

    /// <summary>张开的手掌：提示「这里可以拖」。</summary>
    public static Cursor Open => _open ??= new Cursor(new MemoryStream(BuildCursorFile(closed: false)));

    /// <summary>抓握的手：正在拖动。</summary>
    public static Cursor Closed => _closed ??= new Cursor(new MemoryStream(BuildCursorFile(closed: true)));

    private static readonly int[] Sizes = [32, 48, 64];

    /// <summary>手掌 / 拳头的形状，坐标系 32×32。每个形状先以黑色加粗描边铺一层，再以白色铺一层，
    /// 形状之间留出的细缝因此显示为黑色分隔线（手指之间的线）。</summary>
    private static IEnumerable<Geometry> Shapes(bool closed)
    {
        static Geometry Round(double x, double y, double w, double h, double r)
        {
            var g = new RectangleGeometry(new Rect(x, y, w, h), r, r);
            g.Freeze();
            return g;
        }

        if (closed)
        {
            // 拳头：手背 + 四个指节 + 横在前面的拇指。
            yield return Round(8.5, 14.5, 16.5, 13, 5);
            yield return Round(9.2, 11, 3.4, 9, 1.7);
            yield return Round(13.2, 10.2, 3.4, 9.8, 1.7);
            yield return Round(17.2, 10.6, 3.4, 9.4, 1.7);
            yield return Round(21.2, 11.6, 3.4, 8.4, 1.7);
            yield return Round(9.8, 19.6, 8.2, 3.6, 1.8);
            yield break;
        }

        // 手掌：手心 + 四指 + 斜向左上的拇指。
        yield return Round(9, 16, 15.6, 12, 4.5);
        yield return Round(9.4, 5, 3.3, 15, 1.65);
        yield return Round(13.5, 2.5, 3.3, 17, 1.65);
        yield return Round(17.6, 4, 3.3, 16, 1.65);
        yield return Round(21.7, 8, 3.3, 13, 1.65);

        var thumb = new RectangleGeometry(new Rect(-1.7, 0, 3.4, 11), 1.7, 1.7)
        {
            Transform = new TransformGroup
            {
                Children = [new RotateTransform(143), new TranslateTransform(10, 22)]
            }
        };
        thumb.Freeze();
        yield return thumb;
    }

    /// <summary>把手形渲染成 <paramref name="size"/> 像素见方的 BGRA（非预乘）像素，自上而下。</summary>
    internal static byte[] RenderPixels(bool closed, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 32.0, size / 32.0));
            var outline = new Pen(Brushes.Black, 2.4) { LineJoin = PenLineJoin.Round };
            var shapes = Shapes(closed).ToList();
            foreach (var shape in shapes)
            {
                dc.DrawGeometry(Brushes.Black, outline, shape);
            }

            foreach (var shape in shapes)
            {
                dc.DrawGeometry(Brushes.White, null, shape);
            }

            dc.Pop();
        }

        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var pixels = new byte[size * size * 4];
        target.CopyPixels(pixels, size * 4, 0);

        // 预乘 → 非预乘：.cur 里的 32 位 DIB 按非预乘 alpha 存放。
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha is > 0 and < 255)
            {
                pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / alpha);
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / alpha);
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / alpha);
            }
        }

        return pixels;
    }

    /// <summary>生成多尺寸 .cur 文件内容（32 位带 alpha 的 DIB，热点在手心）。</summary>
    internal static byte[] BuildCursorFile(bool closed)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);
        writer.Write((ushort)2); // 类型：光标
        writer.Write((ushort)Sizes.Length);

        var images = Sizes.Select(size => (Size: size, Data: BuildDib(closed, size))).ToList();
        var offset = 6 + 16 * images.Count;
        foreach (var (size, data) in images)
        {
            writer.Write((byte)size);
            writer.Write((byte)size);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)(size / 2)); // 热点 X：手心
            writer.Write((ushort)(size / 2)); // 热点 Y
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }

        foreach (var (_, data) in images)
        {
            writer.Write(data);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildDib(bool closed, int size)
    {
        var pixels = RenderPixels(closed, size);
        var maskStride = (size + 31) / 32 * 4;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(40);          // BITMAPINFOHEADER
        writer.Write(size);
        writer.Write(size * 2);    // 高度含 AND 掩码，故为两倍
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(size * size * 4 + maskStride * size);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        // DIB 像素自下而上。
        for (var y = size - 1; y >= 0; y--)
        {
            writer.Write(pixels, y * size * 4, size * 4);
        }

        writer.Write(new byte[maskStride * size]); // AND 掩码全 0：完全由 alpha 决定
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>仅供预览 / 测试：把手形渲染为 PNG 字节。</summary>
    internal static byte[] RenderPreviewPng(bool closed, int size)
    {
        var pixels = RenderPixels(closed, size);
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
