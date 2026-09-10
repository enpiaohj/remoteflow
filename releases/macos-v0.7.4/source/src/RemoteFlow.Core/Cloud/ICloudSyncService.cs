namespace RemoteFlow.Core.Cloud;

public enum CloudUnlockState
{
    /// <summary>Vault 已解锁，可同步。</summary>
    Ready,

    /// <summary>该账号还没有 Vault，需本设备执行 <see cref="ICloudSyncService.BootstrapVaultAsync"/>。</summary>
    NeedsBootstrap,

    /// <summary>Vault 存在但本地无缓存 VMK，需要用户补输主口令（或用 Recovery Key 恢复）。</summary>
    NeedsPassword,

    /// <summary>Vault 开启「新设备需批准」，本设备待批准——等已有设备批准，或用 Recovery Key 恢复。</summary>
    NeedsApproval,
}

public sealed record CloudAccountInfo(bool SignedIn, Guid UserId, string Email, CloudUnlockState UnlockState);

/// <summary>一条未决冲突的可读描述。<paramref name="Label"/> 是给用户看的实体名称。</summary>
public sealed record CloudConflictInfo(
    Guid Id,
    string EntityType,
    string EntityId,
    string Label,
    string Kind,
    DateTimeOffset DetectedAt);

/// <summary>某类实体「已同步到云端」的数量。</summary>
public sealed record CloudSyncedCount(string EntityType, string Label, int Count);

/// <summary>
/// 云同步对 UI / ViewModel 的唯一入口。封装登录 / 注册、Vault 解锁 / 初始化 / 恢复、
/// 改口令、手动同步、冲突解决与登出。口令派生模型：新设备只需账号 + 主口令。
/// </summary>
public interface ICloudSyncService
{
    bool IsSignedIn { get; }

    bool IsVaultUnlocked { get; }

    /// <summary>登录 AppsCloud 并尝试解锁 Vault。<paramref name="password"/> 是主口令。</summary>
    Task<CloudUnlockState> SignInAsync(
        string baseUrl, string email, string password, CancellationToken ct = default);

    /// <summary>注册新账号并登录。</summary>
    Task<CloudUnlockState> RegisterAsync(
        string baseUrl, string email, string password, CancellationToken ct = default);

    /// <summary>用已保存的会话尝试解锁（应用启动时调用）。未登录时返回 null。</summary>
    Task<CloudUnlockState?> TryResumeAsync(CancellationToken ct = default);

    /// <summary>NeedsPassword 状态下补输主口令解锁。</summary>
    Task<CloudUnlockState> UnlockWithPasswordAsync(string password, CancellationToken ct = default);

    /// <summary>首设备初始化 Vault，返回一次性展示的 Recovery Key（分组字符串）。</summary>
    Task<string> BootstrapVaultAsync(bool requireDeviceApproval = false, CancellationToken ct = default);

    /// <summary>本设备被批准后重试解锁。</summary>
    Task<CloudUnlockState> RetryUnlockAsync(CancellationToken ct = default);

    // ── 设备批准（可选安全层）──────────────────────────────────

    Task<bool> GetRequireApprovalAsync(CancellationToken ct = default);

    Task SetRequireApprovalAsync(bool enabled, CancellationToken ct = default);

    Task<IReadOnlyList<CloudPendingDevice>> GetPendingDevicesAsync(CancellationToken ct = default);

    Task ApproveDeviceAsync(Guid deviceId, CancellationToken ct = default);

    /// <summary>忘记口令时用 Recovery Key 恢复 Vault 访问权。</summary>
    Task RecoverVaultAsync(string recoveryKey, CancellationToken ct = default);

    /// <summary>更改主口令：换认证密钥并重新包装口令信封。</summary>
    Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default);

    /// <summary>重置 Recovery Key，返回一次性展示的新 Key。</summary>
    Task<string> ResetRecoveryKeyAsync(CancellationToken ct = default);

    /// <summary>立即执行一次同步循环。</summary>
    Task<SyncRunResult> SyncNowAsync(CancellationToken ct = default);

    Task<SyncStateSnapshot> GetStateAsync(CancellationToken ct = default);

    Task<int> GetPendingOutboxCountAsync(CancellationToken ct = default);

    /// <summary>本机已同步到云端的实体数量，按实体类型汇总（连接 / 凭据 / 密码 / 分组 / 标签）。</summary>
    Task<IReadOnlyList<CloudSyncedCount>> GetSyncedCountsAsync(CancellationToken ct = default);

    // ── 冲突 ────────────────────────────────────────────────────

    /// <summary>未决冲突（含可读标签，如「DC01（10.0.0.1）」，方便用户判断在问谁）。</summary>
    Task<IReadOnlyList<CloudConflictInfo>> GetConflictsAsync(CancellationToken ct = default);

    Task ResolveConflictAsync(Guid conflictId, ConflictResolution resolution, CancellationToken ct = default);

    /// <summary>
    /// 登出。<paramref name="wipeLocalCloudData"/> 为真时额外清空本地同步表与 VMK 缓存
    /// （「清除此设备云数据」），本地连接 / 凭据不动。
    /// </summary>
    Task SignOutAsync(bool wipeLocalCloudData, CancellationToken ct = default);

    /// <summary>
    /// 兜底（以云端为准）：清除本机全部业务数据与同步状态，然后从云端完整拉取恢复。
    /// 与「清除此设备云数据」（保留本地、清同步状态）语义相反。
    /// </summary>
    Task RestoreFromCloudAsync(CancellationToken ct = default);
}
