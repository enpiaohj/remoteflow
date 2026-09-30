using System.Windows;
using Microsoft.Extensions.Logging;
using RemoteFlow.App.Views;
using RemoteFlow.Application.FileTransfer;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Services;

/// <summary>
/// 独立文件传输窗口的宿主：每个连接同时只开一个窗口（再次打开则激活已有窗口，避免重复连接同一台主机）。
/// 所有成员都在界面线程上调用（命令入口 / 退出流程）。
/// </summary>
public sealed class FileTransferWindowService(
    IFileTransferConnector connector,
    IDialogService dialogs,
    IUiDispatcher ui,
    ILoggerFactory loggerFactory) : IFileTransferWindowService
{
    private readonly Dictionary<Guid, FileTransferWindow> _windows = [];

    public bool HasActiveTransfers => _windows.Values.Any(w => w.ViewModel.HasActiveTransfers);

    public void Open(ConnectionProfile profile)
    {
        if (_windows.TryGetValue(profile.Id, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        var viewModel = new FileTransferViewModel(
            ct => connector.OpenAsync(profile, ct),
            dialogs,
            ui,
            loggerFactory.CreateLogger<FileTransferViewModel>(),
            $"文件传输 · {profile.Name}");

        var window = new FileTransferWindow(viewModel, dialogs);
        _windows[profile.Id] = window;
        window.Closed += (_, _) => _windows.Remove(profile.Id);
        window.Show();
    }

    public Task CloseAllAsync()
    {
        foreach (var window in _windows.Values.ToList())
        {
            window.ForceClose();
        }

        return Task.CompletedTask;
    }
}
