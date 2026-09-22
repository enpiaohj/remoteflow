using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Security;
using RemoteFlow.Infrastructure.Settings;

namespace RemoteFlow.Infrastructure.Sync;

/// <summary>云同步的统一入口。见 <see cref="ICloudSyncService"/>。</summary>
public sealed class CloudSyncService(
    ICloudClient client,
    ICloudTokenStore tokenStore,
    CloudEndpoint endpoint,
    CloudSyncGate gate,
    VaultMasterKeyService vaultKeys,
    SyncCoordinator coordinator,
    ConflictService conflicts,
    SqliteSyncStore store,
    AppSettings settings,
    JsonSettingsStore settingsStore,
    ILogger<CloudSyncService> logger,
    LocalDataWiper? wiper = null,
    Core.Diagnostics.ISystemInfoCollector? systemInfo = null,
    SyncEntityLabeler? labeler = null,
    VaultSwitchDetector? vaultSwitch = null) : ICloudSyncService
{
    private const string AppId = "com.appscloud.remoteflow";

    private IVaultSession? _session;
    private Guid _userId;
    private string _email = string.Empty;
    private int _keyVersion = 1;

    /// <summary>登录后暂存主口令，供 bootstrap / 解锁使用；解锁成功或退出后清空。</summary>
    private string? _pendingPassword;

    public bool IsSignedIn { get; private set; }

    public bool IsVaultUnlocked => _session is not null;

    public async Task<CloudUnlockState> SignInAsync(
        string baseUrl, string email, string password, CancellationToken ct = default)
        => await AuthenticateAsync(baseUrl, email, password, register: false, ct);

    public async Task<CloudUnlockState> RegisterAsync(
        string baseUrl, string email, string password, CancellationToken ct = default)
        => await AuthenticateAsync(baseUrl, email, password, register: true, ct);

    private async Task<CloudUnlockState> AuthenticateAsync(
        string baseUrl, string email, string password, bool register, CancellationToken ct)
    {
        endpoint.BaseUrl = baseUrl;
        var device = new CloudDeviceInfo(AppId, EnsureDeviceId(), Environment.MachineName, PlatformTag());

        if (register)
        {
            await RegisterThenLoginAsync(email, password, device, ct);
        }
        else
        {
            try
            {
                await client.LoginAsync(email, password, device, ct);
            }
            catch (CloudApiException ex) when (ex.StatusCode is 401)
            {
                throw new CloudSignInException("邮箱或密码不正确。若还没有账号，请点「注册」。");
            }
        }

        _userId = await client.GetUserIdAsync(ct);
        _email = email;
        _pendingPassword = password;
        IsSignedIn = true;

        settings.CloudBaseUrl = endpoint.BaseUrl!;
        settings.CloudEmail = email;
        settings.CloudSyncEnabled = true;
        await settingsStore.SaveAsync(settings, ct);

        await ReportSystemInfoAsync(ct);
        return await UnlockAsync(ct);
    }

    private async Task RegisterThenLoginAsync(
        string email, string password, CloudDeviceInfo device, CancellationToken ct)
    {
        CloudRegisterOutcome outcome;
        try
        {
            outcome = await client.RegisterAsync(email, password, ct);
        }
        catch (CloudApiException ex) when (ex.StatusCode is 400)
        {
            throw new CloudSignInException("创建账号失败：" + ex.Message, ex);
        }

        if (outcome == CloudRegisterOutcome.AlreadyExists)
        {
            throw new CloudSignInException("该邮箱已注册，请改用「登录」。");
        }

        try
        {
            await client.LoginAsync(email, password, device, ct);
        }
        catch (CloudApiException ex)
        {
            throw new CloudSignInException("账号已创建，但登录失败，请重试。", ex);
        }
    }

    public async Task<CloudUnlockState?> TryResumeAsync(CancellationToken ct = default)
    {
        if (!settings.CloudSyncEnabled || string.IsNullOrWhiteSpace(settings.CloudBaseUrl))
        {
            return null;
        }

        if (await tokenStore.GetAsync(ct) is not { } session)
        {
            return null;
        }

        endpoint.BaseUrl = settings.CloudBaseUrl;
        _userId = session.UserId;
        _email = settings.CloudEmail;
        IsSignedIn = true;
        await ReportSystemInfoAsync(ct);
        return await UnlockAsync(ct);
    }

    /// <summary>NeedsPassword 状态下用户补输口令解锁。</summary>
    public async Task<CloudUnlockState> UnlockWithPasswordAsync(string password, CancellationToken ct = default)
    {
        _pendingPassword = password;
        return await UnlockAsync(ct);
    }

    public async Task<string> BootstrapVaultAsync(
        bool requireDeviceApproval = false, CancellationToken ct = default)
    {
        var password = _pendingPassword
            ?? throw new InvalidOperationException("请先登录。");
        var (session, recoveryKey) = await vaultKeys.BootstrapAsync(password, requireDeviceApproval, ct);
        AdoptSession(session, keyVersion: 1);
        return recoveryKey.ToDisplayString();
    }

    public async Task<bool> GetRequireApprovalAsync(CancellationToken ct = default) =>
        (await client.GetVaultStatusAsync(ct)).RequireDeviceApproval;

    public Task SetRequireApprovalAsync(bool enabled, CancellationToken ct = default) =>
        client.SetRequireApprovalAsync(enabled, ct);

    public Task<IReadOnlyList<CloudPendingDevice>> GetPendingDevicesAsync(CancellationToken ct = default) =>
        client.GetPendingDevicesAsync(ct);

    public Task ApproveDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        client.ApproveDeviceAsync(deviceId, ct);

    /// <summary>本设备刚被批准后，重新解锁（此时应能拿到口令信封）。</summary>
    public Task<CloudUnlockState> RetryUnlockAsync(CancellationToken ct = default) => UnlockAsync(ct);

    public async Task RecoverVaultAsync(string recoveryKey, CancellationToken ct = default)
    {
        var session = await vaultKeys.RecoverAsync(recoveryKey, ct);
        AdoptSession(session, await CurrentKeyVersionAsync(ct));
    }

    public async Task ChangePasswordAsync(
        string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var session = RequireContext().Session;
        // 1) 换认证密钥（服务端作废其它会话）；2) 用新会话重登；3) 重新包装口令信封。
        await client.ChangePasswordAsync(_email, currentPassword, newPassword, ct);
        var device = new CloudDeviceInfo(AppId, EnsureDeviceId(), Environment.MachineName, PlatformTag());
        await client.LoginAsync(_email, newPassword, device, ct);
        await vaultKeys.ReWrapPasswordEnvelopeAsync(session, newPassword, ct);
        _pendingPassword = null;
        logger.LogInformation("主口令已更改");
    }

    public async Task<string> ResetRecoveryKeyAsync(CancellationToken ct = default)
    {
        var session = RequireContext().Session;
        var key = await vaultKeys.ResetRecoveryKeyAsync(session, ct);
        return key.ToDisplayString();
    }

    public async Task<SyncRunResult> SyncNowAsync(CancellationToken ct = default)
    {
        var context = RequireContext();
        var result = await coordinator.RunOnceAsync(context, ct);
        logger.LogInformation(
            "同步完成：推 {Pushed} 拉 {Pulled} 冲突 {Conflicts} 状态 {Status}",
            result.Pushed, result.Pulled, result.PushConflicts + result.PullConflicts, result.Status);
        return result;
    }

    public Task<SyncStateSnapshot> GetStateAsync(CancellationToken ct = default) =>
        store.GetStateAsync(AppId, ct);

    public Task<int> GetPendingOutboxCountAsync(CancellationToken ct = default) =>
        store.PendingCountAsync(ct);

    public async Task<IReadOnlyList<CloudConflictInfo>> GetConflictsAsync(CancellationToken ct = default)
    {
        var records = await conflicts.ListAsync(ct);
        var results = new List<CloudConflictInfo>(records.Count);
        var session = _session;

        foreach (var record in records)
        {
            var label = labeler is null
                ? record.EntityId
                : await labeler.DescribeAsync(record.EntityType, record.EntityId, ct);

            // 本机已删除时 DescribeAsync 只能给出「（已删除的 X）」——改用云端那份密文解出的名称，
            // 否则用户看到的是一串 GUID，根本不知道在问哪个分组 / 连接。
            if (label.Contains("（已删除", StringComparison.Ordinal)
                && session is not null
                && !record.Remote.Deleted
                && record.Remote.Ciphertext is { } ciphertext
                && record.Remote.Nonce is { } nonce)
            {
                try
                {
                    var plaintext = session.Decrypt(
                        new PayloadContext(
                            _userId, AppId, record.EntityType, record.EntityId,
                            record.Remote.SchemaVersion, record.Remote.KeyVersion),
                        new EncryptedPayload(ciphertext, nonce, record.Remote.KeyVersion, record.Remote.SchemaVersion));
                    var fromCloud = SyncEntityLabeler.DescribePayload(record.EntityType, plaintext);
                    if (!string.IsNullOrWhiteSpace(fromCloud))
                    {
                        label = fromCloud + "（云端）";
                    }
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    // 解不开就保留原标签，不影响冲突处理。
                }
            }

            var kind = DescribeSituation(record);
            results.Add(new CloudConflictInfo(
                record.Id, record.EntityType, record.EntityId, label, kind, record.DetectedAt));
        }

        return results;
    }

    /// <summary>把「哪边已删 / 哪边还在」讲清楚，用户才知道「保留本机 / 使用云端」各自意味着什么。</summary>
    private static string DescribeSituation(SyncConflictRecord record)
    {
        var type = SyncEntityLabeler.TypeLabel(record.EntityType);
        var localExists = record.Local is not null;
        return (localExists, record.Remote.Deleted) switch
        {
            (false, true) => type + " · 两边都已删除（选任一均会清除这条）",
            (true, true) => type + " · 云端已删除，本机仍有改动 —— 保留本机=恢复它；使用云端=删除它",
            (false, false) => type + " · 本机已删除，云端仍存在 —— 保留本机=仍删除；使用云端=恢复它",
            _ => record.EntityType == SyncEntityTypes.CredentialSecret
                ? type + " · 两边都改了，需你选择"
                : type + " · 两边都改了 —— 保留本机=用本机版本；使用云端=用云端版本",
        };
    }

    public async Task<IReadOnlyList<CloudSyncedCount>> GetSyncedCountsAsync(CancellationToken ct = default)
    {
        var counts = await store.GetSyncedCountsAsync(ct);
        // 固定顺序输出，UI 展示稳定。
        string[] order =
        [
            SyncEntityTypes.Connection, SyncEntityTypes.Credential, SyncEntityTypes.CredentialSecret,
            SyncEntityTypes.Group, SyncEntityTypes.Tag,
        ];
        return
        [
            .. order
                .Select(type => new CloudSyncedCount(
                    type, SyncEntityLabeler.TypeLabel(type),
                    counts.FirstOrDefault(c => c.EntityType == type).Count))
                .Where(x => x.Count > 0)
        ];
    }

    public Task ResolveConflictAsync(
        Guid conflictId, ConflictResolution resolution, CancellationToken ct = default) =>
        conflicts.ResolveAsync(RequireContext(), conflictId, resolution, ct);

    public async Task SignOutAsync(bool wipeLocalCloudData, CancellationToken ct = default)
    {
        await client.LogoutAsync(ct);
        _session?.Dispose();
        _session = null;
        _pendingPassword = null;
        IsSignedIn = false;
        gate.Enabled = false;

        settings.CloudSyncEnabled = false;
        if (wipeLocalCloudData)
        {
            await vaultKeys.ForgetAsync(ct);
            await store.ResetAsync(ct);
            settings.CloudEmail = string.Empty;
            // CloudDeviceId 保留：它是这台机器的稳定标识，清空会让每次「清除并重新登录」
            // 都在服务端注册一个新设备行，越积越多。撤销设备请在账号侧操作。
        }

        await settingsStore.SaveAsync(settings, ct);
    }

    /// <summary>
    /// 兜底：清除本机全部业务数据与同步状态，然后从云端完整拉取恢复（以云端为准）。
    /// 需 Vault 已解锁（<see cref="RequireContext"/> 保证）；保留云会话、设备身份与本地 Secret 之外的一切。
    /// 离线时本地被清空但拉取失败 —— 调用方应提示用户网络后重试（本地仅剩系统默认分组，由启动逻辑补种）。
    /// </summary>
    public async Task RestoreFromCloudAsync(CancellationToken ct = default)
    {
        var context = RequireContext();
        if (wiper is null)
        {
            throw new InvalidOperationException("当前构建未注册 LocalDataWiper（不支持清除本地数据）。");
        }

        await wiper.WipeLocalDataForCloudRestoreAsync(ct);
        // sync_state 已清空 → 游标归 0 → 本轮同步把云端作为权威完整拉取。
        await coordinator.RunOnceAsync(context, ct);
        logger.LogInformation("已清除本地数据并从云端恢复");
    }

    /// <summary>
    /// 上报本机基础系统信息（资产信息，非机密）。尽力而为：失败只记日志，绝不影响登录 / 同步。
    /// </summary>
    private async Task ReportSystemInfoAsync(CancellationToken ct)
    {
        if (systemInfo is null)
        {
            return;
        }

        try
        {
            await client.PutSystemInfoAsync(systemInfo.Collect(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "上报系统信息失败（不影响登录 / 同步）");
        }
    }

    // ── 内部 ────────────────────────────────────────────────────

    private async Task<CloudUnlockState> UnlockAsync(CancellationToken ct)
    {
        var result = await vaultKeys.TryUnlockAsync(_pendingPassword, ct);
        switch (result.State)
        {
            case VaultUnlockState.Unlocked:
            {
                var status = await client.GetVaultStatusAsync(ct);
                if (vaultSwitch is not null)
                {
                    // Vault 被重建（旧账号被清空 / 重新初始化）时，本地游标与版本号不再有意义——
                    // 清掉同步状态，让下一次同步把本机数据作为新 Vault 的全量内容重推。
                    await vaultSwitch.EnsureSameVaultAsync(status.VaultId?.ToString(), ct);
                }

                AdoptSession(result.Session!, status.CurrentKeyVersion ?? 1);
                _pendingPassword = null;
                return CloudUnlockState.Ready;
            }

            case VaultUnlockState.NeedsBootstrap:
                return CloudUnlockState.NeedsBootstrap;

            case VaultUnlockState.NeedsApproval:
                return CloudUnlockState.NeedsApproval;

            default:
                return CloudUnlockState.NeedsPassword;
        }
    }

    private void AdoptSession(IVaultSession session, int keyVersion)
    {
        _session?.Dispose();
        _session = session;
        _keyVersion = keyVersion;
        gate.Enabled = true;
    }

    private async Task<int> CurrentKeyVersionAsync(CancellationToken ct) =>
        (await client.GetVaultStatusAsync(ct)).CurrentKeyVersion ?? 1;

    private SyncContext RequireContext() => new(
        _userId,
        AppId,
        _keyVersion,
        _session ?? throw new InvalidOperationException("Vault 尚未解锁。"));

    private string EnsureDeviceId()
    {
        if (string.IsNullOrWhiteSpace(settings.CloudDeviceId))
        {
            settings.CloudDeviceId = Guid.NewGuid().ToString("N");
        }

        return settings.CloudDeviceId;
    }

    private static string PlatformTag() =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "macos"
        : "unknown";
}
