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

/// <summary>首页时间相对日期段的显示顺序。</summary>
public enum HomeTimeOrder
{
    /// <summary>时间拼在日期段之后（如「2026年9月5日 · 14:05」）。</summary>
    Trailing = 0,

    /// <summary>时间拼在日期段之前（如「14:05 · 2026年9月5日」）。</summary>
    Leading = 1,

    /// <summary>时间在日期行下方另起一行显示时钟。</summary>
    SeparateLine = 2
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

    /// <summary>界面语言。V0.1 仅提供简体中文，保留字段以便后续 i18n。</summary>
    public string Language { get; set; } = "zh-CN";

    // ── 日期与时间 ─────────────────────────────────────────
    public AppDateFormat DateFormat { get; set; } = AppDateFormat.Chinese;
    public AppTimeFormat TimeFormat { get; set; } = AppTimeFormat.Hour24;
    public bool ShowWeekday { get; set; } = true;
    public bool ShowHomeWeekNumber { get; set; } = true;

    /// <summary>首页日期行是否显示当前时间。</summary>
    public bool ShowHomeTime { get; set; }

    /// <summary>首页时间是否显示秒（需 ShowHomeTime）。</summary>
    public bool ShowHomeSeconds { get; set; }

    /// <summary>首页时间相对日期段的显示顺序。</summary>
    public HomeTimeOrder ShowHomeTimeOrder { get; set; } = HomeTimeOrder.Trailing;

    /// <summary>是否已展示过「全屏工具条自动隐藏」的首次提示。</summary>
    public bool SessionFullScreenHintShown { get; set; }

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

    // ── VNC 默认值 ────────────────────────────────────────
    public VncScaleMode VncDefaultScaleMode { get; set; } = VncScaleMode.FitToWindow;
    public bool VncDefaultViewOnly { get; set; }
    public bool VncDefaultSharedConnection { get; set; } = true;

    // ── 安全 ──────────────────────────────────────────────
    /// <summary>连接历史保留天数。0 表示不自动清理。</summary>
    public int HistoryRetentionDays { get; set; }

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
