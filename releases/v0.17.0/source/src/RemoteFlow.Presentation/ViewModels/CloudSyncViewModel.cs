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
    NeedsPassword,
    NeedsApproval,
    Ready,
}

/// <summary>登录卡片的模式：登录已有账号 / 注册新账号。</summary>
public enum CloudAuthMode
{
    SignIn,
    Register,
}

/// <summary>待批准设备列表的一行。</summary>
public sealed record CloudDeviceRow(Guid DeviceId, string Name, string Platform, DateTimeOffset LastSeenAt);

/// <summary>冲突列表的一行。<see cref="Label"/> 是给用户看的实体名称（连接名 / 凭据名…）。</summary>
public sealed record CloudConflictRow(Guid Id, string EntityType, string EntityId, string Label, string Kind, DateTimeOffset DetectedAt);

/// <summary>已同步条目统计的一行。</summary>
public sealed record CloudSyncedCountRow(string Label, int Count);

/// <summary>
/// 「设置 → 云同步」面板。口令派生模型：新设备默认只需账号 + 主口令；
/// 账号可另开「新设备需批准」加一道设备信任因素。View 只做绑定与呈现。
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
    [NotifyPropertyChangedFor(nameof(IsRegisterMode))]
    [NotifyPropertyChangedFor(nameof(SubmitLabel))]
    private CloudAuthMode _authMode = CloudAuthMode.SignIn;

    public bool IsRegisterMode => AuthMode == CloudAuthMode.Register;

    public string SubmitLabel => IsRegisterMode ? "创建账号并启用同步" : "登录并启用同步";

    [ObservableProperty]
    private string _serverUrl = settings.CloudBaseUrl;

    /// <summary>
    /// 服务地址是否可编辑。默认锁定（已内置正式地址）；用户在字段上三击可解锁，界面不作提示。
    /// 没有预配置地址（开发 / 未打包）时默认就可编辑。
    /// </summary>
    [ObservableProperty]
    private bool _serverUrlEditable = string.IsNullOrWhiteSpace(settings.CloudBaseUrl);

    public bool HasConfiguredServerUrl { get; } = !string.IsNullOrWhiteSpace(settings.CloudBaseUrl);

    [ObservableProperty]
    private string _email = settings.CloudEmail;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _passwordConfirm = string.Empty;

    /// <summary>注册时可勾选「本账号新设备需批准」。</summary>
    [ObservableProperty]
    private bool _registerRequireApproval;

    [ObservableProperty]
    private string _recoveryKeyInput = string.Empty;

    [ObservableProperty]
    private string _unlockPassword = string.Empty;

    /// <summary>bootstrap / 重置后一次性展示的 Recovery Key（分组字符串）。用户确认已保存后清空。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewRecoveryKey))]
    private string? _newRecoveryKey;

    public bool HasNewRecoveryKey => !string.IsNullOrEmpty(NewRecoveryKey);

    /// <summary>用户已复制或另存 Recovery Key —— 解锁「我已妥善保存」按钮。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcknowledgeRecoveryKeyCommand))]
    private bool _recoveryKeySecured;

    [ObservableProperty]
    private string _statusLine = "未登录";

    [ObservableProperty]
    private DateTimeOffset? _lastSyncedAt;

    [ObservableProperty]
    private int _pendingChangeCount;

    [ObservableProperty]
    private int _conflictCount;

    /// <summary>上次手动同步的条数摘要，如「上传 3 · 下载 5」。</summary>
    [ObservableProperty]
    private string? _lastSyncSummary;

    /// <summary>本账号「新设备需批准」当前是否开启。</summary>
    [ObservableProperty]
    private bool _requireApproval;

    /// <summary>
    /// 危险操作的「解锁短语」：用户必须在危险区里手动输入「清除」才启用清除类按钮，
    /// 避免误点（比只弹一次确认框更难误触）。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DangerArmed))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectAndWipeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreFromCloudCommand))]
    private string _dangerPhrase = string.Empty;

    public bool DangerArmed => DangerPhrase.Trim() == "清除";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _infoMessage;

    public ObservableCollection<CloudDeviceRow> PendingDevices { get; } = [];

    public ObservableCollection<CloudConflictRow> Conflicts { get; } = [];

    /// <summary>已同步到云端的条目统计（连接 N · 凭据 N · …）。</summary>
    public ObservableCollection<CloudSyncedCountRow> SyncedCounts { get; } = [];

    /// <summary>已同步条目的一句话汇总，空表示还没有同步过。</summary>
    [ObservableProperty]
    private string _syncedSummary = string.Empty;

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

    /// <summary>View 在服务地址字段上三击时调用——解锁编辑，界面不作提示。</summary>
    public void UnlockServerUrlField() => ServerUrlEditable = true;

    [RelayCommand]
    private void ToggleAuthMode()
    {
        AuthMode = IsRegisterMode ? CloudAuthMode.SignIn : CloudAuthMode.Register;
        ErrorMessage = null;
        InfoMessage = null;
    }

    [RelayCommand]
    private Task ConnectAsync() => RunAsync(async () =>
    {
        var url = ServerUrl.Trim();
        var email = Email.Trim();
        if (url.Length == 0 || email.Length == 0 || Password.Length == 0)
        {
            ErrorMessage = "请填写服务地址、邮箱和主口令。";
            return;
        }

        if (!email.Contains('@', StringComparison.Ordinal))
        {
            ErrorMessage = "邮箱格式不正确。";
            return;
        }

        if (IsRegisterMode)
        {
            if (Password.Length < 12)
            {
                ErrorMessage = "主口令至少 12 位。它是解密数据的唯一凭据，请用强口令并牢记。";
                return;
            }

            if (Password != PasswordConfirm)
            {
                ErrorMessage = "两次输入的口令不一致。";
                return;
            }
        }

        var unlock = IsRegisterMode
            ? await sync.RegisterAsync(url, email, Password)
            : await sync.SignInAsync(url, email, Password);

        Password = string.Empty;
        PasswordConfirm = string.Empty;
        ApplyUnlockState(unlock);
        await RefreshInternalAsync();
    });

    [RelayCommand]
    private Task UnlockAsync() => RunAsync(async () =>
    {
        if (UnlockPassword.Length == 0)
        {
            ErrorMessage = "请输入主口令。";
            return;
        }

        var unlock = await sync.UnlockWithPasswordAsync(UnlockPassword);
        UnlockPassword = string.Empty;
        ApplyUnlockState(unlock);
        await RefreshInternalAsync();
    });

    [RelayCommand]
    private Task RetryUnlockAsync() => RunAsync(async () =>
    {
        var unlock = await sync.RetryUnlockAsync();
        ApplyUnlockState(unlock);
        if (State == CloudSyncUiState.NeedsApproval)
        {
            InfoMessage = "本设备还没有被批准。请在一台已登录的设备上打开「云同步」批准本机。";
        }
        else
        {
            await RefreshInternalAsync();
        }
    });

    [RelayCommand]
    private Task CreateVaultAsync() => RunAsync(async () =>
    {
        ShowNewRecoveryKey(await sync.BootstrapVaultAsync(RegisterRequireApproval));
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
        InfoMessage = "已用 Recovery Key 恢复。建议在「更改主口令」里设置一个记得住的新口令。";
        await RefreshInternalAsync();
    });

    [RelayCommand]
    private Task CopyRecoveryKeyAsync() => RunAsync(async () =>
    {
        if (NewRecoveryKey is { } key)
        {
            await dialogs.CopyToClipboardAsync(key);
            RecoveryKeySecured = true;
            InfoMessage = "Recovery Key 已复制到剪贴板，请立即粘贴到安全的地方。";
        }
    });

    [RelayCommand]
    private void SaveRecoveryKey()
    {
        if (NewRecoveryKey is not { } key)
        {
            return;
        }

        var path = dialogs.PickFileToSave("保存 Recovery Key", "文本文件|*.txt", "RemoteFlow-RecoveryKey.txt");
        if (path is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(
                path,
                "RemoteFlow 云同步 Recovery Key —— 忘记主口令时用它恢复访问权。\r\n"
                + "请离线妥善保管，任何持有此 Key 的人都能解密你的同步数据。\r\n\r\n"
                + key + "\r\n");
            RecoveryKeySecured = true;
            InfoMessage = "Recovery Key 已保存到文件。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = "保存失败：" + ex.Message;
        }
    }

    private bool CanAcknowledgeRecoveryKey() => RecoveryKeySecured;

    [RelayCommand(CanExecute = nameof(CanAcknowledgeRecoveryKey))]
    private void AcknowledgeRecoveryKey()
    {
        NewRecoveryKey = null;
        RecoveryKeySecured = false;
        InfoMessage = null;
    }

    [RelayCommand(CanExecute = nameof(IsReady))]
    private Task SyncNowAsync() => RunAsync(async () =>
    {
        var result = await sync.SyncNowAsync();
        var conflicts = result.PushConflicts + result.PullConflicts;
        LastSyncSummary = $"上传 {result.Pushed} · 下载 {result.Pulled}"
            + (conflicts > 0 ? $" · 冲突 {conflicts}" : string.Empty);
        logger.LogInformation("手动同步：{Status}", result.Status);
        await RefreshInternalAsync();
        if (result.Status == SyncStatus.AuthRequired)
        {
            ResetToSignedOut();
            ErrorMessage = "登录已过期，请重新登录。";
        }
    });

    [RelayCommand(CanExecute = nameof(IsReady))]
    private Task RefreshAsync() => RunAsync(RefreshInternalAsync);

    [RelayCommand(CanExecute = nameof(IsReady))]
    private Task ChangePasswordAsync() => RunAsync(async () =>
    {
        var current = await dialogs.PromptPasswordAsync("更改主口令", "输入当前主口令", confirm: false);
        if (string.IsNullOrEmpty(current))
        {
            return;
        }

        var next = await dialogs.PromptPasswordAsync("更改主口令", "设置新主口令（至少 12 位）", confirm: true);
        if (string.IsNullOrEmpty(next))
        {
            return;
        }

        if (next.Length < 12)
        {
            ErrorMessage = "新主口令至少 12 位。";
            return;
        }

        await sync.ChangePasswordAsync(current, next);
        InfoMessage = "主口令已更改。其它设备需要用新口令重新登录。";
    });

    [RelayCommand(CanExecute = nameof(IsReady))]
    private Task ResetRecoveryKeyAsync() => RunAsync(async () =>
    {
        if (!await dialogs.ConfirmAsync(
                "重置 Recovery Key",
                "将生成一把新的 Recovery Key，旧的立即失效。新 Key 只显示一次，请务必保存。", "生成新的"))
        {
            return;
        }

        ShowNewRecoveryKey(await sync.ResetRecoveryKeyAsync());
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

    partial void OnRequireApprovalChanged(bool value)
    {
        if (_suppressRequireApprovalCallback || State != CloudSyncUiState.Ready)
        {
            return;
        }

        _ = RunAsync(async () =>
        {
            await sync.SetRequireApprovalAsync(value);
            InfoMessage = value
                ? "已开启：新设备登录后需在已有设备上批准才能同步。"
                : "已关闭：新设备只需主口令即可同步。";
            await RefreshInternalAsync();
        });
    }

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

    [RelayCommand(CanExecute = nameof(CanRunDanger))]
    private Task DisconnectAndWipeAsync() => RunAsync(async () =>
    {
        if (!await dialogs.ConfirmAsync(
                "清除此设备云数据",
                "将退出登录并清除本机的同步状态与 Vault 密钥缓存。本地连接与凭据不受影响，"
                + "但下次需要重新登录。",
                confirmText: "清除并退出", isDanger: true))
        {
            return;
        }

        await sync.SignOutAsync(wipeLocalCloudData: true);
        ResetToSignedOut();
    });

    /// <summary>兜底：以云端为准 —— 清空本机数据后从云端完整恢复。</summary>
    [RelayCommand(CanExecute = nameof(CanRunDanger))]
    private Task RestoreFromCloudAsync() => RunAsync(async () =>
    {
        if (!await dialogs.ConfirmAsync(
                "清除本地数据并从云端恢复",
                "将删除本机的全部连接、凭据、分组、标签与密码，然后从云端重新拉取一份。\n\n"
                + "仅当另一台设备上的数据才是完整源、且本机没有未上云的改动时使用——"
                + "本机的本地改动会丢失，不可恢复。",
                confirmText: "清除并恢复", isDanger: true))
        {
            return;
        }

        await sync.RestoreFromCloudAsync();
        InfoMessage = "已清除本地数据并从云端恢复。";
        await RefreshInternalAsync();
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

    private bool _suppressRequireApprovalCallback;

    private bool IsReady => State == CloudSyncUiState.Ready && !IsBusy;

    /// <summary>清除类操作：需已就绪且用户手动输入了「清除」。</summary>
    private bool CanRunDanger => IsReady && DangerArmed;

    private void ShowNewRecoveryKey(string key)
    {
        NewRecoveryKey = key;
        RecoveryKeySecured = false;
        InfoMessage = null;
    }

    private async Task RefreshInternalAsync()
    {
        if (!sync.IsVaultUnlocked)
        {
            return;
        }

        var snapshot = await sync.GetStateAsync();
        LastSyncedAt = snapshot.LastSuccessfulSyncAt;
        PendingChangeCount = await sync.GetPendingOutboxCountAsync();

        _suppressRequireApprovalCallback = true;
        RequireApproval = await sync.GetRequireApprovalAsync();
        _suppressRequireApprovalCallback = false;

        PendingDevices.Clear();
        if (RequireApproval)
        {
            foreach (var device in await sync.GetPendingDevicesAsync())
            {
                PendingDevices.Add(new CloudDeviceRow(
                    device.DeviceId, device.DisplayName, device.Platform, device.LastSeenAt));
            }
        }

        Conflicts.Clear();
        foreach (var conflict in await sync.GetConflictsAsync())
        {
            Conflicts.Add(new CloudConflictRow(
                conflict.Id, conflict.EntityType, conflict.EntityId,
                conflict.Label, conflict.Kind, conflict.DetectedAt));
        }

        ConflictCount = Conflicts.Count;

        SyncedCounts.Clear();
        foreach (var count in await sync.GetSyncedCountsAsync())
        {
            SyncedCounts.Add(new CloudSyncedCountRow(count.Label, count.Count));
        }

        SyncedSummary = SyncedCounts.Count == 0
            ? "尚未同步任何条目"
            : string.Join(" · ", SyncedCounts.Select(c => $"{c.Label} {c.Count}"));
        StatusLine = DescribeStatus(snapshot.Status);
    }

    private string DescribeStatus(SyncStatus status) => status switch
    {
        SyncStatus.Synced => LastSyncedAt is { } t ? $"已同步 · {t.LocalDateTime:g}" : "已同步",
        SyncStatus.Syncing => "正在同步…",
        SyncStatus.Conflicted => $"存在 {Conflicts.Count} 个冲突待处理",
        SyncStatus.Offline => "离线，稍后自动重试",
        SyncStatus.AuthRequired => "登录已过期，请重新登录",
        SyncStatus.Error => "上次同步出错，将自动重试",
        _ => PendingChangeCount > 0 ? $"{PendingChangeCount} 项待上传" : "空闲",
    };

    private void ApplyUnlockState(CloudUnlockState unlock)
    {
        ErrorMessage = null;
        State = unlock switch
        {
            CloudUnlockState.Ready => CloudSyncUiState.Ready,
            CloudUnlockState.NeedsBootstrap => CloudSyncUiState.NeedsVaultSetup,
            CloudUnlockState.NeedsApproval => CloudSyncUiState.NeedsApproval,
            _ => CloudSyncUiState.NeedsPassword,
        };
        RaiseCommandStates();
    }

    private void ResetToSignedOut()
    {
        State = CloudSyncUiState.SignedOut;
        AuthMode = CloudAuthMode.SignIn;
        StatusLine = "未登录";
        PendingChangeCount = 0;
        ConflictCount = 0;
        LastSyncedAt = null;
        LastSyncSummary = null;
        NewRecoveryKey = null;
        RecoveryKeySecured = false;
        InfoMessage = null;
        PendingDevices.Clear();
        Conflicts.Clear();
        SyncedCounts.Clear();
        SyncedSummary = string.Empty;
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
        System.Security.Cryptography.CryptographicException => "主口令或 Recovery Key 不正确。",
        CloudApiException { StatusCode: 401 } => "主口令不正确。",
        CloudApiException { StatusCode: 403 } => "本设备暂无访问权，请在已登录的设备上批准，或用 Recovery Key 恢复。",
        CloudApiException { StatusCode: 409 } => "该邮箱已注册，请改用「登录」。",
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
        ChangePasswordCommand.NotifyCanExecuteChanged();
        ResetRecoveryKeyCommand.NotifyCanExecuteChanged();
        RestoreFromCloudCommand.NotifyCanExecuteChanged();
        ApproveDeviceCommand.NotifyCanExecuteChanged();
        KeepLocalCommand.NotifyCanExecuteChanged();
        UseRemoteCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value) => RaiseCommandStates();
}
