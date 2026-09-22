using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.Protocol.Ssh;

/// <summary>
/// SSH 协议 Provider。SSH.NET 是纯托管实现，无外部运行时依赖，因此始终可用。
/// </summary>
public sealed class SshConnectionProvider(ILoggerFactory loggerFactory) : IConnectionProvider
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<SshConnectionProvider>();

    public ProtocolType Protocol => ProtocolType.Ssh;

    public int DefaultPort => 22;

    public bool IsAvailable(out string? unavailableReason)
    {
        unavailableReason = null;
        return true;
    }

    public IRemoteSession CreateSession(SessionRequest request) => new SshSession(request, _logger);
}
