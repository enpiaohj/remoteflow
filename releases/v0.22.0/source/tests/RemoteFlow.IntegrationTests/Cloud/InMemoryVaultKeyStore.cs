using RemoteFlow.Core.Cloud;

namespace RemoteFlow.IntegrationTests.Cloud;

/// <summary>测试用内存版 <see cref="IVaultKeyStore"/>。平台真实实现（DPAPI / Keychain）由其专属测试覆盖。</summary>
public sealed class InMemoryVaultKeyStore : IVaultKeyStore
{
    private CachedMasterKey? _cached;

    public int ClearCalls { get; private set; }

    public Task<CachedMasterKey?> GetCachedMasterKeyAsync(CancellationToken ct = default) =>
        Task.FromResult(_cached is null
            ? null
            : new CachedMasterKey(_cached.MasterKey.ToArray(), _cached.VaultTag));

    public Task SetCachedMasterKeyAsync(byte[] masterKey, string vaultTag, CancellationToken ct = default)
    {
        _cached = new CachedMasterKey(masterKey.ToArray(), vaultTag);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        _cached = null;
        ClearCalls++;
        return Task.CompletedTask;
    }
}
