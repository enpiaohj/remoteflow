namespace RemoteFlow.Core.Cloud;

public enum CloudUnlockState
{
    /// <summary>Vault 已解锁，可同步。</summary>
    Ready,

    /// <summary>该账号还没有 Vault，需本设备执行 <see cref="ICloudSyncService.BootstrapVaultAsync"/>。</summary>
    NeedsBootstrap,

    /// <summary>Vault 存在但本设备未授权，需其他设备批准或用 Recovery Key 恢复。</summary>
    NeedsApproval,
}

public sealed record CloudAccountInfo(bool SignedIn, Guid UserId, string Email, CloudUnlockState UnlockState);

/// <summary>
/// 云同步对 UI / ViewModel 的唯一入口。封装登录、Vault 解锁 / 初始化 / 恢复、
/// 手动同步、设备批准、冲突解决与登出。
/// </summary>
public interface ICloudSyncService
{
    bool IsSignedIn { get; }

    bool IsVaultUnlocked { get; }

    /// <summary>登录 AppsCloud 并尝试解锁 Vault。<paramref name="baseUrl"/> 含 PathBase。返回解锁状态。</summary>
    Task<CloudUnlockState> SignInAsync(
        string baseUrl, string email, string password, CancellationToken ct = default);

    /// <summary>用已保存的会话尝试解锁（应用启动时调用）。未登录时返回 null。</summary>
    Task<CloudUnlockState?> TryResumeAsync(CancellationToken ct = default);

    /// <summary>首设备初始化 Vault，返回一次性展示的 Recovery Key（分组字符串）。</summary>
    Task<string> BootstrapVaultAsync(CancellationToken ct = default);

    /// <summary>用 Recovery Key 恢复 Vault 访问权。</summary>
    Task RecoverVaultAsync(string recoveryKey, CancellationToken ct = default);

    /// <summary>立即执行一次同步循环。</summary>
    Task<SyncRunResult> SyncNowAsync(CancellationToken ct = default);

    Task<SyncStateSnapshot> GetStateAsync(CancellationToken ct = default);

    Task<int> GetPendingOutboxCountAsync(CancellationToken ct = default);

    // ── 设备批准 ────────────────────────────────────────────────

    Task<IReadOnlyList<CloudPendingDevice>> GetPendingDevicesAsync(CancellationToken ct = default);

    Task ApproveDeviceAsync(Guid deviceId, CancellationToken ct = default);

    // ── 冲突 ────────────────────────────────────────────────────

    Task<IReadOnlyList<SyncConflictRecord>> GetConflictsAsync(CancellationToken ct = default);

    Task ResolveConflictAsync(Guid conflictId, ConflictResolution resolution, CancellationToken ct = default);

    /// <summary>
    /// 登出。<paramref name="wipeLocalCloudData"/> 为真时额外清空本地同步表与 VMK 缓存
    /// （「清除此设备云数据」），本地连接 / 凭据不动。
    /// </summary>
    Task SignOutAsync(bool wipeLocalCloudData, CancellationToken ct = default);
}
