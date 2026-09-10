namespace RemoteFlow.Core.Models;

/// <summary>
/// 连接历史记录。
/// <para>
/// <b>禁止记录：</b>密码、私钥正文、Token、剪贴板内容、完整认证报文。
/// 失败原因只允许写入标准化的 <see cref="ConnectionErrorCode"/>，
/// 不得把底层异常的原始文本直接落盘（可能含主机凭据信息）。
/// </para>
/// </summary>
public sealed class ConnectionHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConnectionId { get; set; }

    /// <summary>连接名称快照。即使原连接被删除，历史仍可读。</summary>
    public string ConnectionName { get; set; } = string.Empty;

    /// <summary>主机快照。</summary>
    public string Host { get; set; } = string.Empty;

    public ProtocolType Protocol { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public ConnectionResult Result { get; set; }

    public ConnectionErrorCode ErrorCode { get; set; }

    /// <summary>会话持续时长。会话尚未结束时返回 null。</summary>
    public TimeSpan? Duration => EndedAt is null ? null : EndedAt.Value - StartedAt;
}

/// <summary>
/// 已信任的 SSH Host Key 记录。
/// <para>
/// 首次连接时提示并记录指纹；再次连接时若指纹变化必须明确警告，
/// 避免用户无感接受潜在的中间人攻击。
/// </para>
/// </summary>
public sealed class SshHostKeyRecord
{
    /// <summary>主机标识，格式为 host:port。</summary>
    public string HostKey { get; set; } = string.Empty;

    /// <summary>密钥算法，如 ssh-ed25519、ssh-rsa。</summary>
    public string KeyAlgorithm { get; set; } = string.Empty;

    /// <summary>SHA256 指纹（Base64，OpenSSH 风格）。</summary>
    public string Fingerprint { get; set; } = string.Empty;

    public DateTimeOffset TrustedAt { get; set; } = DateTimeOffset.Now;

    public static string BuildHostKey(string host, int port) => $"{host}:{port}";
}
