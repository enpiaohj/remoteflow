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

    /// <summary>是否已展示过「全屏工具条自动隐藏」的首次提示。</summary>
    public bool SessionFullScreenHintShown { get; set; }

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

    // ── 安全 ──────────────────────────────────────────────
    /// <summary>连接历史保留天数。0 表示不自动清理。</summary>
    public int HistoryRetentionDays { get; set; }

    // ── 数据 ──────────────────────────────────────────────
    /// <summary>数据库文件所在目录。留空表示使用默认的 %LOCALAPPDATA%\RemoteFlow。</summary>
    public string DataDirectory { get; set; } = string.Empty;

    /// <summary>会话区最大并发 Tab 数，用于防止异常情况下无限创建重复连接。</summary>
    public int MaxConcurrentSessions { get; set; } = 20;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
