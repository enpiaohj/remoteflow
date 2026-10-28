using System.Security.Authentication;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteFlow.Infrastructure.Vmware;

/// <summary>
/// ESXi 默认使用自签名证书且主机名通常与访问地址不符，系统校验必然失败。
/// 这里提供「首次取得指纹供用户核对 → 之后只接受该指纹」的固定机制，绝不做全局放行。
/// </summary>
public static class VmwareTls
{
    /// <summary>规范化 SHA-256 指纹：去掉分隔符并转大写，便于用户粘贴 ESXi 页面上的格式。</summary>
    public static string NormalizePin(string? value)
    {
        var hex = new string((value ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return hex.Length == 64 ? hex : string.Empty;
    }

    public static string ComputePin(X509Certificate certificate)
        => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    /// <summary>仅用于首次核对：连接并返回证书指纹，不发送任何应用数据，也不信任该证书。</summary>
    public static async Task<string> FetchPinAsync(string host, int port, CancellationToken ct = default)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);
        string? pin = null;
        await using var ssl = new SslStream(client.GetStream(), false, (_, cert, _, _) =>
        {
            pin = cert is null ? null : ComputePin(cert);
            return false; // 取指纹后拒绝握手：此连接不用于传输数据
        });
        try { await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct); }
        catch (AuthenticationException) when (pin is not null) { }
        return pin ?? throw new InvalidOperationException("未取得服务器证书。");
    }

    /// <summary>只接受指纹完全一致的证书；证书变化即握手失败。</summary>
    public static HttpClientHandler CreatePinnedHandler(string pin)
    {
        var expected = NormalizePin(pin);
        if (expected.Length == 0) throw new ArgumentException("证书指纹必须是 64 位十六进制 SHA-256。", nameof(pin));
        return new HttpClientHandler
        {
            CookieContainer = new System.Net.CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert is not null && CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(ComputePin(cert)), Convert.FromHexString(expected))
        };
    }

    public static bool PinMatches(X509Certificate certificate, string pin)
    {
        var expected = NormalizePin(pin);
        return expected.Length != 0 && CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(ComputePin(certificate)), Convert.FromHexString(expected));
    }
}
