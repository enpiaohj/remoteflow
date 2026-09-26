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

    /// <summary>用一个 secret（主口令 / Recovery Key 显示串）把本会话的 VMK 包装成信封——改口令 / 重置 Recovery Key 时用，VMK 不出会话。</summary>
    VaultKeyEnvelope WrapWithSecret(string kind, string secret);
}
