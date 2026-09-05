using RemoteFlow.Core.Models;

namespace RemoteFlow.Core.Sessions;

/// <summary>
/// 标准化连接异常。协议层必须把底层库的原始异常映射为本异常，
/// 使 UI 能显示可理解的中文提示，而不是直接抛出 SocketException / SshException。
/// <para>
/// <see cref="Message"/> 面向用户，禁止包含任何 Secret。
/// 原始异常保留在 <see cref="Exception.InnerException"/> 中，仅供日志诊断。
/// </para>
/// </summary>
public sealed class ConnectionException(
    ConnectionErrorCode errorCode,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public ConnectionErrorCode ErrorCode { get; } = errorCode;

    /// <summary>把标准错误码翻译为面向用户的中文说明。</summary>
    public static string Describe(ConnectionErrorCode code) => code switch
    {
        ConnectionErrorCode.None => "无错误。",
        ConnectionErrorCode.HostNotFound => "无法解析主机名，请检查主机地址是否正确。",
        ConnectionErrorCode.NetworkUnreachable => "无法连接到目标主机，请检查网络、防火墙或目标端口是否开放。",
        ConnectionErrorCode.Timeout => "连接超时，目标主机未在预期时间内响应。",
        ConnectionErrorCode.AuthenticationFailed => "身份验证失败，请检查用户名、密码或私钥是否正确。",
        ConnectionErrorCode.CredentialMissing => "连接引用的凭据不存在或已被删除，请重新选择凭据。",
        ConnectionErrorCode.HostKeyMismatch => "主机密钥与已记录的指纹不一致，可能存在中间人攻击，连接已中止。",
        ConnectionErrorCode.HostKeyRejected => "已拒绝该主机密钥，连接未建立。",
        ConnectionErrorCode.ProtocolNegotiationFailed => "协议协商失败，目标主机可能不支持当前连接方式。",
        ConnectionErrorCode.RemoteClosed => "远程主机已关闭会话。",
        ConnectionErrorCode.Cancelled => "连接已取消。",
        ConnectionErrorCode.ComponentUnavailable => "本机缺少建立该连接所需的组件。",
        _ => "连接失败，发生未知错误。"
    };

    /// <summary>用标准描述文本构造异常。</summary>
    public static ConnectionException FromCode(ConnectionErrorCode code, Exception? inner = null)
        => new(code, Describe(code), inner);
}
