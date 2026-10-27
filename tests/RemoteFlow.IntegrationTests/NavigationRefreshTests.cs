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
    private readonly SessionManager _sessions;
    private readonly ScriptedSshProvider _provider = new();
    private readonly NullDialogService _dialogs = new();
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
        var sessions = _sessions = new SessionManager(
            new IConnectionProvider[] { _provider },
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
        var dialogs = _dialogs;

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

    [Fact]
    public async Task 会话Tab创建失败时关闭该会话并释放创建占位()
    {
        var profile = new ConnectionProfile { Name = "主机C", Host = "host-c", Protocol = ProtocolType.Ssh };

        _provider.FailTabCreation = true;
        await _main.OpenSessionAsync(profile);

        // Tab 没建出来的会话不能留在管理器里：否则状态栏 / 托盘会多算，而工作区里看不到也关不掉。
        await WaitUntilAsync(() => _sessions.ActiveSessionCount == 0);
        Assert.Empty(_main.Tabs.OfType<SessionTabViewModel>());

        // 创建占位也要释放，否则这台设备此后再点会被当成「正在创建」而静默忽略。
        _provider.FailTabCreation = false;
        await _main.OpenSessionAsync(profile);

        Assert.Single(_main.Tabs.OfType<SessionTabViewModel>());
        Assert.Equal(1, _sessions.ActiveSessionCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "等待条件超时。");
    }

    /// <summary>
    /// 模拟 RDP / SSH 画面控件：只有 Tab 被选中（可见）时才会开始连接，隐藏的 Tab 一直停在「准备中」。
    /// 批量连接若连发打开会话，每个新 Tab 只被选中一瞬间，来不及开始就被顶掉。
    /// </summary>
    private void SimulateViewsThatConnectOnlyWhenSelected(Func<ConnectionProfile, IRemoteSession>? factory = null)
    {
        _provider.Factory = factory ?? (static p => new VisibilityGatedSession(p));
        _main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.SelectedTab)
                || _main.SelectedTab is not SessionTabViewModel { Session: VisibilityGatedSession session } tab)
            {
                return;
            }

            // 画面控件的 Loaded 发生在选中之后、布局渲染完成时：这段时间里若已被切走，就不会开始连接。
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                if (ReferenceEquals(_main.SelectedTab, tab))
                {
                    session.OnBecameVisible();
                }
            });
        };
    }

    [Fact]
    public async Task 对照_连发打开会话时被顶掉的Tab停在准备中()
    {
        SimulateViewsThatConnectOnlyWhenSelected();

        // 旧的批量连接写法：不等待、连发打开。
        foreach (var profile in NineProfiles())
        {
            await _main.OpenSessionAsync(profile);
        }

        await Task.Delay(400);

        var tabs = _main.Tabs.OfType<SessionTabViewModel>().ToList();
        Assert.Equal(9, tabs.Count);
        Assert.Contains(tabs, t => t.Session.State == ConnectionState.Idle);
    }

    private static List<ConnectionProfile> NineProfiles()
        => Enumerable.Range(1, 9)
            .Select(i => new ConnectionProfile { Name = $"主机{i}", Host = $"host-{i}", Protocol = ProtocolType.Ssh })
            .ToList();

    [Fact]
    public async Task 批量连接逐个开始_九台全部离开准备中()
    {
        SimulateViewsThatConnectOnlyWhenSelected();

        await _main.OpenSessionsSequentiallyAsync(NineProfiles());

        var tabs = _main.Tabs.OfType<SessionTabViewModel>().ToList();
        Assert.Equal(9, tabs.Count);
        Assert.All(tabs, t => Assert.NotEqual(ConnectionState.Idle, t.Session.State));
        Assert.Equal(9, tabs.Select(t => t.Session.Profile.Id).Distinct().Count());
    }

    [Fact]
    public async Task 批量连接结束后回到这批第一个会话()
    {
        SimulateViewsThatConnectOnlyWhenSelected();
        var profiles = NineProfiles();

        await _main.OpenSessionsSequentiallyAsync(profiles);

        var selected = Assert.IsType<SessionTabViewModel>(_main.SelectedTab);
        Assert.Equal(profiles[0].Id, selected.Session.Profile.Id);
    }

    [Fact]
    public async Task 批量连接遇到已有会话的设备只聚焦不重复建()
    {
        SimulateViewsThatConnectOnlyWhenSelected();
        var profiles = NineProfiles();
        await _main.OpenSessionAsync(profiles[2]);

        await _main.OpenSessionsSequentiallyAsync(profiles);

        var tabs = _main.Tabs.OfType<SessionTabViewModel>().ToList();
        Assert.Equal(9, tabs.Count);
        Assert.Equal(1, tabs.Count(t => t.Session.Profile.Id == profiles[2].Id));
    }

    [Fact]
    public async Task 批量连接中某个会话迟迟不开始也不会卡住整批()
    {
        // 第 3 个永远不开始连接（模拟异常）：超时后继续，其余照常。
        SimulateViewsThatConnectOnlyWhenSelected(static p => new VisibilityGatedSession(p) { NeverStarts = p.Name == "主机3" });

        _main.BatchStartTimeout = TimeSpan.FromMilliseconds(300);
        _main.BatchSettleTimeout = TimeSpan.FromMilliseconds(300);

        await _main.OpenSessionsSequentiallyAsync(NineProfiles()).WaitAsync(TimeSpan.FromSeconds(30));

        var tabs = _main.Tabs.OfType<SessionTabViewModel>().ToList();
        Assert.Equal(9, tabs.Count);
        Assert.Equal(8, tabs.Count(t => t.Session.State != ConnectionState.Idle));
    }

    [Fact]
    public async Task 批量连接_创建失败的主机不逐个弹框_结束后汇总一次()
    {
        _main.BatchSettleTimeout = TimeSpan.FromMilliseconds(300);
        SimulateViewsThatConnectOnlyWhenSelected(static p => p.Name is "主机2" or "主机5"
            ? throw new ConnectionException(ConnectionErrorCode.ComponentUnavailable, "找不到协议组件")
            : new VisibilityGatedSession(p));

        await _main.OpenSessionsSequentiallyAsync(NineProfiles());

        Assert.Equal(7, _main.Tabs.OfType<SessionTabViewModel>().Count());
        var message = Assert.Single(_dialogs.Messages);
        Assert.Equal("批量连接", message.Title);
        Assert.Contains("已发起 7 台连接", message.Message);
        Assert.Contains("2 台未能连接", message.Message);
        Assert.Contains("主机2：找不到协议组件", message.Message);
        Assert.Contains("主机5：找不到协议组件", message.Message);
    }

    [Fact]
    public async Task 批量连接_全部成功时不弹任何提示()
    {
        SimulateViewsThatConnectOnlyWhenSelected();

        await _main.OpenSessionsSequentiallyAsync(NineProfiles());

        Assert.Empty(_dialogs.Messages);
    }

    [Fact]
    public async Task 批量连接_达到并发上限时提前停止并只提示一次()
    {
        _main.BatchSettleTimeout = TimeSpan.FromMilliseconds(300);
        _sessions.MaxConcurrentSessions = 3;
        SimulateViewsThatConnectOnlyWhenSelected();

        await _main.OpenSessionsSequentiallyAsync(NineProfiles());

        Assert.Equal(3, _main.Tabs.OfType<SessionTabViewModel>().Count());
        var message = Assert.Single(_dialogs.Messages);
        Assert.Contains("并发会话上限（3 个）", message.Message);
        Assert.Contains("6 台没有打开", message.Message);
        Assert.Contains("主机4", message.Message);
    }

    [Fact]
    public async Task 批量连接_连接中途失败的会话也并入汇总并保留标签页()
    {
        _main.BatchSettleTimeout = TimeSpan.FromSeconds(3);
        SimulateViewsThatConnectOnlyWhenSelected(static p => new VisibilityGatedSession(p) { FailOnConnect = p.Name == "主机4" });

        await _main.OpenSessionsSequentiallyAsync(NineProfiles());

        Assert.Equal(9, _main.Tabs.OfType<SessionTabViewModel>().Count());
        var message = Assert.Single(_dialogs.Messages);
        Assert.Contains("已发起 8 台连接", message.Message);
        Assert.Contains("1 台未能连接", message.Message);
        Assert.Contains("主机4：远端拒绝", message.Message);
        Assert.Contains("重新连接", message.Message);
    }

    [Fact]
    public async Task 批量连接_失败太多时只列前几条并说明还有多少()
    {
        _main.BatchSettleTimeout = TimeSpan.FromMilliseconds(300);
        SimulateViewsThatConnectOnlyWhenSelected(static p =>
            throw new ConnectionException(ConnectionErrorCode.ComponentUnavailable, "不可用"));

        await _main.OpenSessionsSequentiallyAsync(NineProfiles());

        var message = Assert.Single(_dialogs.Messages);
        Assert.Contains("没有成功发起任何连接", message.Message);
        Assert.Contains("9 台未能连接", message.Message);
        Assert.Contains("另有 1 台", message.Message);
    }

    [Fact]
    public async Task 单个连接创建失败时仍然直接弹框()
    {
        _provider.Factory = static _ => throw new ConnectionException(ConnectionErrorCode.ComponentUnavailable, "找不到协议组件");

        await _main.OpenSessionAsync(new ConnectionProfile { Name = "单台", Host = "h", Protocol = ProtocolType.Ssh });

        var message = Assert.Single(_dialogs.Messages);
        Assert.Equal("无法建立连接", message.Title);
        Assert.Contains("找不到协议组件", message.Message);
    }
    private sealed class VisibilityGatedSession(ConnectionProfile profile) : RemoteSessionBase
    {
        public bool NeverStarts { get; init; }

        public bool FailOnConnect { get; init; }

        public override ProtocolType Protocol => profile.Protocol;

        public override ConnectionProfile Profile => profile;

        public override Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            SetState(ConnectionState.Connecting);
            if (FailOnConnect)
            {
                ErrorCode = ConnectionErrorCode.RemoteClosed;
                ErrorMessage = "远端拒绝";
                SetState(ConnectionState.Failed);
                return Task.CompletedTask;
            }

            SetState(ConnectionState.Connected);
            return Task.CompletedTask;
        }

        /// <summary>画面控件 Loaded：首次可见时才发起连接。</summary>
        public void OnBecameVisible()
        {
            if (!NeverStarts && State == ConnectionState.Idle)
            {
                _ = ConnectAsync();
            }
        }

        protected override ValueTask PerformTeardownAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedSshProvider : IConnectionProvider
    {
        public Func<ConnectionProfile, IRemoteSession>? Factory { get; set; }

        public bool FailTabCreation { get; set; }

        public ProtocolType Protocol => ProtocolType.Ssh;

        public int DefaultPort => 22;

        public bool IsAvailable(out string? unavailableReason)
        {
            unavailableReason = null;
            return true;
        }

        public IRemoteSession CreateSession(SessionRequest request)
            => Factory?.Invoke(request.Profile) ?? new ScriptedSession(request.Profile, FailTabCreation);
    }

    /// <summary>
    /// 管理器订阅 StateChanged 是第 1 次，Tab 订阅是第 2 次；<paramref name="failSecondSubscriber"/> 时第 2 次抛出，
    /// 以此模拟 SessionCreated 处理里 Tab 构造失败。
    /// </summary>
    private sealed class ScriptedSession(ConnectionProfile profile, bool failSecondSubscriber) : IRemoteSession
    {
        private int _subscribers;
        private EventHandler<SessionStateChangedEventArgs>? _stateChanged;

        public Guid SessionId { get; } = Guid.NewGuid();

        public ProtocolType Protocol => profile.Protocol;

        public ConnectionProfile Profile => profile;

        public ConnectionState State => ConnectionState.Idle;

        public ConnectionErrorCode ErrorCode => ConnectionErrorCode.None;

        public string? ErrorMessage => null;

        public event EventHandler<SessionStateChangedEventArgs>? StateChanged
        {
            add
            {
                if (failSecondSubscriber && ++_subscribers == 2)
                {
                    throw new InvalidOperationException("模拟 Tab 构造失败。");
                }

                _stateChanged += value;
            }
            remove => _stateChanged -= value;
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

        public List<(string Title, string Message, DialogKind Kind)> Messages { get; } = [];

        public Task ShowMessageAsync(string title, string message, DialogKind kind = DialogKind.Info)
        {
            Messages.Add((title, message, kind));
            return Task.CompletedTask;
        }

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
