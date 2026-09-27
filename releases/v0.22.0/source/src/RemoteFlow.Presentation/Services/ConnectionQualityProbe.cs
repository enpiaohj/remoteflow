using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteFlow.Presentation.Services;

/// <summary>
/// 连接质量等级。仅由 <see cref="QualityGradeEvaluator"/> 产生，
/// UI 侧再转成中文与状态点颜色。
/// </summary>
public enum QualityLevel
{
    /// <summary>无可评估的指标（如 ICMP 被禁用且 TCP 超时）。</summary>
    Unknown,

    /// <summary>优秀。</summary>
    Excellent,

    /// <summary>良好。</summary>
    Good,

    /// <summary>一般。</summary>
    Fair,

    /// <summary>较差（延迟高 / 抖动大 / 丢包多 / 多次重连）。</summary>
    Poor
}

/// <summary>
/// 一次轻量「连接质量参考探测」的结构化结果。
/// <para>
/// 全部字段均为「参考探测」而非会话层真实 RTT：RDP / SSH / VNC 三协议当前均不暴露
/// 会话级往返时延，因此用 TCP 连通 + 少量 ICMP Ping 估计网络状况；ICMP 被禁用时
/// 抖动 / 丢包等字段为 <see langword="null"/>，展示层显示 <c>-- / 暂不可用</c>，
/// 且绝不影响「已连接」判定。
/// </para>
/// </summary>
public sealed record ConnectionQualityResult
{
    public static ConnectionQualityResult Empty { get; } = new();

    /// <summary>探测完成时间（本地时钟，供 UI 展示「测量于 …」）。</summary>
    public DateTimeOffset MeasuredAt { get; init; } = DateTimeOffset.Now;

    /// <summary>host:port 是否能建立 TCP 连接。</summary>
    public bool TcpReachable { get; init; }

    /// <summary>TCP 建连耗时（毫秒）。TCP 失败时为 <see langword="null"/>。</summary>
    public long? TcpConnectMs { get; init; }

    /// <summary>ICMP 是否可用（至少一次 Ping 成功）。不可用时抖动 / 丢包无参考值。</summary>
    public bool IcmpAvailable { get; init; }

    /// <summary>成功 Ping 的平均往返时间（毫秒，参考值）。</summary>
    public double? PingRttMs { get; init; }

    /// <summary>成功 Ping 的平均绝对偏差（毫秒，作为抖动参考值）。</summary>
    public double? PingJitterMs { get; init; }

    /// <summary>丢包率（0~100，参考值）。</summary>
    public double? PingLossPct { get; init; }

    /// <summary>本次探测共发出多少个 Ping。</summary>
    public int TotalPings { get; init; }

    /// <summary>成功返回的 Ping 数量。</summary>
    public int SuccessfulPings { get; init; }
}

/// <summary>
/// 把可用的质量指标（延迟 / 抖动 / 丢包）与会话重连次数综合成一个等级。
/// <para>
/// 纯逻辑、无 IO、无 UI，便于单元测试。规则：
/// <list type="number">
///   <item>分别给 延迟 / 抖动 / 丢包 打分（0 好 → 3 差），取最差一档作为基础档；</item>
///   <item>会话真实重连次数叠加惩罚（重连越多代表链路越不稳定）；</item>
///   <item>没有任何可用指标时返回 <see cref="QualityLevel.Unknown"/>。</item>
/// </list>
/// </para>
/// </summary>
public static class QualityGradeEvaluator
{
    public static QualityLevel Evaluate(double? rttMs, double? jitterMs, double? lossPct, int reconnectCount)
    {
        // 没有任何可用指标（例如 ICMP 与 TCP 均不可达）→ 无法评估。
        if (rttMs is null && jitterMs is null && lossPct is null)
        {
            return QualityLevel.Unknown;
        }

        var worst = 0;
        if (rttMs is { } rtt)
        {
            worst = Math.Max(worst, LatencyLevel(rtt));
        }

        if (jitterMs is { } jitter)
        {
            worst = Math.Max(worst, JitterLevel(jitter));
        }

        if (lossPct is { } loss)
        {
            worst = Math.Max(worst, LossLevel(loss));
        }

        // 重连次数本身是「链路不稳定」的强信号：>=3 次直接落到最差档级。
        var reconnectPenalty = reconnectCount switch
        {
            >= 3 => 3,
            1 or 2 => 1,
            _ => 0
        };

        var level = Math.Min(3, worst + reconnectPenalty);
        return level switch
        {
            0 => QualityLevel.Excellent,
            1 => QualityLevel.Good,
            2 => QualityLevel.Fair,
            _ => QualityLevel.Poor
        };
    }

    private static int LatencyLevel(double ms) => ms switch
    {
        < 60 => 0,
        < 150 => 1,
        < 300 => 2,
        _ => 3
    };

    private static int JitterLevel(double ms) => ms switch
    {
        < 15 => 0,
        < 40 => 1,
        < 80 => 2,
        _ => 3
    };

    private static int LossLevel(double pct) => pct switch
    {
        < 1 => 0,
        < 3 => 1,
        < 10 => 2,
        _ => 3
    };
}

/// <summary>
/// 轻量「连接质量参考探测」：TCP 连通 + 少量 ICMP Ping（多包算 延迟 / 抖动 / 丢包）。
/// <para>
/// 全程异步、单步超时可控、可取消；每个套接字 / Ping 用完即释放，无后台残留。
/// 明确是<b>参考探测</b>而非会话真实延迟；ICMP 被禁时对应字段为 <see langword="null"/>，
/// 不影响会话「已连接」的判定。
/// </para>
/// </summary>
public sealed class ConnectionQualityProbe
{
    private const int PingCount = 4;

    /// <summary>TCP 建连单次超时。</summary>
    private static readonly TimeSpan TcpTimeout = TimeSpan.FromSeconds(2);

    /// <summary>单个 Ping 超时（毫秒）。四个 Ping 理论最坏约 3.6s，绝大多数情况远快于此。</summary>
    private const int PingTimeoutMs = 900;

    /// <summary>
    /// 运行一次参考探测。TCP 失败也仍尝试 Ping（区分「主机不可达」与「端口未开」）；
    /// 结果中的可达性与延迟字段相互独立。
    /// </summary>
    public async Task<ConnectionQualityResult> ProbeAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1) TCP 连通（主判据）
        var tcp = await ProbeTcpAsync(host, port, cancellationToken).ConfigureAwait(false);

        // 2) 少量 ICMP Ping（参考延迟 / 抖动 / 丢包）。取消只发生在两次 Ping 之间。
        var pings = await ProbePingsAsync(host, cancellationToken).ConfigureAwait(false);

        return new ConnectionQualityResult
        {
            MeasuredAt = DateTimeOffset.Now,
            TcpReachable = tcp.Reachable,
            TcpConnectMs = tcp.ElapsedMs,
            IcmpAvailable = pings.Successful > 0,
            PingRttMs = pings.RttMs,
            PingJitterMs = pings.JitterMs,
            PingLossPct = pings.LossPct,
            TotalPings = pings.Total,
            SuccessfulPings = pings.Successful
        };
    }

    private static async Task<(bool Reachable, long? ElapsedMs)> ProbeTcpAsync(
        string host, int port, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TcpTimeout);
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            sw.Stop();
            return (true, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (false, null);
        }
        catch (SocketException)
        {
            return (false, null);
        }
        catch (Exception)
        {
            return (false, null);
        }
    }

    private static async Task<PingAggregate> ProbePingsAsync(string host, CancellationToken ct)
    {
        var samples = new List<long>(PingCount);
        using var ping = new Ping();

        for (var i = 0; i < PingCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var reply = await ping.SendPingAsync(host, PingTimeoutMs, new byte[32]).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                {
                    samples.Add(reply.RoundtripTime);
                }
            }
            catch (PingException)
            {
                // ICMP 被禁用 / 协议不支持：记一次失败，继续下一包。
            }
            catch (Exception) when (i == 0)
            {
                // 首包即遇非 Ping 异常（极少见）也按不可用处理，不阻断整个探测。
                break;
            }
        }

        if (samples.Count == 0)
        {
            return new PingAggregate(PingCount, 0, null, null, null);
        }

        var avg = samples.Average();
        var jitter = samples.Average(s => Math.Abs(s - avg));
        var loss = (PingCount - samples.Count) * 100d / PingCount;

        return new PingAggregate(PingCount, samples.Count, avg, jitter, loss);
    }

    private readonly record struct PingAggregate(
        int Total,
        int Successful,
        double? RttMs,
        double? JitterMs,
        double? LossPct);
}
