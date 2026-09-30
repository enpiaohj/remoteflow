using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.FileTransfer;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.FileTransfer;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Infrastructure.Settings;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>一级导航必须统一触发对应页面的数据刷新，不能只切换选中高亮。</summary>
public sealed class NavigationRefreshTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly CountingCredentialRepository _credentialRepository = new();
    private readonly MainViewModel _main;
    private readonly FileTransferLauncher _launcher = new();
    private readonly FakeTransferConnector _connector = new();
    private readonly CredentialsPageViewModel _credentialsPage;

    public NavigationRefreshTests()
    {
        var connectionsRepository = new EmptyConnectionRepository();
        var connections = new ConnectionService(
            connectionsRepository,
            new EmptyGroupRepository(),
            new EmptyTagRepository());
        var credentials = new CredentialService(
            _credentialRepository,
            new InMemoryCredentialVault(),
            NullLogger<CredentialService>.Instance);
        var history = new EmptyHistoryRepository();
        var sessions = new SessionManager(
            Array.Empty<IConnectionProvider>(),
            credentials,
            connectionsRepository,
            history,
            new NoOpSshHostKeyPolicy(),
            NullLogger<SessionManager>.Instance);
        var settings = new AppSettings
        {
            DefaultLandingPage = LandingPage.Home,
            PresenceProbeEnabled = false,
        };
        var settingsStore = new JsonSettingsStore(
            Path.Combine(_workspace.Root, "settings.json"),
            NullLogger<JsonSettingsStore>.Instance);
        var dialogs = new NullDialogService();

        var home = new HomePageViewModel(
            connections,
            history,
            sessions,
            settings,
            settingsStore,
            SynchronousUiDispatcher.Instance,
            NullLogger<HomePageViewModel>.Instance);
        _credentialsPage = new CredentialsPageViewModel(credentials, connections, dialogs);

        // 本组只验证 Home / Credentials 导航，不触发连接页或设置页内部逻辑。
        var connectionsPage = (ConnectionsPageViewModel)RuntimeHelpers.GetUninitializedObject(
            typeof(ConnectionsPageViewModel));
        var settingsPage = (SettingsPageViewModel)RuntimeHelpers.GetUninitializedObject(
            typeof(SettingsPageViewModel));

        _main = new MainViewModel(
            sessions,
            home,
            connectionsPage,
            _credentialsPage,
            settingsPage,
            dialogs,
            settings,
            SynchronousUiDispatcher.Instance,
            new NoOpTimerFactory(),
            NullLogger<MainViewModel>.Instance,
            _launcher,
            _connector,
            NullLoggerFactory.Instance);
    }

    public void Dispose()
    {
        _main.Dispose();
        _workspace.Dispose();
    }


    [Fact]
    public void 文件传输入口在工作区开Tab且同一连接只开一个()
    {
        var profile = new ConnectionProfile { Name = "主机A", Host = "host-a", Protocol = ProtocolType.Ssh };

        _launcher.Open(profile);
        var tab = Assert.Single(_main.Tabs.OfType<FileTransferTabViewModel>());
        Assert.Same(tab, _main.SelectedTab);
        Assert.True(tab.IsActive);
        Assert.Equal(1, _connector.OpenCount);

        // 切回工作区后再次打开同一连接：聚焦已有 Tab，不重复连接。
        _main.SelectedTab = _main.WorkspaceTab;
        _launcher.Open(profile);
        Assert.Single(_main.Tabs.OfType<FileTransferTabViewModel>());
        Assert.Same(tab, _main.SelectedTab);
        Assert.Equal(1, _connector.OpenCount);

        // 文件传输不是会话：不计入会话数，也不进入会话视图。
        Assert.Equal(0, _main.OpenSessionCount);
        Assert.False(_main.IsSessionSelected);
    }

    [Fact]
    public async Task 关闭文件传输Tab后回到工作区并释放连接()
    {
        var profile = new ConnectionProfile { Name = "主机B", Host = "host-b", Protocol = ProtocolType.Ssh };
        _launcher.Open(profile);
        var tab = Assert.Single(_main.Tabs.OfType<FileTransferTabViewModel>());

        await tab.CloseCommand.ExecuteAsync(null);

        Assert.Empty(_main.Tabs.OfType<FileTransferTabViewModel>());
        Assert.Same(_main.WorkspaceTab, _main.SelectedTab);
        Assert.True(_connector.LastFileSystem!.Disposed);
    }

    [Fact]
    public void 不同连接各开各的文件传输Tab()
    {
        _launcher.Open(new ConnectionProfile { Name = "甲", Host = "a", Protocol = ProtocolType.Ssh });
        _launcher.Open(new ConnectionProfile { Name = "乙", Host = "b", Protocol = ProtocolType.Ssh });

        Assert.Equal(2, _main.Tabs.OfType<FileTransferTabViewModel>().Count());
        Assert.Equal("传输 · 乙", _main.SelectedTab!.Title);
    }

    private sealed class FakeTransferConnector : IFileTransferConnector
    {
        public int OpenCount { get; private set; }

        public FakeRemoteFileSystem? LastFileSystem { get; private set; }

        public FileTransferSupport GetSupport(ConnectionProfile profile) => new(true, FileTransferChannel.Sftp, null);

        public Task<IRemoteFileSystem> OpenAsync(ConnectionProfile profile, CancellationToken cancellationToken)
        {
            OpenCount++;
            LastFileSystem = new FakeRemoteFileSystem();
            return Task.FromResult<IRemoteFileSystem>(LastFileSystem);
        }
    }

    [Fact]
    public void CurrentPage绑定切换与同页程序导航都各刷新一次()
    {
        Assert.Equal(0, _credentialRepository.GetAllCount);

        // 模拟 RadioButton TwoWay / UI Automation 只写 CurrentPage 的路径。
        _main.CurrentPage = NavigationPage.Credentials;

        Assert.Same(_credentialsPage, _main.WorkspaceTab.Page);
        Assert.Equal(1, _credentialRepository.GetAllCount);

        // 同页点击没有属性变化，NavigateTo 必须显式重刷一次。
        _main.NavigateTo(NavigationPage.Credentials);

        Assert.Equal(2, _credentialRepository.GetAllCount);
    }

    [Fact]
    public void 从会话Tab返回工作区时刷新当前页面()
    {
        _main.CurrentPage = NavigationPage.Credentials;
        Assert.Equal(1, _credentialRepository.GetAllCount);

        var sessionTab = new SessionTabViewModel(
            new FakeSession(),
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            SynchronousUiDispatcher.Instance,
            new NoOpTimerFactory());
        _main.Tabs.Add(sessionTab);
        _main.SelectedTab = sessionTab;

        _main.SelectedTab = _main.WorkspaceTab;

        Assert.Equal(2, _credentialRepository.GetAllCount);
    }

    private sealed class CountingCredentialRepository : ICredentialRepository
    {
        public int GetAllCount { get; private set; }

        public Task<IReadOnlyList<Credential>> GetAllAsync(CancellationToken ct = default)
        {
            GetAllCount++;
            return Task.FromResult<IReadOnlyList<Credential>>([]);
        }

        public Task<Credential?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<Credential?>(null);

        public Task AddAsync(Credential credential, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateAsync(Credential credential, CancellationToken ct = default) => Task.CompletedTask;

        public Task SetSecretReferencesAsync(
            Guid id,
            string? passwordReference,
            string? privateKeyReference,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class EmptyConnectionRepository : IConnectionRepository
    {
        public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConnectionProfile>>([]);

        public Task<ConnectionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<ConnectionProfile?>(null);

        public Task AddAsync(ConnectionProfile profile, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateAsync(ConnectionProfile profile, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;

        public Task TouchLastConnectedAsync(Guid id, DateTimeOffset when, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<int> CountByCredentialAsync(Guid credentialId, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> CountByGroupAsync(Guid groupId, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private sealed class EmptyGroupRepository : IGroupRepository
    {
        public Task<IReadOnlyList<ConnectionGroup>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConnectionGroup>>([]);

        public Task AddAsync(ConnectionGroup group, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateAsync(ConnectionGroup group, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(Guid id, Guid? moveConnectionsTo, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class EmptyTagRepository : ITagRepository
    {
        public Task<IReadOnlyList<Tag>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Tag>>([]);

        public Task AddAsync(Tag tag, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateAsync(Tag tag, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class EmptyHistoryRepository : IHistoryRepository
    {
        public Task<IReadOnlyList<ConnectionHistoryEntry>> GetRecentAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConnectionHistoryEntry>>([]);

        public Task<IReadOnlyList<ConnectionHistoryEntry>> GetByConnectionAsync(
            Guid connectionId,
            int limit,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConnectionHistoryEntry>>([]);

        public Task<int> CountByConnectionAsync(Guid connectionId, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task AddAsync(ConnectionHistoryEntry entry, CancellationToken ct = default) => Task.CompletedTask;

        public Task CompleteAsync(
            Guid entryId,
            DateTimeOffset endedAt,
            ConnectionResult result,
            ConnectionErrorCode errorCode,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpSshHostKeyPolicy : ISshHostKeyPolicy
    {
        public SshHostKeyVerificationContext Lookup(SshHostKeyVerificationContext context) => context;

        public Task<bool> ConfirmAndRememberAsync(
            SshHostKeyVerificationContext context,
            CancellationToken cancellationToken)
            => Task.FromResult(true);
    }

    private sealed class NoOpTimerFactory : IUiTimerFactory
    {
        public IUiTimer Create(TimeSpan interval) => new NoOpTimer { Interval = interval };
    }

    private sealed class NoOpTimer : IUiTimer
    {
        public TimeSpan Interval { get; set; }

        public event EventHandler? Tick
        {
            add { }
            remove { }
        }

        public void Start() { }

        public void Stop() { }

        public void Dispose() { }
    }

    private sealed class NullDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", bool isDanger = false)
            => Task.FromResult(false);

        public Task ShowMessageAsync(string title, string message, DialogKind kind = DialogKind.Info)
            => Task.CompletedTask;

        public Task CopyToClipboardAsync(string text) => Task.CompletedTask;

        public Task<ConnectionEditorResult?> EditConnectionAsync(
            ConnectionProfile? existing,
            RemoteFlow.Core.Models.ProtocolType? preselectedProtocol = null)
            => Task.FromResult<ConnectionEditorResult?>(null);

        public Task<CredentialEditorResult?> EditCredentialAsync(Credential? existing)
            => Task.FromResult<CredentialEditorResult?>(null);

        public Task<string?> EditGroupNameAsync(GroupNamePrompt prompt) => Task.FromResult<string?>(null);

        public Task<DefaultGroupOption?> PickDefaultGroupAsync(
            string deletedDefaultName,
            IReadOnlyList<DefaultGroupOption> options)
            => Task.FromResult<DefaultGroupOption?>(null);

        public Task<TagEditorResult?> EditTagAsync(TagEditorPrompt prompt)
            => Task.FromResult<TagEditorResult?>(null);

        public Task<IReadOnlyList<Tag>> ManageTagsAsync()
            => Task.FromResult<IReadOnlyList<Tag>>([]);

        public Task<bool> ConfirmHostKeyAsync(SshHostKeyVerificationContext context)
            => Task.FromResult(false);

        public Task<string?> PromptPasswordAsync(string title, string message, bool confirm)
            => Task.FromResult<string?>(null);

        public string? PickFileToOpen(string title, string filter) => null;

        public string? PickFileToSave(string title, string filter, string defaultFileName) => null;

        public string? PickFolder(string title) => null;

        public Task ShowConnectionTestAsync(ConnectionProfile profile) => Task.CompletedTask;
    }
}
