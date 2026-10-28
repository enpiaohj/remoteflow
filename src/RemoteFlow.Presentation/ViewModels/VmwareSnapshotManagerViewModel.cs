using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Vmware;
using RemoteFlow.Core.Vmware;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.Presentation.ViewModels;

/// <summary>快照列表中的一行（已按层级缩进）。</summary>
public sealed record VmwareSnapshotItem(VmSnapshot Snapshot)
{
    public string DisplayName => new string(' ', Snapshot.Depth * 4) + Snapshot.Name + (Snapshot.IsCurrent ? "　· 当前" : string.Empty);

    public string CreatedText => Snapshot.CreatedAt is { } time ? time.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "—";

    public string Description => Snapshot.Description;
}

/// <summary>
/// 一台虚拟机的快照管理：列表、创建、恢复、删除。恢复与删除都要确认，并明确说明后果。
/// 所有操作等到服务端任务真正结束再报告结果。
/// </summary>
public sealed partial class VmwareSnapshotManagerViewModel(
    VmwareService service,
    VmwareSource source,
    VsphereVm vm,
    IDialogService dialogs) : ObservableObject
{
    public string Title => $"快照管理 · {vm.Name}";

    public string Subtitle => $"{source.Name}　·　快照用于回到某个状态，不能替代备份。";

    public ObservableCollection<VmwareSnapshotItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private VmwareSnapshotItem? _selectedItem;

    [ObservableProperty]
    private string _newName = string.Empty;

    [ObservableProperty]
    private string _newDescription = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isStatusError;

    [ObservableProperty]
    private bool _isEmpty;

    partial void OnNewNameChanged(string value) => CreateCommand.NotifyCanExecuteChanged();

    public Task LoadAsync() => RefreshAsync();

    private bool CanAct() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task RefreshAsync()
    {
        await RunAsync("正在读取快照…", async () =>
        {
            var snapshots = await service.ListSnapshotsAsync(source, vm.Reference);
            Items.Clear();
            foreach (var snapshot in snapshots) Items.Add(new VmwareSnapshotItem(snapshot));
            IsEmpty = Items.Count == 0;
            return IsEmpty ? "这台虚拟机还没有快照。" : $"共 {Items.Count} 个快照。";
        });
    }

    private bool CanCreate() => !IsBusy && !string.IsNullOrWhiteSpace(NewName);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        var name = NewName.Trim();
        await RunAsync("正在创建快照，请稍候…", async () =>
        {
            await service.CreateSnapshotAsync(source, vm.Reference, name, NewDescription.Trim());
            NewName = string.Empty;
            NewDescription = string.Empty;
            await ReloadAsync();
            return $"快照“{name}”已创建（不含内存）。";
        });
    }

    private bool CanChangeSelected() => !IsBusy && SelectedItem is not null;

    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private async Task RevertAsync()
    {
        if (SelectedItem is not { } item) return;
        var confirmed = await dialogs.ConfirmAsync(
            "恢复快照",
            $"虚拟机：{vm.Name}\n快照：{item.Snapshot.Name}\n\n恢复会让虚拟机回到该快照时的状态，" +
            "此后产生的所有变更都会丢失，且无法撤销。确定要恢复吗？",
            "恢复快照", isDanger: true);
        if (!confirmed) return;

        await RunAsync("正在恢复快照，请稍候…", async () =>
        {
            await service.RevertSnapshotAsync(source, vm.Reference, item.Snapshot.Reference);
            await ReloadAsync();
            return $"已恢复到快照“{item.Snapshot.Name}”。";
        });
    }

    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private async Task RemoveAsync()
    {
        if (SelectedItem is not { } item) return;
        var confirmed = await dialogs.ConfirmAsync(
            "删除快照",
            $"虚拟机：{vm.Name}\n快照：{item.Snapshot.Name}\n\n删除快照不会回滚虚拟机，只是丢掉这个还原点；" +
            "相关磁盘数据会合并，可能耗时较长，并需要足够的存储空间。确定要删除吗？",
            "删除快照", isDanger: true);
        if (!confirmed) return;

        await RunAsync("正在删除快照，磁盘合并可能需要较长时间…", async () =>
        {
            await service.RemoveSnapshotAsync(source, vm.Reference, item.Snapshot.Reference);
            await ReloadAsync();
            return $"快照“{item.Snapshot.Name}”已删除。";
        });
    }

    private async Task ReloadAsync()
    {
        var snapshots = await service.ListSnapshotsAsync(source, vm.Reference);
        Items.Clear();
        foreach (var snapshot in snapshots) Items.Add(new VmwareSnapshotItem(snapshot));
        IsEmpty = Items.Count == 0;
    }

    private async Task RunAsync(string busyText, Func<Task<string>> action)
    {
        IsBusy = true;
        IsStatusError = false;
        StatusText = busyText;
        try
        {
            StatusText = await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IsStatusError = true;
            StatusText = VmwareErrors.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>把异常翻译成面向用户的中文说明。只输出固定文案，不回显服务器返回的任意文本。</summary>
public static class VmwareErrors
{
    public static string Describe(Exception ex) => ex switch
    {
        VsphereApiException api => api.Message,
        InvalidOperationException invalid => invalid.Message,
        System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException or System.IO.IOException
            => "无法与服务器建立安全连接：地址不可达，或证书与已记录的指纹不一致（证书更换后需编辑来源重新核对）。",
        TaskCanceledException => "连接服务器超时。",
        _ => "操作失败，详情请查看日志。"
    };
}
