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
/// 由 <see cref="MainWindow"/> 把 Tab 列表切换为本控件：收起态显示当前 Tab
/// （会话 / 文件传输 / 「活动连接」）并在会话数 ≥ 2 时于图标右上角挂数字角标，
/// 展开态用 Popup 按打开顺序列出全部可关闭 Tab。单击行切换、行尾按钮关闭、右键复用
/// 主窗口共享的会话菜单；↑/↓ 移动高亮、Enter 切换、Delete 关闭、Esc 收起。
/// 只改 <see cref="MainViewModel.SelectedTab"/>，不创建 / 销毁会话。
/// </para>
/// </summary>
public sealed partial class SessionTabSelector : UserControl
{
    /// <summary>
    /// 列表镜像：<see cref="MainViewModel.Tabs"/> 中可关闭的工作区 Tab（会话 + 文件传输），
    /// 保持打开顺序。页面 Tab 不在此列——它固定存在且不可关闭，常驻紧凑条左侧。
    /// </summary>
    private readonly ObservableCollectionCore _listedTabs = new();

    private MainViewModel? _main;
    private INotifyCollectionChanged? _subscribedTabs;

    /// <summary>正在以代码同步 ListBox 选中项，抑制「用户选择」分支。</summary>
    private bool _syncingSelection;

    /// <summary>正在以代码移动高亮（键盘导航），抑制立即切换会话。</summary>
    private bool _keyboardNavigating;

    /// <summary>收起态头部当前展示的会话（用于订阅其状态刷新）。</summary>
    private SessionTabViewModel? _headerTab;

    /// <summary>收起态头部当前展示的文件传输 Tab（用于订阅其标题刷新）。</summary>
    private FileTransferTabViewModel? _headerTransferTab;

    /// <summary>会话数角标显示上限：超过即显示「99+」，避免数字撑宽头部。</summary>
    private const int CountBadgeMax = 99;

    public SessionTabSelector()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
        SessionsList.ItemsSource = _listedTabs;
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

        // 会话 + 文件传输：两者都可关闭，因此都进列表；页面 Tab 固定常驻紧凑条左侧，不在此列。
        _listedTabs.Reset(_main.Tabs.Where(static t => t is SessionTabViewModel or FileTransferTabViewModel));
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

        if (_main?.SelectedTab is SessionTabViewModel session && _listedTabs.Contains(session))
        {
            _headerTab = session;
            session.PropertyChanged += OnHeaderTabPropertyChanged;
            ShowSessionHeader(session);
        }
        else if (_main?.SelectedTab is FileTransferTabViewModel transfer && _listedTabs.Contains(transfer))
        {
            _headerTransferTab = transfer;
            transfer.PropertyChanged += OnHeaderTabPropertyChanged;
            ShowTransferHeader(transfer);
        }
        else
        {
            ShowWorkspaceHeader();
        }

        UpdateCountBadge();
    }

    private void DetachHeaderTab()
    {
        if (_headerTab is not null)
        {
            _headerTab.PropertyChanged -= OnHeaderTabPropertyChanged;
            _headerTab = null;
        }

        if (_headerTransferTab is not null)
        {
            _headerTransferTab.PropertyChanged -= OnHeaderTabPropertyChanged;
            _headerTransferTab = null;
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
        // 头部按钮服务会话与文件传输两种 Tab，提示语必须跟着切换，
        // 否则文件传输 Tab 上会写着「关闭会话」。
        HeaderClose.ToolTip = "关闭会话 (Ctrl+W)";
        HeaderClose.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 文件传输 Tab 的收起态头部。它不是会话，因此没有连接状态点，但同样可关闭。
    /// 计数角标仍只算会话数，不含本 Tab（口径与 <see cref="MainViewModel.OpenSessionCount"/> 一致）。
    /// </summary>
    private void ShowTransferHeader(FileTransferTabViewModel transfer)
    {
        HeaderIcon.Data = UiIconResources.Find("Ui.FileTransfer");
        HeaderTitle.Text = transfer.Title;
        SelectorToggle.ToolTip = transfer.TabToolTip;
        HeaderDot.Visibility = Visibility.Collapsed;
        HeaderClose.ToolTip = "关闭文件传输 (Ctrl+W)";
        HeaderClose.Visibility = Visibility.Visible;
    }

    private void ShowWorkspaceHeader()
    {
        HeaderIcon.Data = UiIconResources.Find("Ui.Sessions");
        HeaderTitle.Text = "活动连接";
        SelectorToggle.ToolTip = "选择会话";
        HeaderDot.Visibility = Visibility.Collapsed;
        // 工作台 Tab 不可关闭（PageTabViewModel.CanClose = false）。
        HeaderClose.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 会话数角标：≥2 才显示（「1」没有信息量），上限 99。
    /// 角标排在图标右侧、与标题同处一个有宽度上限的 Grid（见 XAML）：
    /// 角标一出现，标题所在的星号列自动收缩并省略，不必在此换算标题上限。
    /// <para>
    /// 计数口径 = 会话 Tab 数，<b>不含</b>文件传输 Tab，与
    /// <see cref="MainViewModel.OpenSessionCount"/> 及底部状态栏的「已连接 N 个会话」口径分开，
    /// 不引入第三种计数。
    /// </para>
    /// </summary>
    private void UpdateCountBadge()
    {
        var count = _listedTabs.Count(static t => t is SessionTabViewModel);
        if (count < 2)
        {
            HeaderCountBadge.Visibility = Visibility.Collapsed;
            return;
        }

        HeaderCountText.Text = count > CountBadgeMax
            ? $"{CountBadgeMax}+"
            : count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        HeaderCountBadge.ToolTip = $"共 {count} 个会话";
        HeaderCountBadge.Visibility = Visibility.Visible;
    }

    // ── 展开与切换 ───────────────────────────────────────────────

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (SelectorPopup.IsOpen)
        {
            SelectorPopup.IsOpen = false;
            return;
        }

        SyncListSelection();

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

        // 鼠标单击行 = 切换到该 Tab 并收起。
        if (SessionsList.SelectedItem is WorkspaceTabViewModel tab)
        {
            Activate(tab);
        }
    }

    private void Activate(WorkspaceTabViewModel tab)
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

        // 行模板同时服务会话行与文件传输行，按实际 DataContext 取关闭命令。
        var command = (sender as FrameworkElement)?.DataContext switch
        {
            SessionTabViewModel session => session.CloseCommand,
            FileTransferTabViewModel transfer => transfer.CloseCommand,
            _ => null
        };

        if (command is not null && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    /// <summary>
    /// 收起态头部的关闭按钮：关闭当前头部展示的 Tab（会话或文件传输）。
    /// <para>
    /// 必须 <c>e.Handled = true</c>：头部整块是 <see cref="ToggleButton"/>，
    /// 不拦截的话点击会冒泡上去，顺带把会话列表 Popup 打开（普通 Tab 的 × 无此问题，
    /// 因为那里的宿主是 IsChecked 由绑定驱动的 RadioButton）。
    /// </para>
    /// </summary>
    private void OnHeaderCloseClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        // 二者互斥：RefreshHeader 每次只挂一个。
        var command = _headerTab?.CloseCommand ?? (ICommand?)_headerTransferTab?.CloseCommand;
        if (command is not null && command.CanExecute(null))
        {
            command.Execute(null);
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
                if (SessionsList.SelectedItem is WorkspaceTabViewModel tab)
                {
                    Activate(tab);
                }

                e.Handled = true;
                break;

            case Key.Delete:
                if (SelectedCloseCommand() is { } deleteCommand && deleteCommand.CanExecute(null))
                {
                    deleteCommand.Execute(null);
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
        if (_listedTabs.Count == 0)
        {
            return;
        }

        var index = SessionsList.SelectedIndex;
        if (index < 0)
        {
            index = delta > 0 ? 0 : _listedTabs.Count - 1;
        }
        else
        {
            index = Math.Clamp(index + delta, 0, _listedTabs.Count - 1);
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

    /// <summary>列表当前高亮项的关闭命令（会话 / 文件传输），供 Delete 键使用。</summary>
    private ICommand? SelectedCloseCommand() => SessionsList.SelectedItem switch
    {
        SessionTabViewModel session when session.CanClose => session.CloseCommand,
        FileTransferTabViewModel transfer when transfer.CanClose => transfer.CloseCommand,
        _ => null
    };

    private void SyncListSelection()
    {
        _syncingSelection = true;
        try
        {
            // 选中的 Tab 不在列表里（页面 Tab）时不强行选中，避免高亮乱跳。
            SessionsList.SelectedItem = _main?.SelectedTab is { } selected && _listedTabs.Contains(selected)
                ? selected
                : null;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    /// <summary>收起态勾选样式随 Popup 开关，避免展开后按钮仍呈「选中」底色。</summary>
    private void OnPopupClosed(object sender, EventArgs e) => SelectorToggle.IsChecked = false;

    /// <summary>仅服务本控件的列表镜像集合：Reset 与单个增删都触发 Reset 通知。</summary>
    private sealed class ObservableCollectionCore : System.Collections.ObjectModel.ObservableCollection<WorkspaceTabViewModel>
    {
        public void Reset(IEnumerable<WorkspaceTabViewModel> items)
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
