using RemoteFlow.Core.Models;

namespace RemoteFlow.Core.Sessions;

/// <summary>目标电脑在启用 PowerShell 远程之前的状态（只读检查所得）。</summary>
/// <param name="PublicNetworks">网络类别为「公用」的网卡名。非空时不允许远程启用。</param>
/// <param name="UacEnabled">目标是否开启了 UAC；关闭时远程登录本来就不过滤本地管理员权限。</param>
/// <param name="TokenFilterPolicy">LocalAccountTokenFilterPolicy 当前值；未设置时为 null。</param>
public sealed record RemotingTargetInfo(
    string ComputerName,
    string OsName,
    bool IsServer,
    bool PartOfDomain,
    string WinRmState,
    string WinRmStartMode,
    IReadOnlyList<string> PublicNetworks,
    bool UacEnabled,
    int? TokenFilterPolicy);

/// <summary>启用过程对 LocalAccountTokenFilterPolicy 造成的结果。</summary>
public enum TokenFilterPolicyOutcome
{
    /// <summary>启用前后没有变化。</summary>
    Unchanged,

    /// <summary>被 Enable-PSRemoting 改动，但目标未开启 UAC（该值不起作用），已恢复原状。</summary>
    Restored,

    /// <summary>被 Enable-PSRemoting 改动并保留：目标开启着 UAC，撤掉它本地管理员账户可能无法远程登录。</summary>
    Kept
}

/// <param name="After">启用之后重新读取的状态。</param>
public sealed record RemotingEnableResult(
    RemotingTargetInfo Before,
    RemotingTargetInfo After,
    TokenFilterPolicyOutcome PolicyOutcome,
    bool PortOpen);

/// <summary>
/// 远程在目标 Windows 电脑上启用 PowerShell 远程。会改变目标电脑的系统设置，
/// 调用方必须先把 <see cref="InspectAsync"/> 的结果与将要发生的改动告诉用户并取得确认。平台不支持时不注册。
/// </summary>
public interface IRemotingEnabler
{
    /// <summary>只读检查，不改变目标电脑任何设置。</summary>
    /// <exception cref="ConnectionException">无法连接或认证失败。</exception>
    Task<RemotingTargetInfo> InspectAsync(string host, ResolvedCredential credential, string? domain, CancellationToken ct = default);

    /// <summary>启用 PowerShell 远程。目标存在「公用」网络时拒绝执行。</summary>
    Task<RemotingEnableResult> EnableAsync(string host, ResolvedCredential credential, string? domain, CancellationToken ct = default);
}
