using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    // 仅调整视图布局：窄窗口把详情移至列表下方，操作列始终保留。
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DetailPanel is null) return;
        var compact = ActualWidth < 1120;
        SourceColumn.Width = new GridLength(compact ? 184 : 204);
        DetailColumn.Width = new GridLength(compact ? 0 : 280);
        DetailGap.Width = new GridLength(compact ? 0 : 12);
        DetailRow.Height = new GridLength(compact ? Math.Min(240, ActualHeight * 0.36) : 0);
        Grid.SetRow(DetailPanel, compact ? 1 : 0);
        Grid.SetColumn(DetailPanel, compact ? 2 : 4);
        DetailPanel.BorderThickness = compact ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
    }

    private void OnTableSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateColumns(TableHeader);
        UpdateRowColumns(VmList);
    }

    private void OnVmRowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Grid row) UpdateColumns(row);
    }

    private void UpdateRowColumns(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is Grid { ColumnDefinitions.Count: 6 } row) UpdateColumns(row);
            else UpdateRowColumns(child);
        }
    }

    private void UpdateColumns(Grid? grid)
    {
        if (grid is null || TablePanel is null) return;
        var showOs = TablePanel.ActualWidth >= 680;
        var showSpec = TablePanel.ActualWidth >= 800;
        grid.ColumnDefinitions[2].Width = showOs ? new GridLength(1.4, GridUnitType.Star) : new GridLength(0);
        grid.ColumnDefinitions[4].Width = new GridLength(showSpec ? 104 : 0);
        foreach (var child in grid.Children.OfType<UIElement>())
        {
            var column = Grid.GetColumn(child);
            if (column is 2 or 4) child.Visibility = (column == 2 ? showOs : showSpec) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static VmwareVmItemViewModel? ItemOf(object sender)
        => (sender as FrameworkElement)?.DataContext as VmwareVmItemViewModel;

    private void OnConsoleClick(object sender, RoutedEventArgs e)
        => ViewModel?.OpenConsoleCommand.Execute(ItemOf(sender));

    private void OnSnapshotsClick(object sender, RoutedEventArgs e)
        => ViewModel?.SnapshotsCommand.Execute(ItemOf(sender));

    private void OnPowerOnClick(object sender, RoutedEventArgs e)
        => ViewModel?.PowerOnCommand.Execute(ItemOf(sender));

    /// <summary>双击行打开控制台（只响应行本身，不响应行内按钮的双击）。</summary>
    private void OnVmDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null) return;
        if (VmList.SelectedItem is VmwareVmItemViewModel { CanOpenConsole: true } item) ViewModel?.OpenConsoleCommand.Execute(item);
    }

    /// <summary>右键先选中被点的那一行，菜单和右侧详情才会对着同一台虚拟机。</summary>
    private void OnVmListRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<ListBoxItem>(source) is { } row)
        {
            row.IsSelected = true;
        }
    }

    // ── 菜单弹出 ──────────────────────────────────────────────────

    private void OnPowerButtonClick(object sender, RoutedEventArgs e) => ShowMenuBelow(sender, "PowerMenu");

    private void OnMoreButtonClick(object sender, RoutedEventArgs e) => ShowMenuBelow(sender, "VmRowMenu");

    private void ShowMenuBelow(object sender, string resourceKey)
    {
        if (sender is not Button button) return;
        var menu = (ContextMenu)FindResource(resourceKey);
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ── 菜单状态 ──────────────────────────────────────────────────

    /// <summary>电源按钮的菜单：关机 / 挂起时只能开机，运行中才能关机、重启、挂起、强制操作。</summary>
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

    /// <summary>
    /// 行右键菜单：不适用的动作直接隐藏（关机的虚拟机没有「打开控制台」和「关机」），
    /// 分隔线由 <see cref="ContextMenuSeparators"/> 统一整理；忙碌中的虚拟机整体禁用，避免重复提交。
    /// </summary>
    private void OnVmMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || (menu.PlacementTarget as FrameworkElement)?.DataContext is not VmwareVmItemViewModel item) return;

        foreach (var entry in menu.Items.OfType<MenuItem>())
        {
            var applicable = entry.Tag switch
            {
                "console" => item.CanOpenConsole,
                "poweron" => item.CanPowerOn,
                "shutdown" or "reboot" or "suspend" or "poweroff" or "reset" => item.CanShutdown,
                "copyip" => item.Vm.IpAddress is not null,
                _ => true
            };
            entry.Visibility = applicable ? Visibility.Visible : Visibility.Collapsed;
            entry.IsEnabled = entry.Tag is "copyname" or "copyip" or "refresh" || !item.IsBusy;
        }

        ContextMenuSeparators.Normalize(menu);
    }

    // ── 菜单动作 ──────────────────────────────────────────────────

    private void OnPowerMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem entry && TargetOf(entry) is { } item) RunMenuAction(entry.Tag as string, item);
    }

    private void OnVmMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem entry && TargetOf(entry) is { } item) RunMenuAction(entry.Tag as string, item);
    }

    private static VmwareVmItemViewModel? TargetOf(MenuItem entry)
        => FindAncestor<ContextMenu>(entry)?.PlacementTarget is FrameworkElement { DataContext: VmwareVmItemViewModel item } ? item : null;

    private void RunMenuAction(string? tag, VmwareVmItemViewModel item)
    {
        if (ViewModel is not { } vm) return;

        switch (tag)
        {
            case "console": vm.OpenConsoleCommand.Execute(item); break;
            case "poweron": vm.PowerOnCommand.Execute(item); break;
            case "shutdown": vm.ShutdownCommand.Execute(item); break;
            case "reboot": vm.RebootCommand.Execute(item); break;
            case "suspend": vm.SuspendCommand.Execute(item); break;
            case "poweroff": vm.PowerOffCommand.Execute(item); break;
            case "reset": vm.ResetCommand.Execute(item); break;
            case "snapshots": vm.SnapshotsCommand.Execute(item); break;
            case "refresh": vm.RefreshCommand.Execute(null); break;
            case "copyname": Copy(item.Name, "虚拟机名称"); break;
            case "copyip": Copy(item.Vm.IpAddress, "IP 地址"); break;
            case "copyid": Copy(item.Vm.InstanceUuid, "虚拟机 ID"); break;
        }
    }

    /// <summary>详情面板里值旁边的复制按钮；Tag 指明复制哪一项。</summary>
    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item || sender is not FrameworkElement { Tag: string tag }) return;
        RunMenuAction(tag, item);
    }

    private void Copy(string? text, string what)
    {
        if (ClipboardText.TryCopy(text)) ViewModel?.ReportInfo($"已复制{what}");
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
