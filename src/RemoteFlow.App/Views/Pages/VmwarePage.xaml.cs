using System.Windows;
using System.Windows.Controls;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Views.Pages;

/// <summary>「虚拟化」页：只做视图与菜单承载，逻辑全部在 <see cref="VmwarePageViewModel"/>。</summary>
public partial class VmwarePage : UserControl
{
    public VmwarePage()
    {
        InitializeComponent();
    }

    private VmwarePageViewModel? ViewModel => DataContext as VmwarePageViewModel;

    private static VmwareVmItemViewModel? ItemOf(object sender)
        => (sender as FrameworkElement)?.DataContext as VmwareVmItemViewModel;

    private void OnConsoleClick(object sender, RoutedEventArgs e)
        => ViewModel?.OpenConsoleCommand.Execute(ItemOf(sender));

    private void OnSnapshotsClick(object sender, RoutedEventArgs e)
        => ViewModel?.SnapshotsCommand.Execute(ItemOf(sender));

    /// <summary>双击行打开控制台（只响应行本身，不响应行内按钮的双击）。</summary>
    private void OnVmDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null) return;
        if (VmList.SelectedItem is VmwareVmItemViewModel item) ViewModel?.OpenConsoleCommand.Execute(item);
    }

    private void OnPowerButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var menu = (ContextMenu)FindResource("PowerMenu");
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>按虚拟机当前状态启用菜单项：关机 / 挂起时只能开机，运行中才能关机、重启、挂起、强制操作。</summary>
    private void OnPowerMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || (menu.PlacementTarget as FrameworkElement)?.DataContext is not VmwareVmItemViewModel item) return;

        foreach (var entry in menu.Items.OfType<MenuItem>())
        {
            entry.IsEnabled = !item.IsBusy && entry.Tag switch
            {
                "poweron" => item.CanPowerOn,
                _ => item.CanShutdown
            };
        }

        ContextMenuSeparators.Normalize(menu);
    }

    private void OnPowerMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem entry || ViewModel is not { } vm) return;
        if (FindAncestor<System.Windows.Controls.ContextMenu>(entry)?.PlacementTarget is not FrameworkElement target
            || target.DataContext is not VmwareVmItemViewModel item) return;

        switch (entry.Tag)
        {
            case "poweron": vm.PowerOnCommand.Execute(item); break;
            case "shutdown": vm.ShutdownCommand.Execute(item); break;
            case "reboot": vm.RebootCommand.Execute(item); break;
            case "suspend": vm.SuspendCommand.Execute(item); break;
            case "poweroff": vm.PowerOffCommand.Execute(item); break;
            case "reset": vm.ResetCommand.Execute(item); break;
        }
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var node = start; node is not null; node = LogicalOrVisualParent(node))
        {
            if (node is T match) return match;
        }

        return null;
    }

    private static DependencyObject? LogicalOrVisualParent(DependencyObject node)
        => node is MenuItem or System.Windows.Controls.ContextMenu
            ? LogicalTreeHelper.GetParent(node)
            : System.Windows.Media.VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);
}
