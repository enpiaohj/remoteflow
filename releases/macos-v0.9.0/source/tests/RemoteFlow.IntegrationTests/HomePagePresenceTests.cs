using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Infrastructure.Data;
using RemoteFlow.Infrastructure.Settings;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>首页最近连接与收藏卡片的在线状态刷新回归。</summary>
public sealed class HomePagePresenceTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly RemoteFlowDatabase _database;
    private readonly ConnectionService _connections;
    private readonly IHistoryRepository _history;
    private readonly SessionManager _sessions;
    private readonly AppSettings _settings = new() { PresenceProbeEnabled = true };
    private readonly JsonSettingsStore _settingsStore;

    public HomePagePresenceTests()
    {
        _database = new RemoteFlowDatabase(_workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        _database.Initialize();

        var connectionRepository = new SqliteConnectionRepository(_database);
        _connections = new ConnectionService(
            connectionRepository,
            new SqliteGroupRepository(_database),
            new SqliteTagRepository(_database));
        _history = new SqliteHistoryRepository(_database);

        var credentials = new CredentialService(
            new SqliteCredentialRepository(_database),
            new InMemoryCredentialVault(),
            NullLogger<CredentialService>.Instance);
        _sessions = new SessionManager(
            new IConnectionProvider[] { new FakeSshProvider() },
            credentials,
            connectionRepository,
            _history,
            new NoOpSshHostKeyPolicy(),
            NullLogger<SessionManager>.Instance);

        _settingsStore = new JsonSettingsStore(
            Path.Combine(_workspace.Root, "settings.json"),
            NullLogger<JsonSettingsStore>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    [Fact]
    public async Task 同一设备同时出现在最近和收藏时只探测一次并同步在线状态()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var acceptCts = new CancellationTokenSource();
        var accepted = 0;
        var acceptLoop = AcceptConnectionsAsync(listener, () => Interlocked.Increment(ref accepted), acceptCts.Token);

        await _connections.CreateAsync(new ConnectionProfile
        {
            Name = "重复卡片",
            Host = "127.0.0.1",
            Port = port,
            Protocol = RemoteFlow.Core.Models.ProtocolType.Ssh,
            Favorite = true,
            LastConnectedAt = DateTimeOffset.Now,
        });

        var viewModel = CreateViewModel(new PresenceProbeService());
        await viewModel.LoadAsync();
        await WaitUntilAsync(
            () => viewModel.RecentItems.Single().Presence == PresenceState.Online
                  && viewModel.FavoriteItems.Single().Presence == PresenceState.Online);
        await Task.Delay(100);

        Assert.Equal(1, Volatile.Read(ref accepted));

        acceptCts.Cancel();
        listener.Stop();
        await IgnoreCancellationAsync(acceptLoop);
    }

    [Fact]
    public async Task 会话状态变化触发的首页刷新不重复探测也不清空在线状态()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var acceptCts = new CancellationTokenSource();
        var accepted = 0;
        var acceptLoop = AcceptConnectionsAsync(listener, () => Interlocked.Increment(ref accepted), acceptCts.Token);

        var profile = await _connections.CreateAsync(new ConnectionProfile
        {
            Name = "会话变化",
            Host = "127.0.0.1",
            Port = port,
            Protocol = RemoteFlow.Core.Models.ProtocolType.Ssh,
            Favorite = true,
        });

        var viewModel = CreateViewModel(new PresenceProbeService());
        await viewModel.LoadAsync();
        await WaitUntilAsync(() => viewModel.FavoriteItems.Single().Presence == PresenceState.Online);
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref accepted));

        // 任一会话集合变化都会触发首页 150ms 去抖全量刷新；该刷新只同步会话状态，不应重新探测。
        await _sessions.CreateSessionAsync(profile);
        await Task.Delay(600);

        Assert.Equal(1, Volatile.Read(ref accepted));
        Assert.Equal(PresenceState.Online, viewModel.FavoriteItems.Single().Presence);

        acceptCts.Cancel();
        listener.Stop();
        await IgnoreCancellationAsync(acceptLoop);
    }

    [Fact]
    public async Task 从会话标签切回首页只做轻量刷新不重新探测()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var acceptCts = new CancellationTokenSource();
        var accepted = 0;
        var acceptLoop = AcceptConnectionsAsync(listener, () => Interlocked.Increment(ref accepted), acceptCts.Token);

        await _connections.CreateAsync(new ConnectionProfile
        {
            Name = "往返",
            Host = "127.0.0.1",
            Port = port,
            Protocol = RemoteFlow.Core.Models.ProtocolType.Ssh,
            Favorite = true,
        });

        _settings.DefaultLandingPage = LandingPage.Home;
        var home = CreateViewModel(new PresenceProbeService());
        var main = new MainViewModel(
            _sessions,
            home,
            (ConnectionsPageViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ConnectionsPageViewModel)),
            new CredentialsPageViewModel(
                new CredentialService(new SqliteCredentialRepository(_database), new InMemoryCredentialVault(), NullLogger<CredentialService>.Instance),
                _connections,
                new NullDialogs()),
            (SettingsPageViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SettingsPageViewModel)),
            new NullDialogs(),
            _settings,
            SynchronousUiDispatcher.Instance,
            new ManualTimerFactory(),
            NullLogger<MainViewModel>.Instance);

        // 进入首页（启动落地页）会探测一次。
        await WaitUntilAsync(() => home.FavoriteItems.Count == 1 && home.FavoriteItems[0].Presence == PresenceState.Online);
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref accepted));

        var sessionTab = new SessionTabViewModel(new FakeSession(), _ => Task.CompletedTask, _ => Task.CompletedTask,
            SynchronousUiDispatcher.Instance, new ManualTimerFactory());
        main.Tabs.Add(sessionTab);
        main.SelectedTab = sessionTab;
        main.SelectedTab = main.WorkspaceTab;
        await Task.Delay(600);

        Assert.Equal(1, Volatile.Read(ref accepted));
        Assert.Equal(PresenceState.Online, home.FavoriteItems.Single().Presence);
        main.Dispose();

        acceptCts.Cancel();
        listener.Stop();
        await IgnoreCancellationAsync(acceptLoop);
    }

    private sealed class NullDialogs : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", bool isDanger = false) => Task.FromResult(false);
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

    private HomePageViewModel CreateViewModel(PresenceProbeService probe)
    {
        var constructor = typeof(HomePageViewModel).GetConstructor(
        [
            typeof(ConnectionService),
            typeof(IHistoryRepository),
            typeof(SessionManager),
            typeof(AppSettings),
            typeof(JsonSettingsStore),
            typeof(IUiDispatcher),
            typeof(ILogger<HomePageViewModel>),
            typeof(PresenceProbeService),
        ]);
        Assert.NotNull(constructor);

        return (HomePageViewModel)constructor.Invoke(
        [
            _connections,
            _history,
            _sessions,
            _settings,
            _settingsStore,
            SynchronousUiDispatcher.Instance,
            NullLogger<HomePageViewModel>.Instance,
            probe,
        ]);
    }

    private static async Task AcceptConnectionsAsync(
        TcpListener listener,
        Action onAccepted,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(ct);
                onAccepted();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (SocketException) when (ct.IsCancellationRequested)
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate())
        {
            Assert.True(DateTime.UtcNow < deadline, "首页在线探测未在时限内完成。");
            await Task.Delay(20);
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class FakeSshProvider : IConnectionProvider
    {
        public RemoteFlow.Core.Models.ProtocolType Protocol => RemoteFlow.Core.Models.ProtocolType.Ssh;

        public int DefaultPort => 22;

        public bool IsAvailable(out string? unavailableReason)
        {
            unavailableReason = null;
            return true;
        }

        public IRemoteSession CreateSession(SessionRequest request) => new FakeSession();
    }

    private sealed class NoOpSshHostKeyPolicy : ISshHostKeyPolicy
    {
        public SshHostKeyVerificationContext Lookup(SshHostKeyVerificationContext context) => context;

        public Task<bool> ConfirmAndRememberAsync(
            SshHostKeyVerificationContext context,
            CancellationToken cancellationToken)
            => Task.FromResult(true);
    }
}
