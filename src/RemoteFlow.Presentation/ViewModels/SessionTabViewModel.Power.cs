using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.Presentation.ViewModels;

public sealed partial class SessionTabViewModel
{
    private IDialogService? _powerDialogs;
    private ILogger? _powerLogger;

    private void InitializePowerActions(IDialogService? dialogs, ILogger? logger)
    {
        _powerDialogs = dialogs;
        _powerLogger = logger;
    }

    public bool SupportsPowerActions => Session is IRemotePowerSession { SupportsPowerActions: true };
    public bool CanRequestPowerAction => SupportsPowerActions && _powerDialogs is not null
        && !_disposed && !IsPowerBusy && Session.State == ConnectionState.Connected && !Session.IsClosing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRequestPowerAction))]
    [NotifyCanExecuteChangedFor(nameof(ShutdownCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestartDeviceCommand))]
    private bool _isPowerBusy;

    partial void OnStateTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanRequestPowerAction));
        ShutdownCommand.NotifyCanExecuteChanged();
        RestartDeviceCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRequestPowerAction))]
    private Task ShutdownAsync() => RequestPowerAsync(RemotePowerAction.Shutdown);

    [RelayCommand(CanExecute = nameof(CanRequestPowerAction))]
    private Task RestartDeviceAsync() => RequestPowerAsync(RemotePowerAction.Restart);

    private async Task RequestPowerAsync(RemotePowerAction action)
    {
        if (!CanRequestPowerAction || Session is not IRemotePowerSession power) return;
        IsPowerBusy = true;
        var verb = action == RemotePowerAction.Shutdown ? "关机" : "重启";
        var target = Profile.Vmware.Enabled
            ? $"虚拟机：{Profile.Vmware.VmName}\n管理端点：{HostPortLine}"
            : $"设备：{Title}\n地址：{HostPortLine}";
        try
        {
            var confirmed = await _powerDialogs!.ConfirmAsync(
                $"{verb}远程设备", $"{target}\n\n此操作将{verb}整台远程设备，并中断该设备上的所有连接。请先保存工作。\n\n是否继续？",
                verb, isDanger: true);
            if (!confirmed) return;
            if (_disposed || Session.State != ConnectionState.Connected || Session.IsClosing || !power.SupportsPowerActions)
            {
                await _powerDialogs.ShowMessageAsync("无法执行电源操作", "会话状态已经变化，未发送电源请求。", DialogKind.Warning);
                return;
            }
            await power.RequestPowerActionAsync(action);
            await _powerDialogs.ShowMessageAsync("电源请求已发送", $"已向远程设备发送{verb}请求。最终结果请以设备状态为准。", DialogKind.Info);
        }
        catch (RemotePowerException ex)
        {
            await _powerDialogs!.ShowMessageAsync("电源操作未完成", ex.Message, DialogKind.Warning);
        }
        catch (Exception ex)
        {
            _powerLogger?.LogWarning("远程电源请求未确认：{ErrorType}", ex.GetType().Name);
            await _powerDialogs!.ShowMessageAsync("无法确认电源操作结果", "连接中断或操作失败，请检查远程设备状态后再决定是否重试。", DialogKind.Warning);
        }
        finally { IsPowerBusy = false; }
    }
}
