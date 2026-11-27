using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.Protocol.PowerShell;

/// <summary>在 Windows 主机（RDP 连接）上开 PowerShell 远程会话。沿用该连接的主机地址与凭据。</summary>
public sealed class PowerShellSessionProvider(ILoggerFactory loggerFactory, ITrustedHostsWriter? trustedHosts = null)
    : ISessionKindProvider
{
    public SessionKind Kind => SessionKind.PowerShell;

    public bool CanOpen(ConnectionProfile profile)
        => profile.Protocol == ProtocolType.Rdp && !profile.Vmware.Enabled;

    public bool IsAvailable(out string? unavailableReason)
    {
        // ConPTY 自 Windows 10 1809 / Server 2019（内部版本 17763）起提供。
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            unavailableReason = "PowerShell 会话需要 Windows 10 1809 或 Windows Server 2019 及以上版本。";
            return false;
        }

        if (!File.Exists(PowerShellSession.PowerShellPath))
        {
            unavailableReason = "本机没有可用的 Windows PowerShell。";
            return false;
        }

        unavailableReason = null;
        return true;
    }

    public IRemoteSession CreateSession(SessionRequest request)
        => new PowerShellSession(request, loggerFactory.CreateLogger<PowerShellSession>(), trustedHosts);
}
