using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Presentation.Services;

/// <summary>单步诊断状态。颜色只作用于图标与少量文字，不整块上色。</summary>
public enum StepStatus
{
    Waiting,
    Running,
    Success,
    Warning,
    Failed
}

/// <summary>
/// 一步诊断的结果。<see cref="Text"/> 是给用户看的简短行文字；
/// <see cref="Detail"/> 与 <see cref="Exception"/> 只进「详细信息」折叠区，不直接出现在结论里。
/// </summary>
/// <param name="Label">步骤名，如「DNS 解析」。</param>
/// <param name="Status">状态。</param>
/// <param name="Text">状态行文字。</param>
/// <param name="Detail">详细信息（可选）。</param>
/// <param name="Exception">底层异常（可选，仅日志 / 详细信息使用）。</param>
public sealed record StepResult(
    string Label,
    StepStatus Status,
    string Text,
    string? Detail = null,
    Exception? Exception = null);

/// <summary>
/// 「测试连接」的诊断报告。纯数据，供对话框展示，不含任何明文 Secret。
/// </summary>
public sealed class ConnectionTestReport
{
    /// <summary>协议显示名：RDP / SSH / VNC。</summary>
    public string ProtocolName { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    /// <summary>Host 本身是 IP（跳过 DNS 解析）。</summary>
    public bool DnsWasIp { get; set; }

    public StepResult Dns { get; set; } = new("DNS 解析", StepStatus.Waiting, "等待中…");

    public StepResult Ping { get; set; } = new("Ping", StepStatus.Waiting, "等待中…");

    public StepResult Tcp { get; set; } = new("TCP 端口", StepStatus.Waiting, "等待中…");

    /// <summary>TCP 是「能否建立 X 连接」的主判据。</summary>
    public bool TcpReachable { get; set; }

    /// <summary>整个诊断流程总耗时（毫秒）。</summary>
    public long TotalMs { get; set; }

    /// <summary>结论主句。TCP 可达 →「目标可以建立 X 连接」；否则「当前无法建立 X 连接」。</summary>
    public string Conclusion { get; set; } = string.Empty;

    /// <summary>失败时的可能原因列表；成功时为空。</summary>
    public IReadOnlyList<string> Causes { get; set; } = [];
}

/// <summary>
/// 轻量连接诊断：DNS 解析 → Ping → TCP 端口探测，全程异步、支持超时与取消。
/// <para>
/// 纯逻辑、无 UI 依赖；Ping / TcpClient 用完即 Dispose，无后台残留。
/// TCP 可达是「能否建立连接」的唯一主判据；Ping 超时只记警告，不判离线。
/// </para>
/// </summary>
public sealed class ConnectionTestService
{
    private const string DnsLabel = "DNS 解析";
    private const string PingLabel = "Ping";
    private const string TcpLabelPrefix = "TCP 端口";

    private readonly ILogger<ConnectionTestService>? _logger;

    public ConnectionTestService(ILogger<ConnectionTestService>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>诊断默认的单步超时（与设计基线一致，约 3 秒）。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <inheritdoc cref="RunAsync(ConnectionProfile, TimeSpan, CancellationToken, Action{StepResult}?)"/>
    public Task<ConnectionTestReport> RunAsync(ConnectionProfile profile, TimeSpan timeout, CancellationToken ct)
        => RunAsync(profile, timeout, ct, progress: null);

    /// <summary>
    /// 运行三步诊断并返回报告。每一步完成时通过 <paramref name="progress"/> 通知调用方
    /// （供 UI 逐步推进各行状态），通知发生的线程与调用方等待上下文一致。
    /// 取消：DNS / TCP 立即中止；Ping 无法中止（ICMP 无取消接口），最长等到其自身超时后
    /// 在下一次检查点让 <see cref="OperationCanceledException"/> 传播。
    /// </summary>
    public async Task<ConnectionTestReport> RunAsync(
        ConnectionProfile profile,
        TimeSpan timeout,
        CancellationToken ct,
        Action<StepResult>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ct.ThrowIfCancellationRequested();

        var overall = Stopwatch.StartNew();
        var timeoutMs = ToTimeoutMilliseconds(timeout);

        // 1) DNS
        var dnsWasIp = IPAddress.TryParse(profile.Host, out _);
        StepResult dns;
        if (dnsWasIp)
        {
            // Host 已是 IP：不做反向解析。
            dns = new StepResult(DnsLabel, StepStatus.Success, "使用 IP 地址");
        }
        else
        {
            dns = await ResolveHostAsync(profile.Host, timeoutMs, ct);
        }

        progress?.Invoke(dns);

        // 2) Ping：仅诊断提示。失败/超时记 Warning（不据此判离线）。
        var ping = await PingAsync(profile.Host, timeoutMs, ct);
        progress?.Invoke(ping);

        // 3) TCP：主判据。
        var (tcp, tcpReachable) = await TcpProbeAsync(profile.Host, profile.Port, timeoutMs, ct);
        progress?.Invoke(tcp);

        overall.Stop();

        var protocolName = ProtocolDisplayName(profile.Protocol);
        var report = new ConnectionTestReport
        {
            ProtocolName = protocolName,
            Host = profile.Host,
            Port = profile.Port,
            DnsWasIp = dnsWasIp,
            Dns = dns,
            Ping = ping,
            Tcp = tcp,
            TcpReachable = tcpReachable,
            TotalMs = overall.ElapsedMilliseconds,
            Conclusion = tcpReachable
                ? $"目标可以建立 {protocolName} 连接"
                : $"当前无法建立 {protocolName} 连接",
            Causes = BuildCauses(tcpReachable, dns.Status == StepStatus.Failed, dnsWasIp)
        };

        _logger?.LogInformation(
            "连接测试完成 {Host}:{Port}（{Protocol}）→ {Conclusion}，DNS {Dns}，Ping {Ping}，TCP {Tcp}，耗时 {TotalMs} ms",
            profile.Host, profile.Port, protocolName, report.Conclusion, dns.Text, ping.Text, tcp.Text, report.TotalMs);

        return report;
    }

    // ── 单步实现 ─────────────────────────────────────────────

    private static async Task<StepResult> ResolveHostAsync(string host, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            var addresses = await Dns.GetHostAddressesAsync(host, cts.Token);
            return addresses.Length > 0
                ? new StepResult(DnsLabel, StepStatus.Success, $"正常 · {addresses[0]}")
                : new StepResult(DnsLabel, StepStatus.Failed, "解析无结果");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new StepResult(DnsLabel, StepStatus.Failed, "解析超时");
        }
        catch (Exception ex)
        {
            return new StepResult(DnsLabel, StepStatus.Failed, "解析失败", ex.Message, ex);
        }
    }

    private static async Task<StepResult> PingAsync(string host, int timeoutMs, CancellationToken ct)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(host, timeoutMs, new byte[32]);
            if (reply.Status == IPStatus.Success)
            {
                return new StepResult(PingLabel, StepStatus.Success, $"{reply.RoundtripTime} ms");
            }

            var text = reply.Status == IPStatus.TimedOut ? "请求超时" : $"请求失败（{reply.Status}）";
            return new StepResult(PingLabel, StepStatus.Warning, text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Ping 无法中止：异常路径不会进入这里产生泄漏，Ping 已随 using 释放。
            return new StepResult(PingLabel, StepStatus.Warning, "Ping 无法完成", ex.Message, ex);
        }
    }

    private static async Task<(StepResult Result, bool Reachable)> TcpProbeAsync(
        string host, int port, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(host, port, cts.Token);
            sw.Stop();
            return (new StepResult($"{TcpLabelPrefix} {port}", StepStatus.Success, $"可访问 · {sw.ElapsedMilliseconds} ms"), true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (new StepResult($"{TcpLabelPrefix} {port}", StepStatus.Failed, "连接超时"), false);
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            sw.Stop();
            return (new StepResult($"{TcpLabelPrefix} {port}", StepStatus.Failed, TcpFailureText(ex), ex.Message, ex), false);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (new StepResult($"{TcpLabelPrefix} {port}", StepStatus.Failed, "连接失败", ex.Message, ex), false);
        }
    }

    private static string TcpFailureText(System.Net.Sockets.SocketException ex) => ex.SocketErrorCode switch
    {
        System.Net.Sockets.SocketError.ConnectionRefused => "连接被拒绝",
        System.Net.Sockets.SocketError.HostNotFound => "无法解析主机名",
        System.Net.Sockets.SocketError.NetworkUnreachable => "网络不可达",
        System.Net.Sockets.SocketError.TimedOut => "连接超时",
        _ => "连接失败"
    };

    // ── 结论与可能原因 ────────────────────────────────────────

    private static IReadOnlyList<string> BuildCauses(bool tcpReachable, bool dnsFailed, bool dnsWasIp)
    {
        if (tcpReachable)
        {
            return [];
        }

        if (dnsFailed && !dnsWasIp)
        {
            return ["无法解析主机名，请检查主机名拼写或 DNS 设置"];
        }

        return
        [
            "目标服务未启动",
            "防火墙阻止了连接",
            "目标端口未开放",
            "网络不可达"
        ];
    }

    private static int ToTimeoutMilliseconds(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return (int)DefaultTimeout.TotalMilliseconds;
        }

        var ms = timeout.TotalMilliseconds;
        return ms >= int.MaxValue ? int.MaxValue : (int)ms;
    }

    private static string ProtocolDisplayName(ProtocolType protocol) => protocol switch
    {
        ProtocolType.Rdp => "RDP",
        ProtocolType.Ssh => "SSH",
        ProtocolType.Vnc => "VNC",
        _ => protocol.ToString()
    };
}
