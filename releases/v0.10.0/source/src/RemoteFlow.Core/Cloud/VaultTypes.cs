using System.Buffers.Binary;
using System.Text;

namespace RemoteFlow.Core.Cloud;

/// <summary>
/// Vault 加密算法标识。字符串与 AppsCloud 服务端 <c>DeviceKeyEnvelope.Algorithm</c> /
/// <c>RecoveryEnvelope.Algorithm</c> 保持一致——服务端不解析，仅作自描述。
/// </summary>
public static class VaultAlgorithms
{
    public const string DeviceEnvelope = "ECDH-P256_HKDF-SHA256_AES-256-GCM";
    public const string RecoveryEnvelope = "RK_HKDF-SHA256_AES-256-GCM";
    public const string Payload = "HKDF-SHA256_AES-256-GCM";
}

/// <summary>面向单个设备的 wrapped VMK 信封。全部字段对 AppsCloud 不透明。</summary>
public sealed record DeviceKeyEnvelope(
    string Algorithm,
    byte[] WrappedKey,
    byte[] Nonce,
    byte[] EphemeralPublicKey);

/// <summary>面向 Recovery Key 的 wrapped VMK 信封。</summary>
public sealed record RecoveryKeyEnvelope(
    string Algorithm,
    byte[] WrappedKey,
    byte[] Nonce,
    byte[] Salt);

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
