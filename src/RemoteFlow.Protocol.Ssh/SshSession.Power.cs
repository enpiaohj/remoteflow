using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.Protocol.Ssh;

public sealed partial class SshSession
{
    private readonly SemaphoreSlim _powerGate = new(1, 1);
    public bool SupportsPowerActions => true;

    public async Task RequestPowerActionAsync(RemotePowerAction action, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        if (!await _powerGate.WaitAsync(0, cancellationToken))
            throw new RemotePowerException("该会话正在执行电源操作，请等待结果。");
        var submitted = false;
        try
        {
            var client = _client;
            if (_disposed || _closing || State != ConnectionState.Connected || client?.IsConnected != true)
                throw new RemotePowerException("会话已经断开，无法执行电源操作。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifecycleCts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            // 独立 exec channel，绝不向当前可能运行编辑器或嵌套 SSH 的交互终端写命令。
            using (var probe = client.CreateCommand("uname -s"))
            {
                probe.CommandTimeout = TimeSpan.FromSeconds(10);
                await probe.ExecuteAsync(timeout.Token);
                if (probe.ExitStatus != 0 || probe.Result.Trim() != "Linux")
                    throw new RemotePowerException("当前仅支持通过 SSH 对 Linux 设备执行关机和重启。");
            }
            if (_closing || State != ConnectionState.Connected)
                throw new RemotePowerException("会话已经断开，未发送电源请求。");
            var operation = action == RemotePowerAction.Shutdown ? "shutdown -h now" : "shutdown -r now";
            using var command = client.CreateCommand($"if [ \"$(id -u)\" = 0 ]; then {operation}; else sudo -n {operation}; fi");
            command.CommandTimeout = TimeSpan.FromSeconds(15);
            submitted = true;
            await command.ExecuteAsync(timeout.Token);
            if (command.ExitStatus is null)
                throw new RemotePowerException("未收到远端执行回执，无法确认结果。请检查设备状态后再决定是否重试。");
            if (command.ExitStatus != 0)
                throw new RemotePowerException("远端未接受电源请求。请确认设备支持 shutdown 命令，且当前账号具有 root 或免密 sudo 权限。");
        }
        catch (RemotePowerException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("SSH 电源请求未完成：{ErrorType}", ex.GetType().Name);
            throw new RemotePowerException(submitted
                ? "电源请求发送后连接中断或等待超时，无法确认结果。请检查设备状态，不要重复提交。"
                : "无法检查远端设备，未发送电源请求。请检查连接后重试。");
        }
        finally { _powerGate.Release(); }
    }
}
