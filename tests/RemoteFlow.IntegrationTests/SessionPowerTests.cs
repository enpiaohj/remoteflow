using System.Reflection;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

public sealed class SessionPowerTests
{
    [Fact]
    public async Task 取消确认不会提交电源请求()
    {
        var session = new PowerSession();
        var dialogs = PowerDialogs.Create(false);
        using var vm = Create(session, dialogs);
        await vm.ShutdownCommand.ExecuteAsync(null);
        Assert.Empty(session.Actions);
        Assert.Contains("所有连接", dialogs.Message);
        Assert.Contains(session.Profile.Host, dialogs.Message);
        Assert.True(dialogs.Danger);
    }

    [Theory]
    [InlineData(RemotePowerAction.Shutdown)]
    [InlineData(RemotePowerAction.Restart)]
    public async Task 确认后仅对当前命令所属会话提交请求(RemotePowerAction action)
    {
        var target = new PowerSession();
        var other = new PowerSession();
        var dialogs = PowerDialogs.Create(true);
        using var vm = Create(target, dialogs);
        using var otherVm = Create(other, PowerDialogs.Create(true));
        await (action == RemotePowerAction.Shutdown ? vm.ShutdownCommand : vm.RestartDeviceCommand).ExecuteAsync(null);
        Assert.Equal(new[] { action }, target.Actions);
        Assert.Empty(other.Actions);
        Assert.Contains("最终结果", dialogs.Notice);
    }

    [Fact]
    public async Task 等待确认期间断线不会发送请求()
    {
        var target = new PowerSession();
        var dialogs = PowerDialogs.Create(true);
        dialogs.BeforeConfirm = () => target.ChangeState(ConnectionState.Disconnected);
        using var vm = Create(target, dialogs);
        await vm.ShutdownCommand.ExecuteAsync(null);
        Assert.Empty(target.Actions);
        Assert.Contains("未发送", dialogs.Notice);
    }

    [Fact]
    public async Task 执行中禁止重复请求并将未知结果明确呈现()
    {
        var target = new PowerSession { Completion = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var dialogs = PowerDialogs.Create(true);
        using var vm = Create(target, dialogs);
        var running = vm.RestartDeviceCommand.ExecuteAsync(null);
        Assert.True(vm.IsPowerBusy);
        Assert.False(vm.ShutdownCommand.CanExecute(null));
        await vm.ShutdownCommand.ExecuteAsync(null);
        target.Completion.SetException(new RemotePowerException("无法确认结果"));
        await running;
        Assert.Single(target.Actions);
        Assert.False(vm.IsPowerBusy);
        Assert.Equal("无法确认结果", dialogs.Notice);
    }

    [Fact]
    public void 无电源能力的会话隐藏入口_断开时禁止执行()
    {
        using var unsupported = new SessionTabViewModel(new FakeSession(), _ => Task.CompletedTask,
            _ => Task.CompletedTask, SynchronousUiDispatcher.Instance, new ManualTimerFactory(), (IDialogService)(object)PowerDialogs.Create(true));
        Assert.False(unsupported.SupportsPowerActions);
        var target = new PowerSession();
        using var vm = Create(target, PowerDialogs.Create(true));
        Assert.True(vm.ShutdownCommand.CanExecute(null));
        target.ChangeState(ConnectionState.Disconnected);
        Assert.False(vm.ShutdownCommand.CanExecute(null));
    }

    private static SessionTabViewModel Create(PowerSession session, PowerDialogs dialogs)
        => new(session, _ => Task.CompletedTask, _ => Task.CompletedTask,
            SynchronousUiDispatcher.Instance, new ManualTimerFactory(), (IDialogService)(object)dialogs);

    private sealed class PowerSession : IRemoteSession, IRemotePowerSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public ProtocolType Protocol => ProtocolType.Ssh;
        public ConnectionProfile Profile { get; } = new() { Name = "演示设备", Host = "server.example.test", Port = 22, Protocol = ProtocolType.Ssh };
        public ConnectionState State { get; private set; } = ConnectionState.Connected;
        public ConnectionErrorCode ErrorCode => ConnectionErrorCode.None;
        public string? ErrorMessage => null;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
        public bool SupportsPowerActions => true;
        public List<RemotePowerAction> Actions { get; } = [];
        public TaskCompletionSource? Completion { get; init; }
        public Task RequestPowerActionAsync(RemotePowerAction action, CancellationToken cancellationToken = default)
        { Actions.Add(action); return Completion?.Task ?? Task.CompletedTask; }
        public void ChangeState(ConnectionState state)
        { var previous = State; State = state; StateChanged?.Invoke(this, new(previous, state)); }
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public class PowerDialogs : DispatchProxy
    {
        public bool Confirm { get; set; }
        public bool Danger { get; private set; }
        public string Message { get; private set; } = "";
        public string Notice { get; private set; } = "";
        public Action? BeforeConfirm { get; set; }
        public static PowerDialogs Create(bool confirm)
        {
            var proxy = DispatchProxy.Create<IDialogService, PowerDialogs>();
            var result = (PowerDialogs)(object)proxy;
            result.Confirm = confirm;
            return result;
        }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == nameof(IDialogService.ConfirmAsync))
            {
                Message = (string)args![1]!;
                Danger = (bool)args[3]!;
                BeforeConfirm?.Invoke();
                return Task.FromResult(Confirm);
            }
            if (method?.Name == nameof(IDialogService.ShowMessageAsync)) Notice = (string)args![1]!;
            if (method?.ReturnType == typeof(Task)) return Task.CompletedTask;
            throw new NotSupportedException(method?.Name);
        }
    }
}
