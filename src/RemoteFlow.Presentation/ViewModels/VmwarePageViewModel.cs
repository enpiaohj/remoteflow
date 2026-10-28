using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Services;
using RemoteFlow.Application.Vmware;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Vmware;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.Presentation.ViewModels;

/// <summary>来源列表中的一项。</summary>
public sealed class VmwareSourceItemViewModel(VmwareSource source)
{
    public VmwareSource Source { get; } = source;
    public string Name => Source.Name;
    public string Address => Source.Port == 443 ? Source.Host : $"{Source.Host}:{Source.Port}";
    public string KindText => Source.Kind == VmwareSourceKind.VCenter ? "vCenter" : "ESXi 主机";
}

/// <summary>虚拟机列表中的一行（实时数据）。</summary>
public sealed partial class VmwareVmItemViewModel(VsphereVm vm) : ObservableObject
{
    public VsphereVm Vm { get; } = vm;

    public string Name => Vm.Name;

    public bool IsPoweredOn => Vm.PowerState == "poweredOn";

    public bool IsPoweredOff => Vm.PowerState == "poweredOff";

    public bool IsSuspended => Vm.PowerState == "suspended";

    /// <summary>状态用文字 + 图形标记，不只靠颜色。</summary>
    public string PowerText => Vm.PowerState switch
    {
        "poweredOn" => "运行中",
        "poweredOff" => "已关机",
        "suspended" => "已挂起",
        _ => "未知"
    };

    public string PowerGlyph => IsPoweredOn ? "●" : IsSuspended ? "◐" : "○";

    public bool CanOpenConsole => IsPoweredOn;
    public bool CanPowerOn => IsPoweredOff || IsSuspended;
    public bool CanShutdown => IsPoweredOn;

    /// <summary>该虚拟机正在执行电源/快照操作，禁用其动作避免重复提交。</summary>
    [ObservableProperty]
    private bool _isBusy;
}

/// <summary>
/// 「虚拟化」页：管理 ESXi 主机 / vCenter 来源，实时列出其下的虚拟机，提供控制台、电源与快照。
/// 虚拟机不落库，每次从服务器读取，因此不会与连接列表重复，也不会与真实情况脱节。
/// </summary>
public sealed partial class VmwarePageViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);

    private readonly VmwareService _service;
    private readonly CredentialService _credentials;
    private readonly IDialogService _dialogs;
    private readonly IUiTimer _timer;
    private readonly List<VmwareVmItemViewModel> _allVms = [];
    private CancellationTokenSource? _loadCts;
    private bool _active;
    private bool _refreshing;

    /// <summary>电源操作的结果提示保留到这个时刻：期间的静默刷新只更新列表，不覆盖提示。</summary>
    private DateTime _statusHoldUntil = DateTime.MinValue;

    private static readonly TimeSpan ResultHold = TimeSpan.FromSeconds(20);

    public VmwarePageViewModel(
        VmwareService service, CredentialService credentials, IDialogService dialogs, IUiTimerFactory timers)
    {
        _service = service;
        _credentials = credentials;
        _dialogs = dialogs;
        _timer = timers.Create(RefreshInterval);
        _timer.Tick += async (_, _) => await RefreshVmsAsync(silent: true);
    }

    public ObservableCollection<VmwareSourceItemViewModel> Sources { get; } = [];

    public ObservableCollection<VmwareVmItemViewModel> Vms { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditSourceCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSourceCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private VmwareSourceItemViewModel? _selectedSource;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>页面顶部的状态 / 结果提示。</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isStatusError;

    public bool HasSources => Sources.Count > 0;

    public bool ShowEmptySources => Sources.Count == 0;

    public bool ShowNoVms => SelectedSource is not null && !IsLoading && Vms.Count == 0 && !IsStatusError;

    /// <summary>用户要求打开某台虚拟机的控制台；由主窗口接到统一的会话漏斗。</summary>
    public event EventHandler<ConnectionProfile>? OpenConsoleRequested;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedSourceChanged(VmwareSourceItemViewModel? value)
    {
        _allVms.Clear();
        Vms.Clear();
        OnPropertyChanged(nameof(ShowNoVms));
        _ = RefreshVmsAsync(silent: false);
    }

    /// <summary>页面显示 / 离开时调用：只有可见时才自动刷新，避免在后台反复登录服务器。</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (active && SelectedSource is not null) _timer.Start();
        else _timer.Stop();
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var previous = SelectedSource?.Source.Id;
        var sources = await _service.GetSourcesAsync(ct);
        Sources.Clear();
        foreach (var source in sources) Sources.Add(new VmwareSourceItemViewModel(source));
        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(ShowEmptySources));

        var next = Sources.FirstOrDefault(s => s.Source.Id == previous) ?? Sources.FirstOrDefault();
        if (ReferenceEquals(next, SelectedSource))
        {
            await RefreshVmsAsync(silent: false);
        }
        else
        {
            SelectedSource = next;
        }

        if (_active && SelectedSource is not null) _timer.Start();
    }

    [RelayCommand]
    private async Task AddSourceAsync() => await EditSourceCoreAsync(null);

    private bool HasSource() => SelectedSource is not null;

    [RelayCommand(CanExecute = nameof(HasSource))]
    private async Task EditSourceAsync() => await EditSourceCoreAsync(SelectedSource?.Source);

    private async Task EditSourceCoreAsync(VmwareSource? existing)
    {
        var credentials = await _credentials.GetAllAsync();
        var editor = new VmwareSourceEditorViewModel(_service, existing, credentials);
        if (!await _dialogs.EditVmwareSourceAsync(editor) || editor.Saved is not { } saved) return;

        await LoadAsync();
        SelectedSource = Sources.FirstOrDefault(s => s.Source.Id == saved.Id) ?? SelectedSource;
    }

    [RelayCommand(CanExecute = nameof(HasSource))]
    private async Task DeleteSourceAsync()
    {
        if (SelectedSource is not { } item) return;
        var confirmed = await _dialogs.ConfirmAsync(
            "删除来源",
            $"确定从 RemoteFlow 中删除“{item.Name}”吗？\n\n只会移除这里保存的连接信息，不会影响服务器和其上的任何虚拟机。" +
            "该来源使用的凭据不会被删除。",
            "删除", isDanger: true);
        if (!confirmed) return;

        await _service.DeleteSourceAsync(item.Source.Id);
        SelectedSource = null;
        await LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSource))]
    private Task RefreshAsync() => RefreshVmsAsync(silent: false);

    /// <summary>从服务器读取虚拟机。<paramref name="silent"/> 为 true（定时刷新）时失败只更新提示、不清空已有列表。</summary>
    private async Task RefreshVmsAsync(bool silent)
    {
        if (SelectedSource is not { } item || _refreshing) return;
        _refreshing = true;
        var version = ++_loadVersion;
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        if (!silent)
        {
            _statusHoldUntil = DateTime.MinValue;
            IsLoading = true;
            IsStatusError = false;
            StatusText = "正在读取虚拟机…";
        }

        try
        {
            var vms = await _service.ListVirtualMachinesAsync(item.Source, ct);
            if (version != _loadVersion || !ReferenceEquals(item, SelectedSource)) return;

            // 保留各行的忙碌标记（正在执行操作的虚拟机在刷新后仍保持禁用）。
            var busy = _allVms.Where(v => v.IsBusy).Select(v => v.Vm.Reference).ToHashSet();
            _allVms.Clear();
            foreach (var vm in vms.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
            {
                _allVms.Add(new VmwareVmItemViewModel(vm) { IsBusy = busy.Contains(vm.Reference) });
            }

            ApplyFilter();
            if (!silent || DateTime.UtcNow >= _statusHoldUntil)
            {
                IsStatusError = false;
                StatusText = $"{item.Name} · {vms.Count} 台虚拟机 · 更新于 {DateTime.Now:HH:mm:ss}";
            }
        }
        catch (OperationCanceledException)
        {
            // 切换来源或重复刷新：旧请求静默退出。
        }
        catch (Exception ex)
        {
            if (version != _loadVersion || !ReferenceEquals(item, SelectedSource)) return;
            if (!silent || DateTime.UtcNow >= _statusHoldUntil)
            {
                IsStatusError = true;
                StatusText = VmwareErrors.Describe(ex);
            }

            if (!silent)
            {
                _allVms.Clear();
                Vms.Clear();
            }
        }
        finally
        {
            _refreshing = false;
            IsLoading = false;
            OnPropertyChanged(nameof(ShowNoVms));
        }
    }

    private int _loadVersion;

    private void ApplyFilter()
    {
        var keyword = SearchText.Trim();
        Vms.Clear();
        foreach (var vm in _allVms.Where(v => keyword.Length == 0 || v.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            Vms.Add(vm);
        }

        OnPropertyChanged(nameof(ShowNoVms));
    }

    // ── 虚拟机动作 ────────────────────────────────────────────────

    [RelayCommand]
    private void OpenConsole(VmwareVmItemViewModel? item)
    {
        if (item is null || SelectedSource is not { } source) return;
        if (!item.CanOpenConsole)
        {
            StatusText = $"“{item.Name}”当前{item.PowerText}，请先开机再打开控制台。";
            IsStatusError = true;
            return;
        }

        try
        {
            OpenConsoleRequested?.Invoke(this, VmwareService.BuildConsoleProfile(source.Source, item.Vm));
        }
        catch (InvalidOperationException ex)
        {
            IsStatusError = true;
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private Task PowerOnAsync(VmwareVmItemViewModel? item) => PowerAsync(item, VmPowerAction.PowerOn);

    [RelayCommand]
    private Task ShutdownAsync(VmwareVmItemViewModel? item) => PowerAsync(item, VmPowerAction.ShutdownGuest);

    [RelayCommand]
    private Task RebootAsync(VmwareVmItemViewModel? item) => PowerAsync(item, VmPowerAction.RebootGuest);

    [RelayCommand]
    private Task SuspendAsync(VmwareVmItemViewModel? item) => PowerAsync(item, VmPowerAction.Suspend);

    [RelayCommand]
    private Task PowerOffAsync(VmwareVmItemViewModel? item) => PowerAsync(item, VmPowerAction.PowerOff);

    [RelayCommand]
    private Task ResetAsync(VmwareVmItemViewModel? item) => PowerAsync(item, VmPowerAction.Reset);

    private async Task PowerAsync(VmwareVmItemViewModel? item, VmPowerAction action)
    {
        if (item is null || item.IsBusy || SelectedSource is not { } source) return;

        var (title, message, confirmText, danger) = Describe(action, item);
        if (message is not null && !await _dialogs.ConfirmAsync(title, message, confirmText, danger)) return;

        item.IsBusy = true;
        IsStatusError = false;
        StatusText = $"正在对“{item.Name}”执行：{title}…";
        try
        {
            var result = await _service.PowerAsync(source.Source, item.Vm.Reference, action);
            StatusText = result == VmPowerResult.RequestSent
                ? $"“{item.Name}”：已向客户机发送请求，是否完成请观察电源状态。"
                : $"“{item.Name}”：{title}已完成。";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IsStatusError = true;
            StatusText = $"“{item.Name}”：{title}失败。{VmwareErrors.Describe(ex)}";
        }
        finally
        {
            item.IsBusy = false;
        }

        _statusHoldUntil = DateTime.UtcNow + ResultHold;
        await RefreshVmsAsync(silent: true);
    }

    private static (string Title, string? Message, string ConfirmText, bool Danger) Describe(VmPowerAction action, VmwareVmItemViewModel item) => action switch
    {
        VmPowerAction.PowerOn => ("开机", null, "开机", false),
        VmPowerAction.ShutdownGuest => ("正常关机",
            $"向“{item.Name}”的客户机发送正常关机请求（需要 VMware Tools）。确定吗？", "正常关机", false),
        VmPowerAction.RebootGuest => ("正常重启",
            $"向“{item.Name}”的客户机发送正常重启请求（需要 VMware Tools）。确定吗？", "正常重启", false),
        VmPowerAction.Suspend => ("挂起",
            $"挂起“{item.Name}”，保存当前运行状态并暂停。确定吗？", "挂起", false),
        VmPowerAction.PowerOff => ("强制断电",
            $"强制断电“{item.Name}”相当于直接拔掉电源，客户机里未保存的数据会丢失，可能损坏文件系统。\n\n如只是想关机，请用“正常关机”。确定要强制断电吗？",
            "强制断电", true),
        VmPowerAction.Reset => ("强制重置",
            $"强制重置“{item.Name}”相当于按下复位键，客户机里未保存的数据会丢失。\n\n如只是想重启，请用“正常重启”。确定要强制重置吗？",
            "强制重置", true),
        _ => (action.ToString(), null, "确定", false)
    };

    [RelayCommand]
    private async Task SnapshotsAsync(VmwareVmItemViewModel? item)
    {
        if (item is null || SelectedSource is not { } source) return;
        var manager = new VmwareSnapshotManagerViewModel(_service, source.Source, item.Vm, _dialogs);
        await _dialogs.ShowVmwareSnapshotsAsync(manager);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
    }
}
