using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using RemoteFlow.App.Services;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Views;

/// <summary>
/// 独立文件传输窗口。窗口只做宿主：连接、传输、提示都在 <see cref="FileTransferViewModel"/>。
/// 关闭时若还有进行中的传输先确认；应用退出时由 <see cref="FileTransferWindowService.CloseAllAsync"/> 统一收尾。
/// </summary>
public partial class FileTransferWindow : Window
{
    private readonly IDialogService _dialogs;
    private bool _closeConfirmed;

    public FileTransferWindow(FileTransferViewModel viewModel, IDialogService dialogs)
    {
        InitializeComponent();

        ViewModel = viewModel;
        _dialogs = dialogs;
        DataContext = viewModel;

        // 标题栏 / 系统绘制部分跟随应用深浅色（标准窗口框架，不使用玻璃材质）。
        SourceInitialized += (_, _) => ApplyDarkMode();
        if (ThemeService.Instance is { } theme)
        {
            void OnAppearanceChanged(object? sender, EventArgs e) => Dispatcher.Invoke(ApplyDarkMode);
            theme.AppearanceChanged += OnAppearanceChanged;
            Closed += (_, _) => theme.AppearanceChanged -= OnAppearanceChanged;
        }

        // 窗口显示后才发起连接：连接期间用户已经能看到「正在连接…」而不是一个不出现的窗口。
        Loaded += async (_, _) => await viewModel.EnsureConnectedAsync();
    }

    public FileTransferViewModel ViewModel { get; }

    /// <summary>不经确认直接关闭（应用退出时使用；确认已在退出流程里统一问过）。</summary>
    public void ForceClose()
    {
        _closeConfirmed = true;
        Close();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        // 应用正在关闭（会话结束等）时不再追问，否则这里取消关闭会卡住整个退出流程。
        if (!_closeConfirmed && !Dispatcher.HasShutdownStarted && ViewModel?.HasActiveTransfers == true)
        {
            e.Cancel = true;
            var confirmed = await _dialogs.ConfirmAsync(
                "关闭文件传输",
                "还有文件正在传输，关闭窗口会取消这些传输，并清理尚未完成的临时文件。\n\n确定要关闭吗？",
                "关闭并取消",
                isDanger: true);
            if (confirmed)
            {
                _closeConfirmed = true;
                Close();
            }

            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel?.Dispose();
        base.OnClosed(e);
    }

    private void ApplyDarkMode()
    {
        if (ThemeService.Instance is { } theme)
        {
            WindowBackdrop.SetDarkMode(new WindowInteropHelper(this).Handle, theme.IsDark);
        }
    }
}
