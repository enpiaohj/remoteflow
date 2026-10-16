using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Protocol.Rdp.Interop;

namespace RemoteFlow.Protocol.Rdp;

/// <summary>
/// RDP 协议 Provider。依赖本机 mstscax.dll 提供的 RDP Client ActiveX 控件。
/// </summary>
public sealed class RdpConnectionProvider(ILoggerFactory loggerFactory) : IConnectionProvider
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<RdpConnectionProvider>();

    public ProtocolType Protocol => ProtocolType.Rdp;

    public int DefaultPort => 3389;

    /// <summary>
    /// 探测本机是否注册了 RDP 控件。在此处提前失败，
    /// 好过等到用户点了「连接」才报错。
    /// </summary>
    public bool IsAvailable(out string? unavailableReason)
    {
        if (RdpControlLocator.Locate() is null)
        {
            unavailableReason = "本机未找到远程桌面客户端控件（mstscax.dll），无法建立 RDP 连接。";
            return false;
        }

        unavailableReason = null;
        return true;
    }

    public IRemoteSession CreateSession(SessionRequest request)
    {
        var control = RdpControlLocator.Locate()
            ?? throw new ConnectionException(
                ConnectionErrorCode.ComponentUnavailable,
                "本机未找到远程桌面客户端控件（mstscax.dll），无法建立 RDP 连接。");

        _logger.LogDebug("使用 RDP 控件 {ProgId}", control.ProgId);

        return new RdpSession(request, control.Clsid.ToString("B"), _logger);
    }
}
