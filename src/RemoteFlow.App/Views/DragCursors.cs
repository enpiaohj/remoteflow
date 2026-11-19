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

    // 矢量路径取自 Font Awesome Free 6.5.2（CC BY 4.0，Copyright 2024 Fonticons, Inc.，https://fontawesome.com/license/free）：
    // 张开的手掌 = solid/hand（viewBox 512×512）；抓握 = solid/hand-back-fist（viewBox 448×512）。
    // 本处缩放到 32×32 画布并加描边渲染成光标位图，署名见 THIRD-PARTY-NOTICES.md。
    private const string OpenHandPath =
        "M288 32c0-17.7-14.3-32-32-32s-32 14.3-32 32V240c0 8.8-7.2 16-16 16s-16-7.2-16-16V64c0-17.7-14.3-32-32-32s-32 14.3-32 32V336c0 1.5 0 3.1 .1 4.6L67.6 283c-16-15.2-41.3-14.6-56.6 1.4s-14.6 41.3 1.4 56.6L124.8 448c43.1 41.1 100.4 64 160 64H304c97.2 0 176-78.8 176-176V128c0-17.7-14.3-32-32-32s-32 14.3-32 32V240c0 8.8-7.2 16-16 16s-16-7.2-16-16V64c0-17.7-14.3-32-32-32s-32 14.3-32 32V240c0 8.8-7.2 16-16 16s-16-7.2-16-16V32z";

    private const string ClosedHandPath =
        "M144 0C117.5 0 96 21.5 96 48V96v28.5V176c0 8.8-7.2 16-16 16s-16-7.2-16-16V149.3l-9 7.5C40.4 169 32 187 32 206V244c0 38 16.9 74 46.1 98.3L128 384v96c0 17.7 14.3 32 32 32H320c17.7 0 32-14.3 32-32V374.7c46.9-19 80-65 80-118.7V176 160 144c0-26.5-21.5-48-48-48c-12.4 0-23.6 4.7-32.1 12.3C350 83.5 329.3 64 304 64c-12.4 0-23.6 4.7-32.1 12.3C270 51.5 249.3 32 224 32c-12.4 0-23.6 4.7-32.1 12.3C190 19.5 169.3 0 144 0z";

    /// <summary>手形几何，已缩放并居中到 32×32 画布（四周留 2.5 单位给描边）。</summary>
    private static Geometry HandShape(bool closed)
    {
        var geometry = Geometry.Parse(closed ? ClosedHandPath : OpenHandPath).Clone();
        var bounds = geometry.Bounds;
        const double box = 27;
        var scale = box / Math.Max(bounds.Width, bounds.Height);
        var group = new TransformGroup();
        group.Children.Add(new TranslateTransform(-bounds.X - bounds.Width / 2, -bounds.Y - bounds.Height / 2));
        group.Children.Add(new ScaleTransform(scale, scale));
        group.Children.Add(new TranslateTransform(16, 16));
        geometry.Transform = group;
        geometry.Freeze();
        return geometry;
    }
    /// <summary>把手形渲染成 <paramref name="size"/> 像素见方的 BGRA（非预乘）像素，自上而下。</summary>
    internal static byte[] RenderPixels(bool closed, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 32.0, size / 32.0));
            // 先铺一层加粗的黑色（外描边），再铺白色填充：描边只向外扩，不吃掉手指之间的细缝。
            var shape = HandShape(closed);
            dc.DrawGeometry(Brushes.Black, new Pen(Brushes.Black, 2.6) { LineJoin = PenLineJoin.Round }, shape);
            dc.DrawGeometry(Brushes.White, null, shape);
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
