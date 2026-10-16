using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 会话 Tab VM 的回归测试。
/// <para>
/// 此前 SessionTabViewModel 直接 new DispatcherTimer、经 Application.Current.Dispatcher
/// marshal，脱离 STA/Dispatcher 根本无法实例化，因此本文件在 ViewModel 迁入
/// Presentation 之前写不出来。改用 IUiDispatcher（同步实现）与 IUiTimer 抽象后，
/// 可以在跨平台测试工程里直接跑——这正是抽象它的目的之一。
/// </para>
/// </summary>
public sealed class SessionTabViewModelTests
{
    [Fact]
    public void 二次Dispose_不抛异常()
    {
        // 退出清理会对 VM dispose 两次（App.OnExit 显式 + 容器兜底），必须幂等。
        using var vm = CreateVm();

        vm.Dispose();
        vm.Dispose(); // 第二次调用不得抛 ObjectDisposedException
    }

    [Fact]
    public void 状态事件在非UI线程触发_经同步调度器直接更新()
    {
        // 用同步调度器：无论从哪个线程触发，状态都立即落地，不依赖 STA。
        using var vm = CreateVm();
        var session = (FakeSession)vm.Session;

        session.RaiseStateChanged(ConnectionState.Connecting, ConnectionState.Connected);

        Assert.Equal("已连接", vm.StateText);
    }

    [Fact]
    public void 会话标签使用主机设备图标且提示中保留协议与状态()
    {
        using var vm = CreateVm();
        var expected = DeviceTypeCatalog.InferFromProtocol(ProtocolType.Ssh);

        Assert.Equal(expected.IconResourceKey, vm.DeviceIconKey);
        Assert.Equal(expected.AccentBrushKey, vm.DeviceBrushKey);
        Assert.Contains("SSH", vm.TabToolTip, StringComparison.Ordinal);
        Assert.Contains(vm.Title, vm.TabToolTip, StringComparison.Ordinal);

        ((FakeSession)vm.Session).RaiseStateChanged(ConnectionState.Connecting, ConnectionState.Connected);
        Assert.Contains("已连接", vm.TabToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void 会话状态色与全应用统一语义一致()
    {
        // 与连接列表 / 详情 / 首页一致：连接中琥珀、已连接蓝、异常红；绿色只表示「在线（可达）」。
        using var vm = CreateVm();
        var session = (FakeSession)vm.Session;

        session.RaiseStateChanged(ConnectionState.Idle, ConnectionState.Connecting);
        Assert.Equal("Status.Warning", vm.StateBrushKey);

        session.RaiseStateChanged(ConnectionState.Connecting, ConnectionState.Connected);
        Assert.Equal("Status.Info", vm.StateBrushKey);

        session.RaiseStateChanged(ConnectionState.Connected, ConnectionState.Reconnecting);
        Assert.Equal("Status.Warning", vm.StateBrushKey);

        session.RaiseStateChanged(ConnectionState.Reconnecting, ConnectionState.Failed);
        Assert.Equal("Status.Danger", vm.StateBrushKey);
    }

    private static SessionTabViewModel CreateVm() => new(
        new FakeSession(),
        _ => Task.CompletedTask,
        _ => Task.CompletedTask,
        SynchronousUiDispatcher.Instance,
        new ManualTimerFactory());
}

/// <summary>最小会话替身。仅实现测试路径会用到的成员。</summary>
internal sealed class FakeSession : IRemoteSession
{
    public Guid SessionId { get; } = Guid.NewGuid();

    public ProtocolType Protocol => ProtocolType.Ssh;

    public ConnectionProfile Profile { get; } = new() { Name = "测试", Protocol = ProtocolType.Ssh };

    public ConnectionState State { get; } = ConnectionState.Idle;

    public ConnectionErrorCode ErrorCode => ConnectionErrorCode.None;

    public string? ErrorMessage => null;

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <summary>测试用：从类内触发状态事件（C# 事件只能在声明类内 invoke）。</summary>
    public void RaiseStateChanged(ConnectionState oldState, ConnectionState newState)
        => StateChanged?.Invoke(this, new SessionStateChangedEventArgs(oldState, newState));

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task DisconnectAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>计时器工厂替身：不真的按秒触发，由测试手动驱动（本组测试不测时长）。</summary>
internal sealed class ManualTimerFactory : IUiTimerFactory
{
    public IUiTimer Create(TimeSpan interval) => new ManualTimer();
}

internal sealed class ManualTimer : IUiTimer
{
    public TimeSpan Interval { get; set; }

    // 本组测试不驱动计时，事件无需保存订阅者。
    public event EventHandler? Tick
    {
        add { }
        remove { }
    }

    public void Start() { }

    public void Stop() { }

    public void Dispose() { }
}