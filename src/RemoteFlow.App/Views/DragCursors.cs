using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RemoteFlow.App.Views;

/// <summary>
/// 拖动用的手掌光标：悬停在拖动手柄上是<b>张开的手掌</b>，拖动中是<b>抓握的手</b>。
/// <para>
/// Windows / WPF 没有内置的抓握手掌（<see cref="Cursors.Hand"/> 是食指指向的手形，用于「可点击」），
/// 所以采用现成的专业设计：KDE Plasma 默认光标 Breeze 的 <c>openhand</c> 与 <c>closedhand</c>（即 <c>dnd-move</c>）。
/// 矢量路径取自 https://invent.kde.org/plasma/breeze 的 <c>cursors/Breeze/src/svg</c>，
/// 许可 LGPL-3.0-or-later（Copyright 2014 Uri Herrera and others），署名与许可全文见 THIRD-PARTY-NOTICES.md。
/// 为适配 WPF 仅改动了渲染方式：按 32 / 48 / 64 三个尺寸栅格化进同一个 .cur，由系统按光标大小与 DPI 挑选。
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

    // 每个光标由三层组成（与 Breeze 源 SVG 一致）：模糊阴影（不透明度 0.2）→ 白色主体 → 黑色轮廓与细节。
    private const string OpenShadow =
        "m17.844 5.482c-.852.052-1.434.602-1.844 1.28-.447-1.668-3.616-1.771-4.25.595-.498-.55-1.136-.934-1.937-.875h-.033a2.487 2.487 0 0 0 -2.25 2.906l1.25 7.469c-.473-.334-1.047-.905-1.468-1.094-.978-.438-1.873-.493-2.594-.281-1.442.424-1.875 1.812-1.875 1.812l-.25.719.625.437c1.376.93 2.774 3.183 4.094 5.282.66 1.049 1.315 2.042 2.062 2.843s1.642 1.476 2.781 1.47l8.845-.033h.969l.03-.969s.09-1.745.25-3.812c.162-2.068.41-4.478.688-5.594l1.5-6.031c.433-1.657-.886-3.252-2.593-3.125-.524.03-.987.26-1.375.562l.031-.906a2.49 2.49 0 0 0 -2.656-2.656";

    private const string OpenBody =
        "m11.75 6.357c-.498-.55-1.136-.934-1.937-.875h-.033a2.487 2.487 0 0 0 -2.25 2.906l1.25 7.469c-.473-.334-1.047-.905-1.468-1.094-.978-.438-1.873-.493-2.594-.281-1.442.424-1.875 1.812-1.875 1.812l-.25.719.625.437c1.376.93 2.774 3.183 4.094 5.282.66 1.049 1.315 2.042 2.062 2.843s1.642 1.476 2.781 1.47l8.845-.033h.969l.03-.969s.09-1.745.25-3.812c.162-2.068.41-4.478.688-5.594l1.5-6.031c.433-1.657-.886-3.252-2.593-3.125-.524.03-.987.26-1.375.562l.031-.906a2.49 2.49 0 0 0 -2.656-2.656c-.852.052-1.434.602-1.844 1.28-.447-1.668-3.616-1.771-4.25.595";

    private const string OpenDetail =
        "m13.906 5.482c-.786.047-1.457.808-1.406 1.593l.406 7.094c.02.354-.298.427-.344.156l-1.092-6.562c-.108-.742-.846-1.336-1.594-1.281-.823.074-1.485.935-1.344 1.75l1.5 9q.008.191.063.375c-.059.237-.094.406-.094.406-5-5-6.219-1.406-6.219-1.406 3.398 2.294 5.459 9.456 8.375 9.437l8.843-.031s.33-7.055.969-9.625l1.5-6.031c.24-.92-.615-1.946-1.563-1.875a1.54 1.54 0 0 0 -1.375 1.156l-1.281 5.094c-.07.276-.136.165-.125-.063l.375-7.594c.055-.848-.746-1.648-1.594-1.593-.733.045-1.377.703-1.406 1.437l-.344 6.938c-.017.352-.26.36-.281-.032l-.375-6.905c-.032-.796-.798-1.488-1.594-1.437z";

    private const string ClosedShadow =
        "m17.292 10.92c-.706.043-1.263.408-1.687.905-.756-.755-1.563-.662-1.813-.656-.937.057-1.556.69-1.937 1.469-.43-.26-.895-.477-1.438-.438h-.03c-1.455.131-2.45 1.439-2.22 2.875v.032l.25 1.656-.062.031c-.48.361-1.205.847-1.75 1.656-.545.81-.818 2.004-.406 3.313.449 1.428 1.292 2.917 2.25 4.093a8.4 8.4 0 0 0 1.53 1.47c.538.394 1.098.723 1.813.718l8.844-.031c.503 0 .969-.435 1-.938 0 0 1.908-3.251 1.344-11.312v-.031c-.19-2.167-.913-3.198-2.594-3.094h-.031c-.251.017-.431.197-.656.281-.271-1.154-1.15-2.081-2.407-2";

    private const string ClosedBody =
        "m17.292 9.92c-.706.043-1.263.408-1.687.905-.756-.755-1.563-.662-1.813-.656-.937.057-1.556.69-1.937 1.469-.43-.26-.895-.477-1.438-.438h-.03c-1.455.131-2.45 1.439-2.22 2.875v.032l.25 1.656-.062.031c-.48.361-1.205.847-1.75 1.656-.545.81-.818 2.004-.406 3.313.449 1.428 1.292 2.917 2.25 4.093a8.4 8.4 0 0 0 1.53 1.47c.538.394 1.098.723 1.813.718l8.844-.031c.503 0 .969-.435 1-.938 0 0 1.908-3.251 1.344-11.312v-.031c-.19-2.166-.913-3.198-2.594-3.094h-.031c-.251.017-.431.197-.656.281-.271-1.154-1.15-2.081-2.407-2";

    private const string ClosedDetail =
        "m13.852 11.183c-.786.047-1.457.808-1.407 1.593l.097 1.393c.025.354-.305.428-.343.156l-.122-.861c-.107-.742-.846-1.336-1.594-1.281-.823.074-1.484.935-1.343 1.75l.527 3.299c.005.127-.03.78-.03.78l-.688-1.405c-1.005.756-2.465 1.723-1.792 3.863.796 2.532 3.177 5.583 4.635 5.574l8.844-.031c1.711-3.792 1.392-7.334 1.375-9.844l-.011-1.658c-.006-.95-.615-1.945-1.562-1.875a1.54 1.54 0 0 0 -1.375 1.156l-.177.94c-.052.28-.146.165-.125-.063l.198-2.158c.055-.848-.745-1.648-1.593-1.593-.734.045-1.377.703-1.407 1.437l-.167 1.502c-.039.35-.26.36-.28-.032l-.066-1.205c-.032-.796-.799-1.488-1.594-1.437";
    /// <summary>栅格化为 <paramref name="size"/> 像素见方的 BGRA（非预乘）像素，自上而下。</summary>
    internal static byte[] RenderPixels(bool closed, int size)
    {
        var (shadow, body, detail) = closed
            ? (ClosedShadow, ClosedBody, ClosedDetail)
            : (OpenShadow, OpenBody, OpenDetail);

        // 源图是 32×32 的画布；用 Viewbox 缩放到目标尺寸，矢量放大不发虚。
        var canvas = new Canvas { Width = 32, Height = 32 };
        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(shadow), Fill = Brushes.Black, Opacity = 0.2,
            Effect = new BlurEffect { Radius = 2.2, KernelType = KernelType.Gaussian }
        });
        canvas.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(body), Fill = Brushes.White });
        canvas.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(detail), Fill = Brushes.Black });

        var host = new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = canvas };
        host.Measure(new Size(size, size));
        host.Arrange(new Rect(0, 0, size, size));
        host.UpdateLayout();

        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(host);
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

    /// <summary>生成多尺寸 .cur 文件内容（32 位带 alpha 的 DIB）。热点与 Breeze 一致，在画布中心（16, 16）。</summary>
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
            writer.Write((ushort)(size / 2)); // 热点 X
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

    /// <summary>仅供预览 / 测试：把光标渲染为 PNG 字节。</summary>
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