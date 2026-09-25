using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure;
using RemoteFlow.Infrastructure.Data;
using RemoteFlow.Infrastructure.Settings;

namespace RemoteFlow.Presentation.ViewModels;

/// <summary>「设置」页面的标签页。</summary>
public enum SettingsTab
{
    General,
    Rdp,
    Ssh,
    Vnc,
    Security,
    Cloud,
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

    /// <summary>「首页时间行」下拉的一项：下拉即预览，<see cref="Sample"/> 是当前格式设置下的真实样例。</summary>
    public sealed record HomeDateLineOption(HomeDateLine Value, string Sample);

    public sealed record SshTerminalThemeOption(SshTerminalTheme Value, string Label);

    /// <summary>终端主题预览里的单个 ANSI 色块。</summary>
    public sealed record TerminalPreviewSwatch(string Hex);

    /// <summary>全屏悬浮工具条延迟下拉的一项：<see cref="Ms"/> 为 0 表示立即。</summary>
    public sealed record PillDelayOption(int Ms, string Label);


    private const string CsvFilter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*";
    private const string BackupFilter = "RemoteFlow 加密备份 (*.rfbackup)|*.rfbackup|所有文件 (*.*)|*.*";

    /// <summary>终端主题预览的调色板。色值与 Assets/Terminal/terminal.html 的 THEMES 保持一致。</summary>
    private sealed record TerminalThemeColors(string Background, string Foreground, string[] Ansi);

    // ANSI 16 色顺序：黑/红/绿/黄/蓝/紫/青/白 + 对应 bright。
    private static readonly IReadOnlyDictionary<string, TerminalThemeColors> TerminalPreviewPalettes =
        new Dictionary<string, TerminalThemeColors>
        {
            ["dark"] = new("#1E1E1E", "#CCCCCC", new[]
            {
                "#000000", "#CD3131", "#0DBC79", "#E5E510", "#2472C8", "#BC3FBC", "#11A8CD", "#E5E5E5",
                "#666666", "#F14C4C", "#23D18B", "#F5F543", "#3B8EEA", "#D670D6", "#29B8DB", "#FFFFFF"
            }),
            ["light"] = new("#FFFFFF", "#333333", new[]
            {
                "#000000", "#CD3131", "#00BC00", "#949800", "#0451A5", "#BC05BC", "#0598BC", "#555555",
                "#666666", "#CD3131", "#14CE14", "#B5BA00", "#0451A5", "#BC05BC", "#0598BC", "#A5A5A5"
            }),
            ["darkGray"] = new("#1E1E1E", "#D4D4D4", new[]
            {
                "#000000", "#CD3131", "#0DBC79", "#E5E510", "#2472C8", "#BC3FBC", "#11A8CD", "#E5E5E5",
                "#666666", "#F14C4C", "#23D18B", "#F5F543", "#3B8EEA", "#D670D6", "#29B8DB", "#FFFFFF"
            }),
            ["black"] = new("#000000", "#D7D7D7", new[]
            {
                "#000000", "#CC0000", "#4E9A06", "#C4A000", "#3465A4", "#75507B", "#06989A", "#D3D7CF",
                "#555753", "#EF2929", "#8AE234", "#FCE94F", "#729FCF", "#AD7FA8", "#34E2E2", "#EEEEEC"
            }),
            ["navy"] = new("#0B1B33", "#CFD8E3", new[]
            {
                "#14283F", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#D5DAE3",
                "#5C7080", "#FF7A85", "#A6E3A1", "#FFD580", "#79B8FF", "#D79BE0", "#6CD6DD", "#FFFFFF"
            }),
            ["solarizedDark"] = new("#002B36", "#839496", new[]
            {
                "#073642", "#DC322F", "#859900", "#B58900", "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
                "#586E75", "#CB4B16", "#859900", "#B58900", "#268BD2", "#D33682", "#2AA198", "#FDF6E3"
            }),
        };

    private readonly AppSettings _settings;
    private readonly JsonSettingsStore _store;
    private readonly IThemeService _themeService;
    private readonly ILaunchOnStartupService _launchOnStartupService;
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

    /// <summary>重建「首页时间行」下拉样例时抑制选中项回调，避免回声递归。</summary>
    private bool _refreshingDateLineOptions;

    public SettingsPageViewModel(
        AppSettings settings,
        JsonSettingsStore store,
        IThemeService theme,
        ILaunchOnStartupService launchOnStartup,
        IHistoryRepository history,
        IHostKeyRepository hostKeys,
        AppPaths paths,
        IDialogService dialogs,
        ImportExportService importExport,
        CredentialBackupService credentialBackup,
        LocalBackupService localBackup,
        GroupService groupService,
        CloudSyncViewModel cloudSync,
        ILogger<SettingsPageViewModel> logger)
    {
        Cloud = cloudSync;
        _settings = settings;
        _store = store;
        _themeService = theme;
        _launchOnStartupService = launchOnStartup;
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

        RefreshTerminalPreview();

        // 应用深浅切换（常规页改主题 / 系统跟随变化）会联动「跟随应用」的终端预览。
        _themeService.EffectiveThemeChanged += (_, _) =>
        {
            if (SelectedSshTerminalTheme?.Value == SshTerminalTheme.FollowApp)
            {
                RefreshTerminalPreview();
            }
        };
    }

    /// <summary>「Cloud Sync」子面板。</summary>
    public CloudSyncViewModel Cloud { get; }

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

    /// <summary>主窗口**初始**尺寸预置 —— 高分屏 / 普通屏 / 笔记本各取所需，下次启动生效。</summary>
    [ObservableProperty]
    private WindowSizePreset _selectedWindowSize;

    public IReadOnlyList<WindowSizePreset> WindowSizeOptions { get; } =
        [WindowSizePreset.Auto, WindowSizePreset.Large, WindowSizePreset.Medium, WindowSizePreset.Compact];

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

    /// <summary>开关标题（对齐设计文档 §6：保护默认分组「{默认组名}」）。</summary>
    public string DefaultGroupSwitchLabel =>
        HasDefaultGroup ? $"保护默认分组「{DefaultGroupLabel}」" : "保护默认分组";

    /// <summary>分组卡片副文案（跟随状态，不再重复组名）。</summary>
    public string DefaultGroupDescription =>
        !HasDefaultGroup
            ? "当前没有默认分组，新建连接默认进入「未分组」。"
            : (DefaultGroupProtected
                ? "已受保护：不可重命名 / 删除 / 移动层级，但仍可增删连接、建子分组。"
                : "未受保护：可重命名 / 删除 / 移动层级。");

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

    /// <summary>「首页时间行」四档详略预设，每项携带当前格式设置下的真实样例
    /// （由 <see cref="RefreshHomeDateLineOptions"/> 重建）。</summary>
    [ObservableProperty]
    private IReadOnlyList<HomeDateLineOption> _homeDateLineOptions = [];

    [ObservableProperty]
    private DateFormatOption _selectedDateFormat = null!;

    [ObservableProperty]
    private TimeFormatOption _selectedTimeFormat = null!;

    [ObservableProperty]
    private HomeDateLineOption _selectedHomeDateLine = null!;

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

    [ObservableProperty]
    private string _sshEncoding = string.Empty;

    [ObservableProperty]
    private SshTerminalThemeOption _selectedSshTerminalTheme = null!;

    [ObservableProperty]
    private bool _sshConfirmMultilinePaste;

    [ObservableProperty]
    private bool _sshWarnLargePaste;

    /// <summary>粘贴时使用括号模式。关闭后不再出现远端的粘贴高亮，但多行粘贴会被逐行执行。</summary>
    [ObservableProperty]
    private bool _sshBracketedPaste = true;

    public IReadOnlyList<string> TerminalTypeOptions { get; } = ["xterm-256color", "xterm", "vt100", "linux"];

    public IReadOnlyList<string> SshEncodingOptions { get; } = ["UTF-8", "GBK", "GB18030", "Big5", "ISO-8859-1"];

    public IReadOnlyList<SshTerminalThemeOption> SshTerminalThemeOptions { get; } =
    [
        new(SshTerminalTheme.FollowApp, "跟随应用"),
        new(SshTerminalTheme.DarkGray, "Dark Gray"),
        new(SshTerminalTheme.Black, "Black"),
        new(SshTerminalTheme.Navy, "Navy"),
        new(SshTerminalTheme.SolarizedDark, "Solarized Dark"),
        new(SshTerminalTheme.Light, "Light"),
    ];

    public IReadOnlyList<int> FontSizeOptions { get; } = [11, 12, 13, 14, 15, 16, 18, 20];

    // ── SSH → 终端外观预览 ────────────────────────────────────────

    [ObservableProperty]
    private string _terminalPreviewBackground = "#1E1E1E";

    [ObservableProperty]
    private string _terminalPreviewForeground = "#D4D4D4";

    [ObservableProperty]
    private IReadOnlyList<TerminalPreviewSwatch> _terminalPreviewColors = [];

    // ── VNC ───────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _vncFitToWindow;

    [ObservableProperty]
    private bool _vncViewOnly;

    [ObservableProperty]
    private bool _vncSharedConnection;

    [ObservableProperty]
    private bool _vncClipboardToLocal;

    // ── 会话 ──────────────────────────────────────────────────────

    [ObservableProperty]
    private int _maxConcurrentSessions;

    /// <summary>连接工作台的在线探测开关（TCP 单步、并发受限、按需触发）。</summary>
    [ObservableProperty]
    private bool _presenceProbeEnabled;

    // ── 会话 → 全屏悬浮工具条 ─────────────────────────────────────

    /// <summary>顶沿悬停唤出延迟的可选值（两个全屏档共用一套刻度）。</summary>
    public IReadOnlyList<PillDelayOption> PillRevealDelayOptions { get; } =
    [
        new(0, "立即"),
        new(500, "0.5 秒"),
        new(1000, "1 秒"),
        new(1500, "1.5 秒"),
        new(2000, "2 秒"),
        new(3000, "3 秒"),
        new(5000, "5 秒"),
    ];

    /// <summary>鼠标离开后收起延迟的可选值。</summary>
    public IReadOnlyList<PillDelayOption> PillHideDelayOptions { get; } =
    [
        new(0, "立即"),
        new(500, "0.5 秒"),
        new(900, "0.9 秒"),
        new(1500, "1.5 秒"),
        new(2000, "2 秒"),
        new(3000, "3 秒"),
        new(5000, "5 秒"),
    ];

    [ObservableProperty]
    private PillDelayOption _selectedPillRevealDelayWindowFull = new(1500, "1.5 秒");

    [ObservableProperty]
    private PillDelayOption _selectedPillHideDelayWindowFull = new(900, "0.9 秒");

    [ObservableProperty]
    private PillDelayOption _selectedPillRevealDelayScreenFull = new(1500, "1.5 秒");

    [ObservableProperty]
    private PillDelayOption _selectedPillHideDelayScreenFull = new(900, "0.9 秒");

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
        SelectedWindowSize = _settings.WindowSize;
        DefaultLandingPage = _settings.DefaultLandingPage;
        Language = string.IsNullOrWhiteSpace(_settings.Language) ? "zh-CN" : _settings.Language;
        SelectedDateFormat = DateFormatOptions.First(o => o.Value == _settings.DateFormat);
        SelectedTimeFormat = TimeFormatOptions.First(o => o.Value == _settings.TimeFormat);

        RdpFitToWindow = _settings.RdpDefaultDisplayMode == RdpDisplayMode.FitToWindow;
        RdpRedirectClipboard = _settings.RdpDefaultRedirectClipboard;
        RdpRedirectAudio = _settings.RdpDefaultRedirectAudio;
        RdpUseMultimon = _settings.RdpDefaultUseMultimon;

        SshFontFamily = _settings.SshFontFamily;
        SshFontSize = _settings.SshFontSize;
        SshKeepAliveSeconds = _settings.SshDefaultKeepAliveSeconds;
        SshTerminalType = _settings.SshDefaultTerminalType;
        SshEncoding = string.IsNullOrWhiteSpace(_settings.SshDefaultEncoding) ? "UTF-8" : _settings.SshDefaultEncoding;
        SelectedSshTerminalTheme = SshTerminalThemeOptions.First(o => o.Value == _settings.SshTerminalTheme);
        SshConfirmMultilinePaste = _settings.SshConfirmMultilinePaste;
        SshWarnLargePaste = _settings.SshWarnLargePaste;
        SshBracketedPaste = _settings.SshBracketedPaste;

        VncFitToWindow = _settings.VncDefaultScaleMode == VncScaleMode.FitToWindow;
        VncViewOnly = _settings.VncDefaultViewOnly;
        VncSharedConnection = _settings.VncDefaultSharedConnection;
        VncClipboardToLocal = _settings.VncDefaultClipboardToLocal;

        MaxConcurrentSessions = _settings.MaxConcurrentSessions;
        PresenceProbeEnabled = _settings.PresenceProbeEnabled;

        SelectedPillRevealDelayWindowFull = NearestPillDelay(PillRevealDelayOptions, _settings.PillRevealDelayWindowFullMs);
        SelectedPillHideDelayWindowFull = NearestPillDelay(PillHideDelayOptions, _settings.PillHideDelayWindowFullMs);
        SelectedPillRevealDelayScreenFull = NearestPillDelay(PillRevealDelayOptions, _settings.PillRevealDelayScreenFullMs);
        SelectedPillHideDelayScreenFull = NearestPillDelay(PillHideDelayOptions, _settings.PillHideDelayScreenFullMs);

        RefreshHomeDateLineOptions();
    }

    /// <summary>设置值不在下拉刻度内（如手改过 JSON）时落到最接近的一档。</summary>
    private static PillDelayOption NearestPillDelay(IReadOnlyList<PillDelayOption> options, int ms)
        => options.OrderBy(o => Math.Abs(o.Ms - ms)).First();

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
            OnPropertyChanged(nameof(DefaultGroupSwitchLabel));
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
            StatusMessage = "切换默认分组保护失败，请查看日志。";
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

    partial void OnSelectedWindowSizeChanged(WindowSizePreset value) => Save();
    partial void OnDefaultLandingPageChanged(LandingPage value) => Save();
    partial void OnLanguageChanged(string value) => Save();

    partial void OnSelectedDateFormatChanged(DateFormatOption value) => ApplyDateTimeSettings(value.Value);
    partial void OnSelectedTimeFormatChanged(TimeFormatOption value) => ApplyDateTimeSettings(value.Value);

    partial void OnSelectedHomeDateLineChanged(HomeDateLineOption value)
    {
        if (_isLoading || _refreshingDateLineOptions)
        {
            return;
        }

        ApplyDateTimeSettings();
    }

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
        // LoadFromSettings 早期 SelectedHomeDateLine 尚未就绪时保留已读入的持久化值，
        // 避免默认值回写把用户选好的详略档覆盖掉。
        if (SelectedHomeDateLine is not null)
        {
            _settings.HomeDateLine = SelectedHomeDateLine.Value;
        }

        DateTimeDisplay.Configure(_settings);
        Save();

        // 日期格式 / 12-24 小时变化会反映进下拉样例文字。
        RefreshHomeDateLineOptions();
    }

    /// <summary>
    /// 用当前日期与 12/24 小时设置重建「首页时间行」下拉的四档样例——下拉即预览。
    /// 日期格式或 12/24 小时改变后由 <see cref="ApplyDateTimeSettings"/> 自动调用刷新。
    /// </summary>
    public void RefreshHomeDateLineOptions()
    {
        var now = DateTimeOffset.Now;
        var selected = SelectedHomeDateLine?.Value ?? _settings.HomeDateLine;

        HomeDateLine[] values =
            [HomeDateLine.DateOnly, HomeDateLine.DateTime, HomeDateLine.DateWeekdayTime, HomeDateLine.Full];
        var options = values
            .Select(v => new HomeDateLineOption(v, DateTimeDisplay.HomeDateLineText(now, v)))
            .ToList();

        _refreshingDateLineOptions = true;
        try
        {
            HomeDateLineOptions = options;
            SelectedHomeDateLine = options.First(o => o.Value == selected);
        }
        finally
        {
            _refreshingDateLineOptions = false;
        }
    }

    /// <summary>
    /// 刷新「终端外观」预览的配色。跟随应用时取当前实际生效的深浅（dark/light），
    /// 固定预设直接按预设取色。色值与 terminal.html 的 THEMES 保持一致，仅是轻量近似。
    /// </summary>
    private void RefreshTerminalPreview()
    {
        var value = SelectedSshTerminalTheme?.Value ?? _settings.SshTerminalTheme;
        var key = value switch
        {
            SshTerminalTheme.FollowApp => _themeService.IsDark ? "dark" : "light",
            SshTerminalTheme.DarkGray => "darkGray",
            SshTerminalTheme.Black => "black",
            SshTerminalTheme.Navy => "navy",
            SshTerminalTheme.SolarizedDark => "solarizedDark",
            SshTerminalTheme.Light => "light",
            _ => "darkGray",
        };

        if (!TerminalPreviewPalettes.TryGetValue(key, out var colors))
        {
            colors = TerminalPreviewPalettes["darkGray"];
        }

        TerminalPreviewBackground = colors.Background;
        TerminalPreviewForeground = colors.Foreground;
        TerminalPreviewColors = colors.Ansi.Select(c => new TerminalPreviewSwatch(c)).ToArray();
    }

    partial void OnRdpFitToWindowChanged(bool value) => Save();
    partial void OnRdpRedirectClipboardChanged(bool value) => Save();
    partial void OnRdpRedirectAudioChanged(bool value) => Save();
    partial void OnRdpUseMultimonChanged(bool value) => Save();
    partial void OnSshFontFamilyChanged(string value) => Save();
    partial void OnSshFontSizeChanged(int value) => Save();
    partial void OnSshKeepAliveSecondsChanged(int value) => Save();
    partial void OnSshTerminalTypeChanged(string value) => Save();
    partial void OnSshEncodingChanged(string value) => Save();
    partial void OnSelectedSshTerminalThemeChanged(SshTerminalThemeOption value)
    {
        Save();
        RefreshTerminalPreview();
    }

    partial void OnSshConfirmMultilinePasteChanged(bool value) => Save();
    partial void OnSshWarnLargePasteChanged(bool value) => Save();
    partial void OnSshBracketedPasteChanged(bool value) => Save();
    partial void OnVncFitToWindowChanged(bool value) => Save();
    partial void OnVncViewOnlyChanged(bool value) => Save();
    partial void OnVncSharedConnectionChanged(bool value) => Save();
    partial void OnVncClipboardToLocalChanged(bool value) => Save();
    partial void OnMaxConcurrentSessionsChanged(int value) => Save();
    partial void OnPresenceProbeEnabledChanged(bool value) => Save();
    partial void OnSelectedPillRevealDelayWindowFullChanged(PillDelayOption value) => Save();
    partial void OnSelectedPillHideDelayWindowFullChanged(PillDelayOption value) => Save();
    partial void OnSelectedPillRevealDelayScreenFullChanged(PillDelayOption value) => Save();
    partial void OnSelectedPillHideDelayScreenFullChanged(PillDelayOption value) => Save();

    /// <summary>任一设置保存后触发。已打开的会话视图订阅它，让新设置对进行中的会话即时生效。</summary>
    public static event EventHandler? SettingsSaved;

    private void Save()
    {
        if (_isLoading)
        {
            return;
        }

        _settings.LaunchOnStartup = LaunchOnStartup;
        _settings.CloseBehavior = MinimizeToTrayOnClose ? WindowCloseBehavior.MinimizeToTray : WindowCloseBehavior.Exit;
        _settings.Theme = SelectedTheme;
        _settings.WindowSize = SelectedWindowSize;
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
        _settings.SshDefaultEncoding = SshEncoding;
        _settings.SshTerminalTheme = SelectedSshTerminalTheme.Value;
        _settings.SshConfirmMultilinePaste = SshConfirmMultilinePaste;
        _settings.SshWarnLargePaste = SshWarnLargePaste;
        _settings.SshBracketedPaste = SshBracketedPaste;

        _settings.VncDefaultScaleMode = VncFitToWindow ? VncScaleMode.FitToWindow : VncScaleMode.Original;
        _settings.VncDefaultViewOnly = VncViewOnly;
        _settings.VncDefaultSharedConnection = VncSharedConnection;
        _settings.VncDefaultClipboardToLocal = VncClipboardToLocal;

        _settings.MaxConcurrentSessions = Math.Clamp(MaxConcurrentSessions, 1, 100);
        _settings.PresenceProbeEnabled = PresenceProbeEnabled;

        _settings.PillRevealDelayWindowFullMs = SelectedPillRevealDelayWindowFull.Ms;
        _settings.PillHideDelayWindowFullMs = SelectedPillHideDelayWindowFull.Ms;
        _settings.PillRevealDelayScreenFullMs = SelectedPillRevealDelayScreenFull.Ms;
        _settings.PillHideDelayScreenFullMs = SelectedPillHideDelayScreenFull.Ms;

        // 写盘失败不应打断用户操作，仅记录并提示。
        _ = SaveAsync();

        // 让已打开的会话视图（SSH 终端等）把新设置重放到进行中的会话上。
        SettingsSaved?.Invoke(this, EventArgs.Empty);
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

    /// <summary>应用「开机自启」设置。失败时提示（不回滚开关，保持与用户意图一致，下次启动重试）。</summary>
    private void ApplyStartupRegistration(bool enabled)
    {
        if (_isLoading)
        {
            return;
        }

        try
        {
            _launchOnStartupService.SetEnabled(enabled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "配置开机启动失败");
            StatusMessage = "无法修改开机启动设置。";
        }
    }

    /// <summary>
    /// 由托盘等外部入口修改「开机启动」：写 / 删注册表并落盘设置，与设置页开关共享同一
    /// <see cref="AppSettings"/> 单例与同一 <see cref="ILaunchOnStartupService"/>，因此两处始终一致。
    /// </summary>
    public void SetLaunchOnStartup(bool enabled)
    {
        if (_isLoading)
        {
            return;
        }

        LaunchOnStartup = enabled; // setter → OnLaunchOnStartupChanged → ApplyStartupRegistration + Save
    }

    /// <summary>
    /// 进入设置页时把 <see cref="AppSettings.LaunchOnStartup"/> 的最新值同步回开关
    /// （托盘可能已通过 <see cref="SetLaunchOnStartup"/> 改过）。属性值与设置一致时不做
    /// 任何写注册表 / 落盘动作。
    /// </summary>
    public void ReloadStartup()
    {
        if (_isLoading || LaunchOnStartup == _settings.LaunchOnStartup)
        {
            return;
        }

        LaunchOnStartup = _settings.LaunchOnStartup;
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

    /// <summary>主机密钥的动作动词：macOS 用「删除」，Windows 沿用既有的「移除」，两端文案各自稳定。</summary>
    private static string HostKeyVerb => OperatingSystem.IsMacOS() ? "删除" : "移除";

    [RelayCommand]
    private async Task RemoveHostKeyAsync(HostKeyItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            $"{HostKeyVerb}主机密钥",
            $"确定要{HostKeyVerb} {item.Host} 的已信任密钥吗？\n\n下次连接该主机时会重新提示确认指纹。",
            HostKeyVerb,
            isDanger: true);

        if (!confirmed)
        {
            return;
        }

        await _hostKeys.DeleteAsync(item.HostName, item.Port);
        await LoadHostKeysAsync();
        StatusMessage = $"已{HostKeyVerb} {item.Host} 的主机密钥。";
    }

    /// <summary>清空全部已信任主机。目前仅 macOS 设置页有入口。</summary>
    [RelayCommand]
    private async Task ClearHostKeysAsync()
    {
        var count = TrustedHostKeys.Count;
        if (count == 0)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            "清空已信任的主机",
            $"确定要{HostKeyVerb}全部 {count} 条已信任的主机密钥吗？\n\n"
            + "此操作不可撤销。之后再连接这些主机时，都会重新提示确认指纹。",
            $"全部{HostKeyVerb}",
            isDanger: true);

        if (!confirmed)
        {
            return;
        }

        await _hostKeys.ClearAsync();
        await LoadHostKeysAsync();
        StatusMessage = $"已清空 {count} 条已信任的主机密钥。";
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
                (OperatingSystem.IsMacOS()
                    ? "凭据密文按本机加密保存，换电脑或换系统账户后通常无法直接解密；跨设备迁移凭据请用「导出 .rfbackup」。"
                    : "vault.dat（凭据密文）只能在当前 Windows 账户下解密，换账户或换机器需要用「导出 .rfbackup」迁移凭据。"),
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
