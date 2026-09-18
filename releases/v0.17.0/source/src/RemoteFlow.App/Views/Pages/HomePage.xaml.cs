using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Views.Pages;

/// <summary>
/// 首页。只呈现最近连接、收藏与最近活动，帮助用户尽快回到工作状态。
/// </summary>
public partial class HomePage : UserControl
{
    /// <summary>开启「首页显示时间」时，以秒级刷新标题行的时钟。页面不可见时停止。</summary>
    private readonly DispatcherTimer _clockTimer = new();

    public HomePage()
    {
        InitializeComponent();
        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.Tick += OnClockTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private HomePageViewModel? ViewModel => DataContext as HomePageViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 页面经导航切回可视区时触发。仅当用户开启「显示时间」才计时，
        // 否则标题行是静态日期，无需秒级刷新。
        if (DataContext is HomePageViewModel { ShowHomeClock: true } && !_clockTimer.IsEnabled)
        {
            _clockTimer.Start();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _clockTimer.Stop();

    private void OnClockTick(object? sender, EventArgs e)
    {
        if (DataContext is HomePageViewModel vm)
        {
            vm.RefreshClock();
        }
    }

    /// <summary>「⋯」按钮点击即在按钮位置弹出其上下文菜单。</summary>
    private void OnItemMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    /// <summary>
    /// 行右键菜单打开时把所在行设为选中（「最近连接 / 收藏」两列表内互斥），
    /// 让菜单动作的作用对象在视觉上明确。行模板内 ContextMenu 的 DataContext
    /// 即该行 VM；「⋯」按钮路径再退化到 PlacementTarget 兜底。
    /// </summary>
    private void OnHomeRowMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        var item = menu.DataContext as ConnectionItemViewModel
            ?? (menu.PlacementTarget as FrameworkElement)?.DataContext as ConnectionItemViewModel;

        // 主操作按行会话状态区分：任意活动（连接中 / 已连 / 失败未清）→ 切换到会话 + 断开连接；
        // 无活动 → 连接。与「我的连接」行菜单同一套约定。
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Tag as string) == HomeRowActions.Connect) is { } connect)
        {
            connect.Header = item?.HasActiveSession == true ? "切换到会话" : "连接";
        }

        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Tag as string) == HomeRowActions.Disconnect) is { } disconnect)
        {
            disconnect.Visibility = item?.HasActiveSession == true ? Visibility.Visible : Visibility.Collapsed;
        }

        // 收藏项 Header 随行当前收藏状态切换：已收藏→取消收藏，未收藏→收藏。
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Tag as string) == HomeRowActions.Favorite) is { } favorite)
        {
            favorite.Header = item?.IsFavorite == true ? "取消收藏" : "收藏";
        }

        ViewModel?.SelectInContext(item);
    }

    /// <summary>
    /// 最近连接 / 收藏行右键菜单的统一入口：把动作名（MenuItem.Tag）与所在行一起抛给
    /// HomePageViewModel.ConnectionActionRequested，由 MainViewModel 桥接到「我的连接」既有命令。
    /// </summary>
    private void OnRowActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string action } menu
            && menu.DataContext is ConnectionItemViewModel item)
        {
            ViewModel?.RequestConnectionAction(item, action);
        }
    }

    /// <summary>收藏行：单击选中（高亮），双击发起连接。单击不连以免误触。</summary>
    private void OnFavoriteRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if ((sender as FrameworkElement)?.DataContext is not ConnectionItemViewModel item)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            ViewModel?.ConnectCommand.Execute(item);
        }
        else
        {
            ViewModel?.SelectFavorite(item);
        }
    }
}
