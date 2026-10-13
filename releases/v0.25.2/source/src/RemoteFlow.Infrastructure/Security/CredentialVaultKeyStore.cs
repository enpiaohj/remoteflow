using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Cloud;

namespace RemoteFlow.Infrastructure.Security;

/// <summary>
/// <see cref="IVaultKeyStore"/> 直接复用 <see cref="ICredentialVault"/> 的平台密钥库保护
/// （Windows DPAPI / macOS Keychain），无需再写平台专属实现。
/// VMK 以 <c>base64(vmk)|vaultTag</c> 形式存入，与连接 Secret 同一保险库文件、同样受保护。
/// </summary>
public sealed class CredentialVaultKeyStore(ICredentialVault vault) : IVaultKeyStore
{
    private const string MasterKeyReference = "cloud:vault-master-key";

    public async Task<CachedMasterKey?> GetCachedMasterKeyAsync(CancellationToken ct = default)
    {
        var raw = await vault.RetrieveSecretAsync(MasterKeyReference, ct);
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var sep = raw.IndexOf('|');
        // sep < 0：旧格式（无 tag），当作陈旧缓存——返回空 tag，让 VaultId 校验必然失败后清除。
        return sep < 0
            ? new CachedMasterKey(Convert.FromBase64String(raw), string.Empty)
            : new CachedMasterKey(Convert.FromBase64String(raw[..sep]), raw[(sep + 1)..]);
    }

    public Task SetCachedMasterKeyAsync(byte[] masterKey, string vaultTag, CancellationToken ct = default) =>
        vault.StoreSecretAsync(MasterKeyReference, Convert.ToBase64String(masterKey) + "|" + vaultTag, ct);

    public Task ClearAsync(CancellationToken ct = default) =>
        vault.DeleteSecretAsync(MasterKeyReference, ct);
}
