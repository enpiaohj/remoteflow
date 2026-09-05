using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Infrastructure.Data;
using RemoteFlow.Infrastructure.Security;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// SessionManager 统一关闭模板（spec §4）的簿记 / 幂等回归：用内存 Fake 会话驱动
/// 真实 SessionManager 关闭编排，验证事件计数精确、重复关闭幂等、失败收敛到 Closed、
/// teardown 挂起时仍能在总超时后被强制收敛且不抛。
/// </summary>
public sealed class SessionManagerLifecycleTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly RemoteFlowDatabase _database;
    private readonly SessionManager _manager;
    private readonly FakeProvider _provider;
    private int _created;
    private int _closed;

    public SessionManagerLifecycleTests()
    {
        _database = new RemoteFlowDatabase(_workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        _database.Initialize();

        var credentials = new CredentialService(
            new SqliteCredentialRepository(_database),
            new DpapiCredentialVault(_workspace.VaultPath, NullLogger<DpapiCredentialVault>.Instance),
            NullLogger<CredentialService>.Instance);

        _provider = new FakeProvider(ProtocolType.Vnc);
        _manager = new SessionManager(
            new IConnectionProvider[] { _provider },
            credentials,
            new SqliteConnectionRepository(_database),
            new SqliteHistoryRepository(_database),
            new NoOpSshHostKeyPolicy(),
            NullLogger<SessionManager>.Instance);

        _manager.SessionCreated += (_, _) => _created++;
        _manager.SessionClosed += (_, _) => _closed++;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    [Fact]
    public async Task 循环25次_簿记与事件精确()
    {
        const int n = 25;
        _manager.MaxConcurrentSessions = n + 10;

        var sessions = new List<IRemoteSession>(n);
        for (var i = 0; i < n; i++)
        {
            sessions.Add(await _manager.CreateSessionAsync(NewProfile($"循环{i}")));
        }

        Assert.Equal(n, _created);
        Assert.Equal(n, _manager.ActiveSessionCount);

        foreach (var session in sessions)
        {
            await _manager.CloseSessionAsync(session.SessionId);
        }

        Assert.Equal(0, _manager.ActiveSessionCount);
        Assert.Empty(_manager.ActiveSessions);
        Assert.Equal(n, _created);
        Assert.Equal(n, _closed);

        // 已关闭会话重复 Dispose / Close 不抛，也不产生额外 SessionClosed。
        foreach (var session in sessions)
        {
            await session.DisposeAsync();
            await _manager.CloseSessionAsync(session.SessionId);
        }

        Assert.Equal(n, _closed);
    }

    [Fact]
    public async Task 重复关闭幂等()
    {
        var session = await _manager.CreateSessionAsync(NewProfile("幂等关闭"));
        var id = session.SessionId;

        await _manager.CloseSessionAsync(id);
        await _manager.CloseSessionAsync(id);
        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(ConnectionState.Closed, session.State);
        Assert.Equal(0, _manager.ActiveSessionCount);
        Assert.Equal(1, _created);
        Assert.Equal(1, _closed);
    }

    [Fact]
    public async Task 失败后Close收敛Closed事件一次()
    {
        _provider.SessionFactory = static p => new FakeSession(p) { FailOnConnect = true };

        var session = await _manager.CreateSessionAsync(NewProfile("失败收敛"));
        await session.ConnectAsync();
        Assert.Equal(ConnectionState.Failed, session.State);

        await _manager.CloseSessionAsync(session.SessionId);

        Assert.Equal(ConnectionState.Closed, session.State);
        Assert.Equal(0, _manager.ActiveSessionCount);
        Assert.Equal(1, _created);
        Assert.Equal(1, _closed);
    }

    [Fact]
    public async Task teardown挂起仍被force收敛不抛()
    {
        _manager.CloseTimeout = TimeSpan.FromMilliseconds(200);

        FakeSession? hung = null;
        _provider.SessionFactory = p =>
        {
            hung = new FakeSession(p, hangTeardown: true);
            return hung;
        };

        var session = await _manager.CreateSessionAsync(NewProfile("挂起teardown"));

        var sw = Stopwatch.StartNew();
        await _manager.CloseSessionAsync(session.SessionId);
        sw.Stop();

        Assert.Equal(ConnectionState.Closed, session.State);
        Assert.Equal(0, _manager.ActiveSessionCount);
        Assert.Equal(1, _created);
        Assert.Equal(1, _closed);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"强制收敛不应远超总时限，实际 {sw.Elapsed}");

        // 放行挂起的 teardown，让悬空的 DisconnectAsync 正常收尾（不抛、状态保持 Closed）。
        Assert.NotNull(hung);
        hung.ReleaseTeardown();
        await Task.Yield();
    }

    // ── StateSync A：快照查询 + 聚合事件 ─────────────────────────

    [Fact]
    public async Task 快照查询_创建连接后派生已连_关闭后回落Idle()
    {
        var profile = NewProfile("快照派生");
        var profileId = profile.Id;

        var session = await _manager.CreateSessionAsync(profile);
        Assert.True(_manager.HasActiveSession(profileId));
        Assert.False(_manager.HasConnectedSession(profileId));
        Assert.Equal(ConnectionState.Idle, _manager.GetSessionState(profileId));

        await session.ConnectAsync();
        Assert.True(_manager.HasActiveSession(profileId));
        Assert.True(_manager.HasConnectedSession(profileId));
        Assert.Equal(ConnectionState.Connected, _manager.GetSessionState(profileId));

        await _manager.CloseSessionAsync(session.SessionId);
        Assert.False(_manager.HasActiveSession(profileId));
        Assert.False(_manager.HasConnectedSession(profileId));
        Assert.Equal(ConnectionState.Idle, _manager.GetSessionState(profileId));
    }

    [Fact]
    public async Task SessionsChanged_创建与关闭均触发_关闭后反映移除()
    {
        var sessionsChanged = 0;
        var createdChanged = 0;
        var closedChanged = 0;
        _manager.SessionsChanged += (_, _) =>
        {
            sessionsChanged++;
            if (_manager.ActiveSessionCount > 0)
            {
                createdChanged++;
            }
            else
            {
                closedChanged++;
            }
        };

        var session = await _manager.CreateSessionAsync(NewProfile("聚合通知")); // 创建后 1 次
        await session.ConnectAsync(); // Connecting + Connected 各 1 次

        // 关闭完成后必须已收到一次「集合已空」的通知（会话已移除后再广播）。
        await _manager.CloseSessionAsync(session.SessionId);

        Assert.Equal(0, _manager.ActiveSessionCount);
        Assert.True(sessionsChanged >= 4, $"预期至少 4 次，实际 {sessionsChanged}");
        Assert.True(createdChanged >= 3, $"预期创建/状态跳变期至少 3 次，实际 {createdChanged}");
        Assert.True(closedChanged >= 1, $"预期关闭后至少 1 次反映移除的通知，实际 {closedChanged}");
    }

    [Fact]
    public async Task SessionStateChanged_连接过程状态跳变经聚合转发()
    {
        var newStates = new List<ConnectionState>();
        SessionStateChangedEventArgs? connected = null;
        _manager.SessionStateChanged += (_, e) =>
        {
            newStates.Add(e.NewState);
            if (e.NewState == ConnectionState.Connected)
            {
                connected = e;
            }
        };

        var session = await _manager.CreateSessionAsync(NewProfile("聚合跳变"));
        await session.ConnectAsync();

        Assert.Contains(ConnectionState.Connecting, newStates);
        Assert.NotNull(connected);
        Assert.Equal(ConnectionState.Connecting, connected.OldState);
        Assert.Equal(ConnectionState.Connected, connected.NewState);
        Assert.Equal(ConnectionErrorCode.None, connected.ErrorCode);
    }

    [Fact]
    public async Task 失败_关闭后不残留活动会话()
    {
        _provider.SessionFactory = static p => new FakeSession(p) { FailOnConnect = true };

        var profile = NewProfile("失败不残留");
        var profileId = profile.Id;
        ConnectionState? failedStateSeen = null;
        _manager.SessionStateChanged += (_, e) =>
        {
            if (e.NewState == ConnectionState.Failed)
            {
                failedStateSeen = e.NewState;
            }
        };

        var session = await _manager.CreateSessionAsync(profile);
        await session.ConnectAsync();

        Assert.Equal(ConnectionState.Failed, session.State);
        Assert.True(_manager.HasActiveSession(profileId));
        Assert.False(_manager.HasConnectedSession(profileId));
        Assert.Equal(ConnectionState.Failed, _manager.GetSessionState(profileId));
        Assert.Equal(ConnectionState.Failed, failedStateSeen);

        await _manager.CloseSessionAsync(session.SessionId);

        Assert.False(_manager.HasActiveSession(profileId));
        Assert.False(_manager.HasConnectedSession(profileId));
        Assert.Equal(ConnectionState.Idle, _manager.GetSessionState(profileId));
    }

    [Fact]
    public async Task 快速开关循环_无旧状态残留()
    {
        _manager.MaxConcurrentSessions = 40;
        const int rounds = 8;
        var removalsNotified = 0;
        _manager.SessionsChanged += (_, _) =>
        {
            if (_manager.ActiveSessionCount == 0)
            {
                removalsNotified++;
            }
        };

        for (var i = 0; i < rounds; i++)
        {
            var profile = NewProfile($"快速开关{i}");
            var profileId = profile.Id;

            var session = await _manager.CreateSessionAsync(profile);
            Assert.True(_manager.HasActiveSession(profileId));

            await session.ConnectAsync();
            Assert.True(_manager.HasConnectedSession(profileId));
            Assert.Equal(ConnectionState.Connected, _manager.GetSessionState(profileId));

            await _manager.CloseSessionAsync(session.SessionId);
            Assert.False(_manager.HasActiveSession(profileId));
            Assert.False(_manager.HasConnectedSession(profileId));
            Assert.Equal(ConnectionState.Idle, _manager.GetSessionState(profileId));
        }

        Assert.Equal(0, _manager.ActiveSessionCount);
        Assert.Empty(_manager.ActiveSessions);
        Assert.True(removalsNotified >= rounds, $"每轮移除都应通知一次，实际 {removalsNotified}");
    }

    private static ConnectionProfile NewProfile(string name) => new()
    {
        Name = name,
        Host = "fake-host",
        Port = ConnectionProfile.GetDefaultPort(ProtocolType.Vnc),
        Protocol = ProtocolType.Vnc,
    };

    // ── 内存 Fake ────────────────────────────────────────────────

    private sealed class FakeProvider : IConnectionProvider
    {
        private readonly ProtocolType _protocol;

        public FakeProvider(ProtocolType protocol) => _protocol = protocol;

        public ProtocolType Protocol => _protocol;

        public int DefaultPort => ConnectionProfile.GetDefaultPort(_protocol);

        public Func<ConnectionProfile, IRemoteSession> SessionFactory { get; set; } = static p => new FakeSession(p);

        public bool IsAvailable(out string? unavailableReason)
        {
            unavailableReason = null;
            return true;
        }

        public IRemoteSession CreateSession(SessionRequest request) => SessionFactory(request.Profile);
    }

    private sealed class NoOpSshHostKeyPolicy : ISshHostKeyPolicy
    {
        public SshHostKeyVerificationContext Lookup(SshHostKeyVerificationContext context) => context;

        public Task<bool> ConfirmAndRememberAsync(SshHostKeyVerificationContext context, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }

    /// <summary>
    /// 继承 <see cref="RemoteSessionBase"/> 以取得真实 IsClosing / MarkClosed / 状态机语义。
    /// teardown 可选挂起（永不完成），用于验证 Manager 的总超时强制收敛路径。
    /// </summary>
    private sealed class FakeSession(ConnectionProfile profile, bool hangTeardown = false) : RemoteSessionBase
    {
        private readonly TaskCompletionSource _teardownHang =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FailOnConnect { get; init; }

        public int TeardownCount { get; private set; }

        public override ProtocolType Protocol => profile.Protocol;

        public override ConnectionProfile Profile => profile;

        public override Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (FailOnConnect)
            {
                ErrorCode = ConnectionErrorCode.AuthenticationFailed;
                ErrorMessage = "模拟认证失败";
                SetState(ConnectionState.Failed);
                return Task.CompletedTask;
            }

            SetState(ConnectionState.Connecting);
            SetState(ConnectionState.Connected);
            return Task.CompletedTask;
        }

        public void ReleaseTeardown() => _teardownHang.TrySetResult();

        protected override ValueTask PerformTeardownAsync()
        {
            TeardownCount++;
            if (!hangTeardown)
            {
                return ValueTask.CompletedTask;
            }

            return new ValueTask(_teardownHang.Task);
        }
    }
}
