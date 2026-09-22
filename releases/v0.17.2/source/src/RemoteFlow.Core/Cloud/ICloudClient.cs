namespace RemoteFlow.Core.Cloud;

/// <summary>
/// AppsCloud REST 客户端。负责 Token 生命周期（Access Token 内存持有、401 自动 Refresh）、
/// 账号 / Vault / Sync 端点调用。所有方法在缺少有效凭据时抛 <see cref="CloudAuthRequiredException"/>。
/// </summary>
public interface ICloudClient
{
    /// <summary>当前是否已有持久化的登录会话。</summary>
    Task<bool> HasSessionAsync(CancellationToken ct = default);

    /// <summary>注册账号。<paramref name="password"/> 是主口令，内部派生认证密钥后发送——服务端拿不到明文口令。</summary>
    Task<CloudRegisterOutcome> RegisterAsync(string email, string password, CancellationToken ct = default);

    /// <summary>登录并持久化会话。<paramref name="password"/> 是主口令（内部派生认证密钥）。</summary>
    Task LoginAsync(string email, string password, CloudDeviceInfo device, CancellationToken ct = default);

    /// <summary>改主口令：换认证密钥并作废其它会话（口令信封另行 PUT）。</summary>
    Task ChangePasswordAsync(string email, string currentPassword, string newPassword, CancellationToken ct = default);

    Task LogoutAsync(CancellationToken ct = default);

    Task<Guid> GetUserIdAsync(CancellationToken ct = default);

    /// <summary>上报本机基础系统信息（资产信息，非机密）。设备须已登记且未撤销。</summary>
    Task PutSystemInfoAsync(Diagnostics.SystemInfo info, CancellationToken ct = default);

    // ── Vault ───────────────────────────────────────────────────

    Task<CloudVaultStatus> GetVaultStatusAsync(CancellationToken ct = default);

    /// <summary>首设备初始化：上传口令信封 + Recovery 信封。已存在时返回 false。</summary>
    Task<bool> BootstrapVaultAsync(
        VaultKeyEnvelope passwordEnvelope, VaultKeyEnvelope recoveryEnvelope,
        bool requireDeviceApproval, CancellationToken ct = default);

    /// <summary>取指定类型（password / recovery）的信封。无 Vault / 无该信封返回 null。开启批准且本设备未批准时 password 抛 403。</summary>
    Task<VaultKeyEnvelope?> GetVaultEnvelopeAsync(string kind, CancellationToken ct = default);

    /// <summary>覆盖指定类型的信封（改口令 / 重置 Recovery Key）。</summary>
    Task PutVaultEnvelopeAsync(VaultKeyEnvelope envelope, CancellationToken ct = default);

    /// <summary>切换「新设备需批准」（本设备须已批准）。</summary>
    Task SetRequireApprovalAsync(bool enabled, CancellationToken ct = default);

    /// <summary>待批准设备列表（本设备须已批准）。</summary>
    Task<IReadOnlyList<CloudPendingDevice>> GetPendingDevicesAsync(CancellationToken ct = default);

    Task ApproveDeviceAsync(Guid targetDeviceId, CancellationToken ct = default);

    // ── Sync ────────────────────────────────────────────────────

    Task<SyncPushResponse> PushAsync(IReadOnlyList<SyncPushOperation> operations, CancellationToken ct = default);

    Task<SyncPullPage> PullAsync(long cursor, int limit, CancellationToken ct = default);
}
