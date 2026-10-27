using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Infrastructure.Data;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>凭据页右键菜单动作：创建副本、新建主机连接（直接引用此凭据，连接名预填账户名）、空白处新建。</summary>
public sealed class CredentialsPageActionsTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly CredentialService _credentials;
    private readonly RecordingDialogs _dialogs = new();
    private readonly CredentialsPageViewModel _page;

    public CredentialsPageActionsTests()
    {
        var database = new RemoteFlowDatabase(_workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        database.Initialize();
        _credentials = new CredentialService(
            new SqliteCredentialRepository(database),
            new InMemoryCredentialVault(),
            NullLogger<CredentialService>.Instance);
        var connections = new ConnectionService(
            new SqliteConnectionRepository(database),
            new SqliteGroupRepository(database),
            new SqliteTagRepository(database));
        _page = new CredentialsPageViewModel(_credentials, connections, _dialogs);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    [Fact]
    public async Task 类型筛选偏离默认值才显示清除筛选_清除后恢复全部且保留搜索词()
    {
        await _credentials.CreateAsync(
            new Credential { Name = "域账号", Type = CredentialType.WindowsDomain, Domain = "CORP", Username = "ops" }, "pw", null);
        await _credentials.CreateAsync(
            new Credential { Name = "跳板机", Type = CredentialType.SshPassword, Username = "root" }, "pw", null);
        await _page.LoadAsync();
        Assert.False(_page.HasActiveFilters);

        var raised = new List<string?>();
        _page.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        _page.TypeOption = _page.TypeOptions.Single(o => o.Value == CredentialTypeFilter.SshPassword);
        Assert.True(_page.HasActiveFilters);
        Assert.Contains(nameof(_page.HasActiveFilters), raised);
        Assert.Equal("跳板机", _page.Items.Single().Name);

        _page.SearchText = "跳";
        _page.ClearFiltersCommand.Execute(null);

        Assert.False(_page.HasActiveFilters);
        Assert.Equal(CredentialTypeFilter.All, _page.TypeOption.Value);
        Assert.Equal("跳", _page.SearchText);
    }

    [Fact]
    public async Task 创建副本后列表多一条并选中副本()
    {
        var source = await _credentials.CreateAsync(
            new Credential { Name = "域管理员", Type = CredentialType.WindowsDomain, Domain = "CORP", Username = "admin" },
            "pw",
            null);
        await _page.LoadAsync();
        var item = _page.Items.Single();

        await _page.DuplicateCommand.ExecuteAsync(item);

        Assert.Equal(2, _page.Items.Count);
        Assert.Equal("域管理员 - 副本", _page.SelectedItem!.Name);
        Assert.NotEqual(source.Id, _page.SelectedItem.Id);
        Assert.Null(_dialogs.LastMessage);
    }

    [Fact]
    public async Task 创建副本时有筛选也能选中副本()
    {
        await _credentials.CreateAsync(
            new Credential { Name = "A", Type = CredentialType.LocalPassword, Username = "u" }, "x", null);
        await _page.LoadAsync();
        var item = _page.Items.Single();

        // 页内有搜索词时复制：副本仍应出现在列表中并被选中（复制前会清掉搜索 / 类型筛选）。
        _page.SearchText = "A";
        await _page.DuplicateCommand.ExecuteAsync(item);

        Assert.Equal("A - 副本", _page.SelectedItem?.Name);
    }

    [Fact]
    public async Task 引用账户时连接名预填为账户名并按凭据类型预选协议()
    {
        var credential = await _credentials.CreateAsync(
            new Credential { Name = "运维域账号", Type = CredentialType.WindowsDomain, Domain = "CORP", Username = "ops" },
            "pw",
            null);
        await _page.LoadAsync();

        ConnectionEditorSeed? requested = null;
        _page.NewConnectionRequested += (_, seed) => requested = seed;

        _page.CreateConnectionFromCredential(_page.Items.Single(), useAccountAsName: true);

        Assert.NotNull(requested);
        Assert.Equal(credential.Id, requested.CredentialId);
        Assert.Equal("CORP\\ops", requested.Name);
        Assert.Equal(ProtocolType.Rdp, requested.Protocol);
    }

    [Fact]
    public async Task 引用名称时连接名预填为凭据名称()
    {
        var credential = await _credentials.CreateAsync(
            new Credential { Name = "生产跳板机", Type = CredentialType.SshPassword, Username = "root" }, "pw", null);
        await _page.LoadAsync();

        ConnectionEditorSeed? requested = null;
        _page.NewConnectionRequested += (_, seed) => requested = seed;

        _page.CreateConnectionFromCredential(_page.Items.Single(), useAccountAsName: false);

        Assert.Equal(credential.Id, requested!.CredentialId);
        Assert.Equal("生产跳板机", requested.Name);
        Assert.Equal(ProtocolType.Ssh, requested.Protocol);
    }

    [Fact]
    public async Task VNC凭据没有用户名时引用账户退回凭据名称()
    {
        await _credentials.CreateAsync(
            new Credential { Name = "机房 VNC", Type = CredentialType.VncPassword }, "pw", null);
        await _page.LoadAsync();

        ConnectionEditorSeed? requested = null;
        _page.NewConnectionRequested += (_, seed) => requested = seed;

        _page.CreateConnectionFromCredential(_page.Items.Single(), useAccountAsName: true);

        Assert.Equal("机房 VNC", requested!.Name);
        Assert.Equal(ProtocolType.Vnc, requested.Protocol);
    }

    [Fact]
    public void 空白处新建主机连接不带任何预填()
    {
        var raised = false;
        ConnectionEditorSeed? seed = new(Guid.NewGuid());
        _page.NewConnectionRequested += (_, s) =>
        {
            raised = true;
            seed = s;
        };

        _page.CreateConnectionCommand.Execute(null);

        Assert.True(raised);
        Assert.Null(seed);
    }

    [Fact]
    public void 编辑器按预填带出凭据与名称且协议用预选值()
    {
        var credential = new Credential { Name = "运维", Type = CredentialType.SshPassword, Username = "ops" };
        var other = new Credential { Name = "其它", Type = CredentialType.SshPassword, Username = "x" };

        var editor = new ConnectionEditorViewModel(
            null,
            [other, credential],
            [],
            [],
            new AppSettings(),
            null,
            null,
            new ConnectionEditorSeed(credential.Id, "ops", ProtocolType.Ssh));

        Assert.Equal("ops", editor.Name);
        Assert.Equal(credential.Id, editor.SelectedCredential.Id);
    }

    [Fact]
    public void 预填的凭据不存在时忽略而不是选到未知项()
    {
        var editor = new ConnectionEditorViewModel(
            null,
            [new Credential { Name = "只有这个", Type = CredentialType.LocalPassword, Username = "u" }],
            [],
            [],
            new AppSettings(),
            null,
            null,
            new ConnectionEditorSeed(Guid.NewGuid(), "x"));

        Assert.Null(editor.SelectedCredential.Id);
    }

    private sealed class RecordingDialogs : IDialogService
    {
        public string? LastMessage { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", bool isDanger = false) => Task.FromResult(false);
        public Task ShowMessageAsync(string title, string message, DialogKind kind = DialogKind.Info)
        {
            LastMessage = message;
            return Task.CompletedTask;
        }
        public Task CopyToClipboardAsync(string text) => Task.CompletedTask;
        public Task<ConnectionEditorResult?> EditConnectionAsync(ConnectionProfile? existing, ProtocolType? preselectedProtocol = null) => Task.FromResult<ConnectionEditorResult?>(null);
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
