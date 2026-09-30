using System.Net.Sockets;
using Renci.SshNet.Common;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.Protocol.Ssh;

/// <summary>把 SSH.NET / Socket 的原始异常映射为标准化连接错误码，UI 只面对可理解的中文提示。</summary>
internal static class SshErrorMapper
{
    /// <param name="ex">底层异常。</param>
    /// <param name="hostKeyFailure">主机密钥闸门记录的精确失败原因；非 None 时优先采用。</param>
    public static ConnectionErrorCode Map(Exception ex, ConnectionErrorCode hostKeyFailure)
    {
        // Host Key 校验失败会以 SshConnectionException 的形式冒泡，
        // 此处优先采用校验阶段记录的精确原因。
        if (hostKeyFailure != ConnectionErrorCode.None)
        {
            return hostKeyFailure;
        }

        return ex switch
        {
            ConnectionException connectionException => connectionException.ErrorCode,
            SshAuthenticationException => ConnectionErrorCode.AuthenticationFailed,
            SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData }
                => ConnectionErrorCode.HostNotFound,
            SocketException { SocketErrorCode: SocketError.TimedOut } => ConnectionErrorCode.Timeout,
            SocketException => ConnectionErrorCode.NetworkUnreachable,
            SshOperationTimeoutException => ConnectionErrorCode.Timeout,
            SshConnectionException => ConnectionErrorCode.NetworkUnreachable,
            SshException => ConnectionErrorCode.ProtocolNegotiationFailed,
            _ => ConnectionErrorCode.Unknown
        };
    }
}
