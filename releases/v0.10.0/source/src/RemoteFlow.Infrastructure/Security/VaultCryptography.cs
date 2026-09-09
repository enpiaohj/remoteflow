using System.Security.Cryptography;
using System.Text;
using RemoteFlow.Core.Cloud;

namespace RemoteFlow.Infrastructure.Security;

/// <summary>
/// Vault 加密原语。全部用 BCL <see cref="System.Security.Cryptography"/>，平台无关、无第三方依赖。
/// <list type="bullet">
///   <item>VMK：256-bit CSPRNG。</item>
///   <item>设备信封：临时 ECDH-P256 → HKDF-SHA256 → AES-256-GCM 包装 VMK。</item>
///   <item>Recovery 信封：Recovery Key → HKDF-SHA256(salt) → AES-256-GCM 包装 VMK。</item>
///   <item>载荷：HKDF-SHA256(VMK, salt=EntityId) → AES-256-GCM，AAD 绑定实体上下文。</item>
/// </list>
/// 禁止自研算法、固定 Nonce、可逆编码。
/// </summary>
public static class VaultCryptography
{
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private static readonly byte[] DeviceInfo = Encoding.UTF8.GetBytes("RemoteFlow/DeviceEnvelope/v1");
    private static readonly byte[] RecoveryInfo = Encoding.UTF8.GetBytes("RemoteFlow/RecoveryEnvelope/v1");
    private static readonly byte[] EntityInfo = Encoding.UTF8.GetBytes("RemoteFlow/Entity/v1");

    // ── VMK / 设备密钥对 ─────────────────────────────────────────

    public static byte[] NewMasterKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    /// <summary>生成设备 ECDH-P256 密钥对，返回 (PKCS#8 私钥 DER, SPKI 公钥 DER)。</summary>
    public static (byte[] PrivateKey, byte[] PublicKey) NewDeviceKeyPair()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return (ecdh.ExportPkcs8PrivateKey(), ecdh.ExportSubjectPublicKeyInfo());
    }

    public static byte[] PublicKeyOf(byte[] pkcs8PrivateKey)
    {
        using var ecdh = ECDiffieHellman.Create();
        ecdh.ImportPkcs8PrivateKey(pkcs8PrivateKey, out _);
        return ecdh.ExportSubjectPublicKeyInfo();
    }

    // ── 设备信封 ────────────────────────────────────────────────

    /// <summary>用接收方设备公钥（SPKI DER）包装 VMK。bootstrap 时接收方公钥即本机公钥。</summary>
    public static DeviceKeyEnvelope WrapForDevice(byte[] recipientPublicKey, ReadOnlySpan<byte> masterKey)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var recipient = ECDiffieHellman.Create();
        recipient.ImportSubjectPublicKeyInfo(recipientPublicKey, out _);

        var shared = ephemeral.DeriveRawSecretAgreement(recipient.PublicKey);
        try
        {
            var kek = Hkdf(shared, salt: default, DeviceInfo);
            var (wrapped, nonce) = AesGcmEncrypt(kek, masterKey, associatedData: default);
            CryptographicOperations.ZeroMemory(kek);
            return new DeviceKeyEnvelope(
                VaultAlgorithms.DeviceEnvelope, wrapped, nonce, ephemeral.ExportSubjectPublicKeyInfo());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    /// <summary>用本机设备私钥解开设备信封，得到 VMK。信封被篡改会抛 <see cref="CryptographicException"/>。</summary>
    public static byte[] UnwrapFromDevice(byte[] devicePrivateKey, DeviceKeyEnvelope envelope)
    {
        using var device = ECDiffieHellman.Create();
        device.ImportPkcs8PrivateKey(devicePrivateKey, out _);
        using var ephemeral = ECDiffieHellman.Create();
        ephemeral.ImportSubjectPublicKeyInfo(envelope.EphemeralPublicKey, out _);

        var shared = device.DeriveRawSecretAgreement(ephemeral.PublicKey);
        try
        {
            var kek = Hkdf(shared, salt: default, DeviceInfo);
            var result = AesGcmDecrypt(kek, envelope.WrappedKey, envelope.Nonce, associatedData: default);
            CryptographicOperations.ZeroMemory(kek);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    // ── Recovery 信封 ───────────────────────────────────────────

    public static RecoveryKeyEnvelope WrapForRecovery(ReadOnlySpan<byte> recoveryKey, ReadOnlySpan<byte> masterKey)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = Hkdf(recoveryKey, salt, RecoveryInfo);
        var (wrapped, nonce) = AesGcmEncrypt(kek, masterKey, associatedData: default);
        CryptographicOperations.ZeroMemory(kek);
        return new RecoveryKeyEnvelope(VaultAlgorithms.RecoveryEnvelope, wrapped, nonce, salt);
    }

    /// <summary>用 Recovery Key 解开 Recovery 信封。Key 错误会抛 <see cref="CryptographicException"/>。</summary>
    public static byte[] UnwrapFromRecovery(ReadOnlySpan<byte> recoveryKey, RecoveryKeyEnvelope envelope)
    {
        var kek = Hkdf(recoveryKey, envelope.Salt, RecoveryInfo);
        var result = AesGcmDecrypt(kek, envelope.WrappedKey, envelope.Nonce, associatedData: default);
        CryptographicOperations.ZeroMemory(kek);
        return result;
    }

    // ── 载荷 ────────────────────────────────────────────────────

    public static EncryptedPayload EncryptPayload(
        ReadOnlySpan<byte> masterKey, PayloadContext context, ReadOnlySpan<byte> plaintext)
    {
        var entityKey = DeriveEntityKey(masterKey, context.EntityId);
        try
        {
            var (blob, nonce) = AesGcmEncrypt(entityKey, plaintext, context.ComputeAad());
            return new EncryptedPayload(blob, nonce, context.KeyVersion, context.SchemaVersion);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entityKey);
        }
    }

    /// <summary>解密载荷。AAD / Nonce / Ciphertext 任一被篡改都抛 <see cref="CryptographicException"/>。</summary>
    public static byte[] DecryptPayload(
        ReadOnlySpan<byte> masterKey, PayloadContext context, EncryptedPayload payload)
    {
        var entityKey = DeriveEntityKey(masterKey, context.EntityId);
        try
        {
            return AesGcmDecrypt(entityKey, payload.Ciphertext, payload.Nonce, context.ComputeAad());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entityKey);
        }
    }

    private static byte[] DeriveEntityKey(ReadOnlySpan<byte> masterKey, string entityId) =>
        Hkdf(masterKey, Encoding.UTF8.GetBytes(entityId), EntityInfo);

    private static byte[] Hkdf(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info)
    {
        var output = new byte[KeyBytes];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, output, salt, info);
        return output;
    }

    // ── AES-256-GCM 封装：blob = ciphertext || tag ──────────────

    private static (byte[] Blob, byte[] Nonce) AesGcmEncrypt(
        byte[] key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var blob = new byte[plaintext.Length + TagBytes];
        using var gcm = new AesGcm(key, TagBytes);
        gcm.Encrypt(
            nonce,
            plaintext,
            blob.AsSpan(0, plaintext.Length),
            blob.AsSpan(plaintext.Length, TagBytes),
            associatedData);
        return (blob, nonce);
    }

    private static byte[] AesGcmDecrypt(
        byte[] key, byte[] blob, byte[] nonce, ReadOnlySpan<byte> associatedData)
    {
        if (blob.Length < TagBytes)
        {
            throw new CryptographicException("Wrapped payload is too short.");
        }

        var plaintextLength = blob.Length - TagBytes;
        var plaintext = new byte[plaintextLength];
        using var gcm = new AesGcm(key, TagBytes);
        gcm.Decrypt(
            nonce,
            blob.AsSpan(0, plaintextLength),
            blob.AsSpan(plaintextLength, TagBytes),
            plaintext,
            associatedData);
        return plaintext;
    }
}
