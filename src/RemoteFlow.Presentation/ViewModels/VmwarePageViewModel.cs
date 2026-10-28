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

public enum VmwareStateFilter
{
    All,
    Running,
    Stopped,
    Suspended
}

public enum VmwareSortKey
{
    Name,
    Ip,
    State,
    Memory
}

/// <summary>左侧「状态」筛选里的一项，数量随刷新更新。</summary>
public sealed partial class VmwareFilterItemViewModel(VmwareStateFilter filter, string title) : ObservableObject
{
    public VmwareStateFilter Filter { get; } = filter;
    public string Title { get; } = title;

    [ObservableProperty]
    private int _count;
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

    /// <summary>
    /// VMware Tools 状态（只对运行中的虚拟机有意义）。没运行 Tools 时鼠标无法精确定位、分辨率不能随窗口调整，
    /// 所以在列表里直接说清楚，而不是让用户在控制台里碰壁。
    /// </summary>
    public string ToolsText => !IsPoweredOn ? string.Empty
        : Vm.ToolsRunning ? "VMware Tools 运行中" : "未运行 VMware Tools";

    public bool ToolsMissing => IsPoweredOn && !Vm.ToolsRunning;

    public bool HasToolsText => ToolsText.Length > 0;

    /// <summary>IP 由客户机里的 VMware Tools 上报，关机或没装 Tools 时没有。</summary>
    public string IpText => Vm.IpAddress ?? "—";

    public string GuestOsText => Vm.GuestOs ?? "—";

    /// <summary>表格里用的短名：去掉厂商前缀与位数后缀（完整名称见详情面板）。</summary>
    public string GuestOsShortText => Vm.GuestOs is { } os
        ? os.Replace("Microsoft ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" (64-bit)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" (32-bit)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim()
        : "—";

    public string CpuText => Vm.CpuCount is { } cpu ? $"{cpu} 核" : "—";

    public string MemoryText => Vm.MemoryMb switch
    {
        null => "—",
        >= 1024 => $"{Vm.MemoryMb.Value / 1024.0:0.#} GB",
        var mb => $"{mb} MB"
    };

    public string CpuMemoryText => Vm.CpuCount is null && Vm.MemoryMb is null ? "—" : $"{CpuText} · {MemoryText}";

    /// <summary>运行时长只对运行中的虚拟机有意义。</summary>
    public string UptimeText
    {
        get
        {
            if (!IsPoweredOn || Vm.UptimeSeconds is not { } seconds) return "—";
            var span = TimeSpan.FromSeconds(seconds);
            if (span.TotalDays >= 1) return $"{(int)span.TotalDays} 天 {span.Hours} 小时";
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours} 小时 {span.Minutes} 分钟";
            return $"{Math.Max(1, (int)span.TotalMinutes)} 分钟";
        }
    }

    public string InstanceIdText => Vm.InstanceUuid ?? "—";

    public bool HasIp => Vm.IpAddress is not null;

    public bool HasInstanceId => Vm.InstanceUuid is not null;

    public string NotesText => Vm.Notes ?? string.Empty;

    public bool HasNotes => !string.IsNullOrWhiteSpace(Vm.Notes);

    /// <summary>按客户机操作系统挑设备图标；识别不了就用通用虚拟机图标。资源键见 DeviceIcons.xaml。</summary>
    public string DeviceIconKey
    {
        get
        {
            var os = Vm.GuestOs ?? string.Empty;
            if (os.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                return os.Contains("Server", StringComparison.OrdinalIgnoreCase) ? "DeviceIcon.WindowsServer" : "DeviceIcon.WindowsPc";
            if (LinuxHints.Any(h => os.Contains(h, StringComparison.OrdinalIgnoreCase))) return "DeviceIcon.Linux";
            if (os.Contains("Mac", StringComparison.OrdinalIgnoreCase) || os.Contains("Darwin", StringComparison.OrdinalIgnoreCase))
                return "DeviceIcon.Mac";
            return "DeviceIcon.VirtualMachine";
        }
    }

    private static readonly string[] LinuxHints =
        ["Linux", "Ubuntu", "Debian", "CentOS", "Red Hat", "RHEL", "SUSE", "Fedora", "Rocky", "Alma", "Photon"];

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
        _selectedFilter = FilterItems[0];
    }

    public ObservableCollection<VmwareFilterItemViewModel> FilterItems { get; } =
    [
        new(VmwareStateFilter.All, "所有虚拟机"),
        new(VmwareStateFilter.Running, "运行中"),
        new(VmwareStateFilter.Stopped, "已关机"),
        new(VmwareStateFilter.Suspended, "已挂起")
    ];

    [ObservableProperty]
    private VmwareFilterItemViewModel? _selectedFilter;

    partial void OnSelectedFilterChanged(VmwareFilterItemViewModel? value)
    {
        if (value is not null) ApplyFilter();
    }

    /// <summary>右侧详情面板展示的虚拟机。刷新后按引用保持选中。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedVm))]
    private VmwareVmItemViewModel? _selectedVm;

    public bool HasSelectedVm => SelectedVm is not null;

    public bool ShowNoSelection => SelectedSource is not null && SelectedVm is null;

    public string NoSelectionDescription => ShowLoadingVms ? "读取完成后，可在这里查看虚拟机详情。"
        : ShowLoadError ? "恢复连接后，即可查看配置与可用操作。"
        : ShowNoMatches ? "重置筛选后，选择虚拟机查看配置与可用操作。"
        : ShowNoVms ? "来源中的虚拟机会显示在左侧列表。"
        : "在列表中选择虚拟机，查看配置、打开控制台或管理快照。";

    partial void OnSelectedVmChanged(VmwareVmItemViewModel? value) => OnPropertyChanged(nameof(ShowNoSelection));

    public int TotalCount => _allVms.Count;

    /// <summary>页头旁的概览，如「22 台虚拟机 · 14 运行中」。</summary>
    public string SummaryText => _allVms.Count == 0 ? string.Empty : $"{_allVms.Count} 台虚拟机 · {RunningCount} 运行中";

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

    private bool _hasLoadedVms;
    private bool _loadFailed;

    public bool ShowLoadingVms => SelectedSource is not null && IsLoading && !HasAnyVms;
    public bool ShowLoadError => SelectedSource is not null && !IsLoading && !HasAnyVms && _loadFailed;
    public bool ShowNoVms => SelectedSource is not null && !IsLoading && _hasLoadedVms && !HasAnyVms && !_loadFailed;
    public bool ShowNoMatches => SelectedSource is not null && HasAnyVms && Vms.Count == 0;

    private void RaiseEmptyStatesChanged()
    {
        OnPropertyChanged(nameof(ShowLoadingVms));
        OnPropertyChanged(nameof(ShowLoadError));
        OnPropertyChanged(nameof(ShowNoVms));
        OnPropertyChanged(nameof(ShowNoMatches));
        OnPropertyChanged(nameof(ShowNoSelection));
        OnPropertyChanged(nameof(NoSelectionDescription));
    }

    partial void OnIsLoadingChanged(bool value) => RaiseEmptyStatesChanged();

    [RelayCommand]
    private void ResetFilters()
    {
        SearchText = string.Empty;
        SelectedFilter = FilterItems[0];
    }

    /// <summary>当前来源下各电源状态的虚拟机数量（不受搜索过滤影响，用于页头的概览徽章）。</summary>
    public int RunningCount => _allVms.Count(v => v.IsPoweredOn);

    public int StoppedCount => _allVms.Count(v => v.IsPoweredOff);

    public int SuspendedCount => _allVms.Count(v => v.IsSuspended);

    public bool HasSuspended => SuspendedCount > 0;

    public bool HasAnyVms => _allVms.Count > 0;

    private void RaiseCountsChanged()
    {
        foreach (var item in FilterItems)
        {
            item.Count = item.Filter switch
            {
                VmwareStateFilter.Running => RunningCount,
                VmwareStateFilter.Stopped => StoppedCount,
                VmwareStateFilter.Suspended => SuspendedCount,
                _ => _allVms.Count
            };
        }

        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ShowNoSelection));
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(StoppedCount));
        OnPropertyChanged(nameof(SuspendedCount));
        OnPropertyChanged(nameof(HasSuspended));
        OnPropertyChanged(nameof(HasAnyVms));
    }

    /// <summary>用户要求打开某台虚拟机的控制台；由主窗口接到统一的会话漏斗。</summary>
    public event EventHandler<ConnectionProfile>? OpenConsoleRequested;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedSourceChanged(VmwareSourceItemViewModel? value)
    {
        ++_loadVersion;
        _loadCts?.Cancel();
        _refreshing = false;
        _hasLoadedVms = false;
        _loadFailed = false;
        _allVms.Clear();
        Vms.Clear();
        SelectedVm = null;
        IsStatusError = false;
        StatusText = string.Empty;
        IsLoading = value is not null;
        RaiseCountsChanged();
        OnPropertyChanged(nameof(ListSummaryText));
        RaiseEmptyStatesChanged();
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
        _loadCts?.Dispose();
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

            _hasLoadedVms = true;
            _loadFailed = false;
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

            _loadFailed = true;
            if (!silent)
            {
                _allVms.Clear();
                ApplyFilter();
            }
        }
        finally
        {
            // 旧来源的请求不能清除新来源仍在加载的标记。
            if (version == _loadVersion)
            {
                _refreshing = false;
                IsLoading = false;
                RaiseEmptyStatesChanged();
            }
        }
    }

    private int _loadVersion;

    private void ApplyFilter()
    {
        var keyword = SearchText.Trim();
        var filter = SelectedFilter?.Filter ?? VmwareStateFilter.All;
        var keepReference = SelectedVm?.Vm.Reference;
        Vms.Clear();
        foreach (var vm in Sort(_allVms.Where(v => MatchesFilter(v, filter) && MatchesKeyword(v, keyword))))
        {
            Vms.Add(vm);
        }

        // 保持原选中；原来的被过滤掉或还没选过时落到第一行，让右侧详情不空着。
        SelectedVm = (keepReference is null ? null : Vms.FirstOrDefault(v => v.Vm.Reference == keepReference)) ?? Vms.FirstOrDefault();

        RaiseCountsChanged();
        OnPropertyChanged(nameof(ListSummaryText));
        RaiseEmptyStatesChanged();
    }

    // ── 排序 ──────────────────────────────────────────────────────

    [ObservableProperty]
    private VmwareSortKey _sortKey = VmwareSortKey.Name;

    [ObservableProperty]
    private bool _sortDescending;

    public string NameSortGlyph => SortGlyph(VmwareSortKey.Name);
    public string IpSortGlyph => SortGlyph(VmwareSortKey.Ip);
    public string StateSortGlyph => SortGlyph(VmwareSortKey.State);
    public string MemorySortGlyph => SortGlyph(VmwareSortKey.Memory);

    private string SortGlyph(VmwareSortKey key) => SortKey != key ? string.Empty : SortDescending ? "↓" : "↑";

    /// <summary>点列头：同一列再点切换升降序，换列则从升序开始。</summary>
    [RelayCommand]
    private void SortBy(string? key)
    {
        if (!Enum.TryParse<VmwareSortKey>(key, out var next)) return;
        if (SortKey == next)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortKey = next;
            SortDescending = false;
        }

        OnPropertyChanged(nameof(NameSortGlyph));
        OnPropertyChanged(nameof(IpSortGlyph));
        OnPropertyChanged(nameof(StateSortGlyph));
        OnPropertyChanged(nameof(MemorySortGlyph));
        ApplyFilter();
    }

    private IEnumerable<VmwareVmItemViewModel> Sort(IEnumerable<VmwareVmItemViewModel> source)
    {
        // 排序键相同（或缺失，如关机的虚拟机没有 IP）时一律回落到名称，保证稳定。
        var ordered = SortKey switch
        {
            VmwareSortKey.Ip => source.OrderBy(v => IpSortKey(v.Vm.IpAddress), StringComparer.Ordinal),
            VmwareSortKey.State => source.OrderBy(v => v.IsPoweredOn ? 0 : v.IsSuspended ? 1 : 2),
            VmwareSortKey.Memory => source.OrderBy(v => v.Vm.MemoryMb ?? -1),
            _ => source.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
        };

        if (SortDescending)
        {
            ordered = SortKey switch
            {
                VmwareSortKey.Ip => source.OrderByDescending(v => IpSortKey(v.Vm.IpAddress), StringComparer.Ordinal),
                VmwareSortKey.State => source.OrderByDescending(v => v.IsPoweredOn ? 0 : v.IsSuspended ? 1 : 2),
                VmwareSortKey.Memory => source.OrderByDescending(v => v.Vm.MemoryMb ?? -1),
                _ => source.OrderByDescending(v => v.Name, StringComparer.OrdinalIgnoreCase)
            };
        }

        return ordered.ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>IPv4 补零成定长字符串后按字典序即数值序；没有 IP / 非 IPv4 排在最后。</summary>
    private static string IpSortKey(string? ip)
    {
        if (ip is null) return "~";
        var parts = ip.Split('.');
        return parts.Length == 4 && parts.All(p => int.TryParse(p, out _))
            ? string.Join('.', parts.Select(p => int.Parse(p).ToString("D3")))
            : "~" + ip;
    }

    /// <summary>表格底部汇总：筛选 / 搜索生效时显示「显示 x / 共 y 台」。</summary>
    public string ListSummaryText => _allVms.Count == 0 ? string.Empty
        : Vms.Count == _allVms.Count ? $"共 {_allVms.Count} 台" : $"显示 {Vms.Count} / 共 {_allVms.Count} 台";

    /// <summary>页面级的轻提示（如「已复制」），沿用状态栏文字并短暂保留，避免马上被静默刷新覆盖。</summary>
    public void ReportInfo(string message)
    {
        IsStatusError = false;
        StatusText = message;
        _statusHoldUntil = DateTime.UtcNow + TimeSpan.FromSeconds(4);
    }

    private static bool MatchesFilter(VmwareVmItemViewModel vm, VmwareStateFilter filter) => filter switch
    {
        VmwareStateFilter.Running => vm.IsPoweredOn,
        VmwareStateFilter.Stopped => vm.IsPoweredOff,
        VmwareStateFilter.Suspended => vm.IsSuspended,
        _ => true
    };

    /// <summary>搜索同时匹配名称、IP 与操作系统。</summary>
    private static bool MatchesKeyword(VmwareVmItemViewModel vm, string keyword)
        => keyword.Length == 0
           || vm.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
           || (vm.Vm.IpAddress?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
           || (vm.Vm.GuestOs?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false);

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
