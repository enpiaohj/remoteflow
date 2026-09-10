using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using RemoteFlow.App.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.App.Views.Dialogs;

/// <summary>
/// 统一「测试连接」对话框：打开即自动跑 DNS → Ping → TCP 三步诊断，
/// 支持取消 / 重新测试；失败时给出结论与可能原因，详细信息默认折叠。
/// 窗体壳沿用 GroupNameDialog 的自绘样式。
/// </summary>
public partial class TestConnectionDialog : Window
{
    private readonly ConnectionProfile _profile;
    private readonly ConnectionTestService _service;
    private readonly ILogger<TestConnectionDialog> _logger;
    private readonly TestConnectionDialogModel _model;
    private readonly TimeSpan _timeout = ConnectionTestService.DefaultTimeout;

    private CancellationTokenSource? _cts;
    private bool _closed;
    private int _nextRow;

    public TestConnectionDialog(
        ConnectionProfile profile,
        ConnectionTestService service,
        ILogger<TestConnectionDialog> logger)
    {
        _profile = profile;
        _service = service;
        _logger = logger;

        InitializeComponent();

        _model = new TestConnectionDialogModel(profile);
        DataContext = _model;

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += async (_, _) => await RunCoreAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _cts?.Cancel();
        };
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        if (_model.IsRunning)
        {
            _cts?.Cancel();
        }
        else
        {
            Close();
        }
    }

    private async Task RunCoreAsync()
    {
        // 重新测试前清掉上一轮 CTS / 状态。
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _nextRow = 0;

        _model.BeginRun();

        try
        {
            var report = await _service.RunAsync(
                _profile,
                _timeout,
                cts.Token,
                progress: step => DispatchStep(step));

            if (_closed)
            {
                return;
            }

            LogStepExceptions(report);
            _model.ShowReport(report, _timeout);
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                _model.ShowCancelled();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "连接测试对话框运行失败（{Host}:{Port}）", _profile.Host, _profile.Port);
            if (!_closed)
            {
                _model.ShowError(ex);
            }
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>每步完成时推进对应行的状态；已在 UI 线程则直接更新，否则调度到 UI 线程。</summary>
    private void DispatchStep(StepResult step)
    {
        if (_closed)
        {
            return;
        }

        var dispatcher = Dispatcher;
        if (dispatcher.CheckAccess())
        {
            ApplyStep(step);
        }
        else
        {
            dispatcher.Invoke(() => ApplyStep(step));
        }
    }

    private void ApplyStep(StepResult step)
    {
        if (_closed)
        {
            return;
        }

        var index = _nextRow;
        if (index >= 0 && index < _model.Rows.Count)
        {
            _model.Rows[index].Apply(step);
        }

        _nextRow = index + 1;
        if (_nextRow < _model.Rows.Count)
        {
            _model.Rows[_nextRow].MarkRunning();
        }
    }

    private void LogStepExceptions(ConnectionTestReport report)
    {
        foreach (var step in new[] { report.Dns, report.Ping, report.Tcp })
        {
            if (step.Exception is { } ex)
            {
                _logger.LogWarning(ex, "连接测试步骤「{Label}」未通过（{Host}:{Port}）", step.Label, report.Host, report.Port);
            }
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        // 结束态才显示「重新测试」：上一轮已完成或已取消，这里不会并发重入。
        await RunCoreAsync();
    }

    private void OnToggleDetailsClick(object sender, RoutedEventArgs e) => _model.IsDetailsOpen = !_model.IsDetailsOpen;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}

/// <summary>
/// 测试连接对话框的展示模型：头部信息 + 三个诊断行 + 结论 / 可能原因 / 折叠详情。
/// 状态更新由对话框在 UI 线程驱动（ObservableCollection / INotifyPropertyChanged）。
/// </summary>
public sealed class TestConnectionDialogModel : ObservableObject
{
    // Segoe Fluent Icons 字形，与 Icons.xaml 保持一致。
    private const string GlyphRdp = "\uE7F4";
    private const string GlyphSsh = "\uE756";
    private const string GlyphVnc = "\uE7F8";
    private const string GlyphSuccess = "\uE930";
    private const string GlyphError = "\uEA39";
    private const string GlyphPending = "\uE895";
    private const string GlyphChevronRight = "\uE76C";
    private const string GlyphChevronDown = "\uE70D";

    public TestConnectionDialogModel(ConnectionProfile profile)
    {
        ConnectionName = string.IsNullOrWhiteSpace(profile.Name) ? "未命名连接" : profile.Name.Trim();

        var protocolName = profile.Protocol switch
        {
            ProtocolType.Rdp => "RDP",
            ProtocolType.Ssh => "SSH",
            _ => "VNC"
        };

        ProtocolIcon = profile.Protocol switch
        {
            ProtocolType.Rdp => GlyphRdp,
            ProtocolType.Ssh => GlyphSsh,
            _ => GlyphVnc
        };

        ProtocolBrushKey = profile.Protocol switch
        {
            ProtocolType.Rdp => "Protocol.Rdp",
            ProtocolType.Ssh => "Protocol.Ssh",
            _ => "Protocol.Vnc"
        };

        HostPortLine = $"{profile.Host} : {profile.Port} · {protocolName}";

        Rows =
        [
            new StepRow("DNS 解析"),
            new StepRow("Ping"),
            new StepRow($"TCP 端口 {profile.Port}")
        ];
    }

    // ── 头部 ──
    public string ProtocolIcon { get; }

    public string ProtocolBrushKey { get; }

    public string ConnectionName { get; }

    public string HostPortLine { get; }

    public ObservableCollection<StepRow> Rows { get; }

    // ── 运行态 ──
    private bool _isRunning;

    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

    // ── 结论行 ──
    private bool _hasConclusion;

    public bool HasConclusion
    {
        get => _hasConclusion;
        private set => SetProperty(ref _hasConclusion, value);
    }

    private string _conclusionGlyph = string.Empty;

    public string ConclusionGlyph
    {
        get => _conclusionGlyph;
        private set => SetProperty(ref _conclusionGlyph, value);
    }

    private string _conclusionBrushKey = "Text.Tertiary";

    public string ConclusionBrushKey
    {
        get => _conclusionBrushKey;
        private set => SetProperty(ref _conclusionBrushKey, value);
    }

    private string _conclusionText = string.Empty;

    public string ConclusionText
    {
        get => _conclusionText;
        private set => SetProperty(ref _conclusionText, value);
    }

    // ── 可能原因 ──
    private IReadOnlyList<string> _causes = [];

    public IReadOnlyList<string> Causes
    {
        get => _causes;
        private set => SetProperty(ref _causes, value);
    }

    private bool _hasCauses;

    public bool HasCauses
    {
        get => _hasCauses;
        private set => SetProperty(ref _hasCauses, value);
    }

    // ── ICMP 附注 ──
    private bool _hasPingNote;

    public bool HasPingNote
    {
        get => _hasPingNote;
        private set => SetProperty(ref _hasPingNote, value);
    }

    private string _pingNote = string.Empty;

    public string PingNote
    {
        get => _pingNote;
        private set => SetProperty(ref _pingNote, value);
    }

    // ── 折叠详情 ──
    private bool _isDetailsOpen;

    public bool IsDetailsOpen
    {
        get => _isDetailsOpen;
        set
        {
            if (SetProperty(ref _isDetailsOpen, value))
            {
                DetailChevron = value ? GlyphChevronDown : GlyphChevronRight;
            }
        }
    }

    private string _detailChevron = GlyphChevronRight;

    public string DetailChevron
    {
        get => _detailChevron;
        private set => SetProperty(ref _detailChevron, value);
    }

    private string _detailsText = string.Empty;

    public string DetailsText
    {
        get => _detailsText;
        private set => SetProperty(ref _detailsText, value);
    }

    /// <summary>新一轮测试开始：三行回到等待，第一行立即转「正在测试」。</summary>
    public void BeginRun()
    {
        foreach (var row in Rows)
        {
            row.MarkWaiting();
        }

        if (Rows.Count > 0)
        {
            Rows[0].MarkRunning();
        }

        HasConclusion = false;
        Causes = [];
        HasCauses = false;
        HasPingNote = false;
        PingNote = string.Empty;
        IsDetailsOpen = false;
        DetailsText = string.Empty;
        IsRunning = true;
    }

    /// <summary>诊断完成：三行落到最终状态，展示结论与可能原因。</summary>
    public void ShowReport(ConnectionTestReport report, TimeSpan timeout)
    {
        if (Rows.Count >= 3)
        {
            Rows[0].Apply(report.Dns);
            Rows[1].Apply(report.Ping);
            Rows[2].Apply(report.Tcp);
        }

        IsRunning = false;
        HasConclusion = true;

        if (report.TcpReachable)
        {
            ConclusionGlyph = GlyphSuccess;
            ConclusionBrushKey = "Status.Success";
        }
        else
        {
            ConclusionGlyph = GlyphError;
            ConclusionBrushKey = "Status.Danger";
        }

        ConclusionText = $"{report.Conclusion} · {report.TotalMs} ms";

        HasCauses = report.Causes.Count > 0;
        Causes = report.Causes;

        HasPingNote = report.TcpReachable && report.Ping.Status == StepStatus.Warning;
        PingNote = HasPingNote ? "可能禁用了 ICMP，不影响连接。" : string.Empty;

        DetailsText = BuildDetails(report, timeout);
    }

    /// <summary>用户取消：把仍在「正在测试」的行收回等待态，结论置为已取消。</summary>
    public void ShowCancelled()
    {
        foreach (var row in Rows)
        {
            if (row.Status == StepStatus.Running)
            {
                row.MarkWaiting();
            }
        }

        IsRunning = false;
        HasConclusion = true;
        ConclusionGlyph = GlyphPending;
        ConclusionBrushKey = "Text.Tertiary";
        ConclusionText = "已取消测试连接。";
        HasCauses = false;
        Causes = [];
        HasPingNote = false;
        PingNote = string.Empty;
        DetailsText = string.IsNullOrWhiteSpace(DetailsText) ? BuildDetailsFromRows() : DetailsText;
    }

    /// <summary>对话框内部异常（极少见）：按失败呈现，异常只进详情。</summary>
    public void ShowError(Exception ex)
    {
        foreach (var row in Rows)
        {
            if (row.Status == StepStatus.Running)
            {
                row.MarkWaiting();
            }
        }

        IsRunning = false;
        HasConclusion = true;
        ConclusionGlyph = GlyphError;
        ConclusionBrushKey = "Status.Danger";
        ConclusionText = "测试过程发生错误。";
        HasCauses = false;
        Causes = [];
        HasPingNote = false;
        PingNote = string.Empty;
        DetailsText = $"错误：{ex}\n\n详细信息已写入日志。";
    }

    private static string BuildDetails(ConnectionTestReport report, TimeSpan timeout)
    {
        var sb = new StringBuilder();
        sb.Append("目标     ").AppendLine(report.Host);
        sb.Append("协议     ").AppendLine(report.ProtocolName);
        sb.Append("端口     ").AppendLine(report.Port.ToString());
        sb.Append("TCP 超时 ").AppendLine($"{(int)timeout.TotalMilliseconds} ms");
        sb.AppendLine();
        AppendStepDetail(sb, report.Dns);
        AppendStepDetail(sb, report.Ping);
        AppendStepDetail(sb, report.Tcp);
        return sb.ToString().TrimEnd();
    }

    private static void AppendStepDetail(StringBuilder sb, StepResult step)
    {
        sb.Append(step.Label).Append("：").AppendLine(step.Text);
        if (step.Exception is { } ex)
        {
            sb.Append("    ").Append(ex).AppendLine();
        }
        else if (!string.IsNullOrWhiteSpace(step.Detail))
        {
            sb.Append("    ").AppendLine(step.Detail.Trim());
        }
    }

    private string BuildDetailsFromRows()
    {
        var sb = new StringBuilder();
        sb.Append("目标     ").AppendLine(HostPortLine);
        sb.AppendLine();
        foreach (var row in Rows)
        {
            sb.Append(row.Label).Append('：').AppendLine(row.StatusText);
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>诊断对话框里的一行：状态图标 + 标签 + 状态文字。</summary>
public sealed class StepRow : ObservableObject
{
    private const string GlyphSuccess = "\uE930";
    private const string GlyphWarning = "\uE7BA";
    private const string GlyphError = "\uEA39";
    private const string GlyphPending = "\uE895";

    public StepRow(string label)
    {
        Label = label;
        Status = StepStatus.Waiting;
        StatusText = "等待中…";
        UpdateVisuals();
    }

    public string Label { get; }

    public StepStatus Status { get; private set; }

    private string _statusText = string.Empty;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private string _glyph = string.Empty;

    public string Glyph
    {
        get => _glyph;
        private set => SetProperty(ref _glyph, value);
    }

    private string _brushKey = "Text.Tertiary";

    public string BrushKey
    {
        get => _brushKey;
        private set => SetProperty(ref _brushKey, value);
    }

    public void MarkWaiting()
    {
        Status = StepStatus.Waiting;
        StatusText = "等待中…";
        UpdateVisuals();
    }

    public void MarkRunning()
    {
        Status = StepStatus.Running;
        StatusText = "正在测试…";
        UpdateVisuals();
    }

    public void Apply(StepResult result)
    {
        Status = result.Status;
        StatusText = result.Text;
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        (Glyph, BrushKey) = Status switch
        {
            StepStatus.Success => (GlyphSuccess, "Status.Success"),
            StepStatus.Warning => (GlyphWarning, "Status.Warning"),
            StepStatus.Failed => (GlyphError, "Status.Danger"),
            StepStatus.Running => (GlyphPending, "Brand.Default"),
            _ => (GlyphPending, "Text.Tertiary")
        };
    }
}
