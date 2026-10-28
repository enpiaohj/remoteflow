using System.Net.WebSockets;
using System.Security.Cryptography;
using MarcusW.VncClient;
using MarcusW.VncClient.Protocol;
using MarcusW.VncClient.Protocol.Implementation;
using MarcusW.VncClient.Protocol.Services;
using MarcusW.VncClient.Utils;

namespace RemoteFlow.Protocol.Vnc;

/// <summary>
/// 经 WebSocket 承载的 RFB 传输参数（如 ESXi 的 webmks 票据流）。
/// 证书只接受 <see cref="PinSha256"/> 指向的那一张，不做全局放行。
/// <para><see cref="Url"/> 含一次性票据，属于敏感数据：不得写入日志，ToString 已刻意隐藏。</para>
/// </summary>
public sealed class WebSocketTransportParameters : TransportParameters
{
    public required Uri Url { get; init; }

    /// <summary>期望的服务器证书 SHA-256 指纹（十六进制，大小写及分隔符不限）。</summary>
    public required string PinSha256 { get; init; }

    public string SubProtocol { get; init; } = "binary";

    public override string ToString() => $"WebSocket {Url.Host}:{Url.Port}";

    public override void Validate()
    {
        if (Url.Scheme != "wss") throw new InvalidOperationException("仅支持 wss 地址。");
        if (string.IsNullOrWhiteSpace(PinSha256)) throw new InvalidOperationException("必须提供证书指纹。");
    }

    protected override IEnumerable<FreezableParametersObject> GetDescendants() => [];
}

/// <summary>只替换传输层：其余 RFB 解码、编码、消息处理沿用库的默认实现。</summary>
public sealed class WebSocketVncImplementation : DefaultImplementation
{
    public override ITransportConnector CreateTransportConnector(RfbConnectionContext context)
        => context.Connection.Parameters.TransportParameters is WebSocketTransportParameters parameters
            ? new WebSocketTransportConnector(parameters)
            : base.CreateTransportConnector(context);
}

public sealed class WebSocketTransportConnector(WebSocketTransportParameters parameters) : ITransportConnector
{
    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var expected = NormalizePin(parameters.PinSha256);
        if (expected.Length != 64) throw new InvalidOperationException("证书指纹必须是 64 位十六进制 SHA-256。");

        var socket = new ClientWebSocket();
        try
        {
            socket.Options.AddSubProtocol(parameters.SubProtocol);
            socket.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
                cert is not null && CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(cert.GetRawCertData()), Convert.FromHexString(expected));
            await socket.ConnectAsync(parameters.Url, cancellationToken).ConfigureAwait(false);
            return new WebSocketTransport(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static string NormalizePin(string value)
        => new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
}

internal sealed class WebSocketTransport(ClientWebSocket socket) : ITransport
{
    private WebSocketStream? _stream;

    public Stream Stream => _stream ??= new WebSocketStream(socket);

    // 证书由指纹固定，通道本身是 TLS。
    public bool IsEncrypted => true;

    public void Dispose() => _stream?.Dispose();
}

/// <summary>
/// 把 WebSocket 二进制消息流适配成字节流。RFB 自己负责分帧，
/// 因此这里不保留消息边界，读到的消息内容按顺序拼接即可。
/// </summary>
internal sealed class WebSocketStream(ClientWebSocket socket) : Stream
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _closed;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        while (true)
        {
            if (_closed || socket.State is not (WebSocketState.Open or WebSocketState.CloseSent)) return 0;
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                _closed = true;
                return 0;
            }
            // 空消息不代表流结束，继续读下一条。
            if (result.Count > 0) return result.Count;
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(buffer, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closed = true;
            socket.Dispose();
            _sendLock.Dispose();
        }
        base.Dispose(disposing);
    }
}
