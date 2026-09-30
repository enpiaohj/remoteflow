using System.Runtime.InteropServices;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.Infrastructure.Smb;

/// <summary>到某台主机的一条已认证 SMB 会话。释放时撤销连接。</summary>
internal interface ISmbConnection : IDisposable
{
}

/// <summary>
/// 用凭据对 <c>\\主机\IPC$</c> 建立 SMB 会话认证。建立后同一进程对该主机（同一服务器名字符串）的 UNC 访问
/// 都使用这条会话的凭据。抽成接口是为了让工厂的流程可以用假实现测试，不需要真实服务器。
/// </summary>
internal interface ISmbAuthenticator
{
    /// <summary>阻塞调用（WNet API 不可取消），调用方负责放到线程池并设超时。</summary>
    ISmbConnection Connect(string host, string userName, string password);
}

/// <summary>基于 <c>WNetAddConnection2</c> 的真实认证实现。</summary>
internal sealed class WNetSmbAuthenticator : ISmbAuthenticator
{
    public ISmbConnection Connect(string host, string userName, string password)
    {
        var remoteName = $@"\\{host}\IPC$";
        var remotePtr = Marshal.StringToHGlobalUni(remoteName);
        try
        {
            var resource = new SmbNative.NetResource
            {
                Type = SmbNative.ResourceTypeAny,
                RemoteName = remotePtr
            };

            var result = SmbNative.WNetAddConnection2(ref resource, password, userName, SmbNative.ConnectTemporary);
            if (result != 0)
            {
                throw SmbErrors.FromWin32(result);
            }

            return new Connection(remoteName);
        }
        finally
        {
            Marshal.FreeHGlobal(remotePtr);
        }
    }

    private sealed class Connection(string remoteName) : ISmbConnection
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            // force = false：有文件仍打开时不强拆，而不是把用户其它程序正在用的同一条连接掐断。
            // 撤销失败（连接已断等）不影响我们收尾，忽略返回值。
            _ = SmbNative.WNetCancelConnection2(remoteName, 0, force: false);
        }
    }
}

/// <summary>组合 WNet 用的账号名。</summary>
internal static class SmbAccountName
{
    /// <summary>
    /// 域账号用 <c>域\用户</c>；本地账号用 <c>主机\用户</c>（让远端按它自己的本地账户库校验，
    /// 而不是拿本机当前登录域去猜）；用户已经写成 <c>域\用户</c> 或 <c>用户@域</c> 的原样使用。
    /// </summary>
    public static string Compose(string host, ResolvedCredential credential, ConnectionProfile profile)
    {
        var user = credential.Username.Trim();
        if (user.Contains('\\') || user.Contains('@'))
        {
            return user;
        }

        var domain = credential.Domain?.Trim();
        if (string.IsNullOrEmpty(domain) && credential.Type == CredentialType.WindowsDomain)
        {
            // 连接里配置的「登录域」是 RDP 用的；凭据自身没写域时借用它，与用户连 RDP 时的登录身份一致。
            domain = profile.Rdp.Domain?.Trim();
        }

        return string.IsNullOrEmpty(domain) ? $@"{host}\{user}" : $@"{domain}\{user}";
    }
}

/// <summary>Win32 错误码 → 面向用户的连接异常 / 传输异常。消息不含账号与密码。</summary>
internal static class SmbErrors
{
    /// <summary>WNetAddConnection2 的返回码。</summary>
    public static ConnectionException FromWin32(int code) => code switch
    {
        // 已用另一账号对该主机建立过连接。不替用户强拆（可能正被资源管理器或其它程序使用）。
        1219 => new ConnectionException(
            ConnectionErrorCode.AuthenticationFailed,
            "本机已使用另一个账号连接过该主机的共享，Windows 不允许同时使用两个账号。" +
            "请先断开原有连接（资源管理器中断开映射网络驱动器，或命令行 net use \\\\主机 /delete）后重试。"),

        5 or 86 or 1326 or 1327 or 1331 or 1244
            => ConnectionException.FromCode(ConnectionErrorCode.AuthenticationFailed),

        1909 => new ConnectionException(ConnectionErrorCode.AuthenticationFailed, "该账号已被锁定，请联系管理员。"),
        1907 => new ConnectionException(ConnectionErrorCode.AuthenticationFailed, "该账号要求先修改密码，请先登录一次修改密码。"),

        53 or 67 or 1203 or 1231 or 64
            => new ConnectionException(
                ConnectionErrorCode.NetworkUnreachable,
                "找不到目标主机的网络共享，请检查主机是否在线、445 端口（SMB）是否放行、管理共享是否被禁用。"),

        1222 or 1225 => ConnectionException.FromCode(ConnectionErrorCode.NetworkUnreachable),
        1460 or 121 => ConnectionException.FromCode(ConnectionErrorCode.Timeout),

        _ => new ConnectionException(ConnectionErrorCode.Unknown, $"连接共享失败（Windows 错误码 {code}）。")
    };
}
