using System.Text.RegularExpressions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.Protocol.PowerShell;

/// <summary>把 <c>New-PSSession</c> 的失败翻译成标准错误码与可操作的中文说明。</summary>
internal static partial class PowerShellErrorMapper
{
    // WinRM 错误码（PSRemotingTransportException.ErrorCode）。
    private const int AccessDenied = 5;
    private const int LogonFailure = 1326;
    private const int CannotConnect = -2144108526;   // 0x80338012
    private const int NotTrusted = -2144108316;      // 0x803380E4

    public static (ConnectionErrorCode Code, string Message) Map(string errorId, int code, string detail, string host)
    {
        if (code is AccessDenied or LogonFailure || errorId.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase)
            || errorId.Contains("LogonFailure", StringComparison.OrdinalIgnoreCase))
        {
            return (ConnectionErrorCode.AuthenticationFailed,
                "身份验证被拒绝。请检查用户名、密码和域；该账号还需要在目标上有远程管理权限"
                + "（Administrators 或 Remote Management Users 组）。用本地账户连接时，可尝试把用户名写成「主机名\\用户名」。"
                + $"\n详细信息：{detail}");
        }

        // 调用前已确认主机名能解析，因此走到这里是认证方式的限制，而不是名字写错。
        if (code == NotTrusted || errorId.Contains("ServerNotTrusted", StringComparison.OrdinalIgnoreCase))
        {
            return (ConnectionErrorCode.ProtocolNegotiationFailed, NotTrustedMessage(host, detail));
        }

        if (code == CannotConnect || errorId.Contains("CannotConnect", StringComparison.OrdinalIgnoreCase))
        {
            return (ConnectionErrorCode.NetworkUnreachable,
                $"无法建立 PowerShell 远程连接。请确认目标已启用 PowerShell 远程（在目标上以管理员运行 Enable-PSRemoting）。\n详细信息：{detail}");
        }

        return (ConnectionErrorCode.Unknown,
            string.IsNullOrWhiteSpace(detail) ? ConnectionException.Describe(ConnectionErrorCode.Unknown) : $"PowerShell 远程连接失败：{detail}");
    }

    private static string NotTrustedMessage(string host, string detail)
    {
        var message = "用 IP 地址或非域环境连接时，Windows 要求目标启用 HTTPS，或把它加入本机的 TrustedHosts"
                      + "（本版暂不支持 HTTPS）。能用域内主机名连接时，请优先把连接的主机地址改为主机名。";

        // 只有主机地址由安全字符组成时才拼进可复制的命令，避免诱导用户执行夹带内容的命令。
        if (SafeHost().IsMatch(host))
        {
            message += "\n如确认目标可信，可在本机以管理员身份运行 PowerShell 执行：\n"
                       + $"Set-Item WSMan:\\localhost\\Client\\TrustedHosts -Value '{host}' -Concatenate\n"
                       + "注意：该设置影响本机所有用户，且列表中的主机不会被验证身份。RemoteFlow 不会替你修改它。";
        }

        return $"{message}\n详细信息：{detail}";
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._:\-\[\]]+\z")]
    private static partial Regex SafeHost();
}
