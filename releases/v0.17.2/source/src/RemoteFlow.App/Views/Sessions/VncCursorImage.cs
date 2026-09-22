using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32.SafeHandles;
using RemoteFlow.Protocol.Vnc;

namespace RemoteFlow.App.Views.Sessions;

/// <summary>
/// 把 VNC 服务端下发的光标形状（<see cref="VncCursorShape"/>，RGBA + 热点）转成一个
/// 原生 <c>HCURSOR</c>，再包成 WPF <see cref="Cursor"/>。
/// <para>
/// 走 <c>CreateIconIndirect(fIcon = FALSE)</c>——和 macOS 侧用 <c>NSCursor</c> 同思路：
/// 让操作系统按热点渲染 / 定位光标，不在 WPF 里做叠加层。用完 <see cref="Cursor.Dispose"/>
/// 释放（SafeHandle 会调 <c>DestroyIcon</c>）。
/// </para>
/// </summary>
internal static class VncCursorImage
{
    /// <summary>形状为 null（服务端隐藏光标）时用它——一个 1×1 全透明光标。</summary>
    public static Cursor Hidden { get; } = FromRgba(new byte[] { 0, 0, 0, 0 }, 1, 1, 0, 0) ?? Cursors.None;

    /// <summary>失败返回 null，调用方回退到系统箭头。</summary>
    public static Cursor? FromRgba(byte[] rgba, int width, int height, int hotX, int hotY)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
        {
            return null;
        }

        // RGBA（服务端字节序）→ 预乘? 不需要——GDI 的 32bpp DIB 直接吃 BGRA，
        // Alpha 由 CreateIconIndirect 在 32bpp 彩色位图存在 Alpha 时自行处理。
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            bgra[i * 4 + 0] = rgba[i * 4 + 2]; // B
            bgra[i * 4 + 1] = rgba[i * 4 + 1]; // G
            bgra[i * 4 + 2] = rgba[i * 4 + 0]; // R
            bgra[i * 4 + 3] = rgba[i * 4 + 3]; // A
        }

        nint colorBitmap = 0;
        nint maskBitmap = 0;
        try
        {
            var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                // 自顶向下 DIB：biHeight 取负。
                var header = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                };

                colorBitmap = CreateDIBitmapFromBits(header, handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }

            if (colorBitmap == 0)
            {
                return null;
            }

            // 32bpp 带 Alpha 时 AND 掩码可全 0（全部取彩色位图，透明由 Alpha 决定）。
            maskBitmap = CreateBitmap(width, height, 1, 1, nint.Zero);
            if (maskBitmap == 0)
            {
                return null;
            }

            var iconInfo = new ICONINFO
            {
                fIcon = false, // 光标（带热点）
                xHotspot = (uint)Math.Clamp(hotX, 0, width - 1),
                yHotspot = (uint)Math.Clamp(hotY, 0, height - 1),
                hbmMask = maskBitmap,
                hbmColor = colorBitmap,
            };

            var hCursor = CreateIconIndirect(ref iconInfo);
            if (hCursor == 0)
            {
                return null;
            }

            return CursorInteropHelper.Create(new SafeIconHandle(hCursor));
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (colorBitmap != 0) DeleteObject(colorBitmap);
            if (maskBitmap != 0) DeleteObject(maskBitmap);
        }
    }

    private static nint CreateDIBitmapFromBits(BITMAPINFOHEADER header, nint bits)
    {
        var screenDc = GetDC(nint.Zero);
        try
        {
            var info = new BITMAPINFO { bmiHeader = header, bmiColors = new byte[256 * 4] };
            return CreateDIBitmap(screenDc, ref header, CBM_INIT, bits, ref info, DIB_RGB_COLORS);
        }
        finally
        {
            ReleaseDC(nint.Zero, screenDc);
        }
    }

    private sealed class SafeIconHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeIconHandle(nint handle) : base(ownsHandle: true) => SetHandle(handle);

        protected override bool ReleaseHandle() => DestroyIcon(handle);
    }

    private const uint CBM_INIT = 0x04;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256 * 4)]
        public byte[] bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateIconIndirect(ref ICONINFO iconInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateBitmap(int width, int height, uint planes, uint bitCount, nint bits);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBitmap(
        nint hdc, ref BITMAPINFOHEADER header, uint init, nint bits, ref BITMAPINFO data, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);
}
