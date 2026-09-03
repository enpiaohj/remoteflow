using RemoteFlow.Core.Models;

namespace RemoteFlow.Core.Sessions;

/// <summary>
/// 协议 Provider。每种协议一个实现，作为会话工厂存在。
/// <para>
/// 设计说明：产品设计文档给出的初版接口是「一个 Provider 即一个连接」，
/// 该形态无法支撑 V0.1 要求的多 Tab 并行会话（同一协议同时存在多个独立连接）。
/// 因此实际实现演进为 <b>Provider = 会话工厂</b>、<see cref="IRemoteSession"/> = 会话实例，
/// 保留了 Provider 抽象的可扩展性（后续 SFTP / Web / PowerShell 直接新增实现即可）。
/// </para>
/// </summary>
public interface IConnectionProvider
{
    ProtocolType Protocol { get; }

    /// <summary>该协议的标准默认端口。</summary>
    int DefaultPort { get; }

    /// <summary>
    /// 校验本机是否具备该协议运行条件（如 RDP ActiveX 控件是否可用）。
    /// 返回 false 时 UI 应给出明确提示，而不是等到连接阶段才失败。
    /// </summary>
    bool IsAvailable(out string? unavailableReason);

    /// <summary>
    /// 创建一个尚未连接的会话实例。
    /// </summary>
    IRemoteSession CreateSession(SessionRequest request);
}

/// <summary>
/// 创建会话所需的全部输入。
/// <para>
/// <see cref="Credential"/> 由 Vault 即时解析得到，其生命周期由 <see cref="IRemoteSession"/> 接管：
/// 会话在连接完成或失败后必须立即 Dispose 该凭据。
/// </para>
/// </summary>
public sealed class SessionRequest
{
    public required ConnectionProfile Profile { get; init; }

    /// <summary>已解析的凭据。可为 null（例如 VNC 无密码场景）。</summary>
    public ResolvedCredential? Credential { get; init; }

    /// <summary>SSH Host Key 校验策略。仅 SSH 使用。</summary>
    public ISshHostKeyPolicy? HostKeyPolicy { get; init; }
}

/// <summary>
/// SSH Host Key 校验策略。由 UI 层实现，负责在首次连接或指纹变化时向用户确认。
/// </summary>
public interface ISshHostKeyPolicy
{
    /// <summary>
    /// 校验 Host Key。返回 true 表示接受并继续连接。
    /// <para>实现必须做到：首次连接提示并记录指纹；指纹变化时明确警告，不得静默接受。</para>
    /// </summary>
    Task<bool> VerifyAsync(SshHostKeyVerificationContext context, CancellationToken cancellationToken);
}

public sealed class SshHostKeyVerificationContext
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string KeyAlgorithm { get; init; }

    /// <summary>本次连接得到的 SHA256 指纹。</summary>
    public required string Fingerprint { get; init; }

    /// <summary>本机此前信任的指纹。null 表示首次连接。</summary>
    public string? KnownFingerprint { get; init; }

    /// <summary>指纹是否与已记录值不一致——这是必须强警告的中间人风险场景。</summary>
    public bool IsMismatch => KnownFingerprint is not null && KnownFingerprint != Fingerprint;
}
