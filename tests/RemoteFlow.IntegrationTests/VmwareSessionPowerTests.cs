using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Core.Vmware;
using RemoteFlow.Protocol.Vnc;
using Xunit;

namespace RemoteFlow.IntegrationTests;

public sealed class VmwareSessionPowerTests
{
    [Theory]
    [InlineData(RemotePowerAction.Shutdown, VmPowerAction.ShutdownGuest)]
    [InlineData(RemotePowerAction.Restart, VmPowerAction.RebootGuest)]
    public async Task 按UUID定位目标且仅发送正常电源请求(RemotePowerAction action, VmPowerAction expected)
    {
        var api = ApiProxy.Create(new VsphereVm("vm-2", "演示", "poweredOn", "target-id", "guestToolsRunning"));
        await using var session = CreateSession(api);
        await session.RequestPowerActionAsync(action);
        Assert.Equal("vm-2", api.Reference);
        Assert.Equal(expected, api.Action);
        Assert.Equal(1, api.Requests);
    }

    [Theory]
    [InlineData("other-id", "poweredOn", "guestToolsRunning")]
    [InlineData("target-id", "poweredOff", "guestToolsRunning")]
    [InlineData("target-id", "poweredOn", null)]
    public async Task UUID不匹配关机状态或Tools不可用时不发请求(string uuid, string state, string? tools)
    {
        var api = ApiProxy.Create(new VsphereVm("vm-2", "演示", state, uuid, tools));
        await using var session = CreateSession(api);
        await Assert.ThrowsAsync<RemotePowerException>(() => session.RequestPowerActionAsync(RemotePowerAction.Shutdown));
        Assert.Equal(0, api.Requests);
    }

    [Fact]
    public async Task 发送后断线不能报告成功且不会自动重试()
    {
        var api = ApiProxy.Create(new VsphereVm("vm-2", "演示", "poweredOn", "target-id", "guestToolsRunning"));
        api.Failure = new HttpRequestException();
        await using var session = CreateSession(api);
        var error = await Assert.ThrowsAsync<RemotePowerException>(() => session.RequestPowerActionAsync(RemotePowerAction.Restart));
        Assert.Contains("无法确认", error.Message);
        Assert.Equal(1, api.Requests);
    }

    private static VncSession CreateSession(ApiProxy api)
    {
        var profile = new ConnectionProfile { Name = "演示", Host = "management.example.test", Port = 443, Protocol = ProtocolType.Vnc };
        profile.Vmware.Enabled = true;
        profile.Vmware.InstanceUuid = "target-id";
        var session = new VncSession(new SessionRequest { Profile = profile }, NullLoggerFactory.Instance, new Connector(api));
        typeof(VncSession).GetProperty(nameof(VncSession.State))!.SetValue(session, ConnectionState.Connected);
        typeof(VncSession).GetField("_userName", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, "test-user");
        typeof(VncSession).GetField("_password", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, "test-password");
        return session;
    }

    private sealed class Connector(ApiProxy api) : IVmwareConnector
    {
        public Task<string> FetchCertificatePinAsync(string host, int port, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IVSphereConsoleApi> ConnectAsync(string host, int port, string certPinSha256, string userName, string password, CancellationToken ct = default)
            => Task.FromResult((IVSphereConsoleApi)(object)api);
    }

    public class ApiProxy : DispatchProxy
    {
        public IReadOnlyList<VsphereVm> Vms { get; private set; } = [];
        public string? Reference { get; private set; }
        public VmPowerAction? Action { get; private set; }
        public int Requests { get; private set; }
        public Exception? Failure { get; set; }
        public static ApiProxy Create(VsphereVm vm)
        {
            var api = (ApiProxy)(object)DispatchProxy.Create<IVSphereConsoleApi, ApiProxy>();
            api.Vms = new[] { vm };
            return api;
        }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case nameof(IVSphereConsoleApi.ListVirtualMachinesAsync): return Task.FromResult(Vms);
                case nameof(IVSphereConsoleApi.PowerAsync):
                    Requests++;
                    Reference = (string)args![0]!;
                    Action = (VmPowerAction)args[1]!;
                    return Failure is null ? Task.FromResult(VmPowerResult.RequestSent) : Task.FromException<VmPowerResult>(Failure);
                case nameof(IAsyncDisposable.DisposeAsync): return ValueTask.CompletedTask;
                default: throw new NotSupportedException(method?.Name);
            }
        }
    }
}
