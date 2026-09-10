using RemoteFlow.Core.Models;

namespace RemoteFlow.Presentation;

/// <summary>
/// 把 <see cref="ConnectionErrorCode"/> 翻成给人看的话 + 一句可操作的建议。
/// 直接把枚举名（"ComponentUnavailable" / "Unknown"）甩给用户既看不懂也无从下手。
/// </summary>
public static class ConnectionErrorText
{
    /// <summary>一句话说明「出了什么事」。</summary>
    public static string Title(ConnectionErrorCode code) => code switch
    {
        ConnectionErrorCode.HostNotFound => "找不到主机",
        ConnectionErrorCode.NetworkUnreachable => "网络不可达",
        ConnectionErrorCode.Timeout => "连接超时",
        ConnectionErrorCode.AuthenticationFailed => "认证失败",
        ConnectionErrorCode.CredentialMissing => "凭据缺失",
        ConnectionErrorCode.HostKeyMismatch => "主机密钥不一致",
        ConnectionErrorCode.HostKeyRejected => "已拒绝该主机密钥",
        ConnectionErrorCode.ProtocolNegotiationFailed => "协议协商失败",
        ConnectionErrorCode.RemoteClosed => "远端关闭了会话",
        ConnectionErrorCode.Cancelled => "已取消",
        ConnectionErrorCode.ComponentUnavailable => "本机组件不可用",
        _ => "连接失败",
    };

    /// <summary>接下来能做什么。空串表示没有额外建议。</summary>
    public static string Hint(ConnectionErrorCode code) => code switch
    {
        ConnectionErrorCode.HostNotFound =>
            "主机名解析不到。检查地址是否写错，或该域名是否需要内网 DNS / VPN。",
        ConnectionErrorCode.NetworkUnreachable =>
            "目标拒绝连接或网络不通。确认对方已开机、服务已启动、端口没被防火墙挡住。",
        ConnectionErrorCode.Timeout =>
            "在超时时间内没有响应。多见于未连 VPN、跨网段不通，或对方负载过高。",
        ConnectionErrorCode.AuthenticationFailed =>
            "用户名、密码、私钥或域不正确。到「凭据」里核对后重试。",
        ConnectionErrorCode.CredentialMissing =>
            "这条连接引用的凭据不存在或已被删除。编辑连接重新选一个凭据。",
        ConnectionErrorCode.HostKeyMismatch =>
            "服务器指纹与上次记录的不一致。可能是对方重装或换了证书，也可能是中间人攻击。"
            + "确认无误后到「设置 → 安全」移除旧记录再连。",
        ConnectionErrorCode.HostKeyRejected =>
            "你拒绝了该主机密钥，连接已中止。",
        ConnectionErrorCode.ProtocolNegotiationFailed =>
            "双方在版本、加密套件或编码上谈不拢。检查服务端的安全层与加密设置。",
        ConnectionErrorCode.RemoteClosed =>
            "会话被远端关闭。可能是管理员注销、会话超时或服务重启。",
        ConnectionErrorCode.ComponentUnavailable =>
            "本机缺少该协议需要的组件。若是 RDP，请确认随包的 FreeRDP 库完整。",
        _ => string.Empty,
    };

    /// <summary>拼成一段可直接展示的文案：优先用协议给出的原文，再补上建议。</summary>
    public static string Describe(ConnectionErrorCode code, string? message)
    {
        var hint = Hint(code);
        if (string.IsNullOrWhiteSpace(message))
        {
            return hint.Length > 0 ? hint : Title(code);
        }

        return hint.Length > 0 ? $"{message}\n\n{hint}" : message!;
    }
}
