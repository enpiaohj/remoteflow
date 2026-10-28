using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Application.Vmware;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Core.Vmware;
using RemoteFlow.Infrastructure.Data;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>「虚拟化」页：来源与虚拟机的实时加载、危险操作确认、控制台请求与错误提示。</summary>
public sealed class VmwarePageViewModelTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly FakeVmwareConnector _connector = new();
    private readonly RecordingDialogs _dialogs = new();
    private readonly RefreshTimerFactory _timers = new();
    private readonly VmwareService _service;
    private readonly VmwarePageViewModel _page;
    private VmwareSource _source = null!;

    public VmwarePageViewModelTests()
    {
        var database = new RemoteFlowDatabase(_workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        database.Initialize();
        var credentials = new CredentialService(
            new SqliteCredentialRepository(database), new InMemoryCredentialVault(), NullLogger<CredentialService>.Instance);
        _service = new VmwareService(_connector, new SqliteVmwareSourceRepository(database), credentials, NullLogger<VmwareService>.Instance);
        _page = new VmwarePageViewModel(_service, credentials, _dialogs, _timers);
        _connector.Vms =
        [
            new VsphereVm("vm-2", "web-02", "poweredOff", "50-02"),
            new VsphereVm("vm-1", "DB-01", "poweredOn", "50-01"),
            new VsphereVm("vm-3", "app-03", "suspended", "50-03")
        ];
    }

    public void Dispose()
    {
        _page.Dispose();
        SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    private async Task LoadWithSourceAsync()
    {
        _source = await _service.SaveSourceAsync(
            new VmwareSource { Name = "机房", Host = "esxi.example.test", CertPinSha256 = FakeVmwareConnector.Pin }, "root", "secret");
        await _page.LoadAsync();
        await WaitForVmsAsync();
    }

    /// <summary>选择来源会异步触发读取；等到列表出现或超时。</summary>
    private async Task WaitForVmsAsync()
    {
        for (var i = 0; i < 100 && _page.Vms.Count == 0 && !_page.IsStatusError; i++) await Task.Delay(20);
    }

    private static VsphereVm RichVm(string reference, string name, string power, string? os, string? ip, int cpu, int memoryMb, long? uptime = null)
        => new(reference, name, power, "50-" + reference, "guestToolsRunning", os, ip, cpu, memoryMb, uptime, "备注");

    [Fact]
    public async Task 状态筛选与数量随列表更新()
    {
        _connector.Vms =
        [
            RichVm("a", "alpha", "poweredOn", "Microsoft Windows Server 2019 (64-bit)", "203.0.113.1", 4, 8192),
            RichVm("b", "bravo", "poweredOn", "Ubuntu Linux (64-bit)", "203.0.113.2", 2, 2048),
            RichVm("c", "charlie", "poweredOff", null, null, 1, 512)
        ];
        await LoadWithSourceAsync();

        Assert.Equal([3, 2, 1, 0], _page.FilterItems.Select(i => i.Count));
        Assert.Equal("3 台虚拟机 · 2 运行中", _page.SummaryText);

        _page.SelectedFilter = _page.FilterItems.Single(i => i.Filter == VmwareStateFilter.Stopped);
        Assert.Equal(["charlie"], _page.Vms.Select(v => v.Name));

        // 数量不受筛选影响。
        Assert.Equal(2, _page.RunningCount);
    }

    [Fact]
    public async Task 搜索同时匹配名称_IP与操作系统()
    {
        _connector.Vms =
        [
            RichVm("a", "alpha", "poweredOn", "Microsoft Windows Server 2019 (64-bit)", "203.0.113.1", 4, 8192),
            RichVm("b", "bravo", "poweredOn", "Ubuntu Linux (64-bit)", "203.0.113.2", 2, 2048)
        ];
        await LoadWithSourceAsync();

        _page.SearchText = "203.0.113.2";
        Assert.Equal(["bravo"], _page.Vms.Select(v => v.Name));

        _page.SearchText = "ubuntu";
        Assert.Equal(["bravo"], _page.Vms.Select(v => v.Name));

        _page.SearchText = "ALPHA";
        Assert.Equal(["alpha"], _page.Vms.Select(v => v.Name));
    }

    [Fact]
    public async Task 刷新后保持选中的虚拟机_并默认选中第一台()
    {
        _connector.Vms =
        [
            RichVm("a", "alpha", "poweredOn", null, null, 1, 1024),
            RichVm("b", "bravo", "poweredOn", null, null, 1, 1024)
        ];
        await LoadWithSourceAsync();
        Assert.Equal("alpha", _page.SelectedVm?.Name);

        _page.SelectedVm = _page.Vms.Single(v => v.Name == "bravo");
        _page.SearchText = string.Empty; // 触发一次重建列表
        Assert.Equal("bravo", _page.SelectedVm?.Name);

        // 选中项被筛掉时落到第一行，而不是留下一个不在列表里的选中项。
        _page.SearchText = "alpha";
        Assert.Equal("alpha", _page.SelectedVm?.Name);
    }

    [Fact]
    public async Task 点列头排序_同列再点反向_IP按数值排()
    {
        _connector.Vms =
        [
            RichVm("a", "alpha", "poweredOff", null, "203.0.113.20", 1, 4096),
            RichVm("b", "bravo", "poweredOn", null, "203.0.113.3", 1, 1024),
            RichVm("c", "charlie", "poweredOn", null, null, 1, 2048)
        ];
        await LoadWithSourceAsync();
        Assert.Equal(["alpha", "bravo", "charlie"], _page.Vms.Select(v => v.Name));

        _page.SortByCommand.Execute("Ip");
        Assert.Equal(["bravo", "alpha", "charlie"], _page.Vms.Select(v => v.Name)); // 3 < 20，没有 IP 的排最后
        Assert.Equal("↑", _page.IpSortGlyph);
        Assert.Equal(string.Empty, _page.NameSortGlyph);

        _page.SortByCommand.Execute("Ip");
        Assert.Equal("↓", _page.IpSortGlyph);
        Assert.Equal("charlie", _page.Vms[0].Name);

        _page.SortByCommand.Execute("State");
        Assert.Equal(["bravo", "charlie", "alpha"], _page.Vms.Select(v => v.Name)); // 运行中在前，同状态按名称

        _page.SortByCommand.Execute("Memory");
        Assert.Equal(["bravo", "charlie", "alpha"], _page.Vms.Select(v => v.Name));
    }

    [Fact]
    public async Task 列表汇总在筛选时显示显示数与总数()
    {
        _connector.Vms =
        [
            RichVm("a", "alpha", "poweredOn", null, null, 1, 1024),
            RichVm("b", "bravo", "poweredOff", null, null, 1, 1024)
        ];
        await LoadWithSourceAsync();
        Assert.Equal("共 2 台", _page.ListSummaryText);

        _page.SearchText = "alpha";
        Assert.Equal("显示 1 / 共 2 台", _page.ListSummaryText);
    }

    [Fact]
    public void 行显示属性_系统短名_图标_内存_时长()
    {
        var win = new VmwareVmItemViewModel(RichVm("a", "w", "poweredOn", "Microsoft Windows Server 2019 (64-bit)", "203.0.113.1", 4, 6144, 90_000));
        Assert.Equal("Windows Server 2019", win.GuestOsShortText);
        Assert.Equal("DeviceIcon.WindowsServer", win.DeviceIconKey);
        Assert.Equal("6 GB", win.MemoryText);
        Assert.Equal("4 核 · 6 GB", win.CpuMemoryText);
        Assert.Equal("1 天 1 小时", win.UptimeText);

        var pc = new VmwareVmItemViewModel(RichVm("b", "p", "poweredOn", "Microsoft Windows 10 (64-bit)", null, 2, 512));
        Assert.Equal("DeviceIcon.WindowsPc", pc.DeviceIconKey);
        Assert.Equal("512 MB", pc.MemoryText);

        var linux = new VmwareVmItemViewModel(RichVm("c", "l", "poweredOff", "Ubuntu Linux (64-bit)", null, 2, 2048, 500));
        Assert.Equal("DeviceIcon.Linux", linux.DeviceIconKey);
        Assert.Equal("—", linux.UptimeText); // 已关机没有运行时长
        Assert.Equal("—", linux.IpText);

        var unknown = new VmwareVmItemViewModel(new VsphereVm("d", "u", "poweredOn", null));
        Assert.Equal("DeviceIcon.VirtualMachine", unknown.DeviceIconKey);
        Assert.Equal("—", unknown.CpuMemoryText);
    }

    [Fact]
    public async Task 没有来源时显示空状态()
    {
        await _page.LoadAsync();

        Assert.True(_page.ShowEmptySources);
        Assert.False(_page.HasSources);
        Assert.Empty(_page.Vms);
    }

    [Fact]
    public async Task 加载来源后实时列出虚拟机并按名称排序()
    {
        await LoadWithSourceAsync();

        Assert.Equal(_source.Id, _page.SelectedSource!.Source.Id);
        Assert.Equal(["app-03", "DB-01", "web-02"], _page.Vms.Select(v => v.Name).ToArray());
        Assert.Equal("运行中", _page.Vms.Single(v => v.Name == "DB-01").PowerText);
        Assert.Equal("已关机", _page.Vms.Single(v => v.Name == "web-02").PowerText);
        Assert.Equal("已挂起", _page.Vms.Single(v => v.Name == "app-03").PowerText);
    }

    [Fact]
    public void 列表直接说明VMwareTools状态且只对运行中的虚拟机提示()
    {
        var running = new VmwareVmItemViewModel(new VsphereVm("1", "a", "poweredOn", "u", "guestToolsRunning"));
        var missing = new VmwareVmItemViewModel(new VsphereVm("2", "b", "poweredOn", "u", "guestToolsNotRunning"));
        var unknown = new VmwareVmItemViewModel(new VsphereVm("3", "c", "poweredOn", "u"));
        var off = new VmwareVmItemViewModel(new VsphereVm("4", "d", "poweredOff", "u", "guestToolsNotRunning"));

        Assert.Equal("VMware Tools 运行中", running.ToolsText);
        Assert.False(running.ToolsMissing);
        Assert.Equal("未运行 VMware Tools", missing.ToolsText);
        Assert.True(missing.ToolsMissing);
        Assert.True(unknown.ToolsMissing);
        Assert.Equal(string.Empty, off.ToolsText);
        Assert.False(off.ToolsMissing);
    }

    [Fact]
    public async Task 搜索按名称过滤且不区分大小写()
    {
        await LoadWithSourceAsync();

        _page.SearchText = "db";
        Assert.Equal(["DB-01"], _page.Vms.Select(v => v.Name).ToArray());

        _page.SearchText = "";
        Assert.Equal(3, _page.Vms.Count);
    }

    [Fact]
    public async Task 运行中的虚拟机请求打开控制台并携带稳定会话Id()
    {
        await LoadWithSourceAsync();
        ConnectionProfile? opened = null;
        _page.OpenConsoleRequested += (_, profile) => opened = profile;

        _page.OpenConsoleCommand.Execute(_page.Vms.Single(v => v.Name == "DB-01"));

        Assert.NotNull(opened);
        Assert.True(opened!.Vmware.Enabled);
        Assert.Equal("50-01", opened.Vmware.InstanceUuid);
        var again = VmwareService.BuildConsoleProfile(_source, new VsphereVm("x", "改名", "poweredOn", "50-01"));
        Assert.Equal(again.Id, opened.Id);
    }

    [Fact]
    public async Task 已关机的虚拟机不打开控制台而是提示先开机()
    {
        await LoadWithSourceAsync();
        var opened = false;
        _page.OpenConsoleRequested += (_, _) => opened = true;

        _page.OpenConsoleCommand.Execute(_page.Vms.Single(v => v.Name == "web-02"));

        Assert.False(opened);
        Assert.True(_page.IsStatusError);
        Assert.Contains("开机", _page.StatusText);
    }

    [Fact]
    public async Task 开机不需要确认()
    {
        await LoadWithSourceAsync();

        await _page.PowerOnCommand.ExecuteAsync(_page.Vms.Single(v => v.Name == "web-02"));

        Assert.Empty(_dialogs.Confirms);
        Assert.Equal([("vm-2", VmPowerAction.PowerOn)], _connector.PowerCalls);
    }

    [Fact]
    public async Task 强制断电必须以警示样式确认且取消则不执行()
    {
        await LoadWithSourceAsync();
        var vm = _page.Vms.Single(v => v.Name == "DB-01");

        _dialogs.ConfirmResult = false;
        await _page.PowerOffCommand.ExecuteAsync(vm);

        var confirm = Assert.Single(_dialogs.Confirms);
        Assert.True(confirm.IsDanger);
        Assert.Contains("DB-01", confirm.Message);
        Assert.Empty(_connector.PowerCalls);

        _dialogs.ConfirmResult = true;
        await _page.PowerOffCommand.ExecuteAsync(vm);
        Assert.Equal([("vm-1", VmPowerAction.PowerOff)], _connector.PowerCalls);
    }

    [Fact]
    public async Task 强制重置同样必须警示确认()
    {
        await LoadWithSourceAsync();
        _dialogs.ConfirmResult = false;

        await _page.ResetCommand.ExecuteAsync(_page.Vms.Single(v => v.Name == "DB-01"));

        Assert.True(Assert.Single(_dialogs.Confirms).IsDanger);
        Assert.Empty(_connector.PowerCalls);
    }

    [Fact]
    public async Task 正常关机需要确认但不是警示且只报告请求已发出()
    {
        await LoadWithSourceAsync();
        _dialogs.ConfirmResult = true;

        await _page.ShutdownCommand.ExecuteAsync(_page.Vms.Single(v => v.Name == "DB-01"));

        Assert.False(Assert.Single(_dialogs.Confirms).IsDanger);
        Assert.Contains("请求", _page.StatusText);
        Assert.DoesNotContain("已完成", _page.StatusText);
    }

    [Fact]
    public async Task 取消删除来源不会删除()
    {
        await LoadWithSourceAsync();
        _dialogs.ConfirmResult = false;

        await _page.DeleteSourceCommand.ExecuteAsync(null);

        Assert.True(Assert.Single(_dialogs.Confirms).IsDanger);
        Assert.Single(await _service.GetSourcesAsync());
    }

    [Fact]
    public async Task 确认删除来源后移除且页面回到空状态()
    {
        await LoadWithSourceAsync();
        _dialogs.ConfirmResult = true;

        await _page.DeleteSourceCommand.ExecuteAsync(null);

        Assert.Empty(await _service.GetSourcesAsync());
        Assert.True(_page.ShowEmptySources);
    }

    [Fact]
    public async Task 读取失败时显示固定中文说明并清空列表()
    {
        await LoadWithSourceAsync();
        _connector.FailLogin = true;

        await _page.RefreshCommand.ExecuteAsync(null);

        Assert.True(_page.IsStatusError);
        Assert.Contains("密码", _page.StatusText);
        Assert.Empty(_page.Vms);
    }

    [Fact]
    public async Task 快照管理在没有快照时显示空状态并可创建()
    {
        await LoadWithSourceAsync();
        var manager = new VmwareSnapshotManagerViewModel(_service, _source, _connector.Vms[1], _dialogs);

        await manager.LoadAsync();
        Assert.True(manager.IsEmpty);

        manager.NewName = "升级前";
        await manager.CreateCommand.ExecuteAsync(null);
        Assert.Equal(["升级前"], _connector.CreatedSnapshots);
        Assert.Equal(string.Empty, manager.NewName);
    }

    [Fact]
    public async Task 恢复快照必须警示确认且取消不执行()
    {
        await LoadWithSourceAsync();
        _connector.Snapshots = [new VmSnapshot("snap-1", "基线", "", null, true, 0)];
        var manager = new VmwareSnapshotManagerViewModel(_service, _source, _connector.Vms[1], _dialogs);
        await manager.LoadAsync();
        manager.SelectedItem = manager.Items.Single();

        _dialogs.ConfirmResult = false;
        await manager.RevertCommand.ExecuteAsync(null);
        Assert.True(Assert.Single(_dialogs.Confirms).IsDanger);
        Assert.Null(_connector.Reverted);

        _dialogs.ConfirmResult = true;
        await manager.RevertCommand.ExecuteAsync(null);
        Assert.Equal("snap-1", _connector.Reverted);
    }

    [Fact]
    public void 来源编辑器修改地址后证书核对作废且保存被禁用()
    {
        var editor = new VmwareSourceEditorViewModel(_service,
            new VmwareSource { Name = "a", Host = "old.example.test", CertPinSha256 = FakeVmwareConnector.Pin }, []);
        Assert.True(editor.IsPinVerified);
        Assert.True(editor.CanSaveNow);

        editor.Host = "new.example.test";

        Assert.False(editor.IsPinVerified);
        Assert.False(editor.CanSaveNow);
        Assert.Equal(string.Empty, editor.CertPin);
    }

    [Fact]
    public async Task 来源编辑器获取指纹后才允许保存()
    {
        var editor = new VmwareSourceEditorViewModel(_service, null, []) { Host = "esxi.example.test" };
        Assert.False(editor.CanSaveNow);

        await editor.FetchPinCommand.ExecuteAsync(null);

        Assert.True(editor.IsPinVerified);
        Assert.Equal(FakeVmwareConnector.Pin, editor.CertPin);
        Assert.True(editor.CanSaveNow);
    }

    [Fact]
    public async Task 来源编辑器指纹按两位一组冒号分隔显示_保存值不变()
    {
        var editor = new VmwareSourceEditorViewModel(_service, null, []) { Host = "esxi.example.test" };
        Assert.False(editor.HasCertPin);
        Assert.Equal(string.Empty, editor.CertPinDisplay);

        await editor.FetchPinCommand.ExecuteAsync(null);

        Assert.True(editor.HasCertPin);
        var expected = string.Join(':', Enumerable.Range(0, FakeVmwareConnector.Pin.Length / 2)
            .Select(i => FakeVmwareConnector.Pin.Substring(i * 2, 2).ToUpperInvariant()));
        Assert.Equal(expected, editor.CertPinDisplay);
        Assert.Equal(FakeVmwareConnector.Pin, editor.CertPin); // 显示用的分组格式不能污染要保存的原值

        // 改了地址，旧指纹立即失效，显示也随之清空。
        editor.Host = "other.example.test";
        Assert.False(editor.HasCertPin);
        Assert.Equal(string.Empty, editor.CertPinDisplay);
    }

    [Fact]
    public void 快照行显示属性_层级缩进_当前标记_说明与数量()
    {
        var root = new VmwareSnapshotItem(new VmSnapshot("s1", "基线", "", null, false, 0));
        var child = new VmwareSnapshotItem(new VmSnapshot("s2", "升级前", "补丁前的状态", new DateTimeOffset(2026, 5, 13, 13, 3, 0, TimeSpan.Zero), true, 1));
        var deep = new VmwareSnapshotItem(new VmSnapshot("s3", "很深", "", null, false, 20));

        Assert.Equal(0, root.IndentLeft);
        Assert.Equal(22, child.IndentLeft);
        Assert.Equal(6 * 22, deep.IndentLeft); // 缩进有上限，层级再深也不会把名称挤出窗口
        Assert.Equal("基线", root.NameText);
        Assert.False(root.IsCurrent);
        Assert.True(child.IsCurrent);
        Assert.False(root.HasDescription);
        Assert.True(child.HasDescription);
    }

    [Fact]
    public async Task 快照管理的数量说明随列表更新()
    {
        await LoadWithSourceAsync();
        var manager = new VmwareSnapshotManagerViewModel(_service, _source, _connector.Vms[1], _dialogs);
        await manager.LoadAsync();
        Assert.Equal(string.Empty, manager.CountText);

        _connector.Snapshots = [new VmSnapshot("a", "甲", "", null, false, 0), new VmSnapshot("b", "乙", "", null, true, 1)];
        await manager.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("2 个快照", manager.CountText);
    }

    [Fact]
    public async Task 来源编辑器新建凭据时缺少密码不保存()
    {
        var editor = new VmwareSourceEditorViewModel(_service, null, []) { Host = "esxi.example.test", UserName = "root" };
        await editor.FetchPinCommand.ExecuteAsync(null);

        await editor.SaveAsync("");

        Assert.Null(editor.Saved);
        Assert.True(editor.IsStatusError);
        Assert.Equal(0, _connector.ConnectCount);
    }

    [Fact]
    public async Task 首次加载只显示加载态_成功零台后才显示来源空态()
    {
        _source = await _service.SaveSourceAsync(
            new VmwareSource { Host = "esxi.example.test", CertPinSha256 = FakeVmwareConnector.Pin }, "root", "secret");
        var pending = new TaskCompletionSource<IReadOnlyList<VsphereVm>>();
        _connector.ListVmsAsync = _ => pending.Task;
        var changed = new List<string?>();
        _page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        await _page.LoadAsync();
        Assert.True(_page.ShowLoadingVms);
        Assert.False(_page.ShowNoVms);
        Assert.False(_page.ShowNoMatches);
        Assert.False(_page.ShowLoadError);

        pending.SetResult([]);
        await WaitUntilAsync(() => !_page.IsLoading);
        Assert.True(_page.ShowNoVms);
        Assert.False(_page.ShowLoadingVms);
        Assert.False(_page.ShowLoadError);
        Assert.Contains(nameof(_page.ShowNoVms), changed);
        Assert.Contains(nameof(_page.NoSelectionDescription), changed);
    }

    [Fact]
    public async Task 无匹配重置搜索与状态筛选_保留来源和排序()
    {
        await LoadWithSourceAsync();
        var source = _page.SelectedSource;
        _page.SortByCommand.Execute("Memory");
        _page.SearchText = "missing";
        _page.SelectedFilter = _page.FilterItems[1];
        Assert.True(_page.ShowNoMatches);
        Assert.False(_page.ShowNoVms);
        Assert.True(_page.ShowNoSelection);

        _page.ResetFiltersCommand.Execute(null);
        Assert.Same(source, _page.SelectedSource);
        Assert.Same(_page.FilterItems[0], _page.SelectedFilter);
        Assert.Equal(string.Empty, _page.SearchText);
        Assert.Equal(VmwareSortKey.Memory, _page.SortKey);
        Assert.Equal(3, _page.Vms.Count);
        Assert.False(_page.ShowNoMatches);
        Assert.False(_page.ShowNoSelection);
    }

    [Fact]
    public async Task 读取失败显示恢复入口_重试成功清除错误态()
    {
        await LoadWithSourceAsync();
        _connector.FailLogin = true;
        await _page.RefreshCommand.ExecuteAsync(null);
        Assert.True(_page.ShowLoadError);
        Assert.False(_page.ShowNoVms);
        Assert.Null(_page.SelectedVm);
        Assert.True(_page.EditSourceCommand.CanExecute(null));

        _connector.FailLogin = false;
        await _page.RefreshCommand.ExecuteAsync(null);
        Assert.False(_page.ShowLoadError);
        Assert.False(_page.IsStatusError);
        Assert.Equal(3, _page.Vms.Count);
    }

    [Fact]
    public async Task 静默刷新失败保留列表与选中项_且错误不会被伪装成空态()
    {
        await LoadWithSourceAsync();
        var selected = _page.SelectedVm;
        _connector.FailLogin = true;
        _timers.Timer.Fire();
        await WaitUntilAsync(() => _page.IsStatusError);
        Assert.Equal(3, _page.Vms.Count);
        Assert.Same(selected, _page.SelectedVm);
        Assert.False(_page.ShowLoadError);
        Assert.False(_page.ShowNoVms);
        Assert.Contains("密码", _page.StatusText);
    }

    [Fact]
    public async Task 切换来源立即启动新加载_旧请求结束不能覆盖新加载状态()
    {
        await LoadWithSourceAsync();
        var other = await _service.SaveSourceAsync(
            new VmwareSource { Name = "第二来源", Host = "other.example.test", CertPinSha256 = FakeVmwareConnector.Pin }, "root", "secret");
        var oldRequest = new TaskCompletionSource<IReadOnlyList<VsphereVm>>();
        _connector.ListVmsAsync = _ => oldRequest.Task;
        var oldRefresh = _page.RefreshCommand.ExecuteAsync(null);
        var newRequest = new TaskCompletionSource<IReadOnlyList<VsphereVm>>();
        var newStarted = false;
        _connector.ListVmsAsync = _ => { newStarted = true; return newRequest.Task; };
        _page.SelectedSource = new VmwareSourceItemViewModel(other);
        await WaitUntilAsync(() => newStarted);
        Assert.True(_page.ShowLoadingVms);

        oldRequest.SetResult(_connector.Vms);
        await oldRefresh;
        Assert.True(_page.ShowLoadingVms);
        Assert.Empty(_page.Vms);
        newRequest.SetResult([]);
        await WaitUntilAsync(() => !_page.IsLoading);
        Assert.True(_page.ShowNoVms);
        Assert.Equal(other.Id, _page.SelectedSource.Source.Id);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition(), "页面未在预期时间内完成状态转换。");
    }

    private sealed class RefreshTimerFactory : RemoteFlow.Presentation.Host.IUiTimerFactory
    {
        public RefreshTimer Timer { get; } = new();
        public RemoteFlow.Presentation.Host.IUiTimer Create(TimeSpan interval) => Timer;
    }

    private sealed class RefreshTimer : RemoteFlow.Presentation.Host.IUiTimer
    {
        public TimeSpan Interval { get; set; }
        public event EventHandler? Tick;
        public void Fire() => Tick?.Invoke(this, EventArgs.Empty);
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class RecordingDialogs : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public List<(string Title, string Message, bool IsDanger)> Confirms { get; } = [];

        public Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", bool isDanger = false)
        {
            Confirms.Add((title, message, isDanger));
            return Task.FromResult(ConfirmResult);
        }

        public Task ShowMessageAsync(string title, string message, DialogKind kind = DialogKind.Info) => Task.CompletedTask;
        public Task CopyToClipboardAsync(string text) => Task.CompletedTask;
        public Task<ConnectionEditorResult?> EditConnectionAsync(ConnectionProfile? existing, RemoteFlow.Core.Models.ProtocolType? preselectedProtocol = null) => Task.FromResult<ConnectionEditorResult?>(null);
        public Task<CredentialEditorResult?> EditCredentialAsync(Credential? existing) => Task.FromResult<CredentialEditorResult?>(null);
        public Task<string?> EditGroupNameAsync(GroupNamePrompt prompt) => Task.FromResult<string?>(null);
        public Task<DefaultGroupOption?> PickDefaultGroupAsync(string deletedDefaultName, IReadOnlyList<DefaultGroupOption> options) => Task.FromResult<DefaultGroupOption?>(null);
        public Task<TagEditorResult?> EditTagAsync(TagEditorPrompt prompt) => Task.FromResult<TagEditorResult?>(null);
        public Task<IReadOnlyList<Tag>> ManageTagsAsync() => Task.FromResult<IReadOnlyList<Tag>>([]);
        public Task<bool> ConfirmHostKeyAsync(SshHostKeyVerificationContext context) => Task.FromResult(false);
        public Task<string?> PromptPasswordAsync(string title, string message, bool confirm) => Task.FromResult<string?>(null);
        public string? PickFileToOpen(string title, string filter) => null;
        public string? PickFileToSave(string title, string filter, string defaultFileName) => null;
        public string? PickFolder(string title) => null;
        public Task ShowConnectionTestAsync(ConnectionProfile profile) => Task.CompletedTask;
    }
}
