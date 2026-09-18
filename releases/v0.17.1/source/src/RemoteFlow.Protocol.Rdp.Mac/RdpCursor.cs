namespace RemoteFlow.Protocol.Rdp.Mac;

/// <summary>
/// 远端光标形状。<see cref="RdpSession.CursorChanged"/> 携带。
/// UI 层据此在本地鼠标位置画光标，实现零延迟。
/// </summary>
/// <param name="Rgba">RGBA32 像素（宽 × 高 × 4）；隐藏 / 默认时为 null。</param>
/// <param name="Width">宽（像素）。<see cref="Hidden"/> 时 0，<see cref="IsDefault"/> 时 -1。</param>
/// <param name="Height">高（像素）。</param>
/// <param name="HotX">热点 X（相对光标图左上角）。</param>
/// <param name="HotY">热点 Y。</param>
public sealed record RdpCursor(byte[]? Rgba, int Width, int Height, int HotX, int HotY)
{
    /// <summary>隐藏光标（服务端 SYSPTR_NULL）。</summary>
    public static readonly RdpCursor Hidden = new(null, 0, 0, 0, 0);

    /// <summary>回到系统默认箭头（服务端 SYSPTR_DEFAULT）。</summary>
    public static readonly RdpCursor Default = new(null, -1, 0, 0, 0);

    public bool IsHidden => Rgba is null && Width == 0;

    public bool IsDefault => Rgba is null && Width < 0;
}
