using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using RemoteFlow.App.Services;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure;
using RemoteFlow.Infrastructure.Data;
using RemoteFlow.Infrastructure.Settings;

namespace RemoteFlow.App.ViewModels;

/// <summary>「设置」页面的标签页。</summary>
public enum SettingsTab
{
    General,
    Rdp,
    Ssh,
    Vnc,
    Security,
    Data
}

/// <summary>
/// 「设置」页面。应用级默认配置 + 数据与备份的统一入口。
/// <para>
/// 「数据与备份」标签页吸收了原「导入 / 导出」页：连接列表 CSV、凭据加密备份、
/// 应用数据完整备份都在这里。安全约束不变——CSV 不含 Secret；<c>.rfbackup</c> 的
/// 明文只在内存中过一遍；完整备份把数据库 / 保险库 / 设置一并复制到用户选定目录。
/// </para>
/// </summary>
public sealed partial class SettingsPageViewModel : ObservableObject
{
    public sealed record DateFormatOption(AppDateFormat Value, string Label);

    public sealed record TimeFormatOption(AppTimeFormat Value, string Label);

    private const string StartupRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "RemoteFlow";

    private const string CsvFilter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*";
    private const string BackupFilter = "RemoteFlow 加密备份 (*.rfbackup)|*.rfbackup|所有文件 (*.*)|*.*";

    private readonly AppSettings _settings;
    private readonly JsonSettingsStore _store;
    private readonly ThemeService _themeService;
    private readonly IHistoryRepository _history;
    private readonly IHostKeyRepository _hostKeys;
    private readonly AppPaths _paths;
    private readonly IDialogService _dialogs;
    private readonly ImportExportService _importExport;
    private readonly CredentialBackupService _credentialBackup;
    private readonly LocalBackupService _localBackup;
    private readonly GroupService _groupService;
    private readonly ILogger<SettingsPageViewModel> _logger;

    /// <summary>加载期间抑制自动保存，避免初始化赋值触发一连串写盘。</summary>
    private bool _isLoading = true;

    public SettingsPageViewModel(
        AppSettings settings,
        JsonSettingsStore store,
        ThemeService theme,
        IHistoryRepository history,
        IHostKeyRepository hostKeys,
        AppPaths paths,
        IDialogService dialogs,
        ImportExportService importExport,
        CredentialBackupService credentialBackup,
        LocalBackupService localBackup,
        GroupService groupService,
        ILogger<SettingsPageViewModel> logger)
    {
        _settings = settings;
        _store = store;
        _themeService = theme;
        _history = history;
        _hostKeys = hostKeys;
        _paths = paths;
        _dialogs = dialogs;
        _importExport = importExport;
        _credentialBackup = credentialBackup;
        _localBackup = localBackup;
        _groupService = groupService;
        _logger = logger;

        LoadFromSettings();
        _isLoading = false;
    }

    /// <summary>导入 / 导出 / 备份改动了本地数据，外部需要据此刷新连接与凭据列表。</summary>
    public event EventHandler? DataChanged;

    // ── 标签页 ────────────────────────────────────────────────────

    [ObservableProperty]
    private SettingsTab _activeTab = SettingsTab.General;

    // ── 常规 ──────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _launchOnStartup;

    [ObservableProperty]
    private bool _minimizeToTrayOnClose;

    [ObservableProperty]
    private AppTheme _selectedTheme;

    public IReadOnlyList<AppTheme> ThemeOptions { get; } = [AppTheme.System, AppTheme.Light, AppTheme.Dark];

    [ObservableProperty]
    private LandingPage _defaultLandingPage;

    public IReadOnlyList<LandingPage> LandingPageOptions { get; } =
        [LandingPage.Home, LandingPage.Connections, LandingPage.Favorites, LandingPage.Recent, LandingPage.Credentials];

    /// <summary>界面语言。V0.1 仅简体中文，保留列表以便后续扩展。</summary>
    public IReadOnlyList<string> LanguageOptions { get; } = ["zh-CN"];

    [ObservableProperty]
    private string _language = "zh-CN";

    // ── 常规 → 分组 ────────────────────────────────────────────────

    /// <summary>正在从库加载默认分组信息（抑制切换回调回写，避免回声）。</summary>
    private bool _loadingGroups;

    [ObservableProperty]
    private string _defaultGroupLabel = "";

    [ObservableProperty]
    private bool _defaultGroupProtected;

    [ObservableProperty]
    private bool _hasDefaultGroup;

    /// <summary>开关是否可操作：存在默认用户组才可。</summary>
    public bool ProtectionSwitchEnabled => HasDefaultGroup;

    /// <summary>分组卡片副文案（跟随状态）。</summary>
    public string DefaultGroupDescription =>
        !HasDefaultGroup
            ? "当前没有默认分组，新建连接默认进入「未分组」。"
            : (DefaultGroupProtected
                ? $"「{DefaultGroupLabel}」已受保护：不可重命名 / 删除 / 移动层级，但仍可增删连接、建子分组。"
                : $"「{DefaultGroupLabel}」未受保护：可重命名 / 删除 / 移动层级。");

    // ── 日期与时间 ────────────────────────────────────────────────

    public IReadOnlyList<DateFormatOption> DateFormatOptions { get; } =
    [
        new(AppDateFormat.System, "跟随系统"),
        new(AppDateFormat.Dash, "2026-09-05"),
        new(AppDateFormat.Slash, "2026/09/05"),
        new(AppDateFormat.Chinese, "2026年9月5日"),
    ];

    public IReadOnlyList<TimeFormatOption> TimeFormatOptions { get; } =
    [
        new(AppTimeFormat.Hour24, "24 小时"),
        new(AppTimeFormat.Hour12, "12 小时"),
    ];

    [ObservableProperty]
    private DateFormatOption _selectedDateFormat = null!;

    [ObservableProperty]
    private TimeFormatOption _selectedTimeFormat = null!;

    [ObservableProperty]
    private bool _showWeekday = true;

    [ObservableProperty]
    private bool _showHomeWeekNumber = true;

    // ── RDP ───────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _rdpFitToWindow;

    [ObservableProperty]
    private bool _rdpRedirectClipboard;

    [ObservableProperty]
    private bool _rdpRedirectAudio;

    [ObservableProperty]
    private bool _rdpUseMultimon;

    // ── SSH ───────────────────────────────────────────────────────

    [ObservableProperty]
    private string _sshFontFamily = string.Empty;

    [ObservableProperty]
    private int _sshFontSize;

    [ObservableProperty]
    private int _sshKeepAliveSeconds;

    [ObservableProperty]
    private string _sshTerminalType = string.Empty;

    public IReadOnlyList<string> TerminalTypeOptions { get; } = ["xterm-256color", "xterm", "vt100", "linux"];

    public IReadOnlyList<int> FontSizeOptions { get; } = [11, 12, 13, 14, 15, 16, 18, 20];

    // ── VNC ───────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _vncFitToWindow;

    [ObservableProperty]
    private bool _vncViewOnly;

    [ObservableProperty]
    private bool _vncSharedConnection;

    // ── 会话 ──────────────────────────────────────────────────────

    [ObservableProperty]
    private int _maxConcurrentSessions;

    // ── 数据与安全 ────────────────────────────────────────────────

    public string DataDirectory => _paths.DataDirectory;

    public string DatabasePath => _paths.DatabasePath;

    public string LogDirectory => _paths.LogDirectory;

    public ObservableCollection<HostKeyItemViewModel> TrustedHostKeys { get; } = [];

    /// <summary>最近一次导入的逐行提示（跳过的重复项等）。</summary>
    public ObservableCollection<string> ImportMessages { get; } = [];

    [ObservableProperty]
    private bool _hasMessages;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public string AppVersion { get; } =
        typeof(SettingsPageViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    private void LoadFromSettings()
    {
        LaunchOnStartup = _settings.LaunchOnStartup;
        MinimizeToTrayOnClose = _settings.CloseBehavior == WindowCloseBehavior.MinimizeToTray;
        SelectedTheme = _settings.Theme;
        DefaultLandingPage = _settings.DefaultLandingPage;
        Language = string.IsNullOrWhiteSpace(_settings.Language) ? "zh-CN" : _settings.Language;
        SelectedDateFormat = DateFormatOptions.First(o => o.Value == _settings.DateFormat);
        SelectedTimeFormat = TimeFormatOptions.First(o => o.Value == _settings.TimeFormat);
        ShowWeekday = _settings.ShowWeekday;
        ShowHomeWeekNumber = _settings.ShowHomeWeekNumber;

        RdpFitToWindow = _settings.RdpDefaultDisplayMode == RdpDisplayMode.FitToWindow;
        RdpRedirectClipboard = _settings.RdpDefaultRedirectClipboard;
        RdpRedirectAudio = _settings.RdpDefaultRedirectAudio;
        RdpUseMultimon = _settings.RdpDefaultUseMultimon;

        SshFontFamily = _settings.SshFontFamily;
        SshFontSize = _settings.SshFontSize;
        SshKeepAliveSeconds = _settings.SshDefaultKeepAliveSeconds;
        SshTerminalType = _settings.SshDefaultTerminalType;

        VncFitToWindow = _settings.VncDefaultScaleMode == VncScaleMode.FitToWindow;
        VncViewOnly = _settings.VncDefaultViewOnly;
        VncSharedConnection = _settings.VncDefaultSharedConnection;

        MaxConcurrentSessions = _settings.MaxConcurrentSessions;
    }

    public async Task LoadHostKeysAsync(CancellationToken ct = default)
    {
        TrustedHostKeys.Clear();
        foreach (var record in await _hostKeys.GetAllAsync(ct))
        {
            TrustedHostKeys.Add(new HostKeyItemViewModel(record));
        }
    }

    /// <summary>进入设置页时读取默认分组名与保护态（分组卡片）。</summary>
    public async Task LoadGroupsAsync(CancellationToken ct = default)
    {
        _loadingGroups = true;
        try
        {
            var def = await _groupService.GetDefaultGroupAsync(ct);
            HasDefaultGroup = def is not null;
            DefaultGroupLabel = def?.Name ?? "";
            DefaultGroupProtected = def is { IsProtected: true };
            OnPropertyChanged(nameof(DefaultGroupDescription));
            OnPropertyChanged(nameof(ProtectionSwitchEnabled));
        }
        finally
        {
            _loadingGroups = false;
        }
    }

    partial void OnDefaultGroupProtectedChanged(bool value)
    {
        if (_loadingGroups || _isLoading)
        {
            return;
        }

        _ = ApplyDefaultGroupProtectionAsync(value);
    }

    private async Task ApplyDefaultGroupProtectionAsync(bool value)
    {
        try
        {
            await _groupService.SetDefaultProtectionAsync(value);
            await LoadGroupsAsync();
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "切换默认分组保护失败");
            await LoadGroupsAsync(); // 回滚到真实状态
        }
    }

    // 任一设置变化后立即持久化，避免用户改完忘记保存。
    partial void OnLaunchOnStartupChanged(bool value)
    {
        ApplyStartupRegistration(value);
        Save();
    }

    partial void OnMinimizeToTrayOnCloseChanged(bool value) => Save();

    partial void OnSelectedThemeChanged(AppTheme value)
    {
        _themeService.Apply(value);
        Save();
    }

    partial void OnDefaultLandingPageChanged(LandingPage value) => Save();
    partial void OnLanguageChanged(string value) => Save();

    partial void OnSelectedDateFormatChanged(DateFormatOption value) => ApplyDateTimeSettings(value.Value);
    partial void OnSelectedTimeFormatChanged(TimeFormatOption value) => ApplyDateTimeSettings(value.Value);
    partial void OnShowWeekdayChanged(bool value) => ApplyDateTimeSettings();
    partial void OnShowHomeWeekNumberChanged(bool value) => ApplyDateTimeSettings();

    private void ApplyDateTimeSettings(AppDateFormat dateFormat)
    {
        _settings.DateFormat = dateFormat;
        ApplyDateTimeSettings();
    }

    private void ApplyDateTimeSettings(AppTimeFormat timeFormat)
    {
        _settings.TimeFormat = timeFormat;
        ApplyDateTimeSettings();
    }

    private void ApplyDateTimeSettings()
    {
        _settings.ShowWeekday = ShowWeekday;
        _settings.ShowHomeWeekNumber = ShowHomeWeekNumber;
        DateTimeDisplay.Configure(_settings);
        Save();
    }

    partial void OnRdpFitToWindowChanged(bool value) => Save();
    partial void OnRdpRedirectClipboardChanged(bool value) => Save();
    partial void OnRdpRedirectAudioChanged(bool value) => Save();
    partial void OnRdpUseMultimonChanged(bool value) => Save();
    partial void OnSshFontFamilyChanged(string value) => Save();
    partial void OnSshFontSizeChanged(int value) => Save();
    partial void OnSshKeepAliveSecondsChanged(int value) => Save();
    partial void OnSshTerminalTypeChanged(string value) => Save();
    partial void OnVncFitToWindowChanged(bool value) => Save();
    partial void OnVncViewOnlyChanged(bool value) => Save();
    partial void OnVncSharedConnectionChanged(bool value) => Save();
    partial void OnMaxConcurrentSessionsChanged(int value) => Save();

    private void Save()
    {
        if (_isLoading)
        {
            return;
        }

        _settings.LaunchOnStartup = LaunchOnStartup;
        _settings.CloseBehavior = MinimizeToTrayOnClose ? WindowCloseBehavior.MinimizeToTray : WindowCloseBehavior.Exit;
        _settings.Theme = SelectedTheme;
        _settings.DefaultLandingPage = DefaultLandingPage;
        _settings.Language = Language;

        _settings.RdpDefaultDisplayMode = RdpFitToWindow ? RdpDisplayMode.FitToWindow : RdpDisplayMode.FixedResolution;
        _settings.RdpDefaultRedirectClipboard = RdpRedirectClipboard;
        _settings.RdpDefaultRedirectAudio = RdpRedirectAudio;
        _settings.RdpDefaultUseMultimon = RdpUseMultimon;

        _settings.SshFontFamily = SshFontFamily;
        _settings.SshFontSize = SshFontSize;
        _settings.SshDefaultKeepAliveSeconds = SshKeepAliveSeconds;
        _settings.SshDefaultTerminalType = SshTerminalType;

        _settings.VncDefaultScaleMode = VncFitToWindow ? VncScaleMode.FitToWindow : VncScaleMode.Original;
        _settings.VncDefaultViewOnly = VncViewOnly;
        _settings.VncDefaultSharedConnection = VncSharedConnection;

        _settings.MaxConcurrentSessions = Math.Clamp(MaxConcurrentSessions, 1, 100);

        // 写盘失败不应打断用户操作，仅记录并提示。
        _ = SaveAsync();
    }

    private async Task SaveAsync()
    {
        try
        {
            await _store.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存设置失败");
            StatusMessage = "设置保存失败，详情请查看日志。";
        }
    }

    /// <summary>写入/移除开机自启注册项。失败时回滚开关状态并提示。</summary>
    private void ApplyStartupRegistration(bool enabled)
    {
        if (_isLoading)
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, writable: true);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                var executablePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(executablePath))
                {
                    return;
                }

                key.SetValue(StartupValueName, $"\"{executablePath}\"");
            }
            else
            {
                key.DeleteValue(StartupValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "配置开机启动失败");
            StatusMessage = "无法修改开机启动设置。";
        }
    }

    // ── 安全命令 ──────────────────────────────────────────────────

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "清理连接历史",
            "确定要清空全部连接历史吗？该操作无法撤销。\n\n此操作不会影响连接配置与凭据。",
            "清空",
            isDanger: true);

        if (!confirmed)
        {
            return;
        }

        await _history.ClearAsync();
        StatusMessage = "连接历史已清空。";
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task RemoveHostKeyAsync(HostKeyItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            "移除主机密钥",
            $"确定要移除 {item.Host} 的已信任密钥吗？\n\n下次连接该主机时会重新提示确认指纹。",
            "移除",
            isDanger: true);

        if (!confirmed)
        {
            return;
        }

        await _hostKeys.DeleteAsync(item.HostName, item.Port);
        await LoadHostKeysAsync();
        StatusMessage = $"已移除 {item.Host} 的主机密钥。";
    }

    // ── 数据目录 ──────────────────────────────────────────────────

    /// <summary>在资源管理器中打开数据目录，便于用户自行备份。</summary>
    [RelayCommand]
    private void OpenDataDirectory() => OpenInExplorer(_paths.DataDirectory);

    [RelayCommand]
    private void OpenLogDirectory() => OpenInExplorer(_paths.LogDirectory);

    private void OpenInExplorer(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开目录失败：{Path}", path);
            StatusMessage = "无法打开该目录。";
        }
    }

    // ── 连接列表 CSV ──────────────────────────────────────────────

    [RelayCommand]
    private async Task ExportConnectionsCsvAsync()
    {
        var path = _dialogs.PickFileToSave(
            "导出连接列表",
            CsvFilter,
            $"RemoteFlow-连接列表-{DateTime.Now:yyyy-MM-dd}.csv");

        if (path is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _importExport.ExportAsync(path);
            StatusMessage = $"已导出到 {path}";
            await _dialogs.ShowMessageAsync(
                "导出完成",
                $"连接列表已导出到：\n{path}\n\n出于安全考虑，导出文件不包含任何密码或私钥。",
                DialogKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导出连接列表失败");
            StatusMessage = "导出失败。";
            await _dialogs.ShowMessageAsync("导出失败", $"无法写入文件：{ex.Message}", DialogKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ImportConnectionsCsvAsync()
    {
        var path = _dialogs.PickFileToOpen("导入连接列表", CsvFilter);
        if (path is null)
        {
            return;
        }

        IsBusy = true;
        ImportMessages.Clear();
        try
        {
            var result = await _importExport.ImportAsync(path);

            foreach (var message in result.Errors)
            {
                ImportMessages.Add(message);
            }

            HasMessages = ImportMessages.Count > 0;
            StatusMessage = $"导入完成：成功 {result.Imported} 条，跳过 {result.Skipped} 条。";

            DataChanged?.Invoke(this, EventArgs.Empty);

            await _dialogs.ShowMessageAsync(
                "导入完成",
                $"成功导入 {result.Imported} 条连接，跳过 {result.Skipped} 条。\n\n" +
                "CSV 中的 CredentialName 只用于关联已存在的凭据；未匹配到的连接需要手工指定凭据后才能使用。",
                result.Skipped > 0 ? DialogKind.Warning : DialogKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导入连接列表失败");
            StatusMessage = "导入失败。";
            await _dialogs.ShowMessageAsync("导入失败", $"无法读取文件：{ex.Message}", DialogKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── 凭据加密备份（.rfbackup）─────────────────────────────────

    [RelayCommand]
    private async Task ExportCredentialsAsync()
    {
        var password = await _dialogs.PromptPasswordAsync(
            "设置备份口令",
            "凭据备份用你设的口令加密。口令弱等于没加密，口令丢了文件就打不开——请用一个只有你知道、且记得住的强口令。",
            confirm: true);

        if (password is null)
        {
            return;
        }

        var path = _dialogs.PickFileToSave(
            "导出凭据",
            BackupFilter,
            $"RemoteFlow-凭据-{DateTime.Now:yyyy-MM-dd}.rfbackup");

        if (path is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var count = await _credentialBackup.ExportAsync(path, password);
            StatusMessage = $"已导出 {count} 条凭据到 {path}";
            await _dialogs.ShowMessageAsync(
                "导出完成",
                $"已把 {count} 条凭据导出到：\n{path}\n\n" +
                "这个文件包含你的密码和私钥，只是用刚才的口令加密。请离线妥善保管，不要随普通文件同步或上传。",
                DialogKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导出凭据失败");
            StatusMessage = "导出失败。";
            await _dialogs.ShowMessageAsync("导出失败", $"无法写入文件：{ex.Message}", DialogKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ImportCredentialsAsync()
    {
        var path = _dialogs.PickFileToOpen("导入凭据", BackupFilter);
        if (path is null)
        {
            return;
        }

        var password = await _dialogs.PromptPasswordAsync(
            "输入备份口令",
            "输入导出这个文件时设置的口令。",
            confirm: false);

        if (password is null)
        {
            return;
        }

        IsBusy = true;
        ImportMessages.Clear();
        try
        {
            var report = await _credentialBackup.ImportAsync(path, password);

            if (report.Failure != CredentialBackupImportFailure.None)
            {
                var reason = report.Failure switch
                {
                    CredentialBackupImportFailure.WrongPasswordOrCorrupted => "口令错误，或文件已损坏 / 被篡改。没有导入任何数据。",
                    CredentialBackupImportFailure.UnsupportedVersion => "这个备份文件的版本比当前程序新，无法读取。请升级 RemoteFlow 后再试。",
                    _ => "这不是一个 RemoteFlow 凭据备份文件。",
                };
                StatusMessage = "导入失败。";
                await _dialogs.ShowMessageAsync("导入失败", reason, DialogKind.Error);
                return;
            }

            foreach (var name in report.SkippedNames)
            {
                ImportMessages.Add($"「{name}」已存在，已跳过。");
            }

            HasMessages = ImportMessages.Count > 0;
            StatusMessage = $"导入完成：新增 {report.Imported} 条，跳过 {report.SkippedNames.Count} 条。";

            DataChanged?.Invoke(this, EventArgs.Empty);

            await _dialogs.ShowMessageAsync(
                "导入完成",
                $"新增 {report.Imported} 条凭据，跳过 {report.SkippedNames.Count} 条同名的。",
                report.SkippedNames.Count > 0 ? DialogKind.Warning : DialogKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导入凭据失败");
            StatusMessage = "导入失败。";
            await _dialogs.ShowMessageAsync("导入失败", $"处理文件时出错：{ex.Message}", DialogKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── 本地数据完整备份 ──────────────────────────────────────────

    [RelayCommand]
    private async Task BackupDataAsync()
    {
        var folder = _dialogs.PickFolder("选择备份保存位置");
        if (folder is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _localBackup.BackupToAsync(folder);
            StatusMessage = $"已备份到 {result.BackupDirectory}";
            await _dialogs.ShowMessageAsync(
                "备份完成",
                $"已把连接数据库、凭据保险库与设置复制到：\n{result.BackupDirectory}\n\n" +
                "vault.dat（凭据密文）只能在当前 Windows 账户下解密，换账户或换机器需要用「导出 .rfbackup」迁移凭据。",
                DialogKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "备份本地数据失败");
            StatusMessage = "备份失败。";
            await _dialogs.ShowMessageAsync("备份失败", $"无法完成备份：{ex.Message}", DialogKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>已信任的 SSH 主机密钥条目。</summary>
public sealed class HostKeyItemViewModel(SshHostKeyRecord record)
{
    public string Host => record.HostKey;

    public string HostName => record.HostKey.Contains(':')
        ? record.HostKey[..record.HostKey.LastIndexOf(':')]
        : record.HostKey;

    public int Port => record.HostKey.Contains(':')
        && int.TryParse(record.HostKey[(record.HostKey.LastIndexOf(':') + 1)..], out var port)
        ? port
        : 22;

    public string Algorithm => record.KeyAlgorithm;

    public string Fingerprint => $"SHA256:{record.Fingerprint}";

    public string TrustedAtDisplay => record.TrustedAt.ToString("yyyy-MM-dd HH:mm");
}
