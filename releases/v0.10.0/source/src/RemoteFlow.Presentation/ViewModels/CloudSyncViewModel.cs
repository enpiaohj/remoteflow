using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.Presentation.ViewModels;

/// <summary>Cloud Sync 面板的 UI 状态机。</summary>
public enum CloudSyncUiState
{
    SignedOut,
    NeedsVaultSetup,
    NeedsApproval,
    Ready,
}

/// <summary>设备批准列表的一行。</summary>
public sealed record CloudDeviceRow(Guid DeviceId, string Name, string Platform, DateTimeOffset LastSeenAt);

/// <summary>冲突列表的一行。</summary>
public sealed record CloudConflictRow(Guid Id, string EntityType, string EntityId, DateTimeOffset DetectedAt);

/// <summary>
/// 「设置 → Cloud Sync」面板。登录 AppsCloud、初始化 / 恢复 Vault、手动同步、
/// 批准新设备、解决冲突、退出。View 只做绑定与呈现。
/// </summary>
public sealed partial class CloudSyncViewModel(
    ICloudSyncService sync,
    IDialogService dialogs,
    AppSettings settings,
    ILogger<CloudSyncViewModel> logger) : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditConnectionFields))]
    private CloudSyncUiState _state = CloudSyncUiState.SignedOut;

    [ObservableProperty]
    private string _serverUrl = settings.CloudBaseUrl;

    /// <summary>
    /// 服务地址是否可编辑。默认：仅当没有预配置地址（开发 / 未打包）时可改；
    /// 已配置时锁定，特殊情况由用户勾选「使用自定义服务地址」解锁。
    /// </summary>
    [ObservableProperty]
    private bool _serverUrlEditable = string.IsNullOrWhiteSpace(settings.CloudBaseUrl);

    public bool HasConfiguredServerUrl { get; } = !string.IsNullOrWhiteSpace(settings.CloudBaseUrl);

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _recoveryKeyInput = string.Empty;

    /// <summary>bootstrap 后一次性展示的 Recovery Key（分组字符串）。用户确认已保存后清空。</summary>
    [ObservableProperty]
    private string? _newRecoveryKey;

    [ObservableProperty]
    private string _statusLine = "未登录";

    [ObservableProperty]
    private DateTimeOffset? _lastSyncedAt;

    [ObservableProperty]
    private int _pendingChangeCount;

    [ObservableProperty]
    private int _conflictCount;

    [ObservableProperty]
    private string? _errorMessage;

    public ObservableCollection<CloudDeviceRow> PendingDevices { get; } = [];

    public ObservableCollection<CloudConflictRow> Conflicts { get; } = [];

    public bool CanEditConnectionFields => State == CloudSyncUiState.SignedOut;

    public async Task InitializeAsync()
    {
        await RunAsync(async () =>
        {
            var resumed = await sync.TryResumeAsync();
            if (resumed is { } unlock)
            {
                ApplyUnlockState(unlock);
                await RefreshInternalAsync();
            }
        });
    }

    [RelayCommand]
    private Task ConnectAsync() => RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(ServerUrl)
            || string.IsNullOrWhiteSpace(Email)
            || string.IsNullOrEmpty(Password))
        {
            ErrorMessage = "请填写服务地址、邮箱和密码。";
            return;
        }

        if (!Email.Contains('@', StringComparison.Ordinal))
        {
            ErrorMessage = "邮箱格式不正确。";
            return;
        }

        var unlock = await sync.SignInAsync(ServerUrl.Trim(), Email.Trim(), Password);
        Password = string.Empty;
        ApplyUnlockState(unlock);
        await RefreshInternalAsync();
    });

    [RelayCommand]
    private Task CreateVaultAsync() => RunAsync(async () =>
    {
        NewRecoveryKey = await sync.BootstrapVaultAsync();
        ApplyUnlockState(CloudUnlockState.Ready);
        await RefreshInternalAsync();
    });

    [RelayCommand]
    private Task RestoreWithRecoveryKeyAsync() => RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(RecoveryKeyInput))
        {
            ErrorMessage = "请输入 Recovery Key。";
            return;
        }

        await sync.RecoverVaultAsync(RecoveryKeyInput.Trim());
        RecoveryKeyInput = string.Empty;
        ApplyUnlockState(CloudUnlockState.Ready);
        await RefreshInternalAsync();
    });

    [RelayCommand]
    private void AcknowledgeRecoveryKey() => NewRecoveryKey = null;

    [RelayCommand(CanExecute = nameof(IsReady))]
    private Task SyncNowAsync() => RunAsync(async () =>
    {
        var result = await sync.SyncNowAsync();
        logger.LogInformation("手动同步：{Status}", result.Status);
        await RefreshInternalAsync();
        if (result.Status == SyncStatus.AuthRequired)
        {
            State = CloudSyncUiState.SignedOut;
        }
    });

    [RelayCommand(CanExecute = nameof(IsReady))]
    private Task RefreshAsync() => RunAsync(RefreshInternalAsync);

    [RelayCommand]
    private Task DisconnectAsync() => RunAsync(async () =>
    {
        if (!await dialogs.ConfirmAsync("退出云账号", "将撤销本机登录并停止同步，本地连接与凭据保留。"))
        {
            return;
        }

        await sync.SignOutAsync(wipeLocalCloudData: false);
        ResetToSignedOut();
    });

    [RelayCommand]
    private Task DisconnectAndWipeAsync() => RunAsync(async () =>
    {
        if (!await dialogs.ConfirmAsync(
                "清除此设备云数据",
                "将退出登录并清除本机的同步状态与 Vault 密钥缓存。本地连接与凭据不受影响，"
                + "但下次需要重新登录并批准 / 恢复。",
                confirmText: "清除并退出", isDanger: true))
        {
            return;
        }

        await sync.SignOutAsync(wipeLocalCloudData: true);
        ResetToSignedOut();
    });

    [RelayCommand(CanExecute = nameof(IsReady))]
    private Task ApproveDeviceAsync(CloudDeviceRow? device) => RunAsync(async () =>
    {
        if (device is null)
        {
            return;
        }

        await sync.ApproveDeviceAsync(device.DeviceId);
        await RefreshInternalAsync();
        await dialogs.ShowMessageAsync("已批准", $"设备「{device.Name}」现在可以同步了。", DialogKind.Success);
    });

    [RelayCommand(CanExecute = nameof(IsReady))]
    private Task KeepLocalAsync(CloudConflictRow? conflict) => ResolveAsync(conflict, ConflictResolution.KeepLocal);

    [RelayCommand(CanExecute = nameof(IsReady))]
    private Task UseRemoteAsync(CloudConflictRow? conflict) => ResolveAsync(conflict, ConflictResolution.UseRemote);

    private Task ResolveAsync(CloudConflictRow? conflict, ConflictResolution resolution) => RunAsync(async () =>
    {
        if (conflict is null)
        {
            return;
        }

        await sync.ResolveConflictAsync(conflict.Id, resolution);
        await RefreshInternalAsync();
    });

    // ── 内部 ────────────────────────────────────────────────────

    private bool IsReady => State == CloudSyncUiState.Ready && !IsBusy;

    private async Task RefreshInternalAsync()
    {
        if (!sync.IsVaultUnlocked)
        {
            return;
        }

        var snapshot = await sync.GetStateAsync();
        LastSyncedAt = snapshot.LastSuccessfulSyncAt;
        PendingChangeCount = await sync.GetPendingOutboxCountAsync();
        StatusLine = DescribeStatus(snapshot.Status);

        PendingDevices.Clear();
        foreach (var device in await sync.GetPendingDevicesAsync())
        {
            PendingDevices.Add(new CloudDeviceRow(
                device.DeviceId, device.DisplayName, device.Platform, device.LastSeenAt));
        }

        Conflicts.Clear();
        foreach (var conflict in await sync.GetConflictsAsync())
        {
            Conflicts.Add(new CloudConflictRow(
                conflict.Id, conflict.EntityType, conflict.EntityId, conflict.DetectedAt));
        }

        ConflictCount = Conflicts.Count;
    }

    private string DescribeStatus(SyncStatus status) => status switch
    {
        SyncStatus.Synced => LastSyncedAt is { } t ? $"已同步 · {t.LocalDateTime:g}" : "已同步",
        SyncStatus.Syncing => "正在同步…",
        SyncStatus.Conflicted => $"存在 {Conflicts.Count} 个冲突待处理",
        SyncStatus.Offline => "离线，稍后自动重试",
        SyncStatus.AuthRequired => "登录已过期，请重新登录",
        SyncStatus.Error => "同步出错，详见日志",
        _ => "空闲",
    };

    private void ApplyUnlockState(CloudUnlockState unlock)
    {
        ErrorMessage = null;
        State = unlock switch
        {
            CloudUnlockState.Ready => CloudSyncUiState.Ready,
            CloudUnlockState.NeedsBootstrap => CloudSyncUiState.NeedsVaultSetup,
            _ => CloudSyncUiState.NeedsApproval,
        };
        RaiseCommandStates();
    }

    private void ResetToSignedOut()
    {
        State = CloudSyncUiState.SignedOut;
        StatusLine = "未登录";
        PendingChangeCount = 0;
        ConflictCount = 0;
        LastSyncedAt = null;
        NewRecoveryKey = null;
        PendingDevices.Clear();
        Conflicts.Clear();
        RaiseCommandStates();
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        RaiseCommandStates();
        try
        {
            await action();
        }
        catch (CloudAuthRequiredException)
        {
            ResetToSignedOut();
            ErrorMessage = "登录已失效，请重新登录 AppsCloud。";
        }
        catch (OperationCanceledException)
        {
            // 应用退出 / 用户离开面板，静默。
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cloud Sync 操作失败");
            ErrorMessage = Describe(ex);
        }
        finally
        {
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    /// <summary>把异常翻成一句面向用户的中文；技术细节只进日志。</summary>
    private static string Describe(Exception ex) => ex switch
    {
        CloudSignInException => ex.Message,
        CloudApiException { StatusCode: 401 } => "邮箱或密码不正确。",
        CloudApiException { StatusCode: 403 } => "本设备暂无访问权，请在已授权设备上批准，或用 Recovery Key 恢复。",
        CloudApiException { StatusCode: 409 } => "该邮箱已注册，请用原密码登录。",
        CloudApiException { StatusCode: >= 500 } => "AppsCloud 服务暂时不可用，请稍后再试。",
        CloudApiException api => api.Message,
        HttpRequestException => "无法连接 AppsCloud 服务，请检查网络和服务地址。",
        TaskCanceledException => "连接 AppsCloud 超时，请检查网络和服务地址。",
        _ => "操作失败：" + ex.Message,
    };

    private void RaiseCommandStates()
    {
        SyncNowCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        ApproveDeviceCommand.NotifyCanExecuteChanged();
        KeepLocalCommand.NotifyCanExecuteChanged();
        UseRemoteCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value) => RaiseCommandStates();
}
