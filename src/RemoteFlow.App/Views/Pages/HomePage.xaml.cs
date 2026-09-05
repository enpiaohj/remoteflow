using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using RemoteFlow.App.ViewModels;

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
        if (DataContext is HomePageViewModel { ShowHomeTimeEnabled: true } && !_clockTimer.IsEnabled)
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

    private void OnItemConnectClick(object sender, RoutedEventArgs e)
    {
        if (Resolve(sender) is { } item)
        {
            ViewModel?.ConnectCommand.Execute(item);
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

    private void OnItemManageClick(object sender, RoutedEventArgs e)
        => ViewModel?.ViewAllFavoritesCommand.Execute(null);

    /// <summary>菜单项的 DataContext 就是所在行 / 卡片的 VM。</summary>
    private static ConnectionItemViewModel? Resolve(object sender)
        => (sender as MenuItem)?.DataContext as ConnectionItemViewModel;
}
