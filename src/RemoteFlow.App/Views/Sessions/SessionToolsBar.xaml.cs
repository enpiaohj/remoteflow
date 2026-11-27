using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Views.Sessions;

/// <summary>
/// 会话工具条：显示在标题栏行（Tab 条右侧）的会话级操作区。
/// <para>
/// 内容自会话视图内的常驻条迁移而来（状态入口 / 主机名 / 协议专属操作 / 全屏档切换）。
/// DataContext 为选中的 <see cref="SessionTabViewModel"/>；协议专属分组（RDP / SSH / VNC）
/// 按 <see cref="SessionTabViewModel.Protocol"/> 显隐。状态入口点击通过
/// <see cref="SessionTabViewModel.StatusEntryRequested"/> 通知宿主打开质量详情 Flyout
/// （Flyout 定位与生命周期仍由会话视图管理）。
/// </para>
/// </summary>
public sealed partial class SessionToolsBar : UserControl
{
    public SessionToolsBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ApplyProtocolVisibility();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => ApplyProtocolVisibility();

    /// <summary>按会话协议显示对应的工具分组，其余隐藏。</summary>
    private void ApplyProtocolVisibility()
    {
        if (DataContext is not SessionTabViewModel tab)
        {
            return;
        }

        RdpToolsDock.Visibility = tab.ToolProtocol == ProtocolType.Rdp ? Visibility.Visible : Visibility.Collapsed;
        SshToolsDock.Visibility = tab.ToolProtocol == ProtocolType.Ssh ? Visibility.Visible : Visibility.Collapsed;
        VncToolsDock.Visibility = tab.ToolProtocol == ProtocolType.Vnc ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>「更多」：按会话类型列出不常驻的操作，每项带文字与快捷键。</summary>
    private void OnMoreToolsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string kind } button || DataContext is not SessionTabViewModel tab)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var item in BuildMoreItems(kind, tab))
        {
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    internal static IEnumerable<Control> BuildMoreItems(string kind, SessionTabViewModel tab)
    {
        static MenuItem Item(string header, ICommand command, string icon, string? gesture = null)
            => new MenuItem { Header = header, Command = command, InputGestureText = gesture ?? string.Empty }.WithIcon(icon);

        switch (kind)
        {
            case "Rdp":
                yield return Item("缩放 / 适应窗口", tab.ToggleScalingCommand, "Ui.Scale");
                yield return Item("发送 Ctrl+Alt+Del", tab.SendCtrlAltDeleteCommand, "Ui.Keyboard");
                yield return Item("启动任务管理器", tab.SendTaskManagerCommand, "Ui.TaskManager");
                if (tab.CanOpenFileTransferTab || tab.CanOpenPowerShell)
                {
                    yield return new Separator();
                }

                if (tab.CanOpenFileTransferTab)
                {
                    yield return Item(tab.FileTransferTabToolTip, tab.OpenFileTransferTabCommand, "Ui.FileTransfer");
                }

                if (tab.CanOpenPowerShell)
                {
                    yield return Item("在此主机上打开 PowerShell 会话", tab.OpenPowerShellSessionCommand, "Ui.ProtocolSsh");
                }

                break;

            case "Ssh":
                yield return Item("复制选中内容", tab.CopySelectionCommand, "Ui.Copy", "Ctrl+Shift+C");
                yield return Item("粘贴", tab.PasteClipboardCommand, "Ui.Paste", "Ctrl+Shift+V");
                yield return Item("在输出中查找", tab.SearchTerminalCommand, "Ui.Search", "Ctrl+Shift+F");
                yield return new Separator();
                yield return Item("清屏", tab.ClearTerminalCommand, "Ui.Clear");
                break;

            case "Vnc":
                yield return Item("发送 Ctrl+Alt+Del", tab.SendCtrlAltDeleteCommand, "Ui.Keyboard");
                if (tab.IsVmwareConsole)
                {
                    yield return Item(tab.VmwareResolutionLabel, tab.CycleVmwareResolutionCommand, "Ui.Resolution");
                }

                break;
        }
    }

    private void OnStatusEntryClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SessionTabViewModel tab)
        {
            tab.RaiseStatusEntryRequested();
        }
    }
}
