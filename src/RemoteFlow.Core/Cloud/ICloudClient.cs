namespace RemoteFlow.Core.Cloud;

/// <summary>
/// AppsCloud REST 客户端。负责 Token 生命周期（Access Token 内存持有、401 自动 Refresh）、
/// 账号 / Vault / Sync 端点调用。所有方法在缺少有效凭据时抛 <see cref="CloudAuthRequiredException"/>。
/// </summary>
public interface ICloudClient
{
    /// <summary>当前是否已有持久化的登录会话。</summary>
    Task<bool> HasSessionAsync(CancellationToken ct = default);

    Task<CloudRegisterOutcome> RegisterAsync(string email, string password, CancellationToken ct = default);

    /// <summary>登录并持久化会话。<paramref name="device"/> 的 PublicKey 用本机设备 ECDH 公钥（SPKI base64）。</summary>
    Task LoginAsync(string email, string password, CloudDeviceInfo device, CancellationToken ct = default);

    Task LogoutAsync(CancellationToken ct = default);

    Task<Guid> GetUserIdAsync(CancellationToken ct = default);

    /// <summary>本机设备在 AppsCloud 的内部 Id（用于 Vault 信封 target）。</summary>
    Task<Guid> GetCurrentDeviceIdAsync(CancellationToken ct = default);

    // ── Vault ───────────────────────────────────────────────────

    Task<CloudVaultStatus> GetVaultStatusAsync(CancellationToken ct = default);

    /// <summary>首设备初始化。已存在时返回 false（并发 / 重复），调用方应改走解锁流程。</summary>
    Task<bool> BootstrapVaultAsync(
        DeviceKeyEnvelope deviceEnvelope, RecoveryKeyEnvelope recoveryEnvelope, CancellationToken ct = default);

    /// <summary>取本设备的 wrapped VMK 信封。未授权 / 无 Vault 返回 null。</summary>
    Task<DeviceKeyEnvelope?> GetDeviceEnvelopeAsync(CancellationToken ct = default);

    Task<IReadOnlyList<CloudPendingDevice>> GetPendingDevicesAsync(CancellationToken ct = default);

    /// <summary>为目标设备（含自己）上传信封。</summary>
    Task AddDeviceEnvelopeAsync(Guid targetDeviceId, DeviceKeyEnvelope envelope, CancellationToken ct = default);

    Task<RecoveryKeyEnvelope?> GetRecoveryEnvelopeAsync(CancellationToken ct = default);

    Task PutRecoveryEnvelopeAsync(RecoveryKeyEnvelope envelope, CancellationToken ct = default);

    // ── Sync ────────────────────────────────────────────────────

    Task<SyncPushResponse> PushAsync(IReadOnlyList<SyncPushOperation> operations, CancellationToken ct = default);

    Task<SyncPullPage> PullAsync(long cursor, int limit, CancellationToken ct = default);
}
