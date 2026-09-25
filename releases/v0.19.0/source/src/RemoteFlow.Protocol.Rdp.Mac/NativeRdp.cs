using System.Runtime.InteropServices;

namespace RemoteFlow.Protocol.Rdp.Mac;

/// <summary>libremoteflow_rdp.dylib（FreeRDP C ABI 封装）的 P/Invoke。</summary>
internal static partial class NativeRdp
{
    private const string Lib = "libremoteflow_rdp";

    // state: 0=connecting 1=connected 2=disconnected 3=failed
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void FrameCallback(nint user, nint bgrx, int width, int height, int stride,
        int dirtyX, int dirtyY, int dirtyWidth, int dirtyHeight);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void StateCallback(nint user, int state, nint message);

    /// <summary>证书校验。返回 0=拒绝 1=接受并记录 2=仅本次接受。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int CertCallback(nint user, nint host, int port, nint commonName, nint fingerprint, int changed);

    /// <summary>
    /// 光标形状变化。rgba != 0 → 设为该图（RGBA32，hotX/hotY 为热点）；
    /// rgba == 0 且 w == 0 → 隐藏；rgba == 0 且 w &lt; 0 → 系统默认箭头。在 FreeRDP 线程上调用。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void CursorCallback(nint user, nint rgba, int w, int h, int hotX, int hotY);

    [LibraryImport(Lib)]
    internal static partial nint rf_rdp_create(nint user, nint frameCb, nint stateCb, nint certCb, nint cursorCb);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int rf_rdp_connect(
        nint handle, string host, int port, string? username, string? domain, string? password,
        int width, int height);

    [LibraryImport(Lib)]
    internal static partial void rf_rdp_send_pointer(nint handle, int x, int y, int ptrFlags);

    [LibraryImport(Lib)]
    internal static partial void rf_rdp_send_wheel(nint handle, int x, int y, int delta);

    [LibraryImport(Lib)]
    internal static partial void rf_rdp_send_key(nint handle, int scancode, int down, int extended);

    [LibraryImport(Lib)]
    internal static partial void rf_rdp_send_unicode(nint handle, ushort code, int down);

    [LibraryImport(Lib)]
    internal static partial void rf_rdp_resize(nint handle, int width, int height);

    [LibraryImport(Lib)]
    internal static partial void rf_rdp_disconnect(nint handle);

    [LibraryImport(Lib)]
    internal static partial void rf_rdp_destroy(nint handle);

    // FreeRDP input.h 常量
    internal const int PtrFlagsMove = 0x0800;
    internal const int PtrFlagsDown = 0x8000;
    internal const int PtrFlagsButton1 = 0x1000; // left
    internal const int PtrFlagsButton2 = 0x2000; // right
    internal const int PtrFlagsButton3 = 0x4000; // middle
}
