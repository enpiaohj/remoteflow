using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Cloud;

namespace RemoteFlow.Infrastructure.Security;

/// <summary>
/// <see cref="IVaultKeyStore"/> 直接复用 <see cref="ICredentialVault"/> 的平台密钥库保护
/// （Windows DPAPI / macOS Keychain），无需再写平台专属实现。
/// 设备私钥与 VMK 以 base64 字符串形式存入，与连接 Secret 同一保险库文件、同样受保护。
/// </summary>
public sealed class CredentialVaultKeyStore(ICredentialVault vault) : IVaultKeyStore
{
    private const string DeviceKeyReference = "cloud:device-private-key";
    private const string MasterKeyReference = "cloud:vault-master-key";

    public async Task<byte[]?> GetDevicePrivateKeyAsync(CancellationToken ct = default) =>
        Decode(await vault.RetrieveSecretAsync(DeviceKeyReference, ct));

    public Task SetDevicePrivateKeyAsync(byte[] pkcs8PrivateKey, CancellationToken ct = default) =>
        vault.StoreSecretAsync(DeviceKeyReference, Convert.ToBase64String(pkcs8PrivateKey), ct);

    public async Task<byte[]?> GetCachedMasterKeyAsync(CancellationToken ct = default) =>
        Decode(await vault.RetrieveSecretAsync(MasterKeyReference, ct));

    public Task SetCachedMasterKeyAsync(byte[] masterKey, CancellationToken ct = default) =>
        vault.StoreSecretAsync(MasterKeyReference, Convert.ToBase64String(masterKey), ct);

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await vault.DeleteSecretAsync(DeviceKeyReference, ct);
        await vault.DeleteSecretAsync(MasterKeyReference, ct);
    }

    private static byte[]? Decode(string? value) =>
        string.IsNullOrEmpty(value) ? null : Convert.FromBase64String(value);
}
