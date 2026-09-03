using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using RemoteFlow.App.ViewModels;
using RemoteFlow.Core.Models;

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

    /// <summary>是否为真正的退出（而非最小化到托盘）。</summary>
    private bool _isExiting;

    /// <summary>进入全屏前的窗口状态，退出全屏时还原。</summary>
    private (WindowStyle Style, WindowState State, ResizeMode Resize)? _preFullScreen;

    public MainWindow(MainViewModel viewModel, AppSettings settings)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _settings = settings;
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

        SizeChanged += OnWindowSizeChanged;
        StateChanged += OnWindowStateChanged;

        // RDP / VNC 会话内嵌的是原生 HWND，键盘焦点在里面时 WPF 收不到 F11/Esc。
        // 用线程级消息预处理在消息分发到子控件之前拦下这两个键。
        ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;
    }

    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkF11 = 0x7A;
    private const int VkEscape = 0x1B;

    private void OnThreadPreprocessMessage(ref MSG msg, ref bool handled)
    {
        if (handled || (msg.message != WmKeyDown && msg.message != WmSysKeyDown))
        {
            return;
        }

        // 仅当前台是本窗口、且正显示远程会话时才拦截，避免影响其他窗口与普通页面。
        if (!IsActive || _viewModel.SelectedTab is not SessionTabViewModel)
        {
            return;
        }

        var key = msg.wParam.ToInt32();

        if (key == VkF11)
        {
            _viewModel.IsSessionFullScreen = !_viewModel.IsSessionFullScreen;
            handled = true;
        }
        else if (key == VkEscape && _viewModel.IsSessionFullScreen)
        {
            _viewModel.IsSessionFullScreen = false;
            handled = true;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSessionFullScreen))
        {
            ApplyFullScreen(_viewModel.IsSessionFullScreen);
        }
    }

    /// <summary>
    /// 进入 / 退出无边框全屏。
    /// <para>
    /// 进入时移除 WindowChrome 边框并最大化——WPF 在 <see cref="WindowStyle.None"/> +
    /// <see cref="WindowState.Maximized"/> 下会铺满整个屏幕（含任务栏区域）。
    /// 左侧导航与右侧详情由 XAML 绑定 IsSessionFullScreen 自动隐藏。
    /// </para>
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

            // 先复位再最大化，否则从最大化进入全屏时尺寸不会重算。
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            if (_preFullScreen is not { } previous)
            {
                return;
            }

            WindowStyle = previous.Style;
            ResizeMode = previous.Resize;
            WindowState = previous.State;
            _preFullScreen = null;
        }

        UpdateRootPadding();
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

    // ── 标题栏按钮 ────────────────────────────────────────────────

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        // 最大化后按钮语义变为「还原」，图标需要同步切换。
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "向下还原" : "最大化";

        UpdateRootPadding();
    }

    // ── 响应式布局 ────────────────────────────────────────────────

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
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

    // ── 键盘快捷键 ────────────────────────────────────────────────

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        // 快捷键与远程会话内部按键存在冲突风险，因此只保留最必要的几个，
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
        else if (e.Key == Key.F11 && _viewModel.SelectedTab is SessionTabViewModel)
        {
            _viewModel.IsSessionFullScreen = !_viewModel.IsSessionFullScreen;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _viewModel.IsSessionFullScreen)
        {
            _viewModel.IsSessionFullScreen = false;
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

        ComponentDispatcher.ThreadPreprocessMessage -= OnThreadPreprocessMessage;

        base.OnClosing(e);
    }
}
