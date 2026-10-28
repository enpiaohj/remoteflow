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
        _page = new VmwarePageViewModel(_service, credentials, _dialogs, new ManualTimerFactory());
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
    public async Task 来源编辑器新建凭据时缺少密码不保存()
    {
        var editor = new VmwareSourceEditorViewModel(_service, null, []) { Host = "esxi.example.test", UserName = "root" };
        await editor.FetchPinCommand.ExecuteAsync(null);

        await editor.SaveAsync("");

        Assert.Null(editor.Saved);
        Assert.True(editor.IsStatusError);
        Assert.Equal(0, _connector.ConnectCount);
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
