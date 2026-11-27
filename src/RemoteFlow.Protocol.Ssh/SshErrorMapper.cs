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
            // 走到这里时 TCP 已经连上（连不上是 SocketException）：对方随即断开或不是 SSH 服务，
            // 不能再说「无法连接到目标主机」，否则会把人引去查网络和防火墙。
            SshConnectionException => ConnectionErrorCode.ProtocolNegotiationFailed,
            SshException => ConnectionErrorCode.ProtocolNegotiationFailed,
            _ => ConnectionErrorCode.Unknown
        };
    }

    /// <summary>需要比错误码的通用说明更具体的提示时返回文案，否则返回 null。</summary>
    public static string? DescribeSpecific(Exception ex, ConnectionErrorCode code)
        => ex is SshConnectionException && code == ConnectionErrorCode.ProtocolNegotiationFailed
            ? "已连上目标端口，但对方随即断开，或这个端口上运行的不是 SSH 服务。请检查端口号、目标的 SSH 服务是否正常，以及对方是否限制了来源地址。"
            : null;
}
