using System.Security.Cryptography;
using RemoteFlow.Core.Cloud;

namespace RemoteFlow.Infrastructure.Security;

/// <summary>
/// 本机设备 ECDH 密钥对的生命周期，以及用它包装 / 解开 VMK 设备信封。
/// 私钥只经 <see cref="IVaultKeyStore"/> 落盘（平台密钥库加密）。
/// </summary>
public sealed class DeviceKeyService(IVaultKeyStore keyStore)
{
    /// <summary>确保本机有设备密钥对，返回可上传给 AppsCloud 的 SPKI 公钥（DER）。</summary>
    public async Task<byte[]> EnsureDeviceKeyAsync(CancellationToken ct = default)
    {
        var existing = await keyStore.GetDevicePrivateKeyAsync(ct);
        if (existing is not null)
        {
            try
            {
                return VaultCryptography.PublicKeyOf(existing);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(existing);
            }
        }

        var (privateKey, publicKey) = VaultCryptography.NewDeviceKeyPair();
        try
        {
            await keyStore.SetDevicePrivateKeyAsync(privateKey, ct);
            return publicKey;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    /// <summary>为目标设备（其 SPKI 公钥）包装 VMK。bootstrap 时目标公钥即本机公钥。</summary>
    public DeviceKeyEnvelope WrapMasterKeyFor(byte[] recipientPublicKey, ReadOnlySpan<byte> masterKey) =>
        VaultCryptography.WrapForDevice(recipientPublicKey, masterKey);

    /// <summary>用本机设备私钥解开设备信封，得到 VMK。</summary>
    public async Task<byte[]> UnwrapMasterKeyAsync(DeviceKeyEnvelope envelope, CancellationToken ct = default)
    {
        var privateKey = await keyStore.GetDevicePrivateKeyAsync(ct)
            ?? throw new InvalidOperationException("No device key is stored on this machine.");
        try
        {
            return VaultCryptography.UnwrapFromDevice(privateKey, envelope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }
}
