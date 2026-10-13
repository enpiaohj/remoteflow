using System.Buffers.Binary;
using System.Text;

namespace RemoteFlow.Core.Cloud;

/// <summary>信封类型（与服务端 <c>VaultKeyEnvelope.Kind</c> 一致）。</summary>
public static class VaultEnvelopeKinds
{
    public const string Password = "password";
    public const string Recovery = "recovery";
}

/// <summary>
/// Vault 加密算法标识。字符串与 AppsCloud 服务端 <c>VaultKeyEnvelope.Algorithm</c> 一致——
/// 服务端不解析，仅作自描述。
/// </summary>
public static class VaultAlgorithms
{
    /// <summary>口令 / Recovery Key 信封统一：PBKDF2-SHA256 派生 KEK + AES-256-GCM 包装 VMK。</summary>
    public const string SecretEnvelope = "PBKDF2-SHA256_AES-256-GCM";

    public const string Payload = "HKDF-SHA256_AES-256-GCM";
}

/// <summary>
/// 一份 wrapped VMK。<see cref="Kind"/> 区分 password / recovery。全部字段对 AppsCloud 不透明：
/// <see cref="Salt"/> / <see cref="Iterations"/> 是客户端 PBKDF2 参数。
/// </summary>
public sealed record VaultKeyEnvelope(
    string Kind,
    string Algorithm,
    byte[] WrappedKey,
    byte[] Nonce,
    byte[] Salt,
    int Iterations);

/// <summary>一个业务实体加密后的载荷，直接对应 AppsCloud <c>SyncEntity</c> 的 Ciphertext / Nonce。</summary>
public sealed record EncryptedPayload(byte[] Ciphertext, byte[] Nonce, int KeyVersion, int SchemaVersion);

/// <summary>
/// 载荷加密上下文。用于派生 Entity Key 并构造 AES-GCM 的 AAD，
/// 防止密文被跨用户 / 跨 App / 跨实体替换（安全设计 §5）。
/// </summary>
public sealed record PayloadContext(
    Guid UserId,
    string AppId,
    string EntityType,
    string EntityId,
    int SchemaVersion,
    int KeyVersion)
{
    /// <summary>
    /// 规范化 AAD：对每个字段以「4 字节大端长度 + UTF-8 字节」编码，整型直接 4 字节大端，
    /// 消除字段拼接歧义。加解密两侧用同一实现。
    /// </summary>
    public byte[] ComputeAad()
    {
        using var buffer = new MemoryStream();
        WriteField(buffer, Encoding.UTF8.GetBytes(UserId.ToString("D")));
        WriteField(buffer, Encoding.UTF8.GetBytes(AppId));
        WriteField(buffer, Encoding.UTF8.GetBytes(EntityType));
        WriteField(buffer, Encoding.UTF8.GetBytes(EntityId));
        WriteInt(buffer, SchemaVersion);
        WriteInt(buffer, KeyVersion);
        return buffer.ToArray();
    }

    private static void WriteField(Stream stream, byte[] value)
    {
        WriteInt(stream, value.Length);
        stream.Write(value);
    }

    private static void WriteInt(Stream stream, int value)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(tmp, value);
        stream.Write(tmp);
    }
}
