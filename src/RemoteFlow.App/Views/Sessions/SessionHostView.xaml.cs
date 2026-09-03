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
using RemoteFlow.App.ViewModels;
using RemoteFlow.Core.Models;

namespace RemoteFlow.App.Views.Sessions;

/// <summary>
/// 会话 Tab 的外壳：工具条 + 协议视图 + 状态层。
/// <para>
/// 非全屏：顶部常驻一条工具条（<c>DockedBar</c>），随窗口标题栏 / Tab 栏一起使用。
/// 全屏：标题栏与 Tab 栏都隐藏了，工具条改为一条<b>悬浮药丸</b>（<c>ToolbarPopup</c>），
/// 承担会话切换、最小化 / 关闭 / 退出全屏；默认自动隐藏——离开 2.5s 后淡出，
/// 鼠标移到屏幕顶沿再淡入，可固定常驻，可拖动到任意位置。
/// </para>
/// <para>
/// 药丸用 <see cref="Popup"/> 承载：RDP 会话用
/// <see cref="System.Windows.Forms.Integration.WindowsFormsHost"/> 承载原生 ActiveX，
/// 普通 WPF 浮层会被这块 airspace 盖住、也收不到其上的鼠标事件。Popup 是独立顶层
/// 窗口，能盖在原生画面之上（mstsc 连接条同理）。因收不到 airspace 上的 WPF 鼠标
/// 事件，顶沿唤出改用 <see cref="_edgeWatch"/> 轮询光标位置实现。
/// </para>
/// </summary>
public partial class SessionHostView : UserControl
{
    /// <summary>未固定时，鼠标离开多久后收起药丸。</summary>
    private static readonly TimeSpan AutoHideDelay = TimeSpan.FromSeconds(2.5);

    /// <summary>光标顶沿轮询间隔。</summary>
    private static readonly TimeSpan EdgeWatchInterval = TimeSpan.FromMilliseconds(90);

    /// <summary>刚唤出后的最短驻留时间，避免鼠标掠过顶沿时一闪而过。</summary>
    private static readonly TimeSpan MinVisibleTime = TimeSpan.FromMilliseconds(600);

    /// <summary>未展开时，认定“鼠标贴到顶沿”的判定高度（DIU）。</summary>
    private const double EdgeRevealBand = 4;

    /// <summary>拖动后药丸至少保留多少像素在可视区内。</summary>
    private const double MinVisibleExtent = 96;

    private FrameworkElement? _protocolView;
    private SessionTabViewModel? _tab;
    private MainViewModel? _main;
    private Window? _window;

    private readonly DispatcherTimer _autoHideTimer;
    private readonly DispatcherTimer _edgeWatch;

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

    private DateTime _shownAt = DateTime.MinValue;

    public SessionHostView()
    {
        InitializeComponent();

        _autoHideTimer = new DispatcherTimer { Interval = AutoHideDelay };
        _autoHideTimer.Tick += OnAutoHideTick;

        _edgeWatch = new DispatcherTimer { Interval = EdgeWatchInterval };
        _edgeWatch.Tick += OnEdgeWatchTick;

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => RepositionPopup();

        // Popup 是独立 HWND，其内部的 WPF 鼠标事件正常可用。
        PillBar.MouseEnter += (_, _) => _autoHideTimer.Stop();
        PillBar.MouseLeave += (_, _) => ScheduleAutoHide();
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

        // 视图可能是在已全屏的状态下加载的。
        ApplyFullScreenMode(_main?.IsSessionFullScreen == true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _autoHideTimer.Stop();
        _edgeWatch.Stop();
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
        if (e.PropertyName == nameof(MainViewModel.IsSessionFullScreen)
            && sender is MainViewModel main)
        {
            ApplyFullScreenMode(main.IsSessionFullScreen);
        }
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
            ScheduleAutoHide();
        }
        else
        {
            // 退出全屏：收起药丸，工具条回到常驻条。
            _edgeWatch.Stop();
            _autoHideTimer.Stop();
            ToolbarPopup.IsOpen = false;
        }
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
            ScheduleAutoHide();
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

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null || !NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        // 光标（物理像素）换算到本视图左上角为原点的坐标系（物理像素）。
        var origin = PointToScreen(new Point(0, 0));
        var toDevice = source.CompositionTarget.TransformToDevice;
        var widthPx = ActualWidth * toDevice.M11;

        var relX = cursor.X - origin.X;
        var relY = cursor.Y - origin.Y;
        var withinX = relX >= 0 && relX <= widthPx;

        // 已展开时在整条范围内都算“停留”；未展开时只认最顶几像素。
        var bandPx = (ToolbarPopup.IsOpen
            ? PillBar.ActualHeight + PillBar.Margin.Top + 6
            : EdgeRevealBand) * toDevice.M22;

        if (withinX && relY >= -2 && relY <= bandPx)
        {
            ShowToolbar();
            _autoHideTimer.Stop();
        }
        else if (ToolbarPopup.IsOpen && !PillBar.IsMouseOver && relY > bandPx)
        {
            ScheduleAutoHide();
        }
    }

    private void OnAutoHideTick(object? sender, EventArgs e)
    {
        _autoHideTimer.Stop();

        if (_pinned || _dragging || PillBar.IsMouseOver || _sessionMenu is { IsOpen: true })
        {
            return;
        }

        HideToolbar();
    }

    private void ScheduleAutoHide()
    {
        if (_pinned)
        {
            return;
        }

        _autoHideTimer.Stop();
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

    private void HideToolbar()
    {
        if (!ToolbarPopup.IsOpen || _pinned)
        {
            return;
        }

        // 连接中 / 断线时始终保留（状态信息与关闭入口重要）。
        if (_tab is { IsConnected: false })
        {
            return;
        }

        // 刚唤出的短暂驻留期内不收起。
        if (DateTime.UtcNow - _shownAt < MinVisibleTime)
        {
            ScheduleAutoHide();
            return;
        }

        ToolbarPopup.IsOpen = false;
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
        ScheduleAutoHide();
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
            var barWidth = PillBar.ActualWidth > 0 ? PillBar.ActualWidth : 320;
            h = Math.Max(0, (RootGrid.ActualWidth - barWidth) / 2);
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

    // ── 互操作 ───────────────────────────────────────────────────

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT point);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }
    }
}
