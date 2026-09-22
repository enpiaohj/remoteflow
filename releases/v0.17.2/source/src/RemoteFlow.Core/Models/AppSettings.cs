namespace RemoteFlow.Core.Models;

/// <summary>应用主题。</summary>
public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2
}

/// <summary>关闭主窗口时的行为。</summary>
public enum WindowCloseBehavior
{
    /// <summary>退出应用。</summary>
    Exit = 0,

    /// <summary>最小化到系统托盘。</summary>
    MinimizeToTray = 1
}

/// <summary>日期格式。</summary>
public enum AppDateFormat
{
    System = 0,
    Dash = 1,
    Slash = 2,
    Chinese = 3
}

/// <summary>时间格式。</summary>
public enum AppTimeFormat
{
    Hour24 = 0,
    Hour12 = 1
}

/// <summary>
/// 首页标题下方那行日期的详略程度。一个下拉即预览，取代原先「显示时间 / 显示秒 /
/// 显示星期 / 显示周数 / 显示顺序」五个分散控件。
/// </summary>
public enum HomeDateLine
{
    /// <summary>只有日期。例：<c>2026年9月8日</c></summary>
    DateOnly = 0,

    /// <summary>日期 + 时间。例：<c>2026年9月8日 · 22:30</c></summary>
    DateTime = 1,

    /// <summary>日期 + 星期 + 时间。例：<c>2026年9月8日 周一 · 22:30</c></summary>
    DateWeekdayTime = 2,

    /// <summary>完整：日期 + 星期 + 周数 + 时间。例：<c>2026年9月8日 周一 · 第37周 · 22:30</c></summary>
    Full = 3
}

/// <summary>
/// 主窗口**初始**尺寸的预置档位。高分屏、普通屏、笔记本适合的大小差得远，写死一个值
/// 总有一头不合适，所以做成可选项放进设置。「跟随屏幕」按可用区域比例算，其余是固定值
/// —— 固定值实际使用时仍会被夹进当前屏幕的可用区域，选「大」也不会在小屏上超出。
/// </summary>
public enum WindowSizePreset
{
    /// <summary>跟随屏幕：可用区域的 72% 宽 / 78% 高。</summary>
    Auto = 0,

    /// <summary>大 —— 适合高分辨率外接显示器。</summary>
    Large = 1,

    /// <summary>中 —— 常规桌面。</summary>
    Medium = 2,

    /// <summary>小 —— 笔记本 / 小屏。</summary>
    Compact = 3
}

/// <summary>
/// 应用级设置。持久化为独立 JSON 文件，写入采用原子替换，避免异常退出导致配置损坏。
/// </summary>
public sealed class AppSettings
{
    // ── 常规 ──────────────────────────────────────────────
    public bool LaunchOnStartup { get; set; }

    public WindowCloseBehavior CloseBehavior { get; set; } = WindowCloseBehavior.Exit;

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>主窗口初始尺寸预置。见 <see cref="WindowSizePreset"/>。</summary>
    public WindowSizePreset WindowSize { get; set; } = WindowSizePreset.Auto;

    /// <summary>界面语言。V0.1 仅提供简体中文，保留字段以便后续 i18n。</summary>
    public string Language { get; set; } = "zh-CN";

    // ── 日期与时间 ─────────────────────────────────────────
    public AppDateFormat DateFormat { get; set; } = AppDateFormat.Chinese;
    public AppTimeFormat TimeFormat { get; set; } = AppTimeFormat.Hour24;

    /// <summary>首页标题下方日期行的详略程度。默认「完整」≈ 旧版全开时的效果。</summary>
    public HomeDateLine HomeDateLine { get; set; } = HomeDateLine.Full;

    /// <summary>是否已展示过「全屏工具条自动隐藏」的首次提示。</summary>
    public bool SessionFullScreenHintShown { get; set; }

    // ── 全屏悬浮工具条（药丸）───────────────────────────────
    /// <summary>窗口最大化档：鼠标在屏幕（内容区）顶沿需悬停多久才唤出药丸。0 = 立即唤出。</summary>
    public int PillRevealDelayWindowFullMs { get; set; } = 1500;

    /// <summary>窗口最大化档：鼠标离开药丸 / 停留感应区后多久收起。0 = 立即收起。</summary>
    public int PillHideDelayWindowFullMs { get; set; } = 900;

    /// <summary>完全全屏档：顶沿悬停唤出延迟。语义同窗口最大化档。</summary>
    public int PillRevealDelayScreenFullMs { get; set; } = 1500;

    /// <summary>完全全屏档：鼠标离开后收起延迟。语义同窗口最大化档。</summary>
    public int PillHideDelayScreenFullMs { get; set; } = 900;

    /// <summary>启动或新建连接后默认打开的页面。</summary>
    public LandingPage DefaultLandingPage { get; set; } = LandingPage.Home;

    /// <summary>用户是否已关闭首页底部的安全提示横幅。</summary>
    public bool HomeSecurityTipDismissed { get; set; }

    /// <summary>
    /// 「我的连接」里被折叠的分组 Id（字符串形式）。分组默认展开，只记录例外，
    /// 这样新建的分组自然是展开状态。
    /// </summary>
    public List<string> CollapsedGroupIds { get; set; } = [];

    /// <summary>是否已完成默认分组种子（首启）。用于区分「真·首次安装」与「用户已删光分组」，
    /// 避免删光后每次启动又自动复活默认组。</summary>
    public bool DefaultGroupSeedDone { get; set; }

    // ── RDP 默认值 ────────────────────────────────────────
    public RdpDisplayMode RdpDefaultDisplayMode { get; set; } = RdpDisplayMode.FitToWindow;
    public bool RdpDefaultRedirectClipboard { get; set; } = true;
    public bool RdpDefaultRedirectAudio { get; set; }
    public bool RdpDefaultUseMultimon { get; set; }

    // ── SSH 默认值 ────────────────────────────────────────
    public string SshDefaultEncoding { get; set; } = "UTF-8";
    public string SshFontFamily { get; set; } = "Cascadia Mono, Consolas, 微软雅黑";
    public int SshFontSize { get; set; } = 14;
    public int SshDefaultKeepAliveSeconds { get; set; } = 30;
    public string SshDefaultTerminalType { get; set; } = "xterm-256color";

    /// <summary>SSH 终端配色主题。默认 Dark Gray（深灰）。</summary>
    public SshTerminalTheme SshTerminalTheme { get; set; } = SshTerminalTheme.DarkGray;

    /// <summary>多行粘贴时先确认，避免误把多行命令直接执行。</summary>
    public bool SshConfirmMultilinePaste { get; set; } = true;

    /// <summary>大文本粘贴时先警告。</summary>
    public bool SshWarnLargePaste { get; set; } = true;

    // ── VNC 默认值 ────────────────────────────────────────
    public VncScaleMode VncDefaultScaleMode { get; set; } = VncScaleMode.FitToWindow;
    public bool VncDefaultViewOnly { get; set; }
    public bool VncDefaultSharedConnection { get; set; } = true;

    /// <summary>新建 VNC 连接默认开启「剪贴板同步（远端 → 本机）」。</summary>
    public bool VncDefaultClipboardToLocal { get; set; } = true;

    // ── 安全 ──────────────────────────────────────────────
    /// <summary>连接历史保留天数。0 表示不自动清理。</summary>
    public int HistoryRetentionDays { get; set; }

    // ── 云同步 ────────────────────────────────────────────
    /// <summary>
    /// AppsCloud 服务地址（API 根，末尾带斜杠）。默认指向正式部署域名，
    /// 该域名专用于同步服务、不设 PathBase。已配置时 UI 锁定该字段，
    /// 需要连自建 / 内网实例时由用户勾选解锁再改。
    /// </summary>
    public string CloudBaseUrl { get; set; } = "https://sync.appscloud.cn/";

    /// <summary>是否已启用云同步（登录过 AppsCloud 且本地保留会话）。</summary>
    public bool CloudSyncEnabled { get; set; }

    /// <summary>上次登录的邮箱，回填到登录框省得重输。「清除此设备云数据」时一并清空。</summary>
    public string CloudEmail { get; set; } = string.Empty;

    /// <summary>
    /// 上一次同步所用 Vault 的标识（服务端 VaultId）。用于识别「Vault 被重建 / 换账号」：
    /// 与当前 Vault 不一致时清空本地同步指针，避免客户端永远以为「已同步」而不再推拉。
    /// </summary>
    public string CloudVaultId { get; set; } = string.Empty;

    /// <summary>本机云设备标识（首次登录时生成的稳定 Guid 字符串）。清除云数据时一并清空。</summary>
    public string CloudDeviceId { get; set; } = string.Empty;

    // ── 数据 ──────────────────────────────────────────────
    /// <summary>数据库文件所在目录。留空表示使用默认的 %LOCALAPPDATA%\RemoteFlow。</summary>
    public string DataDirectory { get; set; } = string.Empty;

    /// <summary>会话区最大并发 Tab 数，用于防止异常情况下无限创建重复连接。</summary>
    public int MaxConcurrentSessions { get; set; } = 20;

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.CollapsedGroupIds = [.. CollapsedGroupIds];
        return copy;
    }
}
