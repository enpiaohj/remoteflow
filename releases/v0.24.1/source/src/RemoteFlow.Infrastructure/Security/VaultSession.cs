using System.Security.Cryptography;
using RemoteFlow.Core.Cloud;

namespace RemoteFlow.Infrastructure.Security;

/// <summary>持有 VMK 副本的解锁会话。Dispose 时清零内存。</summary>
public sealed class VaultSession : IVaultSession
{
    private byte[]? _masterKey;

    public VaultSession(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length != 32)
        {
            throw new ArgumentException("Master key must be 32 bytes.", nameof(masterKey));
        }

        _masterKey = masterKey.ToArray();
    }

    public bool IsUnlocked => _masterKey is not null;

    public EncryptedPayload Encrypt(PayloadContext context, ReadOnlySpan<byte> plaintext) =>
        VaultCryptography.EncryptPayload(RequireKey(), context, plaintext);

    public byte[] Decrypt(PayloadContext context, EncryptedPayload payload) =>
        VaultCryptography.DecryptPayload(RequireKey(), context, payload);

    public VaultKeyEnvelope WrapWithSecret(string kind, string secret) =>
        VaultCryptography.WrapWithSecret(kind, secret, RequireKey());

    private ReadOnlySpan<byte> RequireKey() =>
        _masterKey ?? throw new InvalidOperationException("Vault session is locked.");

    public void Dispose()
    {
        if (_masterKey is not null)
        {
            CryptographicOperations.ZeroMemory(_masterKey);
            _masterKey = null;
        }
    }
}
