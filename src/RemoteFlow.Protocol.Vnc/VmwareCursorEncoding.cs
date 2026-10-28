using MarcusW.VncClient;
using MarcusW.VncClient.Protocol;
using MarcusW.VncClient.Protocol.Implementation.EncodingTypes.Pseudo;

namespace RemoteFlow.Protocol.Vnc;

/// <summary>
/// VMware 光标伪编码（0x574D5664）。客户端声明支持后，ESXi 只下发指针形状、由客户端自己画；
/// 不声明时服务器会把指针合成进画面，指针移动后旧位置不会被擦掉，留下残影（实机现象）。
/// 格式参照 noVNC（MPL-2.0）对该编码的处理：类型 1 为逐像素 RGBA，类型 0 为 AND / XOR 双掩码。
/// </summary>
public sealed class VmwareCursorEncodingType(RfbConnectionContext context) : PseudoEncodingType
{
    public const int EncodingId = 0x574D5664;

    /// <summary>超过该尺寸的光标不解码（仍按长度读走，保证流不错位）。正常指针不超过 64×64。</summary>
    private const int MaxDecodedSide = 256;

    public override int Id => EncodingId;

    public override string Name => "VMware Cursor";

    public override bool GetsConfirmed => true;

    public override void ReadPseudoEncoding(Stream transportStream, Rectangle rectangle)
    {
        // 矩形位置是热点，尺寸是光标宽高。
        var header = new byte[2];
        transportStream.ReadExactly(header);
        var cursorType = header[0];
        var width = rectangle.Size.Width;
        var height = rectangle.Size.Height;

        var length = VmwareCursorDecoder.PayloadLength(cursorType, width, height)
            ?? throw new NotSupportedException($"不支持的 VMware 光标类型 {cursorType}。");

        var payload = new byte[length];
        transportStream.ReadExactly(payload);

        var handler = context.Connection.CursorHandler;
        if (handler is null)
        {
            return;
        }

        if (width <= 0 || height <= 0 || width > MaxDecodedSide || height > MaxDecodedSide)
        {
            handler.HideCursor();
            return;
        }

        var rgba = VmwareCursorDecoder.Decode(cursorType, payload, width, height);
        handler.UpdateCursorWithAlpha(rectangle.Position.X, rectangle.Position.Y, width, height, rgba);
    }
}

/// <summary>VMware 光标数据解码（纯函数，便于单元测试）。输出预乘 alpha 的 RGBA32。</summary>
public static class VmwareCursorDecoder
{
    /// <summary>负载长度（不含 2 字节头）；未知类型返回 null（无法确定长度，调用方必须中止）。</summary>
    public static int? PayloadLength(byte cursorType, int width, int height)
    {
        var pixels = Math.Max(0, width) * Math.Max(0, height);
        return cursorType switch
        {
            0 => pixels * 4 * 2, // AND 掩码 + XOR 掩码
            1 => pixels * 4,     // RGBA
            _ => null
        };
    }

    public static byte[] Decode(byte cursorType, ReadOnlySpan<byte> payload, int width, int height)
    {
        var pixels = width * height;
        var rgba = new byte[pixels * 4];

        if (cursorType == 1)
        {
            // 每像素 R、G、B、A（未预乘），转成预乘 alpha。
            for (var i = 0; i < pixels; i++)
            {
                var a = payload[i * 4 + 3];
                rgba[i * 4] = Premultiply(payload[i * 4], a);
                rgba[i * 4 + 1] = Premultiply(payload[i * 4 + 1], a);
                rgba[i * 4 + 2] = Premultiply(payload[i * 4 + 2], a);
                rgba[i * 4 + 3] = a;
            }

            return rgba;
        }

        // 经典光标：AND 为 0 → 不透明，颜色取 XOR（字节序 B、G、R、X）；
        // AND 全 1 且 XOR 为 0 → 透明；其余（反色等浏览器 / WPF 无法表达的情况）→ 黑色不透明。
        var xorOffset = pixels * 4;
        for (var i = 0; i < pixels; i++)
        {
            var and = ReadUInt32BigEndian(payload, i * 4);
            var xorBytes = payload.Slice(xorOffset + i * 4, 4);
            var xor = ReadUInt32BigEndian(payload, xorOffset + i * 4);
            var o = i * 4;

            if (and == 0)
            {
                rgba[o] = xorBytes[2];
                rgba[o + 1] = xorBytes[1];
                rgba[o + 2] = xorBytes[0];
                rgba[o + 3] = 255;
            }
            else if ((and & 0xFFFFFF00) == 0xFFFFFF00 && xor == 0)
            {
                // 透明：全 0。
            }
            else
            {
                rgba[o + 3] = 255; // 黑色不透明
            }
        }

        return rgba;
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> data, int offset)
        => (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);

    private static byte Premultiply(byte value, byte alpha) => (byte)(value * alpha / 255);
}
