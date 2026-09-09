using RemoteFlow.Core.Cloud;

namespace RemoteFlow.IntegrationTests.Cloud;

/// <summary>测试用内存版 <see cref="IVaultKeyStore"/>。平台真实实现（DPAPI / Keychain）由其专属测试覆盖。</summary>
public sealed class InMemoryVaultKeyStore : IVaultKeyStore
{
    private byte[]? _devicePrivateKey;
    private byte[]? _cachedMasterKey;

    public int SetPrivateKeyCalls { get; private set; }

    public Task<byte[]?> GetDevicePrivateKeyAsync(CancellationToken ct = default) =>
        Task.FromResult(_devicePrivateKey?.ToArray());

    public Task SetDevicePrivateKeyAsync(byte[] pkcs8PrivateKey, CancellationToken ct = default)
    {
        _devicePrivateKey = pkcs8PrivateKey.ToArray();
        SetPrivateKeyCalls++;
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetCachedMasterKeyAsync(CancellationToken ct = default) =>
        Task.FromResult(_cachedMasterKey?.ToArray());

    public Task SetCachedMasterKeyAsync(byte[] masterKey, CancellationToken ct = default)
    {
        _cachedMasterKey = masterKey.ToArray();
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        _devicePrivateKey = null;
        _cachedMasterKey = null;
        return Task.CompletedTask;
    }
}
