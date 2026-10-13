using System.Text.Json;

namespace RemoteFlow.Infrastructure.Sync;

/// <summary>
/// 只读取自家 Access Token 的 payload（不校验签名——签名由服务端把关，客户端只需 <c>sub</c>）。
/// </summary>
internal static class JwtReader
{
    public static Guid ReadSubject(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            throw new FormatException("Access token is not a JWT.");
        }

        using var document = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        if (!document.RootElement.TryGetProperty("sub", out var sub) || sub.GetString() is not { } value)
        {
            throw new FormatException("Access token has no 'sub' claim.");
        }

        return Guid.Parse(value);
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded,
        };
        return Convert.FromBase64String(padded);
    }
}
