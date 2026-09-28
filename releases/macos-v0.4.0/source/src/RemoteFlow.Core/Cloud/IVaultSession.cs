namespace RemoteFlow.Core.Cloud;

/// <summary>
/// 已解锁的 Vault 会话。持有 VMK（进程内存中，生命周期受控），提供实体载荷的加解密。
/// Vault 锁定时不存在会话；被 <see cref="IDisposable.Dispose"/> 后即失效。
/// </summary>
public interface IVaultSession : IDisposable
{
    bool IsUnlocked { get; }

    EncryptedPayload Encrypt(PayloadContext context, ReadOnlySpan<byte> plaintext);

    byte[] Decrypt(PayloadContext context, EncryptedPayload payload);

    /// <summary>用目标设备公钥（SPKI DER）把本会话持有的 VMK 包装成设备信封——批准新设备时用，VMK 不出会话。</summary>
    DeviceKeyEnvelope WrapMasterKeyForDevice(byte[] recipientPublicKey);
}
