using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using RemoteFlow.Presentation.ViewModels;
using RemoteFlow.App.Converters;

namespace RemoteFlow.App.Views.Sessions;

/// <summary>
/// 紧凑模式的「当前会话」选择器。
/// <para>
/// 会话达到 <see cref="SessionTabLayoutPolicy.CompactSessionThreshold"/> 个或标题栏放不下时，
/// 由 <see cref="MainWindow"/> 把 Tab 列表切换为本控件：收起态显示当前会话（或「会话（N）」），
/// 展开态用 Popup 列出全部会话（按打开顺序）。单击行切换、行尾按钮关闭、右键复用
/// 主窗口共享的会话菜单；↑/↓ 移动高亮、Enter 切换、Delete 关闭、Esc 收起。
/// 只改 <see cref="MainViewModel.SelectedTab"/>，不创建 / 销毁会话。
/// </para>
/// </summary>
public sealed partial class SessionTabSelector : UserControl
{
    /// <summary>会话列表镜像：与 <see cref="MainViewModel.Tabs"/> 中的会话保持同序（打开顺序）。</summary>
    private readonly ObservableCollectionCore _sessionTabs = new();

    private MainViewModel? _main;
    private INotifyCollectionChanged? _subscribedTabs;

    /// <summary>正在以代码同步 ListBox 选中项，抑制「用户选择」分支。</summary>
    private bool _syncingSelection;

    /// <summary>正在以代码移动高亮（键盘导航），抑制立即切换会话。</summary>
    private bool _keyboardNavigating;

    /// <summary>收起态头部当前展示的会话（用于订阅其状态刷新）。</summary>
    private SessionTabViewModel? _headerTab;

    public SessionTabSelector()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
        SessionsList.ItemsSource = _sessionTabs;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_main is not null)
        {
            _main.PropertyChanged -= OnMainPropertyChanged;
        }

        _main = e.NewValue as MainViewModel;

        if (_subscribedTabs is not null)
        {
            _subscribedTabs.CollectionChanged -= OnTabsCollectionChanged;
            _subscribedTabs = null;
        }

        if (_main is null)
        {
            return;
        }

        _main.PropertyChanged += OnMainPropertyChanged;

        if (_main.Tabs is INotifyCollectionChanged changed)
        {
            _subscribedTabs = changed;
            changed.CollectionChanged += OnTabsCollectionChanged;
        }

        RebuildSessionTabs();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_main is not null)
        {
            _main.PropertyChanged -= OnMainPropertyChanged;
        }

        if (_subscribedTabs is not null)
        {
            _subscribedTabs.CollectionChanged -= OnTabsCollectionChanged;
        }

        DetachHeaderTab();
        SelectorPopup.IsOpen = false;
    }

    // ── 会话列表同步 ─────────────────────────────────────────────

    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RebuildSessionTabs();

    private void RebuildSessionTabs()
    {
        if (_main is null)
        {
            return;
        }

        _sessionTabs.Reset(_main.Tabs.OfType<SessionTabViewModel>());
        RefreshHeader();
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedTab))
        {
            // 弹出层开着时同步高亮；收起态刷新头部。
            if (SelectorPopup.IsOpen)
            {
                SyncListSelection();
            }

            RefreshHeader();
        }
    }

    // ── 收起态头部 ───────────────────────────────────────────────

    private void RefreshHeader()
    {
        DetachHeaderTab();

        if (_main?.SelectedTab is SessionTabViewModel session && _sessionTabs.Contains(session))
        {
            _headerTab = session;
            session.PropertyChanged += OnHeaderTabPropertyChanged;
            ShowSessionHeader(session);
        }
        else
        {
            ShowWorkspaceHeader();
        }
    }

    private void DetachHeaderTab()
    {
        if (_headerTab is not null)
        {
            _headerTab.PropertyChanged -= OnHeaderTabPropertyChanged;
            _headerTab = null;
        }
    }

    private void OnHeaderTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceTabViewModel.Title)
            or nameof(SessionTabViewModel.StateText)
            or nameof(SessionTabViewModel.StateBrushKey))
        {
            RefreshHeader();
        }
    }

    private void ShowSessionHeader(SessionTabViewModel session)
    {
        HeaderIcon.Data = UiIconResources.Find(UiIconResources.ProtocolKey(session.Protocol));
        HeaderTitle.Text = session.Title;
        SelectorToggle.ToolTip = $"{session.StateText} · {session.Title}";
        if (TryFindResource(session.StateBrushKey) is Brush brush)
        {
            HeaderDot.Fill = brush;
        }

        HeaderDot.Visibility = Visibility.Visible;
    }

    private void ShowWorkspaceHeader()
    {
        HeaderIcon.Data = UiIconResources.Find("Ui.Sessions");
        HeaderTitle.Text = $"活动连接（{_sessionTabs.Count}）";
        SelectorToggle.ToolTip = "选择会话";
        HeaderDot.Visibility = Visibility.Collapsed;
    }

    // ── 展开与切换 ───────────────────────────────────────────────

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (SelectorPopup.IsOpen)
        {
            SelectorPopup.IsOpen = false;
            return;
        }

        _syncingSelection = true;
        try
        {
            SessionsList.SelectedItem = _main?.SelectedTab as SessionTabViewModel;
        }
        finally
        {
            _syncingSelection = false;
        }

        SelectorPopup.IsOpen = true;
        SelectorToggle.IsChecked = true;

        // 等列表布局完成后再把当前会话滚入可视区并交给键盘。
        Dispatcher.BeginInvoke(BringCurrentIntoView, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void BringCurrentIntoView()
    {
        if (SessionsList.SelectedItem is { } current
            && SessionsList.ItemContainerGenerator.ContainerFromItem(current) is FrameworkElement container)
        {
            container.BringIntoView();
            container.Focus();
        }
        else
        {
            SessionsList.Focus();
        }
    }

    private void OnSessionsListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || _keyboardNavigating)
        {
            return;
        }

        // 鼠标单击行 = 切换到该会话并收起。
        if (SessionsList.SelectedItem is SessionTabViewModel tab)
        {
            Activate(tab);
        }
    }

    private void Activate(SessionTabViewModel tab)
    {
        SelectorPopup.IsOpen = false;
        if (_main is not null)
        {
            _main.SelectedTab = tab;
        }
    }

    private void OnRowCloseClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: SessionTabViewModel tab })
        {
            tab.CloseCommand.Execute(null);
        }
    }

    // ── 键盘导航 ─────────────────────────────────────────────────

    private void OnSessionsListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
            case Key.Down:
                MoveHighlight(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;

            case Key.Enter:
                if (SessionsList.SelectedItem is SessionTabViewModel tab)
                {
                    Activate(tab);
                }

                e.Handled = true;
                break;

            case Key.Delete:
                if (SessionsList.SelectedItem is SessionTabViewModel closable
                    && closable.CanClose)
                {
                    closable.CloseCommand.Execute(null);
                }

                e.Handled = true;
                break;

            case Key.Escape:
                SelectorPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void MoveHighlight(int delta)
    {
        if (_sessionTabs.Count == 0)
        {
            return;
        }

        var index = SessionsList.SelectedIndex;
        if (index < 0)
        {
            index = delta > 0 ? 0 : _sessionTabs.Count - 1;
        }
        else
        {
            index = Math.Clamp(index + delta, 0, _sessionTabs.Count - 1);
        }

        _keyboardNavigating = true;
        try
        {
            SessionsList.SelectedIndex = index;
            if (SessionsList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
            {
                container.BringIntoView();
                container.Focus();
            }
        }
        finally
        {
            _keyboardNavigating = false;
        }
    }

    private void SyncListSelection()
    {
        _syncingSelection = true;
        try
        {
            SessionsList.SelectedItem = _main?.SelectedTab as SessionTabViewModel;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    /// <summary>收起态勾选样式随 Popup 开关，避免展开后按钮仍呈「选中」底色。</summary>
    private void OnPopupClosed(object sender, EventArgs e) => SelectorToggle.IsChecked = false;

    /// <summary>仅服务本控件的会话镜像集合：Reset 与单个增删都触发 Reset 通知。</summary>
    private sealed class ObservableCollectionCore : System.Collections.ObjectModel.ObservableCollection<SessionTabViewModel>
    {
        public void Reset(IEnumerable<SessionTabViewModel> items)
        {
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(item);
            }

            OnPropertyChanged(new PropertyChangedEventArgs("Count"));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
