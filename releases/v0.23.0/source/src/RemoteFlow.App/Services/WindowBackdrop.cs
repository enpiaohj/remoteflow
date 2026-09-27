using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace RemoteFlow.App.Services;

/// <summary>系统背景材质种类（对应 DWMWA_SYSTEMBACKDROP_TYPE 的取值）。</summary>
public enum BackdropKind
{
    /// <summary>不使用系统材质，窗口按纯色主题绘制。</summary>
    None = 1,

    /// <summary>Mica：长期存在的主窗口底材，随桌面壁纸淡淡透色。</summary>
    Mica = 2,

    /// <summary>Acrylic：临时浮层（对话框 / 弹窗）的毛玻璃。</summary>
    Acrylic = 3,

    /// <summary>Mica Alt：色彩更饱和的 Mica，用于带 Tab 的标题栏窗口。</summary>
    MicaAlt = 4,
}

/// <summary>
/// Windows 11 系统背景材质（Mica / Acrylic）接线。
/// <para>
/// 做法：窗口不开启 WPF 分层透明（<c>AllowsTransparency</c> 会拖慢渲染并破坏 RDP ActiveX /
/// WebView2 等内嵌原生窗口），而是把 DWM 边框扩展到整个客户区、让 WPF 合成目标透明，
/// 再由 DWM 在窗口下方绘制系统材质。系统关闭透明效果、节能模式、远程会话等场景下，
/// DWM 会自动改画纯色兜底；低于 Windows 11 22H2（build 22621）的系统不支持该属性，
/// 调用方应回退到纯色主题（<see cref="IsSupported"/>）。
/// </para>
/// </summary>
public static class WindowBackdrop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwcpRound = 2;

    /// <summary>系统是否支持 DWMWA_SYSTEMBACKDROP_TYPE（Windows 11 22H2 起）。</summary>
    public static bool IsSupported { get; } =
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    /// <summary>
    /// 把窗口接入玻璃外观：句柄创建后按 <see cref="ThemeService"/> 当前状态设置材质，
    /// 之后玻璃开关或深浅色变化时自动重设；窗口关闭时解除订阅。
    /// 主窗口跟随设置（云母 / 亚克力），对话框与浮层在玻璃外观下统一用亚克力。
    /// </summary>
    public static void Attach(Window window, bool isMainWindow = false)
    {
        if (ThemeService.Instance is not { } theme)
        {
            return;
        }

        void Update()
        {
            if (theme.IsGlass)
            {
                Apply(window, isMainWindow ? theme.MainBackdrop : BackdropKind.Acrylic, theme.IsDark);
            }
            else
            {
                Remove(window);
                SetDarkMode(new WindowInteropHelper(window).Handle, theme.IsDark);
            }
        }

        void OnAppearanceChanged(object? sender, EventArgs e) => window.Dispatcher.Invoke(Update);

        if (new WindowInteropHelper(window).Handle != 0)
        {
            Update();
        }
        else
        {
            window.SourceInitialized += (_, _) => Update();
        }

        theme.AppearanceChanged += OnAppearanceChanged;
        window.Closed += (_, _) => theme.AppearanceChanged -= OnAppearanceChanged;
    }

    /// <summary>
    /// 为窗口启用系统材质。必须在窗口句柄创建后（<c>SourceInitialized</c> 之后）调用。
    /// 返回 false 表示系统不支持或 DWM 拒绝，调用方应保持纯色外观。
    /// </summary>
    public static bool Apply(Window window, BackdropKind kind, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || !IsSupported)
        {
            return false;
        }

        SetDarkMode(hwnd, dark);

        if (kind == BackdropKind.None)
        {
            Remove(window);
            return false;
        }

        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
        {
            target.BackgroundColor = Colors.Transparent;
        }

        // WindowChrome 会在窗口状态变化时按自身的 GlassFrameThickness 重设 DWM 边框，
        // 必须同步改成 -1（整窗），否则最大化 / 还原后材质区域会缩回。
        if (WindowChrome.GetWindowChrome(window) is { } chrome)
        {
            chrome.GlassFrameThickness = new Thickness(-1);
        }

        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);

        // DWM 边框扩展到客户区后，会在自绘标题栏下方再画一套系统最小化 / 最大化 / 关闭按钮，
        // 与应用自己的按钮重叠。去掉 WS_SYSMENU 即不再绘制；关闭 / 最大化仍由应用按钮与
        // WindowChrome 的 SystemCommands 负责。
        SetSystemMenuStyle(hwnd, enabled: false);

        var type = (int)kind;
        return DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref type, sizeof(int)) == 0;
    }

    private const int GwlStyle = -16;
    private const long WsSysMenu = 0x00080000L;

    private static void SetSystemMenuStyle(nint hwnd, bool enabled)
    {
        var style = GetWindowLongPtr(hwnd, GwlStyle);
        var updated = enabled ? style | (nint)WsSysMenu : style & ~(nint)WsSysMenu;
        if (updated != style)
        {
            SetWindowLongPtr(hwnd, GwlStyle, updated);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    /// <summary>
    /// 窗口样式被代码改动后（如退出无边框全屏还原 WindowStyle）重新套用当前外观。
    /// </summary>
    public static void Refresh(Window window, bool isMainWindow = false)
    {
        if (ThemeService.Instance is { IsGlass: true } theme)
        {
            Apply(window, isMainWindow ? theme.MainBackdrop : BackdropKind.Acrylic, theme.IsDark);
        }
    }

    /// <summary>
    /// 把无边框对话框 / 浮层窗口接入玻璃外观。必须在窗口显示前（构造函数里 <c>InitializeComponent</c> 之后）调用。
    /// <para>
    /// 纯色外观下不做任何改动（保持原有的分层透明 + 自绘阴影 + 圆角）。玻璃外观下：
    /// DWM 不会给分层窗口（<c>AllowsTransparency</c>）绘制系统材质，因此改为普通窗口 + 整窗扩展 DWM 边框，
    /// 由系统绘制亚克力、圆角与阴影；同时去掉根容器为自绘阴影预留的外边距、投影与描边，避免与系统阴影重复。
    /// </para>
    /// </summary>
    public static void PrepareDialog(Window dialog)
    {
        if (ThemeService.Instance is not { IsGlass: true })
        {
            return;
        }

        dialog.AllowsTransparency = false;
        dialog.WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(dialog, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(-1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        if (dialog.Content is System.Windows.Controls.Border root)
        {
            root.Margin = new Thickness(0);
            root.Effect = null;
            root.BorderThickness = new Thickness(0);
            // 圆角交给 DWM（系统圆角裁切整窗），根容器铺满，避免两套圆角半径不一致露出缝隙。
            root.CornerRadius = new CornerRadius(0);
        }

        dialog.SourceInitialized += (_, _) => RequestRoundCorners(dialog);
        Attach(dialog);
    }

    /// <summary>关闭系统材质，恢复纯色窗口。</summary>
    public static void Remove(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || !IsSupported)
        {
            return;
        }

        var none = (int)BackdropKind.None;
        DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref none, sizeof(int));

        if (WindowChrome.GetWindowChrome(window) is { } chrome)
        {
            chrome.GlassFrameThickness = new Thickness(0);
        }

        var margins = new Margins();
        DwmExtendFrameIntoClientArea(hwnd, ref margins);

        if (WindowChrome.GetWindowChrome(window) is not null)
        {
            SetSystemMenuStyle(hwnd, enabled: true);
        }

        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
        {
            target.BackgroundColor = SystemColors.WindowColor;
        }
    }

    /// <summary>标题栏 / 系统绘制部分跟随应用深浅色。</summary>
    public static void SetDarkMode(nint hwnd, bool dark)
    {
        if (hwnd == 0)
        {
            return;
        }

        var value = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
    }

    /// <summary>请求系统圆角（Windows 11）。用于去掉自绘圆角与阴影边距后的无边框对话框。</summary>
    public static void RequestRoundCorners(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || !IsSupported)
        {
            return;
        }

        var round = DwmwcpRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
