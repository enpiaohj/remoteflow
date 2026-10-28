using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Core.Vmware;

namespace RemoteFlow.Protocol.Vnc;

/// <summary>
/// VNC / macOS Screen Sharing 协议 Provider。
/// 使用纯托管的 RFB 实现（MIT 许可），无原生库依赖，因此始终可用。
/// </summary>
public sealed class VncConnectionProvider(ILoggerFactory loggerFactory, IVmwareConnector? vmware = null) : IConnectionProvider
{
    public ProtocolType Protocol => ProtocolType.Vnc;

    public int DefaultPort => 5900;

    public bool IsAvailable(out string? unavailableReason)
    {
        unavailableReason = null;
        return true;
    }

    public IRemoteSession CreateSession(SessionRequest request) => new VncSession(request, loggerFactory, vmware);
}
