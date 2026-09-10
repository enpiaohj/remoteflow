using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Application.Services;

/// <summary>
/// 凭据备份包的明文内容。只在内存与加解密前后短暂存在，绝不落盘、绝不打日志。
/// </summary>
public sealed class CredentialBackupPayload
{
    public List<CredentialBackupEntry> Credentials { get; set; } = [];
}

/// <summary>备份包里的单条凭据。<see cref="Password"/> / <see cref="PrivateKey"/> 为明文，可为 null。</summary>
public sealed class CredentialBackupEntry
{
    public string Name { get; set; } = string.Empty;
    public CredentialType Type { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Password { get; set; }
    public string? PrivateKey { get; set; }
}

/// <summary>导入失败的原因，供界面给出准确提示而不是笼统报错。</summary>
public enum CredentialBackupImportFailure
{
    None,
    NotABackupFile,
    UnsupportedVersion,
    WrongPasswordOrCorrupted,
}

public sealed class CredentialBackupImportResult
{
    public CredentialBackupPayload? Payload { get; init; }
    public CredentialBackupImportFailure Failure { get; init; }
    public bool Succeeded => Payload is not null;
}

/// <summary>
/// 凭据的跨设备加密备份包。
/// <para>
/// 为什么需要独立的一套加密：本机 Vault 用 DPAPI（<c>CurrentUser</c> 作用域）保护，
/// 只有同一台机器的同一个 Windows 用户能解开，天然无法跨设备。迁移凭据必须是
/// 「显式导出 + 用户自己设的口令」这一条独立路径。
/// </para>
/// <para>
/// 安全取舍（用户明确选择了「密钥也导出」）：
/// <list type="bullet">
///   <item>口令经 PBKDF2-HMAC-SHA256 派生（迭代次数 <see cref="Pbkdf2Iterations"/> 随包记录，
///     便于将来提高强度时仍能读旧包），配 16 字节随机盐。</item>
///   <item>内容用 AES-256-GCM 加密，12 字节随机 nonce，16 字节认证标签。GCM 是 AEAD：
///     口令错误或文件被篡改都会在解密时被标签校验挡下，不会解出垃圾数据。</item>
///   <item>明文（含密钥）只在内存中存在；本类不写任何文件、不打日志。</item>
/// </list>
/// </para>
/// </summary>
public static class CredentialBackup
{
    private const string FormatMarker = "remoteflow-credentials";
    private const int FormatVersion = 1;

    /// <summary>PBKDF2 迭代次数，取 OWASP 对 PBKDF2-HMAC-SHA256 的推荐量级。</summary>
    public const int Pbkdf2Iterations = 600_000;

    private const int SaltBytes = 16;
    private const int NonceBytes = 12; // AES-GCM 标准 nonce 长度
    private const int TagBytes = 16;
    private const int KeyBytes = 32;   // AES-256

    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions EnvelopeJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class Envelope
    {
        public string Format { get; set; } = FormatMarker;
        public int Version { get; set; } = FormatVersion;
        public string Kdf { get; set; } = "pbkdf2-sha256";
        public int Iterations { get; set; } = Pbkdf2Iterations;
        public string Cipher { get; set; } = "aes-256-gcm";
        public string Salt { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public string Ciphertext { get; set; } = string.Empty;
    }

    /// <summary>把凭据打成一个用口令加密的备份包（返回可直接写盘的 JSON 文本）。</summary>
    public static string Export(CredentialBackupPayload payload, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, PayloadJson));
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var key = DeriveKey(password, salt, Pbkdf2Iterations);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }

        return JsonSerializer.Serialize(
            new Envelope
            {
                Iterations = Pbkdf2Iterations,
                Salt = Convert.ToBase64String(salt),
                Nonce = Convert.ToBase64String(nonce),
                Tag = Convert.ToBase64String(tag),
                Ciphertext = Convert.ToBase64String(ciphertext),
            },
            EnvelopeJson);
    }

    /// <summary>
    /// 解开备份包。口令错误、文件被改动、或根本不是备份文件时返回对应的失败原因，
    /// 绝不抛异常——调用方拿到的要么是完整可用的内容，要么是明确的失败。
    /// </summary>
    public static CredentialBackupImportResult Import(string fileContent, string password)
    {
        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(fileContent, EnvelopeJson);
        }
        catch (JsonException)
        {
            return new CredentialBackupImportResult { Failure = CredentialBackupImportFailure.NotABackupFile };
        }

        if (envelope is null || envelope.Format != FormatMarker)
        {
            return new CredentialBackupImportResult { Failure = CredentialBackupImportFailure.NotABackupFile };
        }

        if (envelope.Version > FormatVersion || envelope.Kdf != "pbkdf2-sha256" || envelope.Cipher != "aes-256-gcm")
        {
            return new CredentialBackupImportResult { Failure = CredentialBackupImportFailure.UnsupportedVersion };
        }

        byte[] salt, nonce, tag, ciphertext;
        try
        {
            salt = Convert.FromBase64String(envelope.Salt);
            nonce = Convert.FromBase64String(envelope.Nonce);
            tag = Convert.FromBase64String(envelope.Tag);
            ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        }
        catch (FormatException)
        {
            return new CredentialBackupImportResult { Failure = CredentialBackupImportFailure.NotABackupFile };
        }

        if (salt.Length != SaltBytes || nonce.Length != NonceBytes || tag.Length != TagBytes ||
            envelope.Iterations is < 1 or > 10_000_000)
        {
            return new CredentialBackupImportResult { Failure = CredentialBackupImportFailure.NotABackupFile };
        }

        var key = DeriveKey(password, salt, envelope.Iterations);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            // 口令错误或内容被篡改时 GCM 标签校验失败，这里会抛 CryptographicException——
            // 正是我们要的：绝不会解出垃圾数据。
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            return new CredentialBackupImportResult { Failure = CredentialBackupImportFailure.WrongPasswordOrCorrupted };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        try
        {
            var payload = JsonSerializer.Deserialize<CredentialBackupPayload>(
                Encoding.UTF8.GetString(plaintext), PayloadJson);

            return payload is null
                ? new CredentialBackupImportResult { Failure = CredentialBackupImportFailure.WrongPasswordOrCorrupted }
                : new CredentialBackupImportResult { Payload = payload };
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        {
            return new CredentialBackupImportResult { Failure = CredentialBackupImportFailure.WrongPasswordOrCorrupted };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
}
