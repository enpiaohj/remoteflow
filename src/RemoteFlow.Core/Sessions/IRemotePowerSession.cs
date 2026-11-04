namespace RemoteFlow.Core.Sessions;

public enum RemotePowerAction
{
    Shutdown,
    Restart
}

/// <summary>可选的远程设备电源能力。成功仅表示请求已被接受，不代表设备已经关机或重启。</summary>
public interface IRemotePowerSession
{
    bool SupportsPowerActions { get; }
    Task RequestPowerActionAsync(RemotePowerAction action, CancellationToken cancellationToken = default);
}

/// <summary>仅携带可向用户显示的安全文案，不包含远端命令输出或凭据。</summary>
public sealed class RemotePowerException(string message) : Exception(message);
