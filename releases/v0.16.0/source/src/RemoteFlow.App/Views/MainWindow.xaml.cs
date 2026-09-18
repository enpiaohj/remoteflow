using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using RemoteFlow.App.Services;
using RemoteFlow.App.Views.Dialogs;
using RemoteFlow.Presentation.ViewModels;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.App.Views;

/// <summary>
/// 主窗口。使用自定义标题栏（WindowChrome），因此最小化/最大化/关闭需要自行处理。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>低于该宽度时自动收起右侧详情面板（产品设计文档 §7.11 响应式规则）。</summary>
    private const double DetailPanelBreakpoint = 1200;

    private readonly MainViewModel _viewModel;
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogs;

    /// <summary>是否为真正的退出（而非最小化到托盘）。</summary>
    private bool _isExiting;

    /// <summary>
    /// 无边框完全全屏生效中（非 null 即 WindowStyle / ResizeMode 已被改写）。
    /// 只表示「是否去边框」；「是否处于某个全屏档」由 <see cref="_windowViewMode"/> 表达。
    /// </summary>
    private (WindowStyle Style, ResizeMode Resize)? _screenFullChrome;

    /// <summary>离开常规档前的窗口状态，回到常规档时还原。</summary>
    private WindowState? _preFullScreenWindowState;

    /// <summary>离开常规档前的窗口位置与尺寸。进入无边框后 WPF 记忆的还原尺寸会被改写，故自行留底。</summary>
    private Rect _preFullScreenBounds = Rect.Empty;

    /// <summary>正在按档位改窗口（程序化设置 WindowState），用于抑制「用户手动还原」的误判。</summary>
    private bool _applyingViewMode;

    /// <summary>已落地的档位，与 ViewModel 的 ViewMode 对齐。</summary>
    private SessionViewMode _windowViewMode = SessionViewMode.Normal;

    /// <summary>是否处于无边框完全全屏（去掉了窗口边框与标题栏）。</summary>
    private bool IsBorderlessActive => _screenFullChrome is not null;

    public MainWindow(MainViewModel viewModel, AppSettings settings, IDialogService dialogs)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _settings = settings;
        _dialogs = dialogs;
        DataContext = viewModel;

        UpdateRootPadding();

        viewModel.FocusSearchRequested += (_, _) =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        };

        // 会话工具条的按钮与 F11 都只改 ViewMode，真正的窗口形态在这里响应。
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 关闭会话是一次打断性的上下文切换（跳到另一个会话或回工作区）——退出全屏
        // 回到常驻条，让用户看清被切到哪。主动切换会话（不涉及移除）则保持全屏。
        ((INotifyCollectionChanged)viewModel.Tabs).CollectionChanged += OnTabsCollectionChanged;

        SizeChanged += OnWindowSizeChanged;
        StateChanged += OnWindowStateChanged;

        // RDP / VNC 会话内嵌原生 HWND，键盘焦点在里面时会把普通按键（含 F11）
        // 直接转发给远端，WPF 的 OnPreviewKeyDown 与线程消息预处理都拦不到。
        // 用低级键盘钩子在系统层拦 F11，仅当本窗口为前台且正显示会话时消费。
        _lowLevelKeyboardProc = LowLevelKeyboardHook;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            InstallKeyboardHook();

            // 首次显示前把窗口收进所在显示器工作区：默认 1560×940 在小分辨率屏上
            // 会超出可视区、标题栏被顶出屏幕，导致既无法拖动也无法点窗口按钮。
            FitInitialWindow();
        };
    }

    private const int WhKeyboardLl = 13;
    private const int HcAction = 0;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkF11 = 0x7A;
    /// <summary>
    /// 已按下、尚未收到抬起的键。用于抑制长按自动重复——F11 改成逐档循环后，
    /// 自动重复会连跳两档，比「被吞掉」更刺眼。
    /// </summary>
    private int _keyDownVk;
    private DateTime _keyDownAt = DateTime.MinValue;

    private nint _hwnd;
    private nint _keyboardHook;
    private LowLevelKeyboardProc? _lowLevelKeyboardProc;

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    private void InstallKeyboardHook()
    {
        if (_keyboardHook != 0 || _lowLevelKeyboardProc is null)
        {
            return;
        }

        // LL 钩子的 proc 在本进程内，hMod 传 exe 基址（GetModuleHandle(NULL)）即可。
        _keyboardHook = NativeMethods.SetWindowsHookEx(
            WhKeyboardLl, _lowLevelKeyboardProc, NativeMethods.GetModuleHandle(null), 0);
    }

    private void RemoveKeyboardHook()
    {
        if (_keyboardHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }
    }

    private nint LowLevelKeyboardHook(int nCode, nint wParam, nint lParam)
    {
        if (nCode != HcAction)
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        // 抬起：清掉「已按下」标记，让下一次真实按键能进档。必须放行，不吞。
        if (wParam == WmKeyUp || wParam == WmSysKeyUp)
        {
            if (Marshal.ReadInt32(lParam) == _keyDownVk)
            {
                _keyDownVk = 0;
            }

            return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        if (wParam != WmKeyDown && wParam != WmSysKeyDown)
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        var vkCode = Marshal.ReadInt32(lParam);
        if (vkCode != VkF11)
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        // 仅当本窗口为前台、且当前是会话 Tab 时才拦截；否则放行给远端 / 其他应用。
        var consume = NativeMethods.GetForegroundWindow() == _hwnd
                      && _viewModel.SelectedTab is SessionTabViewModel;

        if (!consume)
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        // 标记必须在这里<b>同步</b>落下：改成异步分发的话，RDP 渲染繁忙时队列延迟
        // 可能超过按下到抬起的间隔，keyup 会清到一个尚未赋值的标记，导致下一次
        // 真实按键被误判成长按而吞掉。
        if (!MarkKeyDown(vkCode))
        {
            return 1; // 长按自动重复：吞掉，但不改档位。
        }

        // 钩子回调必须快速返回，切回 UI 线程再改档位。
        Dispatcher.BeginInvoke(new Action(() => _viewModel.AdvanceSessionView()));
        return 1;
    }

    /// <summary>
    /// 处理会话全屏热键（F11 逐档循环）。仅当前选中的是会话 Tab 时响应。
    /// <see cref="OnPreviewKeyDown"/>（焦点在 WPF 元素上时）与低级键盘钩子
    /// （焦点在内嵌 HWND 里时）共用此方法，靠 <see cref="_keyDownVk"/> 抑制长按重复。
    /// <para>
    /// Esc 不在此列：全屏档下它曾经被吞掉用于退出全屏，导致误触且无法送达远端，
    /// 现已完全移出全屏链路，正常放行给会话。
    /// </para>
    /// <returns>已消费该按键返回 true。</returns>
    /// </summary>
    private bool TryHandleFullScreenHotkey(int virtualKey)
    {
        if (virtualKey != VkF11)
        {
            return false;
        }

        if (_viewModel.SelectedTab is not SessionTabViewModel)
        {
            return false;
        }

        // 长按自动重复：按住不放时只进一档，重复的 KeyDown 直接吞掉。
        if (!MarkKeyDown(virtualKey))
        {
            return true;
        }

        _viewModel.AdvanceSessionView();
        return true;
    }

    /// <summary>
    /// 记下这次 KeyDown。<b>必须同步调用</b>（钩子回调内直接调，不要塞进 Dispatcher），
    /// 否则抬手事件可能先于标记落地被处理，标记清不掉，下一次真实按键会被误判成重复。
    /// <para>
    /// 同一键在收到抬手前再次按下 = 长按自动重复，返回 false。1 秒兜底：若因失焦等原因
    /// 漏掉了 KeyUp，标记不会永久卡住导致 F11 从此失效。按键抬起由
    /// <see cref="LowLevelKeyboardHook"/> 同步清除。
    /// </para>
    /// </summary>
    /// <returns>本次是一次新的按下返回 true；长按重复返回 false。</returns>
    private bool MarkKeyDown(int virtualKey)
    {
        var now = DateTime.UtcNow;
        if (_keyDownVk == virtualKey && (now - _keyDownAt).TotalMilliseconds < 1000)
        {
            return false;
        }

        _keyDownVk = virtualKey;
        _keyDownAt = now;
        return true;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ViewMode))
        {
            ApplyViewMode(_viewModel.ViewMode);
        }
    }

    /// <summary>
    /// Tab 集合变化。有会话 Tab 被<b>移除</b>（= 关闭会话）且当前处于全屏档时，回到常规档。
    /// 关闭当前会话后 MainViewModel 会自动选中另一个会话或回工作区——这次上下文切换
    /// 应当在常驻条模式下发生，让用户看清自己被切到哪。主动切换会话不移除 Tab，不受影响。
    /// </summary>
    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Remove
            && e.OldItems?.OfType<SessionTabViewModel>().Any() == true
            && _viewModel.IsSessionFullScreen)
        {
            _viewModel.ResetSessionView();
        }
    }

    /// <summary>正在以代码把全屏窗口按回显示器边界，抑制 <see cref="Window.LocationChanged"/> 递归。</summary>
    private bool _snappingFullScreen;

    /// <summary>
    /// 按档位调整窗口形态。<see cref="SessionViewMode.WindowFull"/> 走普通最大化——保留
    /// 标题栏与窗口边框，WindowChrome 的溢出由 <see cref="UpdateRootPadding"/> 补 8px；
    /// <see cref="SessionViewMode.ScreenFull"/> 才去边框铺满显示器。
    /// </summary>
    private void ApplyViewMode(SessionViewMode mode)
    {
        if (_windowViewMode == mode)
        {
            return;
        }

        _applyingViewMode = true;
        try
        {
            // 离开常规档时留底窗口原态，供回到常规档时精确还原。
            // 采样点必须在「离开常规档」而不是「进入无边框」——否则窗口最大化档
            // 与完全全屏之间来回切时，留底值会被无边框那一步冲掉。
            if (_windowViewMode == SessionViewMode.Normal && mode != SessionViewMode.Normal)
            {
                _preFullScreenWindowState = WindowState;
                _preFullScreenBounds = WindowState == WindowState.Maximized
                    ? RestoreBounds
                    : new Rect(Left, Top, Width, Height);
            }

            if (mode == SessionViewMode.ScreenFull)
            {
                if (!IsBorderlessActive)
                {
                    EnterBorderlessFullScreen();
                }
            }
            else if (IsBorderlessActive)
            {
                ExitBorderlessFullScreen();
            }

            if (mode == SessionViewMode.WindowFull)
            {
                WindowState = WindowState.Maximized;
            }
            else if (mode == SessionViewMode.Normal)
            {
                RestoreWindowFromFullScreen();
            }

            _windowViewMode = mode;
        }
        finally
        {
            _applyingViewMode = false;
        }

        UpdateRootPadding();
    }

    /// <summary>
    /// 进入无边框完全全屏。
    /// <para>
    /// <b>不用 <see cref="WindowState.Maximized"/></b>：无边框窗口最大化时 WPF 会
    /// 向四周各溢出约 8px，导致远端画面底部（含目标系统任务栏）和右侧被裁掉。
    /// </para>
    /// <para>
    /// <b>也不走 <see cref="Window.Left"/> / <see cref="Window.Top"/> 等 DIU 属性定位</b>：
    /// PerMonitorV2 下这些属性的 DIU 空间随所在显示器缩放而变；而且在 WindowState /
    /// WindowStyle 刚切换、HWND 尚未稳定关联到目标显示器时，
    /// <see cref="PresentationSource"/> 的 <c>TransformFromDevice</c> 是旧值——
    /// 换算出的边界会差几像素，后果是：
    /// <list type="number">
    ///   <item>窗口与显示器矩形没有<b>严格重合</b> → 系统不认作全屏 →
    ///     本机任务栏仍压在远端画面（含目标系统任务栏）之上；</item>
    ///   <item>上沿落到屏幕可视区之外 → 会话工具条的顶沿唤出带永远碰不到。</item>
    /// </list>
    /// 改为按<b>物理像素</b>用原始 <c>SetWindowPos</c> 铺到 <c>rcMonitor</c>，
    /// 严格边到边、无溢出、覆盖本机任务栏；WindowState 保持 Normal。
    /// </para>
    /// <para>左侧导航、Tab 栏、右侧详情与底部状态栏由 XAML 绑定 IsSessionFullScreen
    /// 收起；应用标题栏只在「完全全屏」档由 IsScreenFull 收起。</para>
    /// </summary>
    private void EnterBorderlessFullScreen()
    {
        if (IsBorderlessActive)
        {
            return;
        }

        _screenFullChrome = (WindowStyle, ResizeMode);

        // 复位后再去边框：从最大化进入时先回到 Normal，避免 WPF 沿用旧的最大化尺寸。
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;

        SnapToMonitorBounds();

        // WindowStyle=None 会触发 WPF 重建窗口框架、可能回写位置，
        // 布局稳定后再断言一次，抢在其之后。
        Dispatcher.BeginInvoke(SnapToMonitorBounds, System.Windows.Threading.DispatcherPriority.Loaded);

        LocationChanged -= OnFullScreenWindowMoved;
        LocationChanged += OnFullScreenWindowMoved;
    }

    /// <summary>退出无边框完全全屏：还原窗口边框，并写回进档前的尺寸。</summary>
    private void ExitBorderlessFullScreen()
    {
        LocationChanged -= OnFullScreenWindowMoved;

        if (_screenFullChrome is not { } previous)
        {
            return;
        }

        WindowStyle = previous.Style;
        ResizeMode = previous.Resize;
        _screenFullChrome = null;

        ApplySavedBounds();
    }

    /// <summary>回到常规档：还原窗口边框（若在）、位置尺寸，以及离开常规档前的窗口状态。</summary>
    private void RestoreWindowFromFullScreen()
    {
        if (IsBorderlessActive)
        {
            ExitBorderlessFullScreen();
        }

        ApplySavedBounds();

        if (_preFullScreenWindowState is { } state)
        {
            WindowState = state;
            _preFullScreenWindowState = null;
        }

        _preFullScreenBounds = Rect.Empty;
    }

    /// <summary>
    /// 写回离开常规档时留底的位置尺寸。无边框期间的 <c>SetWindowPos</c> 会把 WPF
    /// 记忆的「还原尺寸」改写成整块显示器矩形，不写回的话「向下还原」会还原成整屏。
    /// </summary>
    private void ApplySavedBounds()
    {
        if (_preFullScreenBounds.IsEmpty)
        {
            return;
        }

        Left = _preFullScreenBounds.Left;
        Top = _preFullScreenBounds.Top;
        Width = _preFullScreenBounds.Width;
        Height = _preFullScreenBounds.Height;
    }

    /// <summary>全屏期间窗口被 WPF 重排 / 显示器切换挪离边界时，snap 回当前显示器完整边界。</summary>
    private void OnFullScreenWindowMoved(object? sender, EventArgs e)
    {
        if (IsBorderlessActive && !_snappingFullScreen)
        {
            SnapToMonitorBounds();
        }
    }

    /// <summary>把窗口按当前所在显示器的完整物理边界（rcMonitor）严格铺满。已重合则不动。</summary>
    private void SnapToMonitorBounds()
    {
        if (!IsBorderlessActive)
        {
            return;
        }

        var handle = _hwnd != IntPtr.Zero ? _hwnd : new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var bounds = System.Windows.Forms.Screen.FromHandle(handle).Bounds;

        // 已严格重合就别再 SetWindowPos，否则 WM_WINDOWPOSCHANGED → LocationChanged 会自激。
        if (NativeMethods.GetWindowRect(handle, out var current)
            && current.Left == bounds.Left && current.Top == bounds.Top
            && current.Right == bounds.Right && current.Bottom == bounds.Bottom)
        {
            return;
        }

        _snappingFullScreen = true;
        try
        {
            NativeMethods.SetWindowPos(
                handle, IntPtr.Zero,
                bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged);
        }
        finally
        {
            _snappingFullScreen = false;
        }
    }

    /// <summary>
    /// 根容器外边距：
    /// 普通最大化下 WindowChrome 会溢出工作区约 8px，需要补偿；
    /// 全屏（WindowStyle=None）没有 Chrome，必须归零，否则内容四周留一圈边。
    /// </summary>
    private void UpdateRootPadding()
    {
        // 无边框全屏没有 WindowChrome，必须归零；窗口最大化档仍走普通最大化，需要补 8px。
        var borderless = IsBorderlessActive;
        var maximized = WindowState == WindowState.Maximized;

        RootPadding.Padding = maximized && !borderless ? new Thickness(8) : new Thickness(0);
    }

    /// <summary>请求退出应用（由托盘菜单调用）。</summary>
    public void RequestExit()
    {
        _isExiting = true;
        Close();
    }

    // ── 窗口尺寸自适应（小分辨率 / 显示器切换）──────────────────

    /// <summary>当前窗口所在显示器的工作区，换算为设备无关单位。句柄未就绪返回 null。</summary>
    private Rect? GetMonitorWorkAreaDiu()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var screen = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var toDiu = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                    ?? Matrix.Identity;
        var topLeft = toDiu.Transform(new Point(screen.Left, screen.Top));
        var size = toDiu.Transform(new Vector(screen.Width, screen.Height));
        return new Rect(topLeft.X, topLeft.Y, size.X, size.Y);
    }

    /// <summary>
    /// 首次显示前把尺寸收敛到显示器工作区、位置居中：
    /// 默认 1560×940 在比它小的屏上会让标题栏（拖动区 + 窗口按钮）跑到屏幕外，
    /// 表现为「完全无法操作」。可用区小于最小尺寸时放低下限，保证能缩进来。
    /// </summary>
    private void FitInitialWindow()
    {
        if (WindowState != WindowState.Normal || IsBorderlessActive)
        {
            return;
        }

        if (GetMonitorWorkAreaDiu() is not { } area)
        {
            return;
        }

        MinWidth = Math.Min(MinWidth, Math.Floor(area.Width));
        MinHeight = Math.Min(MinHeight, Math.Floor(area.Height));

        Width = Math.Clamp(Width, MinWidth, area.Width);
        Height = Math.Clamp(Height, MinHeight, area.Height);

        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    /// <summary>
    /// 回到普通态（还原 / 从最小化回来）时，确保窗口仍在显示器工作区内且标题栏可达。
    /// 覆盖「曾在更小屏上最大化、再还原」与「拖动到更小显示器」两类越界。
    /// </summary>
    private void KeepWindowOnScreen()
    {
        if (WindowState != WindowState.Normal || IsBorderlessActive)
        {
            return;
        }

        if (GetMonitorWorkAreaDiu() is not { } area)
        {
            return;
        }

        var minW = Math.Min(MinWidth, area.Width);
        var minH = Math.Min(MinHeight, area.Height);
        var w = Math.Clamp(Width, minW, area.Width);
        var h = Math.Clamp(Height, minH, area.Height);
        var left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - w));
        var top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - h));

        var changed = Math.Abs(Width - w) > 0.5 || Math.Abs(Height - h) > 0.5
                      || Math.Abs(Left - left) > 0.5 || Math.Abs(Top - top) > 0.5;
        if (!changed)
        {
            return;
        }

        Width = w;
        Height = h;
        Left = left;
        Top = top;
    }

    /// <summary>标题栏右键弹出系统菜单（移动 / 还原 / 大小 / 最小化 / 最大化 / 关闭）。</summary>
    private void OnTitleBarSystemMenu(object sender, MouseButtonEventArgs e)
    {
        if (IsBorderlessActive)
        {
            return; // 完全全屏（无边框）没有标题栏，不需要系统菜单；窗口最大化档仍有标题栏。
        }

        if (e.ButtonState == MouseButtonState.Released)
        {
            var point = PointToScreen(e.GetPosition(this));
            SystemCommands.ShowSystemMenu(this, point);
        }
    }

    // ── 标题栏按钮 ────────────────────────────────────────────────

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnHelpClick(object sender, RoutedEventArgs e) => await _dialogs.ShowAboutAsync();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        // 最大化后按钮语义变为「还原」，图标需要同步切换。
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "向下还原" : "最大化";

        UpdateRootPadding();

        // 用户在「窗口最大化档」手动取消最大化（点「向下还原」/ 双击标题栏 / 系统菜单）
        // 时退出该档——否则会停在「没最大化，却仍在窗口最大化档」的矛盾态。
        // _applyingViewMode 守卫必不可少：进入完全全屏时程序化设置的 WindowState = Normal
        // 会命中这里，没有守卫就会被自己弹回常规档。
        if (!_applyingViewMode
            && WindowState == WindowState.Normal
            && _windowViewMode == SessionViewMode.WindowFull)
        {
            _viewModel.ResetSessionView();
            return;
        }

        // 还原（退出最大化 / 最小化后回来）时把窗口收进所在显示器工作区，
        // 避免标题栏被顶出屏幕导致无法操作。
        if (WindowState == WindowState.Normal)
        {
            KeepWindowOnScreen();
        }
    }

    // ── 响应式布局 ────────────────────────────────────────────────

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 全屏期间尺寸被 WPF 的窗口态迁移逻辑改动（如从最大化进入时残留的 -8px 溢出
        // 尺寸），snap 回显示器完整边界。SnapToMonitorBounds 已重合则不动，不会自激。
        if (IsBorderlessActive && !_snappingFullScreen)
        {
            SnapToMonitorBounds();
        }

        if (!e.WidthChanged)
        {
            return;
        }

        // 窗口较窄时优先保证中央工作区宽度，自动收起详情面板。
        _viewModel.IsDetailPanelVisible = e.NewSize.Width >= DetailPanelBreakpoint;
    }

    // ── Tab 切换 ──────────────────────────────────────────────────

    /// <summary>
    /// Tab 头被选中。由于内容区采用「常驻可视树 + 切换可见性」，
    /// 这里只需把选中项写回 ViewModel，由它统一更新各 Tab 的 IsActive。
    /// </summary>
    private void OnTabHeaderChecked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceTabViewModel tab })
        {
            _viewModel.SelectedTab = tab;
        }
    }

    // ── 会话 Tab 右键菜单 ─────────────────────────────────────────

    /// <summary>只有会话 Tab 弹右键菜单；首页 Tab 拦掉并同步右键目标为当前会话。</summary>
    private void OnTabContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement
            { DataContext: SessionTabViewModel tab })
        {
            _viewModel.SelectedTab = tab;
            return;
        }

        e.Handled = true;
    }

    private void OnTabContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || ResolveTab(menu) is not { } tab)
        {
            return;
        }

        var sessionCount = _viewModel.Tabs.OfType<SessionTabViewModel>().Count();
        var tabIndex = _viewModel.Tabs.IndexOf(tab);
        var hasRightSession = tabIndex >= 0
            && _viewModel.Tabs
                .Skip(tabIndex + 1)
                .OfType<SessionTabViewModel>()
                .Any();

        if (menu.Items.OfType<MenuItem>().FirstOrDefault(item =>
                Equals(item.Tag, "closeOthers")) is { } closeOthers)
        {
            closeOthers.IsEnabled = sessionCount > 1;
        }

        if (menu.Items.OfType<MenuItem>().FirstOrDefault(item =>
                Equals(item.Tag, "closeRight")) is { } closeRight)
        {
            closeRight.IsEnabled = hasRightSession;
        }
    }

    private static SessionTabViewModel? ResolveTab(object sender)
    {
        var menuItem = sender as MenuItem;
        var menu = sender as ContextMenu ?? menuItem?.Parent as ContextMenu;

        return menuItem?.DataContext as SessionTabViewModel
            ?? menu?.DataContext as SessionTabViewModel
            ?? (menu?.PlacementTarget as FrameworkElement)?
                .DataContext as SessionTabViewModel;
    }

    private void OnTabReconnectClick(object sender, RoutedEventArgs e)
        => ResolveTab(sender)?.ReconnectCommand.Execute(null);

    private void OnTabCloseClick(object sender, RoutedEventArgs e)
        => ResolveTab(sender)?.CloseCommand.Execute(null);

    private void OnTabCloseOthersClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTab(sender) is { } tab)
        {
            _viewModel.CloseOtherSessionsCommand.Execute(tab);
        }
    }

    private void OnTabCloseRightClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTab(sender) is { } tab)
        {
            // fire-and-forget：关闭流程异步执行，不阻塞 UI 线程。
            // 页面 Tab 的右键菜单整体已被 OnTabContextMenuOpening 拦掉，到不了这里。
            _ = _viewModel.CloseRightSessionsAsync(tab);
        }
    }

    private void OnTabCloseAllClick(object sender, RoutedEventArgs e)
        => _viewModel.CloseAllSessionsCommand.Execute(null);

    // ── 键盘快捷键 ────────────────────────────────────────────────

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        // F11：焦点在 WPF 元素上时走这里，焦点在内嵌 HWND 里时走低级键盘钩子，
        // 两者共用同一套长按抑制，一次物理按键只进一档。
        if (e.Key == Key.F11 && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (TryHandleFullScreenHotkey(VkF11))
            {
                e.Handled = true;
                return;
            }
        }

        // 其余快捷键与远程会话内部按键存在冲突风险，只保留最必要的几个，
        // 且都带 Ctrl 修饰，避免影响远端程序的普通输入。
        if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_viewModel.SelectedTab is SessionTabViewModel session)
            {
                session.CloseCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CycleTab(forward: true);
            e.Handled = true;
        }
    }

    private void CycleTab(bool forward)
    {
        if (_viewModel.Tabs.Count <= 1 || _viewModel.SelectedTab is null)
        {
            return;
        }

        var index = _viewModel.Tabs.IndexOf(_viewModel.SelectedTab);
        var next = forward
            ? (index + 1) % _viewModel.Tabs.Count
            : (index - 1 + _viewModel.Tabs.Count) % _viewModel.Tabs.Count;

        _viewModel.SelectedTab = _viewModel.Tabs[next];
    }

    // ── 关闭行为 ──────────────────────────────────────────────────

    protected override void OnClosing(CancelEventArgs e)
    {
        // _isExiting 由托盘「退出」置位：那是用户主动点选的明确动作，不再二次追问。
        if (!_isExiting)
        {
            // 设置为「最小化到托盘」时，关闭按钮不退出应用，
            // 这样后台会话得以保留（托盘图标提供真正的退出入口）。
            if (_settings.CloseBehavior == WindowCloseBehavior.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            // 关窗即退出，而退出会连带结束全部会话。还开着会话时先问一句，
            // 免得单击窗口按钮就把正在进行的连接静默丢掉（并给一条留住会话的出路）。
            var openSessions = _viewModel.OpenSessionCount;
            if (openSessions > 0)
            {
                var choice = MessageDialog.ShowChoice(
                    this,
                    "退出 RemoteFlow？",
                    $"当前还开着 {openSessions} 个会话。退出会关闭全部会话，并断开与远端的连接。\n" +
                    "若想保留会话，请选择「最小化到托盘」——应用会继续在后台运行。",
                    "退出",
                    "最小化到托盘",
                    isDanger: true);

                switch (choice)
                {
                    case MessageDialogChoice.Cancel:
                        e.Cancel = true;
                        return;

                    case MessageDialogChoice.Alternative:
                        e.Cancel = true;
                        Hide();
                        return;
                }
            }
        }

        RemoveKeyboardHook();

        base.OnClosing(e);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint GetModuleHandle(string? moduleName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc proc, nint hMod, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(nint hook);

        [DllImport("user32.dll")]
        public static extern nint CallNextHookEx(nint hook, int nCode, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        public static extern nint GetForegroundWindow();

        // ── 无边框全屏定位（物理像素，绕过 WPF 的 DIU 往返）──────────

        public const uint SwpNoZOrder = 0x0004;
        public const uint SwpNoActivate = 0x0010;
        public const uint SwpFrameChanged = 0x0020;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(
            nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(nint hWnd, out RECT rect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
