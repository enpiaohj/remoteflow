using System.Security.Cryptography;
using System.Text;
using RemoteFlow.Core.Cloud;

namespace RemoteFlow.Infrastructure.Security;

/// <summary>
/// Vault 加密原语。全部用 BCL <see cref="System.Security.Cryptography"/>，平台无关、无第三方依赖。
/// <list type="bullet">
///   <item>VMK：256-bit CSPRNG。</item>
///   <item>认证密钥：PBKDF2-SHA256(password, salt=email) —— 客户端派生后发给服务端，服务端拿不到明文口令。</item>
///   <item>信封（password / recovery）：PBKDF2-SHA256(secret, salt) → KEK → AES-256-GCM 包装 VMK。</item>
///   <item>载荷：HKDF-SHA256(VMK, salt=EntityId) → AES-256-GCM，AAD 绑定实体上下文。</item>
/// </list>
/// 禁止自研算法、固定 Nonce、可逆编码。
/// </summary>
public static class VaultCryptography
{
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int SaltBytes = 16;

    /// <summary>PBKDF2 迭代次数。与后端存储的 <c>Iterations</c> 字段一致；提高需同时改客户端 + 已存信封。</summary>
    public const int Pbkdf2Iterations = 600_000;

    private static readonly byte[] EntityInfo = Encoding.UTF8.GetBytes("RemoteFlow/Entity/v1");

    // ── VMK ────────────────────────────────────────────────────

    public static byte[] NewMasterKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    // ── 认证密钥 ────────────────────────────────────────────────

    /// <summary>
    /// 从主口令派生发给 AppsCloud 的认证密钥（base64(32B)）。salt = 规范化邮箱，
    /// 确定性——每次登录本地重算即可，无需 prelogin。
    /// </summary>
    public static string DeriveAuthKey(string password, string email)
    {
        var salt = Encoding.UTF8.GetBytes("RemoteFlow/auth/v1:" + email.Trim().ToLowerInvariant());
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyBytes);
        try
        {
            return Convert.ToBase64String(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    // ── 口令 / Recovery 信封 ────────────────────────────────────

    /// <summary>用一个 secret（主口令或 Recovery Key 显示串）派生 KEK 并包装 VMK。</summary>
    public static VaultKeyEnvelope WrapWithSecret(string kind, string secret, ReadOnlySpan<byte> masterKey)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var kek = Pbkdf2Kek(secret, salt, Pbkdf2Iterations);
        try
        {
            var (wrapped, nonce) = AesGcmEncrypt(kek, masterKey, associatedData: default);
            return new VaultKeyEnvelope(
                kind, VaultAlgorithms.SecretEnvelope, wrapped, nonce, salt, Pbkdf2Iterations);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>用 secret 解开信封得到 VMK。secret / 信封被篡改会抛 <see cref="CryptographicException"/>。</summary>
    public static byte[] UnwrapWithSecret(string secret, VaultKeyEnvelope envelope)
    {
        var kek = Pbkdf2Kek(secret, envelope.Salt, envelope.Iterations > 0 ? envelope.Iterations : Pbkdf2Iterations);
        try
        {
            return AesGcmDecrypt(kek, envelope.WrappedKey, envelope.Nonce, associatedData: default);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static byte[] Pbkdf2Kek(string secret, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret), salt, iterations, HashAlgorithmName.SHA256, KeyBytes);

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
            nonce, plaintext,
            blob.AsSpan(0, plaintext.Length), blob.AsSpan(plaintext.Length, TagBytes),
            associatedData);
        return (blob, nonce);
    }

    private static byte[] AesGcmDecrypt(byte[] key, byte[] blob, byte[] nonce, ReadOnlySpan<byte> associatedData)
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
            blob.AsSpan(0, plaintextLength), blob.AsSpan(plaintextLength, TagBytes),
            plaintext, associatedData);
        return plaintext;
    }
}
