using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteFlow.Infrastructure.Vmware;
using Xunit;

namespace RemoteFlow.IntegrationTests;

public sealed class VmwareTlsTests
{
    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99", true)]
    [InlineData("aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899", true)]
    [InlineData("AABB", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 指纹规范化只接受64位十六进制(string? input, bool valid)
        => Assert.Equal(valid, VmwareTls.NormalizePin(input).Length == 64);

    [Fact]
    public async Task 取得指纹并且只接受该指纹的证书()
    {
        using var cert = CreateCertificate();
        using var other = CreateCertificate();
        await using var server = new TlsServer(cert);
        var pin = await VmwareTls.FetchPinAsync("127.0.0.1", server.Port);
        Assert.Equal(VmwareTls.ComputePin(cert), pin);

        using (var ok = new HttpClient(VmwareTls.CreatePinnedHandler(pin)))
            Assert.Equal("pong", await ok.GetStringAsync($"https://127.0.0.1:{server.Port}/"));

        var wrong = VmwareTls.ComputePin(other);
        using var rejected = new HttpClient(VmwareTls.CreatePinnedHandler(wrong));
        await Assert.ThrowsAsync<HttpRequestException>(() => rejected.GetStringAsync($"https://127.0.0.1:{server.Port}/"));
    }

    [Fact]
    public void 指纹比较大小写和分隔符无关()
    {
        using var cert = CreateCertificate();
        var pin = VmwareTls.ComputePin(cert);
        var spaced = string.Join(":", Enumerable.Range(0, 32).Select(i => pin.Substring(i * 2, 2).ToLowerInvariant()));
        Assert.True(VmwareTls.PinMatches(cert, spaced));
        Assert.False(VmwareTls.PinMatches(cert, "00" + pin[2..]));
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }

    private sealed class TlsServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public TlsServer(X509Certificate2 certificate)
        {
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                    catch (OperationCanceledException) { return; }
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            try
                            {
                                await using var ssl = new SslStream(client.GetStream());
                                await ssl.AuthenticateAsServerAsync(certificate);
                                var buffer = new byte[2048];
                                _ = await ssl.ReadAsync(buffer);
                                await ssl.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\npong"u8.ToArray());
                                await ssl.FlushAsync();
                            }
                            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or SocketException) { }
                        }
                    });
                }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            await _loop;
            _cts.Dispose();
        }
    }
}
