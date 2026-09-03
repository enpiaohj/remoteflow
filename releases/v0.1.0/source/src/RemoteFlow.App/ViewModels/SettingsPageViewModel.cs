using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using RemoteFlow.App.Services;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure;
using RemoteFlow.Infrastructure.Settings;

namespace RemoteFlow.App.ViewModels;

/// <summary>
/// 「设置」页面。只负责应用级默认配置，不承担高频连接操作。
/// </summary>
public sealed partial class SettingsPageViewModel : ObservableObject
{
    private const string StartupRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "RemoteFlow";

    private readonly AppSettings _settings;
    private readonly JsonSettingsStore _store;
    private readonly ThemeService _themeService;
    private readonly IHistoryRepository _history;
    private readonly IHostKeyRepository _hostKeys;
    private readonly AppPaths _paths;
    private readonly IDialogService _dialogs;
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
        ILogger<SettingsPageViewModel> logger)
    {
        _settings = settings;
        _store = store;
        _themeService = theme;
        _history = history;
        _hostKeys = hostKeys;
        _paths = paths;
        _dialogs = dialogs;
        _logger = logger;

        LoadFromSettings();
        _isLoading = false;
    }

    // ── 常规 ──────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _launchOnStartup;

    [ObservableProperty]
    private bool _minimizeToTrayOnClose;

    [ObservableProperty]
    private AppTheme _selectedTheme;

    public IReadOnlyList<AppTheme> ThemeOptions { get; } = [AppTheme.System, AppTheme.Light, AppTheme.Dark];

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

    // ── 会话 ──────────────────────────────────────────────────────

    [ObservableProperty]
    private int _maxConcurrentSessions;

    // ── 数据与安全 ────────────────────────────────────────────────

    public string DataDirectory => _paths.DataDirectory;

    public string DatabasePath => _paths.DatabasePath;

    public string LogDirectory => _paths.LogDirectory;

    public ObservableCollection<HostKeyItemViewModel> TrustedHostKeys { get; } = [];

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public string AppVersion { get; } =
        typeof(SettingsPageViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    private void LoadFromSettings()
    {
        LaunchOnStartup = _settings.LaunchOnStartup;
        MinimizeToTrayOnClose = _settings.CloseBehavior == WindowCloseBehavior.MinimizeToTray;
        SelectedTheme = _settings.Theme;

        RdpFitToWindow = _settings.RdpDefaultDisplayMode == RdpDisplayMode.FitToWindow;
        RdpRedirectClipboard = _settings.RdpDefaultRedirectClipboard;
        RdpRedirectAudio = _settings.RdpDefaultRedirectAudio;
        RdpUseMultimon = _settings.RdpDefaultUseMultimon;

        SshFontFamily = _settings.SshFontFamily;
        SshFontSize = _settings.SshFontSize;
        SshKeepAliveSeconds = _settings.SshDefaultKeepAliveSeconds;
        SshTerminalType = _settings.SshDefaultTerminalType;

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

    partial void OnRdpFitToWindowChanged(bool value) => Save();
    partial void OnRdpRedirectClipboardChanged(bool value) => Save();
    partial void OnRdpRedirectAudioChanged(bool value) => Save();
    partial void OnRdpUseMultimonChanged(bool value) => Save();
    partial void OnSshFontFamilyChanged(string value) => Save();
    partial void OnSshFontSizeChanged(int value) => Save();
    partial void OnSshKeepAliveSecondsChanged(int value) => Save();
    partial void OnSshTerminalTypeChanged(string value) => Save();
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

        _settings.RdpDefaultDisplayMode = RdpFitToWindow ? RdpDisplayMode.FitToWindow : RdpDisplayMode.FixedResolution;
        _settings.RdpDefaultRedirectClipboard = RdpRedirectClipboard;
        _settings.RdpDefaultRedirectAudio = RdpRedirectAudio;
        _settings.RdpDefaultUseMultimon = RdpUseMultimon;

        _settings.SshFontFamily = SshFontFamily;
        _settings.SshFontSize = SshFontSize;
        _settings.SshDefaultKeepAliveSeconds = SshKeepAliveSeconds;
        _settings.SshDefaultTerminalType = SshTerminalType;

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

    // ── 命令 ──────────────────────────────────────────────────────

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
