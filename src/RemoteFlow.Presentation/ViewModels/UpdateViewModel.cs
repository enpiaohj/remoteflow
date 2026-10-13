using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Settings;
using RemoteFlow.Infrastructure.Updates;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.Presentation.ViewModels;

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToRestart,
    Failed
}

/// <summary>
/// 应用内更新（关于页的「软件更新」区）。
/// <para>
/// 只在用户点「检查更新」或开启了「启动时自动检查」后才联网；默认不自动检查。
/// 安装 = 下载 → 校验大小与 SHA-256 → 原地替换可执行文件（路径不变，开机启动项继续有效）→ 用户确认后重启。
/// 任何一步失败都保留「打开下载页」这条退路，不让用户卡住。
/// </para>
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly IUpdateService _service;
    private readonly IUpdateInstaller _installer;
    private readonly IDialogService _dialogs;
    private readonly AppSettings _settings;
    private readonly JsonSettingsStore? _settingsStore;
    private readonly IUiDispatcher _ui;
    private readonly ILogger _logger;
    private readonly Version _currentVersion;
    private CancellationTokenSource? _downloadCts;

    public UpdateViewModel(
        IUpdateService service,
        IUpdateInstaller installer,
        IDialogService dialogs,
        AppSettings settings,
        JsonSettingsStore? settingsStore,
        IUiDispatcher uiDispatcher,
        ILogger<UpdateViewModel> logger,
        Version? currentVersion = null)
    {
        _service = service;
        _installer = installer;
        _dialogs = dialogs;
        _settings = settings;
        _settingsStore = settingsStore;
        _ui = uiDispatcher;
        _logger = logger;
        _currentVersion = Normalize(currentVersion ?? typeof(UpdateViewModel).Assembly.GetName().Version ?? new Version(0, 0, 0));
        _autoCheck = settings.AutoCheckUpdates;
        _statusText = "尚未检查。";
    }

    public string CurrentVersionText => "v" + _currentVersion.ToString(3);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    [NotifyPropertyChangedFor(nameof(IsDownloading))]
    [NotifyPropertyChangedFor(nameof(IsReadyToRestart))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(CanOpenReleasePage))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private UpdateState _state = UpdateState.Idle;

    [ObservableProperty]
    private string _statusText;

    /// <summary>检查到的最新版本（可能不比当前新）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestVersionText))]
    [NotifyPropertyChangedFor(nameof(Notes))]
    [NotifyPropertyChangedFor(nameof(CanOpenReleasePage))]
    private UpdateRelease? _latest;

    /// <summary>下载进度 0–100。</summary>
    [ObservableProperty]
    private double _progress;

    /// <summary>启动时自动检查更新。默认关闭；改动立即保存。</summary>
    [ObservableProperty]
    private bool _autoCheck;

    public string LatestVersionText => Latest is null ? string.Empty : "v" + Latest.Version.ToString(3);

    /// <summary>
    /// 新版本的更新说明。发布页正文是整份更新日志时，只截取该版本自己的条目（<c>## [x.y.z]</c> 到下一个 <c>## [</c>）。
    /// </summary>
    public string Notes => Latest is null ? string.Empty : ExtractNotes(Latest.Notes, Latest.Version);

    internal static string ExtractNotes(string body, Version version)
    {
        var heading = $"## [{version.ToString(3)}]";
        var start = body.IndexOf(heading, StringComparison.Ordinal);
        if (start < 0)
        {
            return body.Trim();
        }

        var end = body.IndexOf("\n## [", start + heading.Length, StringComparison.Ordinal);
        var section = end < 0 ? body[start..] : body[start..end];
        return section.TrimEnd('-', ' ', '\r', '\n').Trim();
    }

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading;

    public bool IsDownloading => State == UpdateState.Downloading;

    public bool IsReadyToRestart => State == UpdateState.ReadyToRestart;

    /// <summary>有比当前更新的版本（含已下载待重启）。状态栏提示与关于页都看它。</summary>
    public bool HasUpdate => Latest is not null && Latest.Version > _currentVersion
        && State is UpdateState.Available or UpdateState.Downloading or UpdateState.ReadyToRestart or UpdateState.Failed;

    /// <summary>能否应用内安装：有新版本、当前运行形态支持原地替换。</summary>
    public bool CanInstall => State is UpdateState.Available or UpdateState.Failed
        && Latest is not null && Latest.Version > _currentVersion && _installer.CanSelfUpdate;

    public bool CanOpenReleasePage => Latest is { PageUrl.Length: > 0 } && HasUpdate;

    partial void OnAutoCheckChanged(bool value)
    {
        _settings.AutoCheckUpdates = value;
        if (_settingsStore is not null)
        {
            _ = SaveSettingsAsync();
        }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await _settingsStore!.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存「启动时自动检查更新」设置失败");
        }
    }

    /// <summary>启动时的自动检查：只在开关打开时执行，失败不打扰用户。</summary>
    public async Task CheckOnStartupAsync()
    {
        if (!AutoCheck || IsBusy)
        {
            return;
        }

        await CheckCoreAsync(silent: true);
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync() => CheckCoreAsync(silent: false);

    private bool CanCheck() => !IsBusy && State != UpdateState.ReadyToRestart;

    private async Task CheckCoreAsync(bool silent)
    {
        State = UpdateState.Checking;
        StatusText = "正在检查更新…";

        try
        {
            var latest = await _service.GetLatestAsync(CancellationToken.None);
            Latest = latest;

            if (latest is null || latest.Version <= _currentVersion)
            {
                State = UpdateState.UpToDate;
                StatusText = $"已是最新版本（{CurrentVersionText}）。";
                return;
            }

            State = UpdateState.Available;
            StatusText = _installer.CanSelfUpdate
                ? $"发现新版本 {LatestVersionText}。"
                : $"发现新版本 {LatestVersionText}。当前运行方式不支持应用内更新，请到下载页手动更新。";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "检查更新失败");
            if (silent)
            {
                // 自动检查失败（没网、访问不了 GitHub）不值得打扰：回到未检查状态。
                State = UpdateState.Idle;
                StatusText = "尚未检查。";
                return;
            }

            State = UpdateState.Failed;
            StatusText = DescribeCheckFailure(ex);
        }
        finally
        {
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(CanOpenReleasePage));
            InstallCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (Latest is not { } release)
        {
            return;
        }

        State = UpdateState.Downloading;
        Progress = 0;
        StatusText = $"正在下载 {LatestVersionText}…";

        string package;
        var cts = new CancellationTokenSource();
        _downloadCts = cts;
        try
        {
            var progress = new Progress<double>(value => _ui.Post(() => Progress = value));
            package = await _service.DownloadAsync(release, progress, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            State = UpdateState.Available;
            StatusText = $"已取消下载。新版本 {LatestVersionText} 仍可随时下载。";
            return;
        }
        catch (UpdateVerificationException ex)
        {
            _logger.LogError(ex, "更新包校验失败");
            Fail(ex.Message + " 可以稍后重试，或到下载页手动更新。");
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "下载更新失败");
            Fail(DescribeDownloadFailure(ex));
            return;
        }
        finally
        {
            _downloadCts = null;
            cts.Dispose();
        }

        try
        {
            _installer.Apply(package);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "登记更新包失败");
            Fail("无法更新程序文件：程序所在目录没有写入权限。请到下载页手动更新，或把程序移到有写入权限的目录。");
            return;
        }

        Progress = 100;
        State = UpdateState.ReadyToRestart;
        StatusText = $"{LatestVersionText} 已下载并校验，退出或重启后生效。文件名会跟随新版本，开机启动项与快捷方式自动指向新文件。";
    }

    /// <summary>取消正在进行的下载（下载卡住或不想等时的出路）。</summary>
    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    private static string DescribeCheckFailure(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests }
            => "检查更新失败：GitHub 暂时限制了请求频率，请过一会儿再试，或直接打开下载页。",
        HttpRequestException { StatusCode: not null } http
            => $"检查更新失败：GitHub 返回了错误（{(int)http.StatusCode!}），请稍后重试。",
        OperationCanceledException or TimeoutException
            => "检查更新失败：连接 GitHub 超时，请检查网络或代理后重试。",
        System.Text.Json.JsonException
            => "检查更新失败：GitHub 返回的内容无法识别，请稍后重试。",
        _ => "检查更新失败：无法访问 GitHub，请检查网络或代理后重试。"
    };

    private static string DescribeDownloadFailure(Exception ex) => ex switch
    {
        TimeoutException => "下载失败：连接长时间没有数据。可以重试，或到下载页手动更新。",
        IOException => "下载失败：无法写入临时目录（磁盘空间不足或没有权限）。请清理磁盘后重试，或到下载页手动更新。",
        UnauthorizedAccessException => "下载失败：没有权限写入临时目录。请到下载页手动更新。",
        _ => "下载失败：网络中断或无法访问 GitHub。可以重试，或到下载页手动更新。"
    };

    private void Fail(string message)
    {
        State = UpdateState.Failed;
        StatusText = message;
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        if (State != UpdateState.ReadyToRestart)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            "重启以完成更新",
            "重启会断开当前打开的会话与进行中的文件传输。\n\n现在重启吗？",
            "立即重启");
        if (confirmed)
        {
            _installer.Restart();
        }
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        if (Latest is { PageUrl.Length: > 0 } release)
        {
            _installer.OpenUrl(release.PageUrl);
        }
    }

    private static Version Normalize(Version version)
        => new(version.Major, version.Minor, Math.Max(version.Build, 0));
}
