using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Views.Sessions;

/// <summary>
/// 文件传输面板（会话内侧栏与工作区文件传输 Tab 共用）。所有逻辑都在 <see cref="FileTransferViewModel"/>，
/// 这里只做视图互操作：右键选中、双击进入、右键菜单显隐、路径框的编辑 / 面包屑切换、快捷键。
/// </summary>
public partial class FileTransferPanel : UserControl
{
    public static readonly DependencyProperty ShowCloseButtonProperty = DependencyProperty.Register(
        nameof(ShowCloseButton), typeof(bool), typeof(FileTransferPanel),
        new PropertyMetadata(false, (d, e) => ((FileTransferPanel)d).CloseButton.Visibility =
            e.NewValue is true ? Visibility.Visible : Visibility.Collapsed));

    private FileTransferViewModel? _observed;

    public FileTransferPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        PreviewKeyDown += OnPanelPreviewKeyDown;
    }

    /// <summary>显示标题栏右上角的「收起」按钮（侧栏用；文件传输 Tab 的标签头自带关闭按钮，不需要）。</summary>
    public bool ShowCloseButton
    {
        get => (bool)GetValue(ShowCloseButtonProperty);
        set => SetValue(ShowCloseButtonProperty, value);
    }

    /// <summary>点了「收起」。宿主负责隐藏面板。</summary>
    public event EventHandler? CloseRequested;

    private FileTransferViewModel? Vm => DataContext as FileTransferViewModel;

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _observed = e.NewValue as FileTransferViewModel;
        if (_observed is not null)
        {
            _observed.PropertyChanged += OnViewModelPropertyChanged;
        }

        EndEditPath();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 进入深层目录后面包屑滚到最右，保证当前目录名可见。
        if (e.PropertyName == nameof(FileTransferViewModel.CurrentPath))
        {
            Dispatcher.BeginInvoke(() => BreadcrumbScroll.ScrollToRightEnd(), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    // ── 文件列表 ──────────────────────────────────────────────────

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: RemoteFileItemViewModel item } && Vm is { } vm)
        {
            vm.OpenEntryCommand.Execute(item);
            e.Handled = true;
        }
    }

    /// <summary>右键先选中所点行（与资源管理器一致）；已在多选集合里的行保持现有选择，便于对多项右键操作。</summary>
    private void OnRowRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: RemoteFileItemViewModel item } || Vm is not { } vm || item.IsSelected)
        {
            return;
        }

        foreach (var entry in vm.Entries)
        {
            entry.IsSelected = false;
        }

        item.IsSelected = true;
    }

    private void OnFileListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Vm is not { } vm)
        {
            return;
        }

        var selected = vm.Entries.Where(x => x.IsSelected).ToList();
        if (selected is [{ IsDirectory: true } only])
        {
            vm.OpenEntryCommand.Execute(only);
            e.Handled = true;
        }
    }

    // ── 右键菜单 ──────────────────────────────────────────────────

    /// <summary>按当前选择与通道状态显隐菜单项，再整理分隔线（避免首尾悬空 / 连续重复）。</summary>
    private void OnRowMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || Vm is not { } vm)
        {
            return;
        }

        var selectedCount = vm.Entries.Count(x => x.IsSelected);
        var inShare = !vm.IsAtShareList;

        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.Visibility = (item.Tag as string) switch
            {
                "download" => Show(selectedCount > 0 && inShare),
                "rename" => Show(selectedCount == 1 && inShare),
                "delete" => Show(selectedCount > 0 && inShare),
                "uploadfiles" or "newfolder" => Show(vm.CanUpload),
                _ => Visibility.Visible
            };
        }

        ContextMenuSeparators.Normalize(menu);

        static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRowMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || Vm is not { } vm)
        {
            return;
        }

        switch (tag)
        {
            case "download":
                vm.DownloadCommand.Execute(null);
                break;
            case "rename":
                vm.RenameCommand.Execute(null);
                break;
            case "delete":
                vm.DeleteCommand.Execute(null);
                break;
            case "copypath":
                vm.CopyPathCommand.Execute(null);
                break;
            case "uploadfiles":
                vm.UploadFilesCommand.Execute(null);
                break;
            case "newfolder":
                vm.NewFolderCommand.Execute(null);
                break;
            case "refresh":
                vm.RefreshCommand.Execute(null);
                break;
        }
    }

    // ── 上传菜单 ──────────────────────────────────────────────────

    private void OnUploadButtonClick(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)FindResource("UploadMenu");
        menu.PlacementTarget = UploadButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnUploadFilesClick(object sender, RoutedEventArgs e) => Vm?.UploadFilesCommand.Execute(null);

    private void OnUploadFolderClick(object sender, RoutedEventArgs e) => Vm?.UploadFolderCommand.Execute(null);

    // ── 路径栏：面包屑 ↔ 可编辑路径 ──────────────────────────────

    private void OnBreadcrumbMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 面包屑里的按钮会自己处理点击（不会冒泡到这里）；能到这里的都是点在空白处。
        BeginEditPath();
        e.Handled = true;
    }

    private void BeginEditPath()
    {
        if (Vm is not { IsConnected: true } vm)
        {
            return;
        }

        vm.PathText = vm.CurrentPath;
        BreadcrumbHost.Visibility = Visibility.Collapsed;
        PathBox.Visibility = Visibility.Visible;
        PathBox.Focus();
        PathBox.SelectAll();
    }

    private void EndEditPath()
    {
        PathBox.Visibility = Visibility.Collapsed;
        BreadcrumbHost.Visibility = Visibility.Visible;
    }

    private void OnPathBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                vm.CommitPathCommand.Execute(null);
                EndEditPath();
                e.Handled = true;
                break;

            case Key.Escape:
                vm.PathText = vm.CurrentPath;
                EndEditPath();
                e.Handled = true;
                break;
        }
    }

    private void OnPathBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndEditPath();

    private void OnPanelPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
        {
            BeginEditPath();
            e.Handled = true;
        }
    }
}
