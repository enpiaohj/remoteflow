using System.Text.RegularExpressions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.Protocol.PowerShell;

/// <summary>一次连接失败的呈现内容：说明、可复制的命令、系统原始报错。</summary>
/// <param name="Message">默认显示的一两句结论。</param>
/// <param name="Intro">处理办法前的原因说明，界面折叠在「查看处理办法」里。</param>
internal sealed record PowerShellFailure(
    ConnectionErrorCode Code, string Message, string? Detail, IReadOnlyList<SessionCommandHint> Commands, string? Intro = null);

/// <summary>把 <c>New-PSSession</c> 的失败翻译成标准错误码、可操作的中文说明和处理命令。</summary>
internal static partial class PowerShellErrorMapper
{
    // WinRM 错误码（PSRemotingTransportException.ErrorCode）。
    private const int AccessDenied = 5;
    private const int LogonFailure = 1326;
    private const int CannotConnect = -2144108526;   // 0x80338012
    private const int NotTrusted = -2144108316;      // 0x803380E4

    private const string PolicyPath = @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    /// <summary>在目标电脑上启用 PowerShell 远程的命令。</summary>
    public static SessionCommandHint EnableRemoting(string where) => new(
        $"在{where}以管理员身份运行 PowerShell，启用 PowerShell 远程", "Enable-PSRemoting -Force",
        "撤销：Disable-PSRemoting -Force；Stop-Service WinRM; Set-Service WinRM -StartupType Manual");

    /// <param name="userName">实际发给目标的用户名；空表示使用当前 Windows 身份。</param>
    /// <param name="isLocalTarget">目标是否就是本机。</param>
    public static PowerShellFailure Map(
        string errorId, int code, string detail, string host, string userName = "", bool isLocalTarget = false)
    {
        var raw = string.IsNullOrWhiteSpace(detail) ? null : detail;

        if (code is AccessDenied or LogonFailure || errorId.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase)
            || errorId.Contains("LogonFailure", StringComparison.OrdinalIgnoreCase))
        {
            return AccessDeniedFailure(userName, isLocalTarget, raw);
        }

        // 调用前已确认主机名能解析，因此走到这里是认证方式的限制，而不是名字写错。
        if (IsNotTrusted(errorId, code))
        {
            return NotTrustedFailure(host, raw);
        }

        if (code == CannotConnect || errorId.Contains("CannotConnect", StringComparison.OrdinalIgnoreCase))
        {
            return new PowerShellFailure(ConnectionErrorCode.NetworkUnreachable,
                "无法建立 PowerShell 远程连接。请确认目标已启用 PowerShell 远程。", raw, [EnableRemoting("目标电脑上")]);
        }

        return new PowerShellFailure(ConnectionErrorCode.Unknown,
            raw is null ? ConnectionException.Describe(ConnectionErrorCode.Unknown) : "PowerShell 远程连接失败，原因见下方详细信息。",
            raw, []);
    }

    public static bool IsNotTrusted(string errorId, int code)
        => code == NotTrusted || errorId.Contains("ServerNotTrusted", StringComparison.OrdinalIgnoreCase);

    private static PowerShellFailure AccessDeniedFailure(string userName, bool isLocalTarget, string? detail)
    {
        var target = isLocalTarget ? "本机" : "目标电脑";

        // 带 @ 的是域账户（UPN），不受本地账户的 UAC 远程限制影响。
        if (userName.Contains('@'))
        {
            return new PowerShellFailure(ConnectionErrorCode.AuthenticationFailed,
                $"身份验证被拒绝。请检查用户名和密码，并确认该账号在{target}上属于 Administrators 或 Remote Management Users 组。",
                detail, []);
        }

        var message = (isLocalTarget ? "连接本机被拒绝。" : "身份验证被拒绝。")
                      + "请先确认用户名和密码正确；用的是本地账户时，也可能是 Windows 的 UAC 远程限制。";
        var intro = $"如果用的是{target}的本地账户：只要{target}开着 UAC，除内置 Administrator 外，即使密码正确、账户在管理员组，"
                    + "Windows 默认也会拒绝它远程登录（远程桌面不受影响）。确认属于这种情况后，在下面两种办法里选一种。\n"
                    + $"域账户则请确认它在{target}上属于 Administrators 或 Remote Management Users 组。";

        var account = LocalAccountName(userName);
        return new PowerShellFailure(ConnectionErrorCode.AuthenticationFailed, message, detail,
        [
            new SessionCommandHint(
                $"办法一（保留管理员权限）：在{target}上以管理员身份运行",
                $"New-ItemProperty '{PolicyPath}' -Name LocalAccountTokenFilterPolicy -Value 1 -PropertyType DWord -Force",
                "风险：该电脑上所有本地管理员账户从网络登录都将拥有完整权限，请使用强密码并只在可信内网使用。"
                + $"撤销（原来没有这个值时）：Remove-ItemProperty '{PolicyPath}' -Name LocalAccountTokenFilterPolicy"),
            new SessionCommandHint(
                $"办法二（只放开这一个账户，连上后没有管理员权限）：在{target}上以管理员身份运行",
                $"Add-LocalGroupMember -SID S-1-5-32-580 -Member '{account}'",
                $"撤销：Remove-LocalGroupMember -SID S-1-5-32-580 -Member '{account}'")
        ], intro);
    }

    /// <summary>
    /// 取用户名里的账户部分拼进可复制的命令。含引号等字符时改用占位符，避免诱导用户执行夹带内容的命令。
    /// </summary>
    internal static string LocalAccountName(string userName)
    {
        var name = userName[(userName.LastIndexOf('\\') + 1)..];
        return SafeAccount().IsMatch(name) ? name : "<用户名>";
    }

    private static PowerShellFailure NotTrustedFailure(string host, string? detail)
    {
        const string message = "Windows 不信任这台电脑：用 IP 地址或非域方式连接时，需要先把它加入本机的信任列表。";
        const string intro = "Windows 要求目标启用 HTTPS（本版暂不支持），或把它加入本机的 TrustedHosts。"
                             + "能用域内主机名连接时，请优先把主机地址改为主机名。";

        // 只有主机地址由安全字符组成时才拼进可复制的命令，避免诱导用户执行夹带内容的命令。
        return new PowerShellFailure(ConnectionErrorCode.ProtocolNegotiationFailed, message, detail,
            SafeHost().IsMatch(host)
                ?
                [
                    new SessionCommandHint(
                        "确认目标可信后，在本机以管理员身份运行 PowerShell",
                        $"Set-Item WSMan:\\localhost\\Client\\TrustedHosts -Value '{host}' -Concatenate",
                        "该设置影响本机所有用户，且列表中的主机不会被验证身份。")
                ]
                : [], intro);
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._:\-\[\]]+\z")]
    private static partial Regex SafeHost();

    [GeneratedRegex(@"\A[\p{L}\p{N}._\- ]{1,64}\z")]
    private static partial Regex SafeAccount();
}
