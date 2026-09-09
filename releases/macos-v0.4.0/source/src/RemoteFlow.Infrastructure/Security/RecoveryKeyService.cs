using RemoteFlow.Core.Cloud;

namespace RemoteFlow.Infrastructure.Security;

/// <summary>Recovery Key 的生成、展示格式化与用它恢复 VMK。</summary>
public sealed class RecoveryKeyService
{
    /// <summary>
    /// 首次开启 Cloud Vault 时调用：生成 Recovery Key（返回给 UI 一次性展示）与其 Envelope（上传）。
    /// </summary>
    public (RecoveryKey Key, RecoveryKeyEnvelope Envelope) Create(ReadOnlySpan<byte> masterKey)
    {
        var key = RecoveryKey.Generate();
        var envelope = VaultCryptography.WrapForRecovery(key.AsSpan(), masterKey);
        return (key, envelope);
    }

    /// <summary>用户输入 Recovery Key + 服务端 Envelope 恢复 VMK。Key 错误抛 <see cref="System.Security.Cryptography.CryptographicException"/>。</summary>
    public byte[] RecoverMasterKey(string userInput, RecoveryKeyEnvelope envelope)
    {
        var key = RecoveryKey.Parse(userInput);
        return VaultCryptography.UnwrapFromRecovery(key.AsSpan(), envelope);
    }
}
