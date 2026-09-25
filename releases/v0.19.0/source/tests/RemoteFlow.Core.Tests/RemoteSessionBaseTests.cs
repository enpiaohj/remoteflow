using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using Xunit;

namespace RemoteFlow.Core.Tests;

/// <summary>
/// Session 生命周期状态机与幂等关闭模板测试。
/// ProbeSession 继承 <see cref="RemoteSessionBase"/>，用内存态模拟连接推进，验证：
/// Closed 终态、Disconnect 幂等模板、非法跳转忽略、自动重连方向与事件触发。
/// </summary>
public sealed class RemoteSessionBaseTests
{
    private sealed class ProbeSession : RemoteSessionBase
    {
        /// <summary>模拟 ConnectAsync 开头：Idle → Connecting。</summary>
        public ProbeSession() => SetState(ConnectionState.Connecting);

        public override ProtocolType Protocol => ProtocolType.Vnc;

        public override ConnectionProfile Profile { get; } = new()
        {
            Name = "probe",
            Host = "probe.invalid",
            Port = 5900,
            Protocol = ProtocolType.Vnc
        };

        public override Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            SetState(ConnectionState.Connecting);
            SetState(ConnectionState.Connected);
            return Task.CompletedTask;
        }

        /// <summary>模拟连接成功：Connecting → Connected。</summary>
        public void GoConnected() => SetState(ConnectionState.Connected);

        /// <summary>测试观察口：向状态机请求一次任意迁移（是否生效由状态机规则决定）。</summary>
        public void TrySet(ConnectionState state) => SetState(state);

        /// <summary>在会话级取消令牌上注册一个会抛异常的取消回调，模拟 Cancel() 抛 AggregateException。</summary>
        public void RegisterThrowingCancelCallback()
            => LifecycleToken.Register(() => throw new InvalidOperationException("cancel callback boom"));

        protected override ValueTask PerformTeardownAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void 状态_Closed后不再变化()
    {
        var s = new ProbeSession();
        s.GoConnected();
        Assert.Equal(ConnectionState.Connected, s.State);

        s.MarkClosed();
        Assert.Equal(ConnectionState.Closed, s.State);

        // MarkClosed 幂等：重复调用不抛、状态不变。
        s.MarkClosed();
        Assert.Equal(ConnectionState.Closed, s.State);

        // Closed 为终态：任何再激活请求都被忽略。
        s.TrySet(ConnectionState.Connected);
        s.TrySet(ConnectionState.Failed);
        Assert.Equal(ConnectionState.Closed, s.State);
    }

    [Fact]
    public async Task 状态_Disconnect模板到Disconnected且幂等()
    {
        var s = new ProbeSession();
        s.GoConnected();
        Assert.Equal(ConnectionState.Connected, s.State);

        // 首次：Disconnecting → teardown → Disconnected。
        await s.DisconnectAsync();
        Assert.Equal(ConnectionState.Disconnected, s.State);

        // 重复调用：不抛、状态不再变化。
        await s.DisconnectAsync();
        Assert.Equal(ConnectionState.Disconnected, s.State);
    }

    [Fact]
    public async Task 状态_非法跳转被忽略()
    {
        var s = new ProbeSession();
        s.GoConnected();
        await s.DisconnectAsync();
        Assert.Equal(ConnectionState.Disconnected, s.State);

        // 会话单次使用：已 Disconnected 后不允许回到任何活动态 / Failed。
        s.TrySet(ConnectionState.Connected);
        Assert.Equal(ConnectionState.Disconnected, s.State);

        s.TrySet(ConnectionState.Connecting);
        Assert.Equal(ConnectionState.Disconnected, s.State);

        s.TrySet(ConnectionState.Reconnecting);
        Assert.Equal(ConnectionState.Disconnected, s.State);

        s.TrySet(ConnectionState.Failed);
        Assert.Equal(ConnectionState.Disconnected, s.State);
    }

    [Fact]
    public async Task 状态_自动重连进出方向合法()
    {
        var s = new ProbeSession();
        s.GoConnected();

        // 连接成功后自动重连：Connected → Reconnecting → Connected。
        s.TrySet(ConnectionState.Reconnecting);
        Assert.Equal(ConnectionState.Reconnecting, s.State);
        s.TrySet(ConnectionState.Connected);
        Assert.Equal(ConnectionState.Connected, s.State);

        // 重连中用户关闭：Reconnecting → Disconnecting → Disconnected。
        s.TrySet(ConnectionState.Reconnecting);
        await s.DisconnectAsync();
        Assert.Equal(ConnectionState.Disconnected, s.State);
    }

    [Fact]
    public void 状态_StateChanged仅在真实变化时触发且携带正确新旧状态()
    {
        var s = new ProbeSession();
        var transitions = new List<(ConnectionState Old, ConnectionState New)>();
        s.StateChanged += (_, e) => transitions.Add((e.OldState, e.NewState));

        s.GoConnected();                                    // Connecting → Connected
        s.TrySet(ConnectionState.Connected);                // 同态：不触发
        s.MarkClosed();                                     // Connected → Closed

        Assert.Equal(2, transitions.Count);
        Assert.Equal((ConnectionState.Connecting, ConnectionState.Connected), transitions[0]);
        Assert.Equal((ConnectionState.Connected, ConnectionState.Closed), transitions[1]);
    }

    [Fact]
    public async Task 状态_DisposeAsync防重入()
    {
        var s = new ProbeSession();
        s.GoConnected();

        await s.DisposeAsync();
        await s.DisposeAsync(); // 二次不抛

        Assert.Equal(ConnectionState.Disconnected, s.State);
    }

    [Fact]
    public async Task 状态_Cancel回调抛异常不阻断关闭模板()
    {
        var s = new ProbeSession();
        s.GoConnected();
        s.RegisterThrowingCancelCallback();

        // Cancel() 会因取消回调抛异常而抛 AggregateException，但关闭模板必须吞掉并继续收尾。
        await s.DisconnectAsync();

        Assert.Equal(ConnectionState.Disconnected, s.State);
    }
}

/// <summary>SessionResourceTracker 清理顺序 / 幂等 / 有界等待的基础约定。</summary>
public sealed class SessionResourceTrackerTests
{
    private sealed class DisposeRecorder(string name, List<string> log) : IDisposable
    {
        public void Dispose() => log.Add(name);
    }

    [Fact]
    public void DisposeAll_依次取消退订释放_且幂等()
    {
        var tracker = new SessionResourceTracker();
        var log = new List<string>();
        using var cts = new CancellationTokenSource();
        cts.Token.Register(() => log.Add("cancel"));
        var disposable = new DisposeRecorder("dispose", log);

        tracker.Track("res", cts: cts, disposable: disposable, unsubscribe: () => log.Add("unsub"));

        tracker.DisposeAll();
        tracker.DisposeAll(); // 幂等：二次调用不再执行

        Assert.Equal(new[] { "cancel", "unsub", "dispose" }, log);
        Assert.Equal(0, tracker.ActiveCount);
        Assert.Equal(string.Empty, tracker.Dump());
    }

    [Fact]
    public void DisposeAll_单项释放异常不影响其余资源()
    {
        var tracker = new SessionResourceTracker();
        var log = new List<string>();

        // 抛异常的 disposable 排在前面，验证后续资源仍被释放。
        tracker.Track("bad", disposable: new ThrowingDisposable());
        tracker.Track("good", disposable: new DisposeRecorder("good", log));

        tracker.DisposeAll();

        Assert.Equal(["good"], log);
        Assert.Equal(0, tracker.ActiveCount);
    }

    [Fact]
    public async Task WaitAllAsync_未完成超时返回true_完成后返回false()
    {
        var tracker = new SessionResourceTracker();
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.Track("bg", task: signal.Task);

        // 尚未完成：极短超时后返回 true（仍有任务在运行）。
        var stillRunning = await tracker.WaitAllAsync(TimeSpan.FromMilliseconds(20));
        Assert.True(stillRunning);

        // 放行后：等待到完成返回 false。
        signal.TrySetResult();
        var allFinished = await tracker.WaitAllAsync(TimeSpan.FromSeconds(5));
        Assert.False(allFinished);
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("boom");
    }
}
