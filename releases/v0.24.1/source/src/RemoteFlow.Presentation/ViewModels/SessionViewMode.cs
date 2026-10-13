namespace RemoteFlow.Presentation.ViewModels;

/// <summary>
/// 会话画面的三档视图模式。与 macOS 的 <c>ViewMode</c> 概念对齐，
/// 但「窗口最大化档」的落地方式两端不同：macOS 只折 chrome、窗口本身不变，
/// Windows 则把窗口最大化（保留标题栏与边框）。
/// </summary>
public enum SessionViewMode
{
    /// <summary>常规：标题栏 + 左导航 + Tab 栏 + 内容 + 详情 + 状态栏。</summary>
    Normal,

    /// <summary>窗口最大化档：保留标题栏与窗口边框，折掉左导航 / Tab 栏 / 详情 / 状态栏。</summary>
    WindowFull,

    /// <summary>完全全屏：无边框铺满整块显示器，连标题栏也隐藏。</summary>
    ScreenFull,
}

/// <summary>
/// 三档之间的转移规则与派生语义。纯函数、无依赖，因此两端都能直接单测。
/// </summary>
public static class SessionViewModeRules
{
    /// <summary>逐档进（F11）：常规 → 窗口最大化 → 完全全屏 → 常规。</summary>
    public static SessionViewMode Advance(SessionViewMode mode) => mode switch
    {
        SessionViewMode.Normal => SessionViewMode.WindowFull,
        SessionViewMode.WindowFull => SessionViewMode.ScreenFull,
        _ => SessionViewMode.Normal,
    };

    /// <summary>
    /// 一路退到底（药丸「退出全屏」）：无论当前在哪一档都回常规。
    /// <para>
    /// 刻意与 <see cref="ToggleScreenFull"/> 区分开：后者只退一档（完全全屏 → 窗口最大化），
    /// 若这里也退一档，完全全屏档下两颗按钮就会做同一件事。
    /// </para>
    /// </summary>
    public static SessionViewMode ExitFullScreen(SessionViewMode mode) => SessionViewMode.Normal;

    /// <summary>完全全屏开关（药丸「完全全屏」）：两档互切，常规档直上完全全屏。</summary>
    public static SessionViewMode ToggleScreenFull(SessionViewMode mode)
        => mode == SessionViewMode.ScreenFull ? SessionViewMode.WindowFull : SessionViewMode.ScreenFull;

    /// <summary>是否处于任意全屏档——决定侧边 chrome（导航 / Tab 栏 / 详情 / 状态栏）是否折起。</summary>
    public static bool IsFullScreenLevel(this SessionViewMode mode) => mode != SessionViewMode.Normal;

    /// <summary>是否处于完全全屏档——决定是否连应用标题栏一起隐藏。</summary>
    public static bool IsScreenFull(this SessionViewMode mode) => mode == SessionViewMode.ScreenFull;
}
