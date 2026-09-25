using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using RemoteFlow.App.Services;
using RemoteFlow.Presentation.ViewModels;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Settings;

namespace RemoteFlow.App.Views.Sessions;

/// <summary>
/// 会话 Tab 的外壳：工具条 + 协议视图 + 状态层。
/// <para>
/// 非全屏：顶部常驻一条工具条（<c>DockedBar</c>）。
/// 全屏：应用标题栏与 Tab 栏都隐藏了，工具条改为一条<b>悬浮药丸</b>
/// （<c>ToolbarPopup</c>），承担会话切换、最小化 / 关闭 / 退出全屏；默认自动隐藏——
/// 鼠标离开后按设置延迟淡出，移到屏幕顶沿需悬停片刻（延迟可配）再淡入，
/// 单击远端画面立即淡出；可固定常驻，可拖动。
/// </para>
/// <para>
/// 药丸用 <see cref="Popup"/> 承载：RDP 会话用
/// <see cref="System.Windows.Forms.Integration.WindowsFormsHost"/> 承载原生 ActiveX，
/// 普通 WPF 浮层会被这块 airspace 盖住、也收不到其上的鼠标事件。Popup 是独立顶层
/// 窗口，能盖在原生画面之上（mstsc 连接条同理）。顶沿唤出用 <see cref="_edgeWatch"/>
/// 轮询光标位置——光标进带后由 <see cref="_revealTimer"/> 做悬停意图判定，避免掠过
/// 顶沿误弹；单击远端隐藏用 <see cref="_mouseHook"/> 低级鼠标钩子。
/// </para>
/// </summary>
public partial class SessionHostView : UserControl
{
    /// <summary>
    /// 进入全屏后，若鼠标未落到药丸上，多久自动收起。
    /// <para>
    /// Windows 端取 3s——比其他平台略长，给用户看清工具条的时间；单击远端画面
    /// 仍由低级鼠标钩子立即收起（见 <see cref="LowLevelMouseHook"/>），不受此值影响。
    /// </para>
    /// </summary>
    private static readonly TimeSpan InitialAutoHideDelay = TimeSpan.FromSeconds(3);

    /// <summary>设置不可用时的兜底显示延迟，与设置默认值一致。</summary>
    private static readonly TimeSpan FallbackRevealDelay = TimeSpan.FromMilliseconds(1500);

    /// <summary>设置不可用时的兜底消失延迟，与设置默认值一致。</summary>
    private static readonly TimeSpan FallbackHideDelay = TimeSpan.FromMilliseconds(900);

    /// <summary>首次进全屏、带提示时药丸的驻留时间。</summary>
    private static readonly TimeSpan HintVisibleDelay = TimeSpan.FromSeconds(5);

    /// <summary>光标顶沿轮询间隔。</summary>
    private static readonly TimeSpan EdgeWatchInterval = TimeSpan.FromMilliseconds(80);

    /// <summary>刚唤出后的最短驻留时间，避免鼠标掠过顶沿时一闪而过。</summary>
    private static readonly TimeSpan MinVisibleTime = TimeSpan.FromMilliseconds(450);

    /// <summary>
    /// 未展开时，认定“鼠标贴到顶沿”的判定高度（DIU）。
    /// 取值刻意放宽：无边框全屏若定位有几像素误差（窗口上沿略高 / 略低于显示器上沿），
    /// 光标贴到屏幕最顶端时相对 <see cref="RootGrid"/> 的 Y 会偏离 0 十几像素；
    /// 严格的边到边定位由 <c>MainWindow.SnapToMonitorBounds</c> 保证，这里只是兜底。
    /// </summary>
    private const double EdgeRevealBand = 20;

    /// <summary>展开时，药丸左右各留多少 DIU 作为“停留”感应区。</summary>
    private const double KeepZonePadX = 52;

    /// <summary>展开时，药丸下沿再向下留多少 DIU 作为“停留”感应区。</summary>
    private const double KeepZonePadY = 14;

    /// <summary>拖动后药丸至少保留多少像素在可视区内。</summary>
    private const double MinVisibleExtent = 96;

    private FrameworkElement? _protocolView;
    private SessionTabViewModel? _tab;
    private MainViewModel? _main;
    private Window? _window;

    private readonly DispatcherTimer _autoHideTimer;
    private readonly DispatcherTimer _edgeWatch;

    /// <summary>
    /// 顶沿唤出的「悬停意图」计时器：光标进带装表、离带取消、到点复核仍在带内才显示，
    /// 防止鼠标掠过顶沿误弹药丸。间隔每次装表时按档位从设置解析，改设置即生效。
    /// </summary>
    private readonly DispatcherTimer _revealTimer;

    /// <summary>true=药丸固定常驻；false=自动隐藏。仅全屏下有意义。</summary>
    private bool _pinned;

    /// <summary>正在以代码同步 <see cref="PinToggle"/> 的选中态，用于抑制回调递归。</summary>
    private bool _syncingPin;

    /// <summary>用户是否手动拖动过药丸——是则不再自动回到顶部居中。</summary>
    private bool _userMoved;

    private bool _dragging;
    private Point _dragAnchorPx;
    private double _dragStartH;
    private double _dragStartV;

    /// <summary>会话切换下拉菜单（代码构建，独立 HWND，可盖在 airspace 之上）。</summary>
    private ContextMenu? _sessionMenu;

    /// <summary>连接质量详情 Flyout（同一会话 Tab 同时只允许一个）。</summary>
    private ConnectionQualityFlyout? _flyout;

    /// <summary>Flyout 的锚点（常驻条状态入口或全屏药丸状态入口）。</summary>
    private FrameworkElement? _flyoutAnchor;

    private DateTime _shownAt = DateTime.MinValue;

    /// <summary>低级鼠标钩子：全屏未固定时，单击药丸以外区域立即收起药丸。</summary>
    private nint _mouseHook;
    private NativeMethods.LowLevelMouseProc? _mouseProc;

    public SessionHostView()
    {
        InitializeComponent();

        _autoHideTimer = new DispatcherTimer { Interval = InitialAutoHideDelay };
        _autoHideTimer.Tick += OnAutoHideTick;

        _revealTimer = new DispatcherTimer { Interval = FallbackRevealDelay };
        _revealTimer.Tick += OnRevealTimerTick;

        _edgeWatch = new DispatcherTimer { Interval = EdgeWatchInterval };
        _edgeWatch.Tick += OnEdgeWatchTick;

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => RepositionPopup();

        // Popup 是独立 HWND，其内部的 WPF 鼠标事件正常可用。
        PillBar.MouseEnter += (_, _) => _autoHideTimer.Stop();
        PillBar.MouseLeave += (_, _) => ScheduleAutoHide(ResolveHideDelay());
        PillBar.SizeChanged += (_, _) => RepositionPopup();
    }

    // ── 生命周期 ─────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window ??= Window.GetWindow(this);
        _main ??= System.Windows.Application.Current?.MainWindow?.DataContext as MainViewModel
                  ?? _window?.DataContext as MainViewModel;

        if (_main is not null)
        {
            _main.PropertyChanged -= OnMainViewModelChanged;
            _main.PropertyChanged += OnMainViewModelChanged;
        }

        if (_window is not null)
        {
            _window.LocationChanged -= OnWindowMovedOrResized;
            _window.LocationChanged += OnWindowMovedOrResized;
            _window.SizeChanged -= OnWindowMovedOrResized;
            _window.SizeChanged += OnWindowMovedOrResized;
        }

        // 多会话下切换 Tab 靠 Collapsed/Visible，视图实例不销毁——每次切回来都要
        // 重新套用全屏模式（重建药丸 / 计时器 / 钩子），切走时拆掉交给下一个可见视图。
        IsVisibleChanged -= OnViewVisibilityChanged;
        IsVisibleChanged += OnViewVisibilityChanged;

        if (IsVisible)
        {
            RefreshScreenFullButton(_main?.IsScreenFull == true);
            ApplyFullScreenMode(_main?.IsSessionFullScreen == true);
        }
    }

    /// <summary>
    /// 本视图可见性变化。切回来（且当前处于全屏）→ 重新套用全屏模式；
    /// 切走 → 拆掉本视图的悬浮药丸、计时器与低级鼠标钩子，避免多个后台视图的
    /// 药丸 / 钩子并存。（一次性的「首次可见」不够——切走再切回就不会再触发。）
    /// </summary>
    private void OnViewVisibilityChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            RefreshScreenFullButton(_main?.IsScreenFull == true);
            ApplyFullScreenMode(_main?.IsSessionFullScreen == true);
        }
        else
        {
            _edgeWatch.Stop();
            _autoHideTimer.Stop();
            _revealTimer.Stop();
            RemoveMouseHook();
            CloseQualityFlyout();
            FullScreenHint.Visibility = Visibility.Collapsed;
            ToolbarPopup.IsOpen = false;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        IsVisibleChanged -= OnViewVisibilityChanged;
        _autoHideTimer.Stop();
        _revealTimer.Stop();
        _edgeWatch.Stop();
        RemoveMouseHook();
        CloseQualityFlyout();
        ToolbarPopup.IsOpen = false;

        if (_window is not null)
        {
            _window.LocationChanged -= OnWindowMovedOrResized;
            _window.SizeChanged -= OnWindowMovedOrResized;
            _window = null;
        }

        if (_main is not null)
        {
            _main.PropertyChanged -= OnMainViewModelChanged;
            _main = null;
        }

        if (_protocolView is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _protocolView = null;
        SessionContent.Content = null;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not SessionTabViewModel tab)
        {
            return;
        }

        _tab = tab;
        ShowToolsFor(tab.Protocol);

        // 协议视图只创建一次；Tab 切换靠可见性，不重建视图，
        // 否则会话会被反复销毁重连。
        if (_protocolView is not null)
        {
            return;
        }

        var factory = App.Services.GetRequiredService<ISessionViewFactory>();
        _protocolView = factory.Create(tab);
        SessionContent.Content = _protocolView;
    }

    /// <summary>按协议显示对应的工具条分组（常驻条与悬浮药丸各一套）。</summary>
    private void ShowToolsFor(ProtocolType protocol)
    {
        var rdp = protocol == ProtocolType.Rdp ? Visibility.Visible : Visibility.Collapsed;
        var ssh = protocol == ProtocolType.Ssh ? Visibility.Visible : Visibility.Collapsed;
        var vnc = protocol == ProtocolType.Vnc ? Visibility.Visible : Visibility.Collapsed;

        RdpToolsDock.Visibility = rdp;
        SshToolsDock.Visibility = ssh;
        VncToolsDock.Visibility = vnc;
        RdpToolsPill.Visibility = rdp;
        SshToolsPill.Visibility = ssh;
        VncToolsPill.Visibility = vnc;
    }

    // ── 全屏 <-> 非全屏 ──────────────────────────────────────────

    private void OnMainViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel main)
        {
            return;
        }

        switch (e.PropertyName)
        {
            // 窗口最大化 ↔ 完全全屏：不装卸药丸，只换那颗按钮的图标与提示。
            case nameof(MainViewModel.IsScreenFull):
                RefreshScreenFullButton(main.IsScreenFull);
                break;

            // 常规 ↔ 任意全屏档：药丸与常驻条互换，计时器与鼠标钩子随之装卸。
            case nameof(MainViewModel.IsSessionFullScreen):
                ApplyFullScreenMode(main.IsSessionFullScreen);
                RefreshScreenFullButton(main.IsScreenFull);
                break;
        }
    }

    /// <summary>
    /// 同步药丸上「完全全屏」按钮的双态图标与提示。图标已有的
    /// <c>Icon.ExitFullScreen</c> 此前无人使用，正好用在这里。
    /// 该按钮从不禁用、从不隐藏——与 macOS 药丸上的同类按钮一致。
    /// </summary>
    private void RefreshScreenFullButton(bool screenFull)
    {
        ScreenFullButton.Content = TryFindResource(
            screenFull ? "Icon.ExitFullScreen" : "Icon.FullScreen") ?? ScreenFullButton.Content;
        ScreenFullButton.ToolTip = screenFull ? "退出完全全屏 (F11)" : "完全全屏 (F11)";
    }

    /// <summary>只对当前可见（选中）的会话联动，避免影响后台会话。</summary>
    private void ApplyFullScreenMode(bool fullScreen)
    {
        if (!IsVisible)
        {
            return;
        }

        if (fullScreen)
        {
            // 进入全屏：药丸默认自动隐藏（沉浸式），从顶部居中出现，随即排定收起。
            _userMoved = false;
            SetPinnedState(false);
            ShowToolbar();
            _edgeWatch.Start();
            InstallMouseHook();
            ScheduleAutoHide(MaybeShowFirstRunHint() ? HintVisibleDelay : InitialAutoHideDelay);
        }
        else
        {
            // 退出全屏：收起药丸，工具条回到常驻条。
            _edgeWatch.Stop();
            _autoHideTimer.Stop();
            _revealTimer.Stop();
            RemoveMouseHook();
            FullScreenHint.Visibility = Visibility.Collapsed;
            ToolbarPopup.IsOpen = false;
        }
    }

    /// <summary>
    /// 首次进全屏时展示一次性提示：工具条会自动隐藏、鼠标移到顶沿再唤出。
    /// <returns>本次展示了提示返回 true（调用方据此延长驻留时间）。</returns>
    /// </summary>
    private bool MaybeShowFirstRunHint()
    {
        var settings = App.Services.GetService<AppSettings>();
        if (settings is null || settings.SessionFullScreenHintShown)
        {
            return false;
        }

        FullScreenHint.Visibility = Visibility.Visible;
        settings.SessionFullScreenHintShown = true;
        _ = App.Services.GetService<JsonSettingsStore>()?.SaveAsync(settings);
        return true;
    }

    private void OnPinChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPin)
        {
            return;
        }

        _pinned = PinToggle.IsChecked == true;

        if (_pinned)
        {
            _autoHideTimer.Stop();
            ShowToolbar();
        }
        else
        {
            ScheduleAutoHide(ResolveHideDelay());
        }
    }

    private void SetPinnedState(bool pinned)
    {
        _pinned = pinned;
        if (PinToggle.IsChecked != pinned)
        {
            _syncingPin = true;
            PinToggle.IsChecked = pinned;
            _syncingPin = false;
        }
    }

    // ── 顶沿唤出 / 自动隐藏 ───────────────────────────────────────

    private void OnEdgeWatchTick(object? sender, EventArgs e)
    {
        if (_pinned || _dragging || !IsLoaded || !IsVisible)
        {
            return;
        }

        if (!TryGetCursorRelativeToRoot(out var rel))
        {
            return;
        }

        if (ToolbarPopup.IsOpen)
        {
            if (IsInKeepZone(rel))
            {
                _autoHideTimer.Stop();
            }
            else if (!_autoHideTimer.IsEnabled)
            {
                ScheduleAutoHide(ResolveHideDelay());
            }
        }
        else if (IsInRevealBand(rel))
        {
            if (!_revealTimer.IsEnabled)
            {
                // 只在未计时装表、不反复重置：光标沿顶沿缓慢横移也算持续悬停。
                _revealTimer.Interval = ResolveRevealDelay();
                _revealTimer.Start();
            }
        }
        else
        {
            _revealTimer.Stop();
        }
    }

    /// <summary>
    /// 取光标位置并换算到 RootGrid 左上角为原点的坐标系（DIU）。取不到（无呈现源等）返回 false。
    /// </summary>
    private bool TryGetCursorRelativeToRoot(out Point rel)
    {
        rel = default;
        var source = PresentationSource.FromVisual(RootGrid);
        if (source?.CompositionTarget is null || !NativeMethods.GetCursorPos(out var cursor))
        {
            return false;
        }

        var origin = RootGrid.PointToScreen(new Point(0, 0));
        rel = source.CompositionTarget.TransformFromDevice.Transform(
            new Point(cursor.X - origin.X, cursor.Y - origin.Y));
        return true;
    }

    /// <summary>光标是否落在顶沿唤出带（全宽，含上下容差）。</summary>
    private bool IsInRevealBand(Point rel)
    {
        // 两侧都用 EdgeRevealBand 容差：窗口上沿被顶出可视区时 rel.Y 落在带的上方，
        // 窗口上沿略低于显示器上沿时落在带的下方，两种都要能唤出。
        return rel.X >= 0 && rel.X <= ActualWidth
            && rel.Y >= -EdgeRevealBand && rel.Y <= EdgeRevealBand;
    }

    /// <summary>悬停意图计时到点：复核光标仍贴着顶沿带才唤出（装表后光标可能早已离开）。</summary>
    private void OnRevealTimerTick(object? sender, EventArgs e)
    {
        _revealTimer.Stop();

        if (_pinned || _dragging || !IsLoaded || !IsVisible
            || !TryGetCursorRelativeToRoot(out var rel) || !IsInRevealBand(rel))
        {
            return;
        }

        ShowToolbar();
    }

    /// <summary>顶沿悬停唤出延迟——按当前全屏档位从设置实时解析，改设置即生效。</summary>
    private TimeSpan ResolveRevealDelay() =>
        App.Services.GetService<AppSettings>()?.GetRevealDelay(IsScreenFullTier) ?? FallbackRevealDelay;

    /// <summary>鼠标离开后收起延迟——按当前全屏档位从设置实时解析，改设置即生效。</summary>
    private TimeSpan ResolveHideDelay() =>
        App.Services.GetService<AppSettings>()?.GetHideDelay(IsScreenFullTier) ?? FallbackHideDelay;

    /// <summary>当前会话是否处于「完全全屏」档（相对「窗口最大化」档）。</summary>
    private bool IsScreenFullTier => _main?.IsScreenFull == true;

    /// <summary>光标是否处于“停留”感应区（药丸本体 + 其上到屏幕顶、四周留边）。</summary>
    private bool IsInKeepZone(Point rel)
    {
        var pillLeft = ToolbarPopup.HorizontalOffset + PillBar.Margin.Left;
        var left = pillLeft - KeepZonePadX;
        var right = pillLeft + PillBar.ActualWidth + KeepZonePadX;
        var bottom = ToolbarPopup.VerticalOffset + PillBar.Margin.Top + PillBar.ActualHeight + KeepZonePadY;

        return rel.X >= left && rel.X <= right && rel.Y >= -EdgeRevealBand && rel.Y <= bottom;
    }

    private void OnAutoHideTick(object? sender, EventArgs e)
    {
        _autoHideTimer.Stop();

        if (_pinned || _dragging || PillBar.IsMouseOver || _sessionMenu is { IsOpen: true } || _flyout is { IsVisible: true })
        {
            return;
        }

        HideToolbar(immediate: false);
    }

    private void ScheduleAutoHide(TimeSpan delay)
    {
        // 连接质量 Flyout 打开期间不自动收起药丸（Flyout 由它呼出，需保持可见）。
        if (_pinned || _flyout is { IsVisible: true })
        {
            return;
        }

        _autoHideTimer.Stop();
        _autoHideTimer.Interval = delay;
        _autoHideTimer.Start();
    }

    private void ShowToolbar()
    {
        if (ToolbarPopup.IsOpen)
        {
            return;
        }

        ToolbarPopup.IsOpen = true;
        _shownAt = DateTime.UtcNow;
        Dispatcher.BeginInvoke(RepositionPopup, DispatcherPriority.Loaded);
    }

    private void HideToolbar(bool immediate)
    {
        if (!ToolbarPopup.IsOpen || _pinned || _dragging)
        {
            return;
        }

        // 连接中 / 断线时始终保留（状态信息与关闭入口重要）。
        if (_tab is { IsConnected: false })
        {
            return;
        }

        // 定时收起时给一小段驻留期，避免顶沿掠过一闪而过；单击远端隐藏则立即。
        if (!immediate && DateTime.UtcNow - _shownAt < MinVisibleTime)
        {
            ScheduleAutoHide(ResolveHideDelay());
            return;
        }

        FullScreenHint.Visibility = Visibility.Collapsed;
        ToolbarPopup.IsOpen = false;
    }

    // ── 单击远端画面即收起（低级鼠标钩子）──────────────────────

    private void InstallMouseHook()
    {
        if (_mouseHook != 0)
        {
            return;
        }

        _mouseProc ??= LowLevelMouseHook;
        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhMouseLl, _mouseProc, NativeMethods.GetModuleHandle(null), 0);
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }
    }

    private nint LowLevelMouseHook(int nCode, nint wParam, nint lParam)
    {
        // 连接质量 Flyout 打开期间不因「点击药丸以外」收起药丸——Flyout 也需要被交互。
        if (nCode >= 0 && !_pinned && _flyout is not { IsVisible: true } && IsButtonDown(wParam))
        {
            var x = Marshal.ReadInt32(lParam);
            var y = Marshal.ReadInt32(lParam, 4);

            if (ToolbarPopup.IsOpen)
            {
                if (!IsPointOverPill(x, y))
                {
                    // 不吞事件——点击照常送到远端画面，只是顺手收起药丸。
                    Dispatcher.BeginInvoke(new Action(() => HideToolbar(immediate: true)));
                }
            }
            else if (_revealTimer.IsEnabled)
            {
                // 唤出挂起期间点击远端 = 在与远端交互，取消挂起——否则点远端
                // 顶部 UI 后药丸会在延迟到点时突然弹出，挡住刚点的地方。
                Dispatcher.BeginInvoke(new Action(_revealTimer.Stop));
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static bool IsButtonDown(nint wParam)
    {
        var msg = (int)wParam;
        return msg is NativeMethods.WmLButtonDown
            or NativeMethods.WmRButtonDown
            or NativeMethods.WmMButtonDown;
    }

    private bool IsPointOverPill(int screenX, int screenY)
    {
        if (!PillBar.IsVisible || PresentationSource.FromVisual(PillBar) is null)
        {
            return false;
        }

        try
        {
            var topLeft = PillBar.PointToScreen(new Point(0, 0));
            var bottomRight = PillBar.PointToScreen(new Point(PillBar.ActualWidth, PillBar.ActualHeight));
            return screenX >= topLeft.X - 4 && screenX <= bottomRight.X + 4
                && screenY >= topLeft.Y - 4 && screenY <= bottomRight.Y + 4;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // ── 拖动 ─────────────────────────────────────────────────────

    private void OnToolbarDragStart(object sender, MouseButtonEventArgs e)
    {
        // 点在按钮 / 切换器等交互控件上时不拖动。
        if (IsInteractive(e.OriginalSource) || !NativeMethods.GetCursorPos(out var p))
        {
            return;
        }

        _dragging = true;
        _userMoved = true;
        _dragAnchorPx = new Point(p.X, p.Y);
        _dragStartH = ToolbarPopup.HorizontalOffset;
        _dragStartV = ToolbarPopup.VerticalOffset;
        _autoHideTimer.Stop();
        PillBar.CaptureMouse();
        e.Handled = true;
    }

    private void OnToolbarDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }

        if (!NativeMethods.GetCursorPos(out var p))
        {
            return;
        }

        var source = PresentationSource.FromVisual(this);
        var toDiu = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var delta = toDiu.Transform(new Vector(p.X - _dragAnchorPx.X, p.Y - _dragAnchorPx.Y));

        ToolbarPopup.HorizontalOffset = ClampHorizontal(_dragStartH + delta.X);
        ToolbarPopup.VerticalOffset = ClampVertical(_dragStartV + delta.Y);
    }

    private void OnToolbarDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            EndDrag();
            e.Handled = true;
        }
    }

    private void EndDrag()
    {
        _dragging = false;
        PillBar.ReleaseMouseCapture();
        ScheduleAutoHide(ResolveHideDelay());
    }

    private static bool IsInteractive(object? source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ButtonBase or MenuItem)
            {
                return true;
            }
        }

        return false;
    }

    // ── Popup 定位（跟随窗口、居中、限制在可视区）────────────────

    private void OnWindowMovedOrResized(object? sender, EventArgs e) => RepositionPopup();

    /// <summary>
    /// Popup 默认不会随窗口移动 / 布局变化重新定位。这里重算偏移：
    /// 未被用户拖动过则回到顶部居中，否则夹在可视区内，再轻推一下触发重排。
    /// </summary>
    private void RepositionPopup()
    {
        if (!ToolbarPopup.IsOpen)
        {
            return;
        }

        double h;
        double v;

        if (_userMoved)
        {
            h = ClampHorizontal(ToolbarPopup.HorizontalOffset);
            v = ClampVertical(ToolbarPopup.VerticalOffset);
        }
        else
        {
            // 居中的是「可见的药丸本体」，需要扣掉 Border 自身的左边距。
            var barWidth = PillBar.ActualWidth > 0 ? PillBar.ActualWidth : 320;
            h = Math.Max(0, (RootGrid.ActualWidth - barWidth) / 2 - PillBar.Margin.Left);
            v = 0;
        }

        ToolbarPopup.HorizontalOffset = h + 0.5;
        ToolbarPopup.HorizontalOffset = h;
        ToolbarPopup.VerticalOffset = v;
    }

    private double ClampHorizontal(double value)
    {
        var barWidth = PillBar.ActualWidth > 0 ? PillBar.ActualWidth : 320;
        var min = MinVisibleExtent - barWidth;
        var max = Math.Max(min, RootGrid.ActualWidth - MinVisibleExtent);
        return Math.Clamp(value, min, max);
    }

    private double ClampVertical(double value)
    {
        var max = Math.Max(0, RootGrid.ActualHeight - MinVisibleExtent / 2);
        return Math.Clamp(value, 0, max);
    }

    // ── 会话切换 / 窗口控制 ──────────────────────────────────────

    private void OnSessionSwitchClick(object sender, RoutedEventArgs e)
    {
        if (_main is null)
        {
            return;
        }

        _sessionMenu ??= new ContextMenu { PlacementTarget = SessionSwitchButton, Placement = PlacementMode.Bottom };
        _sessionMenu.Items.Clear();

        var iconFont = TryFindResource("IconFont") as FontFamily ?? new FontFamily("Segoe MDL2 Assets");

        foreach (var tab in _main.Tabs.OfType<SessionTabViewModel>())
        {
            var current = tab;
            var item = new MenuItem
            {
                Header = current.Title,
                IsChecked = ReferenceEquals(current, _main.SelectedTab),
                Icon = new TextBlock { Text = current.Icon, FontFamily = iconFont, FontSize = 13 }
            };
            item.Click += (_, _) => _main.SelectedTab = current;
            _sessionMenu.Items.Add(item);
        }

        if (_sessionMenu.Items.Count == 0)
        {
            return;
        }

        _autoHideTimer.Stop();
        _sessionMenu.IsOpen = true;
    }

    private void OnMinimizeWindow(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.WindowState = WindowState.Minimized;
        }
    }

    // ── 连接质量详情 Flyout ──────────────────────────────────────

    private DateTime _lastStatusClick = DateTime.MinValue;

    /// <summary>常驻条 / 全屏药丸的状态入口点击 → 打开 Flyout。</summary>
    private void OnStatusEntryClick(object sender, RoutedEventArgs e)
    {
        // 双击这里会触发 Flyout 开→（第二击令主窗激活、Flyout 失活关闭）→开 的竞态；
        // owned + ShowInTaskbar=False 的无边框窗口在这种反复激活/关闭中会把宿主窗口
        // 最小化（已知 WPF 问题）。250ms 内的第二次点击直接吞掉。
        var now = DateTime.UtcNow;
        if (now - _lastStatusClick < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        _lastStatusClick = now;

        if (sender is FrameworkElement anchor)
        {
            OpenQualityFlyout(anchor);
        }
    }

    /// <summary>
    /// 打开（或切换）连接质量详情 Flyout。同一会话 Tab 只允许一个实例：
    /// 已打开时再次点击同一入口 = 关闭；点击另一入口 = 重新定位。
    /// Flyout 用独立顶层窗口承载，因此能盖在 RDP ActiveX airspace 之上。
    /// </summary>
    private void OpenQualityFlyout(FrameworkElement anchor)
    {
        if (_tab is null)
        {
            return;
        }

        if (_flyout is { IsVisible: true })
        {
            if (ReferenceEquals(_flyoutAnchor, anchor))
            {
                CloseQualityFlyout();
            }
            else
            {
                _flyoutAnchor = anchor;
                PositionQualityFlyout();
            }

            return;
        }

        _flyout = null; // 上一实例已关闭，清引用重建
        _flyoutAnchor = anchor;

        var window = _window ?? Window.GetWindow(this);
        var flyout = new ConnectionQualityFlyout
        {
            Owner = window,
            DataContext = _tab
        };
        flyout.Closed += OnQualityFlyoutClosed;
        _flyout = flyout;

        // 先按估算高度定位，Show 后拿到真实高度再精修，避免闪到 (0,0)。
        PositionQualityFlyout();
        flyout.Show();
        flyout.UpdateLayout();
        PositionQualityFlyout();

        // 打开即测一次（命令运行中自动禁用，天然防重入）。
        _tab.Quality.RedetectCommand.Execute(null);
    }

    private void CloseQualityFlyout()
    {
        if (_flyout is not { } flyout)
        {
            return;
        }

        flyout.Closed -= OnQualityFlyoutClosed;
        flyout.Close();
        _flyout = null;
        _flyoutAnchor = null;
    }

    private void OnQualityFlyoutClosed(object? sender, EventArgs e)
    {
        if (sender is ConnectionQualityFlyout flyout)
        {
            flyout.Closed -= OnQualityFlyoutClosed;
            _tab?.Quality.CancelRunningProbe();

            // Esc / 关闭按钮等用户主动关闭时，把键盘焦点还给会话画面（RDP ActiveX 等）；
            // 因点击外部（Deactivated）而关闭时焦点已由该点击决定，不再争夺。
            if (flyout.CloseByUserIntent)
            {
                _tab?.RequestSessionFocus();
            }
        }

        // 兜底：owned 无任务栏按钮的无边框窗口关闭时，Windows 偶尔会把宿主窗口最小化。
        if (_window is { WindowState: WindowState.Minimized })
        {
            _window.WindowState = WindowState.Normal;
            _window.Activate();
        }

        if (ReferenceEquals(_flyout, sender))
        {
            _flyout = null;
        }

        _flyoutAnchor = null;
    }

    /// <summary>把 Flyout 定位到锚点按钮下方，并夹在主窗口可视区内。</summary>
    private void PositionQualityFlyout()
    {
        if (_flyout is not { } flyout || _flyoutAnchor is not { } anchor)
        {
            return;
        }

        var window = flyout.Owner;
        if (window is null)
        {
            return;
        }

        var source = PresentationSource.FromVisual(window);
        var toDiu = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

        var windowOriginPx = window.PointToScreen(new Point(0, 0));
        var anchorBottomPx = anchor.PointToScreen(new Point(0, anchor.ActualHeight));
        var relDiu = toDiu.Transform(new Point(
            anchorBottomPx.X - windowOriginPx.X,
            anchorBottomPx.Y - windowOriginPx.Y));

        const double gap = 8;
        const double edgeMargin = 8;
        var flyoutWidth = flyout.ActualWidth > 0 ? flyout.ActualWidth : flyout.Width;
        var flyoutHeight = flyout.ActualHeight > 0 ? flyout.ActualHeight : 430;

        var x = Math.Clamp(
            window.Left + relDiu.X,
            window.Left + edgeMargin,
            window.Left + window.ActualWidth - flyoutWidth - edgeMargin);

        var yBelow = window.Top + relDiu.Y + gap;
        var yMax = window.Top + window.ActualHeight - flyoutHeight - edgeMargin;
        var y = yBelow > yMax
            ? Math.Max(window.Top + edgeMargin, window.Top + relDiu.Y - gap - flyoutHeight)
            : yBelow;

        flyout.Left = Math.Round(x);
        flyout.Top = Math.Round(y);
    }

    // ── 互操作 ───────────────────────────────────────────────────

    private static class NativeMethods
    {
        public const int WhMouseLl = 14;
        public const int WmLButtonDown = 0x0201;
        public const int WmRButtonDown = 0x0204;
        public const int WmMButtonDown = 0x0207;

        public delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT point);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint GetModuleHandle(string? moduleName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc proc, nint hMod, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(nint hook);

        [DllImport("user32.dll")]
        public static extern nint CallNextHookEx(nint hook, int nCode, nint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }
    }
}
