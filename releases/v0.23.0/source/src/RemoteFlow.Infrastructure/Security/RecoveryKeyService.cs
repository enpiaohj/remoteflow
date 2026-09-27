using RemoteFlow.Core.Cloud;

namespace RemoteFlow.Infrastructure.Security;

/// <summary>Recovery Key 的生成、展示格式化与用它恢复 VMK。</summary>
public sealed class RecoveryKeyService
{
    /// <summary>
    /// 生成 Recovery Key（返回给 UI 一次性展示）与其信封（上传）。
    /// KDF 输入用规范分组串——用户抄下的就是这个。
    /// </summary>
    public (RecoveryKey Key, VaultKeyEnvelope Envelope) Create(ReadOnlySpan<byte> masterKey)
    {
        var key = RecoveryKey.Generate();
        var envelope = VaultCryptography.WrapWithSecret(
            VaultEnvelopeKinds.Recovery, key.ToDisplayString(), masterKey);
        return (key, envelope);
    }

    /// <summary>
    /// 用户输入 Recovery Key + 服务端信封恢复 VMK。输入非法抛 <see cref="FormatException"/>，
    /// Key 不匹配抛 <see cref="System.Security.Cryptography.CryptographicException"/>。
    /// </summary>
    public byte[] RecoverMasterKey(string userInput, VaultKeyEnvelope envelope)
    {
        var canonical = RecoveryKey.Parse(userInput).ToDisplayString();
        return VaultCryptography.UnwrapWithSecret(canonical, envelope);
    }
}
