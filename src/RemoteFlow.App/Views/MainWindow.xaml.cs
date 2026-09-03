using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    public MainWindow(MainViewModel viewModel, AppSettings settings)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;

        viewModel.FocusSearchRequested += (_, _) =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        };

        SizeChanged += OnWindowSizeChanged;
        StateChanged += OnWindowStateChanged;
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
        else if (e.Key == Key.Escape && _viewModel.IsSessionFullScreen)
        {
            _viewModel.IsSessionFullScreen = false;
            WindowState = WindowState.Normal;
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

        base.OnClosing(e);
    }
}
