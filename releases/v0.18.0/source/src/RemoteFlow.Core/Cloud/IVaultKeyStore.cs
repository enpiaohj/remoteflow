namespace RemoteFlow.Core.Cloud;

/// <summary>
/// VMK 缓存的本地安全存储。Windows 用 DPAPI、macOS 用 Keychain 实现，
/// 落盘内容必须是平台密钥库加密后的密文（安全设计 §11）。
/// </summary>
public interface IVaultKeyStore
{
    /// <summary>取缓存的 VMK 及其所属 Vault 标识。不存在返回 null。</summary>
    Task<CachedMasterKey?> GetCachedMasterKeyAsync(CancellationToken ct = default);

    /// <summary>
    /// 缓存 VMK，避免每次启动都要重新解开信封。<paramref name="vaultTag"/> 是
    /// <see cref="CloudVaultStatus.VaultId"/>，用于识别 Vault 被重建 / 切换账号后缓存已陈旧。
    /// </summary>
    Task SetCachedMasterKeyAsync(byte[] masterKey, string vaultTag, CancellationToken ct = default);

    /// <summary>清除 VMK 缓存（退出云账号 / 清除此设备云数据 / 缓存已陈旧）。</summary>
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>缓存的 VMK（32 字节明文，已由平台密钥库在落盘时保护）及其所属 Vault 的标识。</summary>
public sealed record CachedMasterKey(byte[] MasterKey, string VaultTag);
