using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Presentation.ViewModels;

/// <summary>
/// 单个会话的连接质量详情（Flyout 的数据源）。
/// <para>
/// 只在 Flyout 打开 / 点「重新检测」时执行一次参考探测，不自动周期扫描；
/// 探测异步、可取消、防连点（运行中禁用重测按钮）。
/// 质量状态与重连次数 / 会话时长 / 连接状态统一来自宿主
/// <see cref="SessionTabViewModel"/>，本类不自行维护 IsConnected。
/// </para>
/// </summary>
public sealed partial class SessionQualityState : ObservableObject, IDisposable
{
    private readonly SessionTabViewModel _tab;
    private readonly ConnectionQualityProbe _probe = new();
    private readonly CancellationTokenSource _lifetimeCts = new();

    /// <summary>最近一次探测的原始结果；未探测过为 <see langword="null"/>。</summary>
    private ConnectionQualityResult? _last;

    /// <summary>当前在途探测的 CTS（关闭 Flyout / 会话时用于取消）。</summary>
    private CancellationTokenSource? _runningCts;

    public SessionQualityState(SessionTabViewModel tab, string host, int port)
    {
        _tab = tab;
        Host = host;
        Port = port;

        _tab.PropertyChanged += OnTabPropertyChanged;
    }

    public string Host { get; }

    public int Port { get; }

    public ProtocolType Protocol => _tab.Protocol;

    // ── 展示字段 ─────────────────────────────────────────────────

    /// <summary>是否正在探测（用于按钮文案与忙碌态）。</summary>
    [ObservableProperty]
    private bool _isProbing;

    /// <summary>是否已至少完成一次探测。</summary>
    [ObservableProperty]
    private bool _hasResult;

    /// <summary>当前质量等级。</summary>
    [ObservableProperty]
    private QualityLevel _gradeLevel = QualityLevel.Unknown;

    /// <summary>
    /// 是否「已连接但网络波动」：会话处于已连接、而最近一次探测质量为较差。
    /// 驱动 Flyout 头部把「已连接」改为「网络波动」（橙色），常驻条不受影响。
    /// </summary>
    [ObservableProperty]
    private bool _isVolatile;

    /// <summary>质量等级中文（优秀 / 良好 / 一般 / 较差 / 未测得）。</summary>
    [ObservableProperty]
    private string _gradeText = "未测得";

    /// <summary>质量等级状态色资源键（仅状态点与少量文字用色）。</summary>
    [ObservableProperty]
    private string _gradeBrushKey = "Text.Tertiary";

    /// <summary>参考延迟文本（ms）。ICMP 被禁用 / 未探测为 "--"。</summary>
    [ObservableProperty]
    private string _latencyText = "--";

    /// <summary>参考抖动文本（ms）。ICMP 被禁用 / 未探测为 "--"。</summary>
    [ObservableProperty]
    private string _jitterText = "--";

    /// <summary>参考丢包文本（%）。ICMP 被禁用 / 未探测为 "--"。</summary>
    [ObservableProperty]
    private string _lossText = "--";

    /// <summary>「最近测量 HH:mm:ss」。未探测过为空串。</summary>
    [ObservableProperty]
    private string _lastProbeText = string.Empty;

    /// <summary>脚注：ICMP 被禁用 / TCP 不可达等提示。</summary>
    [ObservableProperty]
    private string _noteText = string.Empty;

    [ObservableProperty]
    private bool _hasNote;

    /// <summary>
    /// 「重新检测」命令。运行中自动禁用（防连点 / 防重入）；
    /// Flyout 打开时也通过执行本命令触发一次自动探测。
    /// </summary>
    [RelayCommand]
    private async Task RedetectAsync() => await RunProbeCoreAsync();

    /// <summary>
    /// 取消在途探测（Flyout 关闭 / 会话结束调用）。幂等。
    /// </summary>
    public void CancelRunningProbe() => _runningCts?.Cancel();

    /// <summary>
    /// 探测实现：每次都新建本轮 CTS，取消只作用于本轮；探测仅在打开 / 点击时执行，
    /// 不自动周期扫描。异常一律吞掉并落到「无可评估」展示，不让 UI 崩溃。
    /// </summary>
    private async Task RunProbeCoreAsync()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _runningCts = cts;
        IsProbing = true;

        try
        {
            var result = await _probe.ProbeAsync(Host, Port, cts.Token);
            _last = result;
            HasResult = true;
        }
        catch (OperationCanceledException)
        {
            // 关闭 Flyout / 会话结束触发取消：保留上一次结果即可。
        }
        catch (Exception)
        {
            // 探测失败不崩 UI：整次视为「无可评估」，由 Recompute 显示 "--"。
            _last = null;
            HasResult = true;
        }
        finally
        {
            if (ReferenceEquals(_runningCts, cts))
            {
                _runningCts = null;
            }

            IsProbing = false;
            Recompute();
        }
    }

    /// <summary>
    /// 用「最近一次探测 + 会话当前状态」重算全部展示字段。
    /// 触发时机：探测完成、会话连接状态变化、重连次数变化。
    /// </summary>
    private void Recompute()
    {
        var last = _last;
        var reconnectCount = _tab.ReconnectCount;

        QualityLevel level;
        if (last is null)
        {
            level = QualityLevel.Unknown;
        }
        else if (last.IcmpAvailable)
        {
            // 延迟 / 抖动 / 丢包全部取自 ICMP 参考探测；TCP 建连耗时仅作可达性依据。
            level = QualityGradeEvaluator.Evaluate(
                last.PingRttMs, last.PingJitterMs, last.PingLossPct, reconnectCount);
        }
        else
        {
            // ICMP 被禁用 → 无可评估的抖动 / 丢包参考值，等级未知（保持诚实，不猜测）。
            level = QualityLevel.Unknown;
        }

        GradeLevel = level;
        IsVolatile = _tab.IsConnected && level == QualityLevel.Poor;
        GradeText = level switch
        {
            QualityLevel.Excellent => "优秀",
            QualityLevel.Good => "良好",
            QualityLevel.Fair => "一般",
            QualityLevel.Poor => "较差",
            _ => "未测得"
        };
        GradeBrushKey = level switch
        {
            QualityLevel.Excellent or QualityLevel.Good => "Status.Success",
            QualityLevel.Fair => "Status.Warning",
            QualityLevel.Poor => "Status.Danger",
            _ => "Text.Tertiary"
        };

        LatencyText = last?.IcmpAvailable == true && last.PingRttMs is { } rtt
            ? $"{rtt:0} ms"
            : "--";
        JitterText = last?.IcmpAvailable == true && last.PingJitterMs is { } jitter
            ? $"{jitter:0.#} ms"
            : "--";
        LossText = last?.IcmpAvailable == true && last.PingLossPct is { } loss
            ? $"{loss:0.#}%"
            : "--";

        LastProbeText = last is { } measured
            ? $"最近测量 {measured.MeasuredAt:HH:mm:ss}"
            : string.Empty;

        NoteText = BuildNote(last);
        HasNote = NoteText.Length > 0;
    }

    private string BuildNote(ConnectionQualityResult? last)
    {
        if (last is null)
        {
            return string.Empty;
        }

        if (!last.TcpReachable)
        {
            return "目标端口当前不可达，延迟 / 抖动 / 丢包暂不可用。";
        }

        if (!last.IcmpAvailable)
        {
            return "ICMP 被禁用，抖动 / 丢包无法测量；不影响会话连接判定。";
        }

        if (last.SuccessfulPings < last.TotalPings)
        {
            return $"探测丢失 {last.TotalPings - last.SuccessfulPings}/{last.TotalPings} 个包。";
        }

        return string.Empty;
    }

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 会话连接状态 / 重连次数变化会改变「网络波动」与质量展示，即时重算。
        if (e.PropertyName is nameof(SessionTabViewModel.IsConnected)
            or nameof(SessionTabViewModel.ReconnectCount))
        {
            Recompute();
        }
    }

    private bool _disposed;

    /// <summary>取消进行中的探测并释放订阅。幂等——退出清理时本对象会被 dispose 两次
    /// （App.OnExit 显式 dispose MainViewModel + 容器兜底 dispose），无守卫会在
    /// 已释放的 <see cref="_lifetimeCts"/> 上再次 Cancel 抛 ObjectDisposedException。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tab.PropertyChanged -= OnTabPropertyChanged;
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
    }
}
