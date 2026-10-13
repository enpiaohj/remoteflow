using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace RemoteFlow.Presentation.Services;

/// <summary>连接的在线探测状态。</summary>
public enum PresenceState
{
    /// <summary>尚未探测。</summary>
    Unknown,

    /// <summary>探测进行中。</summary>
    Probing,

    /// <summary>TCP 可达。</summary>
    Online,

    /// <summary>连接被拒 / 超时 / 不可达。</summary>
    Offline
}

/// <summary>
/// 轻量在线探测：对「主机：端口」做一次 TCP 连接尝试，能建立 = 在线。
/// <para>
/// 与 <see cref="ConnectionTestService"/> 的分工：那是用户主动发起的深度诊断
/// （DNS → Ping → TCP 三步，带报告）；这是列表批量探测的快路径——只做 TCP 单步，
/// 超时短（默认约 1.5s）、调用方控制并发与取消。纯逻辑、无 UI 依赖；
/// <see cref="TcpClient"/> 用完即 Dispose，无后台残留。
/// </para>
/// </summary>
public sealed class PresenceProbeService
{
    /// <summary>单次探测的默认超时。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly ILogger<PresenceProbeService>? _logger;

    public PresenceProbeService(ILogger<PresenceProbeService>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// 探测一次。<paramref name="ct"/> 是批量探测的总取消令牌：请求取消时抛出
    /// <see cref="OperationCanceledException"/>；自身超时返回 <see cref="PresenceState.Offline"/>。
    /// </summary>
    public async Task<PresenceState> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            await client.ConnectAsync(host, port, timeoutCts.Token);
            return PresenceState.Online;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 只是自己超时（总令牌未取消）→ 按离线处理。
            return PresenceState.Offline;
        }
        catch (Exception ex)
        {
            // 连接拒绝 / 主机不可达 / DNS 解析失败等都按离线；总取消从下面重新抛出。
            if (ct.IsCancellationRequested)
            {
                _logger?.LogDebug(ex, "探测被取消：{Host}:{Port}", host, port);
                throw;
            }

            return PresenceState.Offline;
        }
    }
}
