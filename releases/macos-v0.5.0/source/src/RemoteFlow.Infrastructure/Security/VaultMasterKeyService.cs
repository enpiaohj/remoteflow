using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Cloud;

namespace RemoteFlow.Infrastructure.Security;

public enum VaultUnlockState
{
    /// <summary>已解锁，<see cref="VaultUnlockResult.Session"/> 可用。</summary>
    Unlocked,

    /// <summary>该账号还没有 Vault，需要本设备执行 bootstrap。</summary>
    NeedsBootstrap,

    /// <summary>Vault 存在但本地没有缓存 VMK 且未提供口令，需要用户输入口令（或用 Recovery Key 恢复）。</summary>
    NeedsPassword,

    /// <summary>Vault 开启「新设备需批准」，本设备尚未获批准——等已有设备批准，或用 Recovery Key 恢复。</summary>
    NeedsApproval,
}

public sealed record VaultUnlockResult(VaultUnlockState State, IVaultSession? Session);

/// <summary>
/// VMK 解锁编排（口令派生模型）：缓存 VMK → 口令解开信封 → 否则 NeedsPassword / NeedsBootstrap。
/// 会话内 VMK 生命周期受控；持久缓存由 <see cref="IVaultKeyStore"/> 经平台密钥库保护。
/// </summary>
public sealed class VaultMasterKeyService(
    ICloudClient client,
    IVaultKeyStore keyStore,
    RecoveryKeyService recoveryKeys,
    ILogger<VaultMasterKeyService> logger)
{
    /// <summary>尝试解锁。<paramref name="password"/> 为 null 时只走本地缓存。</summary>
    public async Task<VaultUnlockResult> TryUnlockAsync(string? password, CancellationToken ct = default)
    {
        var cached = await keyStore.GetCachedMasterKeyAsync(ct);
        if (cached is not null)
        {
            try
            {
                return Unlocked(cached);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(cached);
            }
        }

        var status = await client.GetVaultStatusAsync(ct);
        if (!status.Exists)
        {
            return new VaultUnlockResult(VaultUnlockState.NeedsBootstrap, null);
        }

        if (status.RequireDeviceApproval && !status.ThisDeviceApproved)
        {
            return new VaultUnlockResult(VaultUnlockState.NeedsApproval, null);
        }

        if (string.IsNullOrEmpty(password))
        {
            return new VaultUnlockResult(VaultUnlockState.NeedsPassword, null);
        }

        var envelope = await client.GetVaultEnvelopeAsync(VaultEnvelopeKinds.Password, ct);
        if (envelope is null)
        {
            // Vault 存在但没有口令信封（异常状态）——只能用 Recovery Key。
            return new VaultUnlockResult(VaultUnlockState.NeedsPassword, null);
        }

        var masterKey = VaultCryptography.UnwrapWithSecret(password, envelope);
        try
        {
            await keyStore.SetCachedMasterKeyAsync(masterKey, ct);
            logger.LogInformation("Vault 已通过口令解锁");
            return Unlocked(masterKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    /// <summary>首设备初始化 Vault。返回会话与一次性展示的 Recovery Key。</summary>
    public async Task<(IVaultSession Session, RecoveryKey RecoveryKey)> BootstrapAsync(
        string password, bool requireDeviceApproval, CancellationToken ct = default)
    {
        var masterKey = VaultCryptography.NewMasterKey();
        try
        {
            var passwordEnvelope = VaultCryptography.WrapWithSecret(
                VaultEnvelopeKinds.Password, password, masterKey);
            var (recoveryKey, recoveryEnvelope) = recoveryKeys.Create(masterKey);

            if (!await client.BootstrapVaultAsync(passwordEnvelope, recoveryEnvelope, requireDeviceApproval, ct))
            {
                throw new InvalidOperationException("Vault already exists for this account; unlock instead.");
            }

            await keyStore.SetCachedMasterKeyAsync(masterKey, ct);
            logger.LogInformation("Vault 已初始化（本设备为首设备）");
            return (Session(masterKey), recoveryKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    /// <summary>忘记口令时用 Recovery Key 恢复 VMK。恢复后应引导用户设置新口令。</summary>
    public async Task<IVaultSession> RecoverAsync(string recoveryKeyInput, CancellationToken ct = default)
    {
        var recoveryEnvelope = await client.GetVaultEnvelopeAsync(VaultEnvelopeKinds.Recovery, ct)
            ?? throw new InvalidOperationException("No recovery envelope is stored on the server.");

        var masterKey = recoveryKeys.RecoverMasterKey(recoveryKeyInput, recoveryEnvelope);
        try
        {
            await keyStore.SetCachedMasterKeyAsync(masterKey, ct);
            logger.LogInformation("Vault 已通过 Recovery Key 恢复");
            return Session(masterKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    /// <summary>用新口令重新包装 VMK 并上传（改口令流程的一步）。</summary>
    public Task ReWrapPasswordEnvelopeAsync(
        IVaultSession session, string newPassword, CancellationToken ct = default)
    {
        var envelope = session.WrapWithSecret(VaultEnvelopeKinds.Password, newPassword);
        return client.PutVaultEnvelopeAsync(envelope, ct);
    }

    /// <summary>重置 Recovery Key：生成新的、重新包装 VMK 并上传，返回新 Key 供一次性展示。</summary>
    public async Task<RecoveryKey> ResetRecoveryKeyAsync(IVaultSession session, CancellationToken ct = default)
    {
        var key = RecoveryKey.Generate();
        var envelope = session.WrapWithSecret(VaultEnvelopeKinds.Recovery, key.ToDisplayString());
        await client.PutVaultEnvelopeAsync(envelope, ct);
        logger.LogInformation("Recovery Key 已重置");
        return key;
    }

    /// <summary>退出云账号 / 清除此设备云数据。</summary>
    public Task ForgetAsync(CancellationToken ct = default) => keyStore.ClearAsync(ct);

    private static VaultUnlockResult Unlocked(ReadOnlySpan<byte> masterKey) =>
        new(VaultUnlockState.Unlocked, new VaultSession(masterKey));

    private static IVaultSession Session(ReadOnlySpan<byte> masterKey) => new VaultSession(masterKey);
}
