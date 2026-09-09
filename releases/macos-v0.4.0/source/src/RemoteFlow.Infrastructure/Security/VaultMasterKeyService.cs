using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Cloud;

namespace RemoteFlow.Infrastructure.Security;

public enum VaultUnlockState
{
    /// <summary>已解锁，<see cref="VaultUnlockResult.Session"/> 可用。</summary>
    Unlocked,

    /// <summary>该账号在本 App 下还没有 Vault，需要本设备执行 bootstrap。</summary>
    NeedsBootstrap,

    /// <summary>Vault 存在但本设备未获授权，需要其他设备批准或用 Recovery Key 恢复。</summary>
    NeedsApproval,
}

public sealed record VaultUnlockResult(VaultUnlockState State, IVaultSession? Session);

/// <summary>
/// VMK 解锁编排：缓存 → 设备信封 → （否则）待批准 / bootstrap。
/// 会话内 VMK 生命周期受控；持久缓存由 <see cref="IVaultKeyStore"/> 经平台密钥库保护。
/// </summary>
public sealed class VaultMasterKeyService(
    ICloudClient client,
    IVaultKeyStore keyStore,
    DeviceKeyService deviceKeys,
    RecoveryKeyService recoveryKeys,
    ILogger<VaultMasterKeyService> logger)
{
    /// <summary>无需用户交互地尝试解锁。</summary>
    public async Task<VaultUnlockResult> TryUnlockAsync(CancellationToken ct = default)
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

        if (!status.ThisDeviceAuthorized)
        {
            return new VaultUnlockResult(VaultUnlockState.NeedsApproval, null);
        }

        var envelope = await client.GetDeviceEnvelopeAsync(ct);
        if (envelope is null)
        {
            return new VaultUnlockResult(VaultUnlockState.NeedsApproval, null);
        }

        var masterKey = await deviceKeys.UnwrapMasterKeyAsync(envelope, ct);
        try
        {
            await keyStore.SetCachedMasterKeyAsync(masterKey, ct);
            logger.LogInformation("Vault 已通过设备信封解锁");
            return Unlocked(masterKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    /// <summary>首设备初始化 Vault。返回会话与一次性展示的 Recovery Key。</summary>
    public async Task<(IVaultSession Session, RecoveryKey RecoveryKey)> BootstrapAsync(CancellationToken ct = default)
    {
        var masterKey = VaultCryptography.NewMasterKey();
        try
        {
            var publicKey = await deviceKeys.EnsureDeviceKeyAsync(ct);
            var deviceEnvelope = deviceKeys.WrapMasterKeyFor(publicKey, masterKey);
            var (recoveryKey, recoveryEnvelope) = recoveryKeys.Create(masterKey);

            if (!await client.BootstrapVaultAsync(deviceEnvelope, recoveryEnvelope, ct))
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

    /// <summary>无旧设备时用 Recovery Key 恢复 VMK，并自助登记本设备信封。</summary>
    public async Task<IVaultSession> RecoverAsync(string recoveryKeyInput, CancellationToken ct = default)
    {
        var recoveryEnvelope = await client.GetRecoveryEnvelopeAsync(ct)
            ?? throw new InvalidOperationException("No recovery envelope is stored on the server.");

        var masterKey = recoveryKeys.RecoverMasterKey(recoveryKeyInput, recoveryEnvelope);
        try
        {
            var publicKey = await deviceKeys.EnsureDeviceKeyAsync(ct);
            var deviceEnvelope = deviceKeys.WrapMasterKeyFor(publicKey, masterKey);
            var deviceId = await client.GetCurrentDeviceIdAsync(ct);
            await client.AddDeviceEnvelopeAsync(deviceId, deviceEnvelope, ct);

            await keyStore.SetCachedMasterKeyAsync(masterKey, ct);
            logger.LogInformation("Vault 已通过 Recovery Key 恢复");
            return Session(masterKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    /// <summary>在已授权设备上批准一个待批准设备：用它的公钥包装 VMK 并上传信封。</summary>
    public async Task ApprovePendingDeviceAsync(
        IVaultSession session, CloudPendingDevice pending, CancellationToken ct = default)
    {
        var envelope = session.WrapMasterKeyForDevice(pending.PublicKey);
        await client.AddDeviceEnvelopeAsync(pending.DeviceId, envelope, ct);
        logger.LogInformation("已批准设备 {DeviceId}", pending.DeviceId);
    }

    /// <summary>退出云账号 / 清除此设备云数据。</summary>
    public Task ForgetAsync(CancellationToken ct = default) => keyStore.ClearAsync(ct);

    private static VaultUnlockResult Unlocked(ReadOnlySpan<byte> masterKey) =>
        new(VaultUnlockState.Unlocked, new VaultSession(masterKey));

    private static IVaultSession Session(ReadOnlySpan<byte> masterKey) => new VaultSession(masterKey);
}
