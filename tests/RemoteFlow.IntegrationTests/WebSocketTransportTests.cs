using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RemoteFlow.Protocol.Vnc;
using Xunit;

namespace RemoteFlow.IntegrationTests;

public sealed class WebSocketTransportTests
{
    [Fact]
    public async Task 指纹一致时可收发且按字节流读取()
    {
        using var cert = CreateCertificate();
        await using var server = new WsServer(cert);
        var connector = new WebSocketTransportConnector(Parameters(server.Port, Pin(cert)));

        using var transport = await connector.ConnectAsync();
        Assert.True(transport.IsEncrypted);
        var stream = transport.Stream;

        var greeting = new byte[12];
        await stream.ReadExactlyAsync(greeting);
        Assert.Equal("RFB 003.008\n", Encoding.ASCII.GetString(greeting));

        await stream.WriteAsync("ping"u8.ToArray());
        var echo = new byte[4];
        await stream.ReadExactlyAsync(echo);
        Assert.Equal("ping", Encoding.ASCII.GetString(echo));
    }

    [Fact]
    public async Task 指纹不一致时拒绝连接()
    {
        using var cert = CreateCertificate();
        using var other = CreateCertificate();
        await using var server = new WsServer(cert);
        var connector = new WebSocketTransportConnector(Parameters(server.Port, Pin(other)));

        await Assert.ThrowsAnyAsync<Exception>(() => connector.ConnectAsync());
    }

    [Fact]
    public void 参数拒绝非wss地址和空指纹()
    {
        var plain = new WebSocketTransportParameters { Url = new Uri("ws://127.0.0.1:1/x"), PinSha256 = "AA" };
        Assert.Throws<InvalidOperationException>(plain.Validate);
        var noPin = new WebSocketTransportParameters { Url = new Uri("wss://127.0.0.1:1/x"), PinSha256 = " " };
        Assert.Throws<InvalidOperationException>(noPin.Validate);
    }

    [Fact]
    public void ToString不包含票据()
    {
        var parameters = new WebSocketTransportParameters { Url = new Uri("wss://host.example:443/ticket/secret-ticket"), PinSha256 = "AA" };
        Assert.DoesNotContain("secret-ticket", parameters.ToString());
    }

    private static WebSocketTransportParameters Parameters(int port, string pin)
        => new() { Url = new Uri($"wss://127.0.0.1:{port}/ticket/sample"), PinSha256 = pin };

    private static string Pin(X509Certificate2 cert) => Convert.ToHexString(SHA256.HashData(cert.RawData));

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }

    /// <summary>最小 TLS WebSocket 服务端：握手后先发 RFB 版本串，再把收到的数据原样回送。</summary>
    private sealed class WsServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public WsServer(X509Certificate2 certificate)
        {
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                    catch (OperationCanceledException) { return; }
                    _ = Task.Run(() => Serve(client, certificate));
                }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private static async Task Serve(TcpClient client, X509Certificate2 certificate)
        {
            using (client)
            {
                try
                {
                    await using var ssl = new SslStream(client.GetStream());
                    await ssl.AuthenticateAsServerAsync(certificate);
                    var request = new StringBuilder();
                    var one = new byte[1];
                    while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (await ssl.ReadAsync(one) == 0) return;
                        request.Append((char)one[0]);
                    }
                    var key = request.ToString().Split("\r\n")
                        .First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim();
                    var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes(
                        "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                        $"Sec-WebSocket-Accept: {accept}\r\nSec-WebSocket-Protocol: binary\r\n\r\n"));
                    await SendFrame(ssl, "RFB 003.008\n"u8.ToArray());

                    // 读一个客户端帧（带掩码），去掩码后回送。
                    var head = new byte[2];
                    await ssl.ReadExactlyAsync(head);
                    var length = head[1] & 0x7F;
                    var mask = new byte[4];
                    await ssl.ReadExactlyAsync(mask);
                    var payload = new byte[length];
                    await ssl.ReadExactlyAsync(payload);
                    for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
                    await SendFrame(ssl, payload);
                    await Task.Delay(200);
                }
                catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or SocketException) { }
            }
        }

        private static async Task SendFrame(Stream stream, byte[] payload)
        {
            await stream.WriteAsync(new byte[] { 0x82, (byte)payload.Length });
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            await _loop;
            _cts.Dispose();
        }
    }
}
