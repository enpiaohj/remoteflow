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
    DeviceKeyService deviceKeys,
    SyncCoordinator coordinator,
    ConflictService conflicts,
    SqliteSyncStore store,
    AppSettings settings,
    JsonSettingsStore settingsStore,
    ILogger<CloudSyncService> logger) : ICloudSyncService
{
    private const string AppId = "com.appscloud.remoteflow";

    private IVaultSession? _session;
    private Guid _userId;
    private string _email = string.Empty;
    private int _keyVersion = 1;

    public bool IsSignedIn { get; private set; }

    public bool IsVaultUnlocked => _session is not null;

    public async Task<CloudUnlockState> SignInAsync(
        string baseUrl, string email, string password, CancellationToken ct = default)
    {
        endpoint.BaseUrl = baseUrl;

        var publicKey = Convert.ToBase64String(await deviceKeys.EnsureDeviceKeyAsync(ct));
        var device = new CloudDeviceInfo(
            AppId, EnsureDeviceId(), Environment.MachineName, PlatformTag(), publicKey);
        await LoginOrRegisterAsync(email, password, device, ct);

        _userId = await client.GetUserIdAsync(ct);
        _email = email;
        IsSignedIn = true;

        settings.CloudBaseUrl = endpoint.BaseUrl!;
        settings.CloudSyncEnabled = true;
        await settingsStore.SaveAsync(settings, ct);

        return await UnlockAsync(ct);
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
        IsSignedIn = true;
        return await UnlockAsync(ct);
    }

    public async Task<string> BootstrapVaultAsync(CancellationToken ct = default)
    {
        var (session, recoveryKey) = await vaultKeys.BootstrapAsync(ct);
        AdoptSession(session, keyVersion: 1);
        return recoveryKey.ToDisplayString();
    }

    public async Task RecoverVaultAsync(string recoveryKey, CancellationToken ct = default)
    {
        var session = await vaultKeys.RecoverAsync(recoveryKey, ct);
        AdoptSession(session, await CurrentKeyVersionAsync(ct));
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

    public Task<IReadOnlyList<CloudPendingDevice>> GetPendingDevicesAsync(CancellationToken ct = default) =>
        client.GetPendingDevicesAsync(ct);

    public async Task ApproveDeviceAsync(Guid deviceId, CancellationToken ct = default)
    {
        var session = RequireContext().Session;
        var pending = (await client.GetPendingDevicesAsync(ct)).FirstOrDefault(p => p.DeviceId == deviceId)
            ?? throw new InvalidOperationException("待批准设备不存在（可能已被批准或撤销）。");
        await vaultKeys.ApprovePendingDeviceAsync(session, pending, ct);
    }

    public Task<IReadOnlyList<SyncConflictRecord>> GetConflictsAsync(CancellationToken ct = default) =>
        conflicts.ListAsync(ct);

    public Task ResolveConflictAsync(
        Guid conflictId, ConflictResolution resolution, CancellationToken ct = default) =>
        conflicts.ResolveAsync(RequireContext(), conflictId, resolution, ct);

    public async Task SignOutAsync(bool wipeLocalCloudData, CancellationToken ct = default)
    {
        await client.LogoutAsync(ct);
        _session?.Dispose();
        _session = null;
        IsSignedIn = false;
        gate.Enabled = false;

        settings.CloudSyncEnabled = false;
        if (wipeLocalCloudData)
        {
            await vaultKeys.ForgetAsync(ct);
            await store.ResetAsync(ct);
            settings.CloudDeviceId = string.Empty;
        }

        await settingsStore.SaveAsync(settings, ct);
    }

    // ── 内部 ────────────────────────────────────────────────────

    /// <summary>创建账号时 AppsCloud 要求的最短密码长度（与服务端 RegisterRequest 对齐）。</summary>
    private const int MinNewAccountPasswordLength = 12;

    /// <summary>
    /// 登录；若该邮箱在 AppsCloud 尚无账号（首次使用），自动创建后再登录。
    /// 设计文档「注册 / 登录」为同一步：新部署或换服务地址时用户无需先去别处开户。
    /// 账号已存在但密码不符时按凭据错误处理，绝不覆盖既有账号。
    /// </summary>
    /// <exception cref="CloudSignInException">凭据错误、或需要用户修正后重试的可读失败。</exception>
    private async Task LoginOrRegisterAsync(
        string email, string password, CloudDeviceInfo device, CancellationToken ct)
    {
        try
        {
            await client.LoginAsync(email, password, device, ct);
            return;
        }
        catch (CloudApiException ex) when (ex.StatusCode is 401)
        {
            logger.LogInformation("AppsCloud 登录被拒（401），按首次使用尝试创建账号");
        }

        if (password.Length < MinNewAccountPasswordLength)
        {
            // 账号不存在才会走到这里；密码太短则注册必失败，直接给准话。
            throw new CloudSignInException(
                $"该邮箱在 AppsCloud 尚无账号。若要创建，密码至少需 {MinNewAccountPasswordLength} 位；"
                + "若账号已存在，请检查密码是否输错。");
        }

        CloudRegisterOutcome outcome;
        try
        {
            outcome = await client.RegisterAsync(email, password, ct);
        }
        catch (CloudApiException ex) when (ex.StatusCode is 400)
        {
            throw new CloudSignInException("创建 AppsCloud 账号失败：" + ex.Message, ex);
        }

        if (outcome == CloudRegisterOutcome.AlreadyExists)
        {
            throw new CloudSignInException("邮箱或密码不正确。");
        }

        try
        {
            await client.LoginAsync(email, password, device, ct);
        }
        catch (CloudApiException ex)
        {
            throw new CloudSignInException("账号已创建，但随后登录失败，请重试。", ex);
        }

        logger.LogInformation("已创建 AppsCloud 账号并登录");
    }

    private async Task<CloudUnlockState> UnlockAsync(CancellationToken ct)
    {
        var result = await vaultKeys.TryUnlockAsync(ct);
        switch (result.State)
        {
            case VaultUnlockState.Unlocked:
                AdoptSession(result.Session!, await CurrentKeyVersionAsync(ct));
                return CloudUnlockState.Ready;

            case VaultUnlockState.NeedsBootstrap:
                return CloudUnlockState.NeedsBootstrap;

            default:
                return CloudUnlockState.NeedsApproval;
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
