using MarcusW.VncClient;
using MarcusW.VncClient.Protocol;
using MarcusW.VncClient.Protocol.Implementation.EncodingTypes.Pseudo;

namespace RemoteFlow.Protocol.Vnc;

// VMware 私有光标伪编码（rfbproto 登记）。声明它们是为了让 ESXi 知道客户端自己处理指针，
// 从而不再把指针画进帧缓冲（否则客户机刷新时会在画面里留下残影）。
// 载荷只需按长度读完以保持流同步；指针显示沿用本地圆点光标，暂不使用形状数据。

/// <summary>0x574D5664：光标形状。类型 1 = 32 位带 Alpha；其余 = 掩码式（异或 + 与各一份像素）。</summary>
public sealed class VmwareCursorShapeEncoding : PseudoEncodingType
{
    public const int EncodingId = 0x574D5664;

    public override int Id => EncodingId;
    public override string Name => "VMware Cursor";
    public override bool GetsConfirmed => true;

    public override void ReadPseudoEncoding(Stream transportStream, Rectangle rectangle)
    {
        Span<byte> header = stackalloc byte[2];
        transportStream.ReadExactly(header);
        var pixels = rectangle.Size.Width * rectangle.Size.Height * 4;
        var length = header[0] == 1 ? pixels : pixels * 2;
        var payload = new byte[length];
        transportStream.ReadExactly(payload);
    }
}

/// <summary>0x574D5665：光标状态（可见 / 隐藏等标志，2 字节）。</summary>
public sealed class VmwareCursorStateEncoding : PseudoEncodingType
{
    public const int EncodingId = 0x574D5665;

    public override int Id => EncodingId;
    public override string Name => "VMware Cursor State";
    public override bool GetsConfirmed => true;

    public override void ReadPseudoEncoding(Stream transportStream, Rectangle rectangle)
    {
        Span<byte> flags = stackalloc byte[2];
        transportStream.ReadExactly(flags);
    }
}
