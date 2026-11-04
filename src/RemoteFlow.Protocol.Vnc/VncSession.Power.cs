using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Core.Vmware;

namespace RemoteFlow.Protocol.Vnc;

public sealed partial class VncSession
{
    private readonly SemaphoreSlim _powerGate = new(1, 1);
    public bool SupportsPowerActions => Profile.Vmware.Enabled && _vmware is not null;

    public async Task RequestPowerActionAsync(RemotePowerAction action, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        if (!await _powerGate.WaitAsync(0, cancellationToken))
            throw new RemotePowerException("该会话正在执行电源操作，请等待结果。");
        var submitted = false;
        try
        {
            if (!SupportsPowerActions || State != ConnectionState.Connected || _closing
                || string.IsNullOrEmpty(_userName) || string.IsNullOrEmpty(_password)
                || string.IsNullOrEmpty(Profile.Vmware.InstanceUuid))
                throw new RemotePowerException("当前会话不具备可用的虚拟机电源管理通道。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifecycleCts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await using var api = await _vmware!.ConnectAsync(Profile.Host, Profile.Port,
                Profile.Vmware.CertPinSha256, _userName, _password, timeout.Token);
            var matches = (await api.ListVirtualMachinesAsync(timeout.Token))
                .Where(vm => string.Equals(vm.InstanceUuid, Profile.Vmware.InstanceUuid, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                throw new RemotePowerException("无法唯一确认当前虚拟机的 UUID，未发送电源请求。请刷新虚拟机列表后重新连接。");
            var target = matches[0];
            if (target.PowerState != "poweredOn")
                throw new RemotePowerException("虚拟机当前未处于开机状态，未发送电源请求。");
            if (!target.ToolsRunning)
                throw new RemotePowerException("虚拟机未运行 VMware Tools，无法正常关机或重启。");
            if (_closing || State != ConnectionState.Connected)
                throw new RemotePowerException("会话已经断开，未发送电源请求。");
            submitted = true;
            await api.PowerAsync(target.Reference, action == RemotePowerAction.Shutdown
                ? VmPowerAction.ShutdownGuest : VmPowerAction.RebootGuest, timeout.Token);
        }
        catch (RemotePowerException) { throw; }
        catch (VsphereApiException ex)
        {
            _logger.LogWarning("VMware 电源请求失败：{FaultCode}", ex.FaultCode.Split(':')[0]);
            throw new RemotePowerException(submitted
                ? "虚拟机电源请求未获确认。请检查设备状态；服务端可能拒绝了请求或连接已中断。"
                : "无法读取虚拟机状态或当前账号无权执行此操作，未发送电源请求。");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("VMware 电源请求未完成：{ErrorType}", ex.GetType().Name);
            throw new RemotePowerException(submitted
                ? "电源请求发送后连接中断或等待超时，无法确认结果。请检查虚拟机状态，不要重复提交。"
                : "无法连接虚拟机管理服务，未发送电源请求。");
        }
        finally { _powerGate.Release(); }
    }
}
