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

    /// <summary>进入全屏前的窗口状态，退出全屏时还原。</summary>
    private (WindowStyle Style, WindowState State, ResizeMode Resize)? _preFullScreen;

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

        // 会话工具条的「全屏」按钮与 F11 都只翻转 IsSessionFullScreen，
        // 真正的窗口去边框铺满在这里响应。
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 关闭会话是一次打断性的上下文切换（跳到另一个会话或回工作区）——退出全屏
        // 回到常驻条，让用户看清被切到哪。主动切换会话（不涉及移除）则保持全屏。
        ((INotifyCollectionChanged)viewModel.Tabs).CollectionChanged += OnTabsCollectionChanged;

        SizeChanged += OnWindowSizeChanged;
        StateChanged += OnWindowStateChanged;

        // RDP / VNC 会话内嵌原生 HWND，键盘焦点在里面时会把普通按键（含 F11）
        // 直接转发给远端，WPF 的 OnPreviewKeyDown 与线程消息预处理都拦不到。
        // 用低级键盘钩子在系统层拦 F11 / Esc，仅当本窗口为前台且正显示会话时消费。
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
    private const int WmSysKeyDown = 0x0104;
    private const int VkF11 = 0x7A;
    private const int VkEscape = 0x1B;

    /// <summary>上次处理全屏热键的时刻，用于去抖——同一次物理按键可能被多次投递。</summary>
    private DateTime _lastHotkeyAt = DateTime.MinValue;

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
        if (nCode != HcAction || (wParam != WmKeyDown && wParam != WmSysKeyDown))
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        var vkCode = Marshal.ReadInt32(lParam);
        if (vkCode != VkF11 && vkCode != VkEscape)
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        // 仅当本窗口为前台、且当前是会话 Tab 时才拦截；否则放行给远端 / 其他应用。
        var consume = NativeMethods.GetForegroundWindow() == _hwnd
                      && _viewModel.SelectedTab is SessionTabViewModel
                      && (vkCode == VkF11 || _viewModel.IsSessionFullScreen);

        if (!consume)
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        // 钩子回调必须快速返回，切回 UI 线程再翻转状态。
        Dispatcher.BeginInvoke(new Action(() => TryHandleFullScreenHotkey(vkCode)));
        return 1;
    }

    /// <summary>
    /// 处理会话全屏热键。仅当前选中的是会话 Tab 时响应。<see cref="OnPreviewKeyDown"/>
    /// （焦点在 WPF 元素上时）与低级键盘钩子（焦点在内嵌 HWND 里时）共用此方法，
    /// 靠 <see cref="_lastHotkeyAt"/> 去抖，一次按键只翻转一次。
    /// <returns>已消费该按键返回 true。</returns>
    /// </summary>
    private bool TryHandleFullScreenHotkey(int virtualKey)
    {
        if (virtualKey != VkF11 && virtualKey != VkEscape)
        {
            return false;
        }

        if (_viewModel.SelectedTab is not SessionTabViewModel)
        {
            return false;
        }

        // 去抖：同一次物理按键可能被预处理链多次 peek，或同时被两个入口看到。
        var now = DateTime.UtcNow;
        if ((now - _lastHotkeyAt).TotalMilliseconds < 300)
        {
            return true;
        }

        if (virtualKey == VkF11)
        {
            _lastHotkeyAt = now;
            _viewModel.IsSessionFullScreen = !_viewModel.IsSessionFullScreen;
            return true;
        }

        // Esc 仅在全屏时用于退出；其余情况放行给会话。
        if (_viewModel.IsSessionFullScreen)
        {
            _lastHotkeyAt = now;
            _viewModel.IsSessionFullScreen = false;
            return true;
        }

        return false;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSessionFullScreen))
        {
            ApplyFullScreen(_viewModel.IsSessionFullScreen);
        }
    }

    /// <summary>
    /// Tab 集合变化。有会话 Tab 被<b>移除</b>（= 关闭会话）且当前处于全屏时，退出全屏。
    /// 关闭当前会话后 MainViewModel 会自动选中另一个会话或回工作区——这次上下文切换
    /// 应当在常驻条模式下发生，让用户看清自己被切到哪。主动切换会话不移除 Tab，不受影响。
    /// </summary>
    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Remove
            && e.OldItems?.OfType<SessionTabViewModel>().Any() == true
            && _viewModel.IsSessionFullScreen)
        {
            _viewModel.IsSessionFullScreen = false;
        }
    }

    /// <summary>进入全屏前的窗口位置与尺寸，退出时精确还原。</summary>
    private (double Left, double Top, double Width, double Height)? _preFullScreenBounds;

    /// <summary>正在以代码把全屏窗口按回显示器边界，抑制 <see cref="Window.LocationChanged"/> 递归。</summary>
    private bool _snappingFullScreen;

    /// <summary>
    /// 进入 / 退出无边框全屏。
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
    /// <para>左侧导航、右侧详情、应用标题栏由 XAML 绑定 IsSessionFullScreen 收起；
    /// 会话工具条（含「退出全屏」按钮）始终保留，是全屏下退出的可靠入口。</para>
    /// </summary>
    private void ApplyFullScreen(bool fullScreen)
    {
        if (fullScreen)
        {
            if (_preFullScreen is not null)
            {
                return;
            }

            _preFullScreen = (WindowStyle, WindowState, ResizeMode);
            _preFullScreenBounds = (Left, Top, Width, Height);

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
        else
        {
            LocationChanged -= OnFullScreenWindowMoved;

            if (_preFullScreen is not { } previous)
            {
                return;
            }

            WindowStyle = previous.Style;
            ResizeMode = previous.Resize;

            if (_preFullScreenBounds is { } bounds && previous.State != WindowState.Maximized)
            {
                Left = bounds.Left;
                Top = bounds.Top;
                Width = bounds.Width;
                Height = bounds.Height;
            }

            WindowState = previous.State;
            _preFullScreen = null;
            _preFullScreenBounds = null;
        }

        UpdateRootPadding();
    }

    /// <summary>全屏期间窗口被 WPF 重排 / 显示器切换挪离边界时，snap 回当前显示器完整边界。</summary>
    private void OnFullScreenWindowMoved(object? sender, EventArgs e)
    {
        if (_preFullScreen is not null && !_snappingFullScreen)
        {
            SnapToMonitorBounds();
        }
    }

    /// <summary>把窗口按当前所在显示器的完整物理边界（rcMonitor）严格铺满。已重合则不动。</summary>
    private void SnapToMonitorBounds()
    {
        if (_preFullScreen is null)
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
        var fullScreen = _preFullScreen is not null;
        var maximized = WindowState == WindowState.Maximized;

        RootPadding.Padding = maximized && !fullScreen ? new Thickness(8) : new Thickness(0);
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
        if (WindowState != WindowState.Normal || _preFullScreen is not null)
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
        if (WindowState != WindowState.Normal || _preFullScreen is not null)
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
        if (_preFullScreen is not null)
        {
            return; // 会话全屏（无边框）不需要系统菜单。
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
        if (_preFullScreen is not null && !_snappingFullScreen)
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

    /// <summary>只有会话 Tab 弹右键菜单；首页 Tab 拦掉。</summary>
    private void OnTabContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SessionTabViewModel })
        {
            e.Handled = true;
        }
    }

    private static SessionTabViewModel? ResolveTab(object sender)
        => (sender as MenuItem)?.DataContext as SessionTabViewModel;

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

        // F11 / Esc：焦点在 WPF 元素上时走这里，焦点在内嵌 HWND 里时走
        // OnThreadPreprocessMessage，两者共用去抖，一次按键只翻转一次。
        if ((e.Key == Key.F11 || e.Key == Key.Escape)
            && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (TryHandleFullScreenHotkey(e.Key == Key.F11 ? VkF11 : VkEscape))
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
        // 设置为「最小化到托盘」时，关闭按钮不退出应用，
        // 这样后台会话得以保留（托盘图标提供真正的退出入口）。
        if (!_isExiting && _settings.CloseBehavior == WindowCloseBehavior.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
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
