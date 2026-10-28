using RemoteFlow.Protocol.Vnc;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>VMware 光标伪编码解码：输出预乘 alpha 的 RGBA32。</summary>
public sealed class VmwareCursorDecoderTests
{
    [Theory]
    [InlineData(0, 2, 3, 2 * 3 * 4 * 2)]
    [InlineData(1, 2, 3, 2 * 3 * 4)]
    [InlineData(1, 0, 0, 0)]
    public void 负载长度按类型计算(byte type, int width, int height, int expected)
        => Assert.Equal(expected, VmwareCursorDecoder.PayloadLength(type, width, height));

    [Fact]
    public void 未知类型无法确定长度必须中止()
        => Assert.Null(VmwareCursorDecoder.PayloadLength(7, 16, 16));

    [Fact]
    public void 带透明度光标转为预乘alpha()
    {
        // 两个像素：不透明红、半透明白。
        byte[] payload = [255, 0, 0, 255, /**/ 255, 255, 255, 128];

        var rgba = VmwareCursorDecoder.Decode(1, payload, 2, 1);

        Assert.Equal([255, 0, 0, 255], rgba[..4]);
        Assert.Equal([128, 128, 128, 128], rgba[4..]);
    }

    [Fact]
    public void 经典光标_AND为0时取XOR颜色且字节序为BGRX()
    {
        // AND = 0；XOR 字节 B=0x10 G=0x20 R=0x30 X=0
        byte[] payload = [0, 0, 0, 0, /**/ 0x10, 0x20, 0x30, 0x00];

        var rgba = VmwareCursorDecoder.Decode(0, payload, 1, 1);

        Assert.Equal([0x30, 0x20, 0x10, 255], rgba);
    }

    [Fact]
    public void 经典光标_AND全1且XOR为0时透明()
    {
        byte[] payload = [0xFF, 0xFF, 0xFF, 0xFF, /**/ 0, 0, 0, 0];

        Assert.Equal([0, 0, 0, 0], VmwareCursorDecoder.Decode(0, payload, 1, 1));
    }

    [Fact]
    public void 经典光标_反色像素无法表达时画成黑色不透明()
    {
        byte[] payload = [0xFF, 0xFF, 0xFF, 0xFF, /**/ 0xFF, 0xFF, 0xFF, 0xFF];

        Assert.Equal([0, 0, 0, 255], VmwareCursorDecoder.Decode(0, payload, 1, 1));
    }

    [Fact]
    public void 编码编号与VMware约定一致()
        => Assert.Equal(0x574D5664, VmwareCursorEncodingType.EncodingId);
}
