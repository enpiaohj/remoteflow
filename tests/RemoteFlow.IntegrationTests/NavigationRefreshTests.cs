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
    private readonly StubRdpProvider _rdpProvider = new();
    private readonly StubPowerShellProvider _powerShell = new();
    private readonly FakeRemotingEnabler _remoting = new();
    private readonly NullDialogService _dialogs = new();
    private readonly FileTransferLauncher _launcher = new();
    private readonly FakeTransferConnector _connector = new();
    private readonly CredentialsPageViewModel _credentialsPage;
    private readonly EmptyConnectionRepository _connectionRepository = new();
    private readonly AppSettings _settings = new()
    {
        DefaultLandingPage = LandingPage.Home,
        PresenceProbeEnabled = false,
    };

    public NavigationRefreshTests()
    {
        var connectionsRepository = _connectionRepository;
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
            new IConnectionProvider[] { _provider, _rdpProvider },
            credentials,
            connectionsRepository,
            history,
            new NoOpSshHostKeyPolicy(),
            NullLogger<SessionManager>.Instance,
            [_powerShell]);
        var settings = _settings;
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
            NullLoggerFactory.Instance,
            remotingEnabler: _remoting);
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
    public async Task 返回入口保留离开前会话及本次实际账号且状态实时刷新()
    {
        _provider.Factory = _ => new FakeSession();
        using var credential = new ResolvedCredential { Type = CredentialType.SshPassword, Username = "operator", Domain = "EXAMPLE" };
        var session = await _sessions.CreateSessionAsync(
            new ConnectionProfile { Name = "演示主机", Host = "demo.example", Protocol = ProtocolType.Ssh, Port = 2222 },
            credentialOverride: credential);
        var tab = Assert.Single(_main.Tabs.OfType<SessionTabViewModel>());
        _main.SelectedTab = _main.WorkspaceTab;

        Assert.Same(tab, _main.ReturnTab);
        Assert.True(_main.CanReturnToSession);
        Assert.Contains("EXAMPLE\\operator", _main.ReturnTabToolTip, StringComparison.Ordinal);
        Assert.Equal(tab.TabToolTip, _main.ReturnTabToolTip);
        var changes = new List<string?>();
        _main.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        ((FakeSession)session).RaiseStateChanged(ConnectionState.Idle, ConnectionState.Connected);
        Assert.Contains("已连接", _main.ReturnTabToolTip, StringComparison.Ordinal);
        Assert.Contains(nameof(MainViewModel.ReturnTabToolTip), changes);

        _main.ReturnToSessionCommand.Execute(null);
        Assert.Same(tab, _main.SelectedTab);
        Assert.Null(_main.ReturnTab);
        Assert.False(_main.CanReturnToSession);
        await _sessions.CloseSessionAsync(session.SessionId);
    }

    [Fact]
    public async Task 返回目标关闭后改用剩余会话且全部关闭后隐藏()
    {
        var first = await _sessions.CreateSessionAsync(new ConnectionProfile
        {
            Name = "甲", Host = "a.example", Protocol = ProtocolType.Ssh
        });
        var second = await _sessions.CreateSessionAsync(new ConnectionProfile
        {
            Name = "乙", Host = "b.example", Protocol = ProtocolType.Ssh
        });
        _main.SelectedTab = _main.WorkspaceTab;
        Assert.Equal("乙", _main.ReturnTab!.Title);

        await _sessions.CloseSessionAsync(second.SessionId);
        Assert.True(_main.CanReturnToSession);
        Assert.Equal("甲", _main.ReturnTab!.Title);
        Assert.Contains("a.example", _main.ReturnTabToolTip, StringComparison.Ordinal);

        await _sessions.CloseSessionAsync(first.SessionId);
        Assert.False(_main.CanReturnToSession);
        Assert.Null(_main.ReturnTab);
        Assert.Empty(_main.ReturnTabToolTip);
    }

    [Fact]
    public void 文件传输返回入口显示传输详情()
    {
        _launcher.Open(new ConnectionProfile { Name = "文件主机", Host = "files.example", Protocol = ProtocolType.Ssh });
        var transfer = Assert.IsType<FileTransferTabViewModel>(_main.SelectedTab);
        _main.SelectedTab = _main.WorkspaceTab;

        Assert.Same(transfer, _main.ReturnTab);
        Assert.True(_main.CanReturnToSession);
        Assert.Equal(transfer.TabToolTip, _main.ReturnTabToolTip);
        Assert.Equal(0, _main.OpenSessionCount);
        _main.ReturnToSessionCommand.Execute(null);
        Assert.Same(transfer, _main.SelectedTab);
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

    [Fact]
    public async Task 连接没分配凭据时每次连接都弹框输入账号和密码()
    {
        _dialogs.SupportsConnectionCredentialPrompt = true;
        _dialogs.CredentialAnswer = new ConnectionCredentialInput("root", "", "pw-1");
        var profile = new ConnectionProfile { Name = "主机A", Host = "host-a", Port = 22, Protocol = ProtocolType.Ssh };

        await _main.OpenSessionAsync(profile);

        var request = Assert.Single(_dialogs.CredentialRequests);
        Assert.Equal(ConnectionCredentialPrompt.UsernameAndPassword, request.Requirement.Prompt);
        Assert.Equal("host-a:22", request.Target);
        Assert.Equal("root", _provider.LastCredential?.Username);
        Assert.Equal("pw-1", _provider.LastCredential?.Password);

        // 关掉会话再连：不记忆上一次输入，必须再问一次。
        var tab = Assert.Single(_main.Tabs.OfType<SessionTabViewModel>());
        await _sessions.CloseSessionAsync(tab.Session.SessionId);
        _dialogs.CredentialAnswer = new ConnectionCredentialInput("root", "", "pw-2");
        await _main.OpenSessionAsync(profile);

        Assert.Equal(2, _dialogs.CredentialRequests.Count);
        Assert.Equal("pw-2", _provider.LastCredential?.Password);
    }

    [Fact]
    public async Task 凭据只有账户名时账户名固定_只让用户输入密码()
    {
        var credential = new Credential
        {
            Name = "只有账号", Type = CredentialType.SshPassword, Username = "deploy", SecretReference = null,
        };
        _credentialRepository.Items[credential.Id] = credential;
        _dialogs.SupportsConnectionCredentialPrompt = true;
        _dialogs.CredentialAnswer = new ConnectionCredentialInput("deploy", "", "typed");

        await _main.OpenSessionAsync(new ConnectionProfile
        {
            Name = "主机B", Host = "host-b", Protocol = ProtocolType.Ssh, CredentialId = credential.Id,
        });

        var request = Assert.Single(_dialogs.CredentialRequests);
        Assert.Equal(ConnectionCredentialPrompt.PasswordOnly, request.Requirement.Prompt);
        Assert.Equal("deploy", request.Requirement.Username);
        Assert.Equal("deploy", _provider.LastCredential?.Username);
        Assert.Equal("typed", _provider.LastCredential?.Password);
    }

    [Fact]
    public async Task 用户取消输入凭据_不创建会话也不弹错误_且可再次发起()
    {
        _dialogs.SupportsConnectionCredentialPrompt = true;
        _dialogs.CredentialAnswer = null;
        var profile = new ConnectionProfile { Name = "主机C", Host = "host-c", Protocol = ProtocolType.Ssh };

        await _main.OpenSessionAsync(profile);

        Assert.Equal(0, _provider.CreatedCount);
        Assert.Empty(_main.Tabs.OfType<SessionTabViewModel>());
        Assert.Empty(_dialogs.Messages);

        // 取消后创建占位必须释放，否则再点这台设备会被当成「正在创建」而静默忽略。
        _dialogs.CredentialAnswer = new ConnectionCredentialInput("u", "", "p");
        await _main.OpenSessionAsync(profile);
        Assert.Single(_main.Tabs.OfType<SessionTabViewModel>());
    }

    [Fact]
    public async Task 平台不提供输入框时保持原有行为_直接创建会话()
    {
        _dialogs.SupportsConnectionCredentialPrompt = false;

        await _main.OpenSessionAsync(new ConnectionProfile { Name = "主机D", Host = "host-d", Protocol = ProtocolType.Ssh });

        Assert.Empty(_dialogs.CredentialRequests);
        Assert.Equal(1, _provider.CreatedCount);
        Assert.Null(_provider.LastCredential);
    }

    // ── 关闭会话确认（设置 ConfirmCloseSession）──────────────────────

    [Fact]
    public async Task 关闭仍连着的会话先确认_点否则会话保留_点是才关闭()
    {
        SimulateViewsThatConnectOnlyWhenSelected();
        await _main.OpenSessionAsync(new ConnectionProfile { Name = "生产库", Host = "h", Protocol = ProtocolType.Ssh });
        var tab = Assert.Single(_main.Tabs.OfType<SessionTabViewModel>());
        await WaitUntilAsync(() => tab.Session.State == ConnectionState.Connected);

        _dialogs.ConfirmAnswer = false;
        await tab.CloseCommand.ExecuteAsync(null);

        var asked = Assert.Single(_dialogs.Confirms);
        Assert.Contains("生产库", asked.Message);
        Assert.Single(_main.Tabs.OfType<SessionTabViewModel>());

        _dialogs.ConfirmAnswer = true;
        await tab.CloseCommand.ExecuteAsync(null);

        await WaitUntilAsync(() => _main.Tabs.OfType<SessionTabViewModel>().Count() == 0);
        Assert.Equal(2, _dialogs.Confirms.Count);
    }

    [Fact]
    public async Task 设置里关闭确认后直接关闭_已断开的会话也不打扰()
    {
        SimulateViewsThatConnectOnlyWhenSelected(static p => new VisibilityGatedSession(p) { FailOnConnect = p.Name == "坏主机" });

        // 已失败的会话：即使开着确认也直接关。
        await _main.OpenSessionAsync(new ConnectionProfile { Name = "坏主机", Host = "b", Protocol = ProtocolType.Ssh });
        var bad = Assert.Single(_main.Tabs.OfType<SessionTabViewModel>());
        await bad.Session.ConnectAsync();
        await bad.CloseCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => _main.Tabs.OfType<SessionTabViewModel>().Count() == 0);
        Assert.Empty(_dialogs.Confirms);

        // 关掉设置后，连着的会话也直接关。
        _settings.ConfirmCloseSession = false;
        await _main.OpenSessionAsync(new ConnectionProfile { Name = "好主机", Host = "g", Protocol = ProtocolType.Ssh });
        var good = Assert.Single(_main.Tabs.OfType<SessionTabViewModel>());
        await WaitUntilAsync(() => good.Session.State == ConnectionState.Connected);
        await good.CloseCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => _main.Tabs.OfType<SessionTabViewModel>().Count() == 0);
        Assert.Empty(_dialogs.Confirms);
    }

    [Fact]
    public async Task 批量关闭与关闭非当前标签都不提示_只提示当前会话()
    {
        SimulateViewsThatConnectOnlyWhenSelected();
        await _main.OpenSessionsSequentiallyAsync(NineProfiles().Take(3).ToList());
        var tabs = _main.Tabs.OfType<SessionTabViewModel>().ToList();
        await WaitUntilAsync(() => tabs.All(t => t.Session.State == ConnectionState.Connected));
        _main.SelectedTab = tabs[0];
        _dialogs.ConfirmAnswer = false; // 若误弹确认并答「否」，下面的关闭就会失败。

        // 关闭不是当前查看的标签（点其他标签上的 ×）：直接关。
        await tabs[2].CloseCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => _main.Tabs.OfType<SessionTabViewModel>().Count() == 2);

        // 批量关闭其他会话：直接关。
        await _main.CloseOtherSessionsCommand.ExecuteAsync(tabs[0]);
        await WaitUntilAsync(() => _main.Tabs.OfType<SessionTabViewModel>().Count() == 1);

        Assert.Empty(_dialogs.Confirms);
    }

    // ── 返回离开前的会话 ──────────────────────────────────────────

    [Fact]
    public async Task 从会话切到页面后可一键返回离开前的会话()
    {
        SimulateViewsThatConnectOnlyWhenSelected();
        await _main.OpenSessionAsync(new ConnectionProfile { Name = "主机A", Host = "a", Protocol = ProtocolType.Ssh });
        await _main.OpenSessionAsync(new ConnectionProfile { Name = "主机B", Host = "b", Protocol = ProtocolType.Ssh });
        var first = _main.Tabs.OfType<SessionTabViewModel>().First();
        _main.SelectedTab = first;
        Assert.False(_main.CanReturnToSession);

        // 切出去建凭据：记下离开的会话。
        _main.NavigateTo(NavigationPage.Credentials);
        Assert.Same(_main.WorkspaceTab, _main.SelectedTab);
        Assert.Same(first, _main.ReturnTab);
        Assert.True(_main.CanReturnToSession);
        Assert.Equal("返回 主机A", _main.ReturnTabLabel);

        // 页面之间来回换不丢失返回目标。
        _main.NavigateTo(NavigationPage.Home);
        Assert.Same(first, _main.ReturnTab);

        _main.ReturnToSessionCommand.Execute(null);
        Assert.Same(first, _main.SelectedTab);
        Assert.Null(_main.ReturnTab);
        Assert.False(_main.CanReturnToSession);
    }

    [Fact]
    public async Task 离开前的会话被关闭后不再提供返回入口()
    {
        SimulateViewsThatConnectOnlyWhenSelected();
        _settings.ConfirmCloseSession = false;
        await _main.OpenSessionAsync(new ConnectionProfile { Name = "主机A", Host = "a", Protocol = ProtocolType.Ssh });
        var tab = Assert.Single(_main.Tabs.OfType<SessionTabViewModel>());
        _main.NavigateTo(NavigationPage.Credentials);
        Assert.True(_main.CanReturnToSession);

        await _sessions.CloseSessionAsync(tab.Session.SessionId);

        await WaitUntilAsync(() => !_main.CanReturnToSession);
        Assert.Null(_main.ReturnTab);
    }

    // ── 记住并恢复上次的会话 ──────────────────────────────────────

    [Fact]
    public async Task 退出前记下仍开着的会话_不含凭据且按标签顺序()
    {
        SimulateViewsThatConnectOnlyWhenSelected();
        var a = new ConnectionProfile { Name = "主机A", Host = "a", Protocol = ProtocolType.Ssh };
        var b = new ConnectionProfile { Name = "主机B", Host = "b", Protocol = ProtocolType.Ssh };
        await _main.OpenSessionAsync(a);
        await _main.OpenSessionAsync(b);

        _main.SaveSessionSnapshot();

        Assert.Equal([a.Id.ToString(), b.Id.ToString()], _settings.LastSessionProfileIds);
    }

    [Fact]
    public async Task 启动时询问恢复_确认后按原顺序重新打开_已删除的连接跳过()
    {
        SimulateViewsThatConnectOnlyWhenSelected();
        var a = new ConnectionProfile { Name = "主机A", Host = "a", Protocol = ProtocolType.Ssh };
        var b = new ConnectionProfile { Name = "主机B", Host = "b", Protocol = ProtocolType.Ssh };
        _connectionRepository.Items[a.Id] = a;
        _connectionRepository.Items[b.Id] = b;
        var deleted = Guid.NewGuid();
        _settings.LastSessionProfileIds = [a.Id.ToString(), deleted.ToString(), b.Id.ToString(), "不是Guid"];
        _dialogs.ConfirmAnswer = true;

        await _main.OfferRestoreSessionsAsync();

        var asked = Assert.Single(_dialogs.Confirms);
        Assert.Contains("2 个会话", asked.Message);
        // 一行一个、带协议，不再用顿号连成一串。
        Assert.Contains("•  主机A（SSH）\n•  主机B（SSH）", asked.Message);
        Assert.Equal(
            ["主机A", "主机B"],
            _main.Tabs.OfType<SessionTabViewModel>().Select(t => t.Session.Profile.Name).ToArray());
        Assert.Empty(_settings.LastSessionProfileIds);

        // 每次启动只问一次。
        await _main.OfferRestoreSessionsAsync();
        Assert.Single(_dialogs.Confirms);
    }

    [Fact]
    public async Task 启动时选择不恢复_不开会话且记录被清空()
    {
        var a = new ConnectionProfile { Name = "主机A", Host = "a", Protocol = ProtocolType.Ssh };
        _connectionRepository.Items[a.Id] = a;
        _settings.LastSessionProfileIds = [a.Id.ToString()];
        _dialogs.ConfirmAnswer = false;

        await _main.OfferRestoreSessionsAsync();

        Assert.Single(_dialogs.Confirms);
        Assert.Empty(_main.Tabs.OfType<SessionTabViewModel>());
        Assert.Empty(_settings.LastSessionProfileIds);
    }

    [Fact]
    public async Task 关闭恢复提示的设置后不询问也不开会话()
    {
        var a = new ConnectionProfile { Name = "主机A", Host = "a", Protocol = ProtocolType.Ssh };
        _connectionRepository.Items[a.Id] = a;
        _settings.OfferRestoreSessions = false;
        _settings.LastSessionProfileIds = [a.Id.ToString()];

        await _main.OfferRestoreSessionsAsync();

        Assert.Empty(_dialogs.Confirms);
        Assert.Empty(_main.Tabs.OfType<SessionTabViewModel>());
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
    [Fact]
    public async Task 选中会话时收起左侧导航栏_回到页面时恢复()
    {
        SimulateViewsThatConnectOnlyWhenSelected();
        var changed = new List<string?>();
        _main.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.False(_main.IsNavRailHidden);

        await _main.OpenSessionAsync(new ConnectionProfile { Name = "主机A", Host = "a", Protocol = ProtocolType.Ssh });

        Assert.True(_main.IsSessionSelected);
        Assert.True(_main.IsNavRailHidden);
        Assert.Contains(nameof(MainViewModel.IsNavRailHidden), changed);

        changed.Clear();
        _main.SelectedTab = _main.WorkspaceTab;

        Assert.False(_main.IsNavRailHidden);
        Assert.Contains(nameof(MainViewModel.IsNavRailHidden), changed);
    }

    [Fact]
    public async Task 会话数角标随会话增减通知_中断时标记警示()
    {
        SimulateViewsThatConnectOnlyWhenSelected(static p => new VisibilityGatedSession(p) { FailOnConnect = p.Name == "坏主机" });
        var changed = new List<string?>();
        _main.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await _main.OpenSessionAsync(new ConnectionProfile { Name = "好主机", Host = "a", Protocol = ProtocolType.Ssh });
        Assert.Equal(1, _main.OpenSessionCount);
        Assert.Contains(nameof(MainViewModel.OpenSessionCount), changed);
        Assert.False(_main.HasInterruptedSession);

        await _main.OpenSessionAsync(new ConnectionProfile { Name = "坏主机", Host = "b", Protocol = ProtocolType.Ssh });
        var bad = _main.Tabs.OfType<SessionTabViewModel>().Single(t => t.Session.Profile.Name == "坏主机");
        changed.Clear();
        await bad.Session.ConnectAsync();

        Assert.Equal(2, _main.OpenSessionCount);
        Assert.True(_main.HasInterruptedSession);
        Assert.Contains(nameof(MainViewModel.HasInterruptedSession), changed);
    }
    [Fact]
    public async Task 虚拟机控制台会话只计入虚拟化角标_不计入连接工作台()
    {
        SimulateViewsThatConnectOnlyWhenSelected(static p => new VisibilityGatedSession(p) { FailOnConnect = p.Name == "坏控制台" });
        var changed = new List<string?>();
        _main.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await _main.OpenSessionAsync(new ConnectionProfile { Name = "普通主机", Host = "a", Protocol = ProtocolType.Ssh });
        await _main.OpenSessionAsync(new ConnectionProfile
        {
            // 角标只看 Vmware.Enabled；测试只注册了 SSH 提供者，所以用 SSH 协议承载。
            Name = "控制台", Host = "esxi", Protocol = ProtocolType.Ssh, Vmware = new VmwareOptions { Enabled = true, InstanceUuid = "50-aa" }
        });

        Assert.Equal(1, _main.ConnectionSessionCount);
        Assert.Equal(1, _main.VmwareSessionCount);
        Assert.Equal(2, _main.OpenSessionCount); // 退出确认等处仍按全部会话算
        Assert.Contains(nameof(MainViewModel.VmwareSessionCount), changed);

        // 控制台连接失败只让「虚拟化」角标转警示色。
        await _main.OpenSessionAsync(new ConnectionProfile
        {
            Name = "坏控制台", Host = "esxi", Protocol = ProtocolType.Ssh, Vmware = new VmwareOptions { Enabled = true, InstanceUuid = "50-bb" }
        });
        var bad = _main.Tabs.OfType<SessionTabViewModel>().Single(t => t.Session.Profile.Name == "坏控制台");
        await bad.Session.ConnectAsync();

        Assert.True(_main.HasInterruptedVmwareSession);
        Assert.False(_main.HasInterruptedConnectionSession);
        Assert.True(_main.HasInterruptedSession);
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

    private sealed class StubRdpProvider : IConnectionProvider
    {
        public ProtocolType Protocol => ProtocolType.Rdp;
        public int DefaultPort => 3389;
        public bool IsAvailable(out string? unavailableReason) { unavailableReason = null; return true; }
        public IRemoteSession CreateSession(SessionRequest request) => new KindSession(request.Profile, SessionKind.Default);
    }

    private sealed class StubPowerShellProvider : ISessionKindProvider
    {
        public SessionKind Kind => SessionKind.PowerShell;
        public int CreatedCount { get; private set; }
        public bool CanOpen(ConnectionProfile profile) => profile.Protocol == ProtocolType.Rdp && !profile.Vmware.Enabled;
        public bool IsAvailable(out string? unavailableReason) { unavailableReason = null; return true; }
        /// <summary>非 None 时，新建的会话连接后以该错误失败。</summary>
        public ConnectionErrorCode FailWith { get; set; }

        public IRemoteSession CreateSession(SessionRequest request)
        {
            CreatedCount++;
            return FailWith == ConnectionErrorCode.None
                ? new TerminalKindSession(request.Profile)
                : new FailingKindSession(request.Profile, FailWith);
        }
    }

    private sealed class FailingKindSession(ConnectionProfile profile, ConnectionErrorCode code)
        : KindSession(profile, SessionKind.PowerShell)
    {
        public override Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            SetState(ConnectionState.Connecting);
            ErrorCode = code;
            ErrorMessage = "测试失败";
            SetState(ConnectionState.Failed);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRemotingEnabler : IRemotingEnabler
    {
        public RemotingTargetInfo Target { get; set; } = Info();
        public TokenFilterPolicyOutcome Outcome { get; set; } = TokenFilterPolicyOutcome.Unchanged;
        public List<(string Host, string User)> Inspected { get; } = [];
        public List<(string Host, string User)> Enabled { get; } = [];

        public static RemotingTargetInfo Info(string[]? publicNetworks = null, bool uac = true, int? policy = null)
            => new("TARGETPC", "Windows 10", false, false, "Stopped", "Manual", publicNetworks ?? [], uac, policy);

        public Task<RemotingTargetInfo> InspectAsync(string host, ResolvedCredential credential, string? domain, CancellationToken ct = default)
        {
            Inspected.Add((host, credential.Username));
            return Task.FromResult(Target);
        }

        public Task<RemotingEnableResult> EnableAsync(string host, ResolvedCredential credential, string? domain, CancellationToken ct = default)
        {
            Enabled.Add((host, credential.Username));
            var after = Target with
            {
                WinRmState = "Running",
                WinRmStartMode = "Auto",
                TokenFilterPolicy = Outcome == TokenFilterPolicyOutcome.Kept ? 1 : Target.TokenFilterPolicy
            };
            return Task.FromResult(new RemotingEnableResult(Target, after, Outcome, PortOpen: true));
        }
    }

    private async Task<SessionTabViewModel> FailedPowerShellTabAsync(ConnectionErrorCode code = ConnectionErrorCode.NetworkUnreachable)
    {
        _powerShell.FailWith = code;
        _dialogs.SupportsConnectionCredentialPrompt = true;
        _dialogs.CredentialAnswer = new ConnectionCredentialInput("admin", "", "test-password", false, null, null, CredentialType.LocalPassword);
        await _main.OpenPowerShellAsync(WindowsHost());
        var tab = Assert.Single(_main.Tabs.OfType<SessionTabViewModel>());
        await tab.Session.ConnectAsync();
        return tab;
    }

    // ── 远程启用 PowerShell 远程 ─────────────────────────────────

    [Fact]
    public async Task 远程启用按钮只在PowerShell端口不通时出现()
    {
        var tab = await FailedPowerShellTabAsync();
        Assert.True(tab.CanEnableRemoting);
        await _main.CloseAllSessionsCommand.ExecuteAsync(null);

        _dialogs.ConfirmAnswer = true;
        _dialogs.Confirms.Clear();
        await _main.CloseAllSessionsCommand.ExecuteAsync(null);
        var authFailed = await FailedPowerShellTabAsync(ConnectionErrorCode.AuthenticationFailed);
        Assert.False(authFailed.CanEnableRemoting);
    }

    [Fact]
    public async Task 目标有公用网络时不启用也不询问()
    {
        _remoting.Target = FakeRemotingEnabler.Info(publicNetworks: ["以太网"]);
        var tab = await FailedPowerShellTabAsync();

        await tab.EnableRemotingCommand.ExecuteAsync(null);

        Assert.Single(_remoting.Inspected);
        Assert.Empty(_remoting.Enabled);
        Assert.Empty(_dialogs.Confirms);
        Assert.Contains("没有做任何改动", _dialogs.Messages[^1].Message);
        Assert.Empty(tab.RemotingStatus);
    }

    [Fact]
    public async Task 取消确认不启用_确认内容如实说明会改什么()
    {
        var tab = await FailedPowerShellTabAsync();
        _dialogs.ConfirmAnswer = false;

        await tab.EnableRemotingCommand.ExecuteAsync(null);

        var confirm = Assert.Single(_dialogs.Confirms);
        Assert.Contains("TARGETPC", confirm.Message);
        Assert.Contains("Enable-PSRemoting", confirm.Message);
        Assert.Contains("LocalAccountTokenFilterPolicy", confirm.Message);
        Assert.Contains("5985", confirm.Message);
        Assert.Empty(_remoting.Enabled);
    }

    [Fact]
    public async Task 确认后用同一目标与账号启用_保留的策略和撤销方法写在结果里()
    {
        _remoting.Outcome = TokenFilterPolicyOutcome.Kept;
        var tab = await FailedPowerShellTabAsync();
        var asked = 0;
        _dialogs.ConfirmAnswers = () => ++asked == 1; // 同意启用，不重新连接

        await tab.EnableRemotingCommand.ExecuteAsync(null);

        Assert.Equal(_remoting.Inspected, _remoting.Enabled);
        Assert.Equal(("win.example.test", "admin"), Assert.Single(_remoting.Enabled));
        var result = _dialogs.Confirms[^1].Message;
        Assert.Contains("已保留", result);
        Assert.Contains("Remove-ItemProperty", result);
        Assert.Contains("Disable-PSRemoting", result);
        Assert.Equal(1, _powerShell.CreatedCount);
    }

    [Fact]
    public void 目标未开UAC时确认文案说明会恢复_域电脑不提该策略()
    {
        Assert.Contains("自动恢复原状", RemotingMessages.Confirm("h", FakeRemotingEnabler.Info(uac: false)));
        var domain = FakeRemotingEnabler.Info() with { PartOfDomain = true };
        Assert.DoesNotContain("LocalAccountTokenFilterPolicy", RemotingMessages.Confirm("h", domain));
    }

    private class KindSession(ConnectionProfile profile, SessionKind kind) : RemoteSessionBase
    {
        public override ProtocolType Protocol => profile.Protocol;
        public override ConnectionProfile Profile => profile;
        public override SessionKind Kind => kind;
        public override Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            SetState(ConnectionState.Connecting);
            SetState(ConnectionState.Connected);
            return Task.CompletedTask;
        }
        protected override ValueTask PerformTeardownAsync() => ValueTask.CompletedTask;
    }

    private sealed class TerminalKindSession(ConnectionProfile profile)
        : KindSession(profile, SessionKind.PowerShell), ITerminalSession
    {
        public event EventHandler<byte[]>? DataReceived { add { } remove { } }
        public void SendInput(byte[] data) { }
        public void SendInput(string text) { }
        public void Resize(int columns, int rows, int pixelWidth, int pixelHeight) { }
    }

    private sealed class NoticeSession(ConnectionProfile profile)
        : KindSession(profile, SessionKind.PowerShell), IConnectNoticeSource
    {
        public string ConnectNotice { get; private set; } = string.Empty;
        public event EventHandler? ConnectNoticeChanged;
        public bool HasSubscribers => ConnectNoticeChanged is not null;

        public void Announce(string text)
        {
            ConnectNotice = text;
            ConnectNoticeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [Fact]
    public void 连接过程提示显示在标签上_关闭后解除订阅()
    {
        var session = new NoticeSession(WindowsHost());
        var tab = new SessionTabViewModel(session, _ => Task.CompletedTask, _ => Task.CompletedTask,
            SynchronousUiDispatcher.Instance, new ManualTimerFactory());

        session.Announce("请在管理员确认框中选择“是”");
        Assert.True(tab.HasConnectNotice);
        Assert.Equal("请在管理员确认框中选择“是”", tab.ConnectNotice);
        session.Announce(string.Empty);
        Assert.False(tab.HasConnectNotice);

        tab.Dispose();
        Assert.False(session.HasSubscribers);
    }

    private sealed class GuidedFailureSession(ConnectionProfile profile)
        : KindSession(profile, SessionKind.PowerShell), ISessionGuidanceSource
    {
        public string? ErrorDetail => "raw system error";
        public IReadOnlyList<SessionCommandHint> CommandHints { get; } = [new("在目标上运行", "Do-Something", "撤销：Undo-Something")];

        public override Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            SetState(ConnectionState.Connecting);
            ErrorCode = ConnectionErrorCode.AuthenticationFailed;
            ErrorMessage = "身份验证被拒绝";
            SetState(ConnectionState.Failed);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task 失败时的命令与原始报错单独呈现_复制全部一次带齐()
    {
        var session = new GuidedFailureSession(WindowsHost());
        using var tab = new SessionTabViewModel(session, _ => Task.CompletedTask, _ => Task.CompletedTask,
            SynchronousUiDispatcher.Instance, new ManualTimerFactory());
        Assert.False(tab.HasCommandHints);
        Assert.False(tab.HasErrorDetail);

        await session.ConnectAsync();

        Assert.True(tab.HasCommandHints);
        Assert.True(tab.HasErrorDetail);
        var text = tab.BuildDiagnosticText();
        foreach (var expected in new[] { "PowerShell", "身份验证被拒绝", "Do-Something", "Undo-Something", "raw system error" })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task 同一主机的远程桌面与PowerShell标签靠类型标记区分_名字不加后缀()
    {
        var profile = WindowsHost();
        await _main.OpenSessionAsync(profile);
        await _main.OpenPowerShellAsync(profile);

        var tabs = _main.Tabs.OfType<SessionTabViewModel>().ToList();
        Assert.Equal(["RDP", "PS"], tabs.Select(t => t.KindBadge));
        Assert.All(tabs, t => Assert.Equal(profile.Name, t.TabTitle));
        Assert.Equal(["Protocol.Rdp", "Protocol.PowerShell"], tabs.Select(t => t.KindBrushKey));
    }

    private ConnectionProfile WindowsHost()
    {
        var profile = new ConnectionProfile { Name = "Windows 主机", Host = "win.example.test", Port = 3389, Protocol = ProtocolType.Rdp };
        _connectionRepository.Items[profile.Id] = profile;
        return profile;
    }

    // ── PowerShell 会话与远程桌面相互独立 ─────────────────────────

    [Fact]
    public async Task 同一主机的远程桌面与PowerShell各开各的_再次打开只聚焦同种类()
    {
        var host = WindowsHost();
        await _main.OpenSessionAsync(host);
        await _main.OpenPowerShellAsync(host);

        var tabs = _main.Tabs.OfType<SessionTabViewModel>().ToList();
        Assert.Equal([SessionKind.Default, SessionKind.PowerShell], tabs.Select(t => t.Session.Kind).ToArray());
        Assert.Same(tabs[1], _main.SelectedTab);

        await _main.OpenSessionAsync(host);
        Assert.Same(tabs[0], _main.SelectedTab);
        await _main.OpenPowerShellAsync(host);
        Assert.Same(tabs[1], _main.SelectedTab);
        Assert.Equal(2, _main.Tabs.OfType<SessionTabViewModel>().Count());
    }

    [Fact]
    public async Task 只开着PowerShell时打开主机仍会建立远程桌面()
    {
        var host = WindowsHost();
        await _main.OpenPowerShellAsync(host);
        await _main.OpenSessionAsync(host);

        Assert.Equal(SessionKind.Default, ((SessionTabViewModel)_main.SelectedTab!).Session.Kind);
        Assert.Equal(2, _main.Tabs.OfType<SessionTabViewModel>().Count());
    }

    [Fact]
    public async Task PowerShell标签按终端呈现_不套用远程桌面的行为()
    {
        var host = WindowsHost();
        host.Rdp.StartFullScreen = true;
        await _main.OpenPowerShellAsync(host);
        var tab = Assert.Single(_main.Tabs.OfType<SessionTabViewModel>());
        var requested = new List<SessionAction>();
        tab.ActionRequested += (_, action) => requested.Add(action);

        await tab.Session.ConnectAsync();

        Assert.Equal(ProtocolType.Ssh, tab.ToolProtocol);   // 终端工具组：没有 Ctrl+Alt+Del
        Assert.Equal("PowerShell", tab.ProtocolName);
        Assert.Equal("Windows 主机 · PowerShell", tab.Title);
        Assert.Equal("win.example.test", tab.HostPortLine); // 不显示 RDP 端口
        Assert.False(tab.CanOpenPowerShell);
        Assert.DoesNotContain(SessionAction.EnterScreenFull, requested);
    }

    [Fact]
    public async Task 远程桌面标签上可以另开PowerShell_且重新连接保持各自种类()
    {
        var host = WindowsHost();
        await _main.OpenSessionAsync(host);
        var rdp = Assert.Single(_main.Tabs.OfType<SessionTabViewModel>());
        Assert.True(rdp.CanOpenPowerShell);

        await rdp.OpenPowerShellSessionCommand.ExecuteAsync(null);
        var shell = Assert.IsType<SessionTabViewModel>(_main.SelectedTab);
        Assert.Equal(SessionKind.PowerShell, shell.Session.Kind);

        await shell.ReconnectCommand.ExecuteAsync(null);

        var tabs = _main.Tabs.OfType<SessionTabViewModel>().ToList();
        Assert.Equal(2, tabs.Count);
        Assert.Contains(tabs, t => ReferenceEquals(t, rdp));
        Assert.Single(tabs, t => t.Session.Kind == SessionKind.PowerShell && !ReferenceEquals(t, shell));
        Assert.Equal(2, _powerShell.CreatedCount);
    }

    [Fact]
    public async Task 恢复上次会话不记PowerShell()
    {
        var host = WindowsHost();
        await _main.OpenPowerShellAsync(host);
        _main.SaveSessionSnapshot();
        Assert.Empty(_settings.LastSessionProfileIds);

        await _main.OpenSessionAsync(host);
        _main.SaveSessionSnapshot();
        Assert.Equal([host.Id.ToString()], _settings.LastSessionProfileIds);
    }

    [Fact]
    public async Task 凭据输入框对PowerShell不写远程桌面端口()
    {
        var host = WindowsHost();
        _dialogs.SupportsConnectionCredentialPrompt = true;
        _dialogs.CredentialAnswer = null; // 用户取消：只检查弹框内容

        await _main.OpenPowerShellAsync(host);
        await _main.OpenSessionAsync(host);

        Assert.Equal(["win.example.test（PowerShell 远程）", "win.example.test:3389"],
            _dialogs.CredentialRequests.Select(r => r.Target).ToArray());
        Assert.Equal(0, _powerShell.CreatedCount);
    }

    [Fact]
    public void 只有Windows主机连接提供PowerShell会话()
    {
        Assert.True(_sessions.SupportsSessionKind(WindowsHost(), SessionKind.PowerShell));
        Assert.False(_sessions.SupportsSessionKind(new ConnectionProfile { Protocol = ProtocolType.Ssh }, SessionKind.PowerShell));
        Assert.False(_sessions.SupportsSessionKind(WindowsHost(), SessionKind.Default));
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

        public int CreatedCount { get; private set; }

        /// <summary>最近一次创建会话时收到的凭据（用于断言「当场输入」的凭据确实传到了协议层）。</summary>
        public ResolvedCredential? LastCredential { get; private set; }

        public IRemoteSession CreateSession(SessionRequest request)
        {
            CreatedCount++;
            LastCredential = request.Credential;
            return Factory?.Invoke(request.Profile) ?? new ScriptedSession(request.Profile, FailTabCreation);
        }
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

        public Dictionary<Guid, Credential> Items { get; } = [];

        public Task<Credential?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(Items.GetValueOrDefault(id));

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
        /// <summary>按 Id 能取回的连接（恢复上次会话的测试用）；默认空。</summary>
        public Dictionary<Guid, ConnectionProfile> Items { get; } = [];

        public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConnectionProfile>>([]);

        public Task<ConnectionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(Items.GetValueOrDefault(id));

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
        /// <summary>对 ConfirmAsync 的应答，默认「否」。</summary>
        public bool ConfirmAnswer { get; set; }

        /// <summary>需要按次序给出不同应答时使用；优先于 <see cref="ConfirmAnswer"/>。</summary>
        public Func<bool>? ConfirmAnswers { get; set; }

        public List<(string Title, string Message)> Confirms { get; } = [];

        public Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", bool isDanger = false)
        {
            Confirms.Add((title, message));
            return Task.FromResult(ConfirmAnswers?.Invoke() ?? ConfirmAnswer);
        }

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

        public bool SupportsConnectionCredentialPrompt { get; set; }

        public List<ConnectionCredentialRequest> CredentialRequests { get; } = [];

        /// <summary>对「连接前输入凭据」的应答；null 表示用户取消。</summary>
        public ConnectionCredentialInput? CredentialAnswer { get; set; }

        public Task<ConnectionCredentialInput?> PromptConnectionCredentialAsync(ConnectionCredentialRequest request)
        {
            CredentialRequests.Add(request);
            return Task.FromResult(CredentialAnswer);
        }

        public string? PickFileToOpen(string title, string filter) => null;

        public string? PickFileToSave(string title, string filter, string defaultFileName) => null;

        public string? PickFolder(string title) => null;

        public Task ShowConnectionTestAsync(ConnectionProfile profile) => Task.CompletedTask;
    }
}
