using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.FileTransfer;
using RemoteFlow.Presentation.Host;

namespace RemoteFlow.Presentation.ViewModels;

/// <summary>
/// 传输队列里的一项。把后台线程上的 <see cref="TransferJob"/> 状态翻译成界面文案，
/// 变化通知经 <see cref="IUiDispatcher"/> 切回界面线程（Job 的变化事件可能来自任意线程，且已节流到约 10Hz）。
/// </summary>
public sealed partial class TransferItemViewModel : ObservableObject, IDisposable
{
    private readonly TransferJob _job;
    private readonly FileTransferService _service;
    private readonly IUiDispatcher _ui;
    private int _disposed;
    private bool _finishedRaised;

    public TransferItemViewModel(TransferJob job, FileTransferService service, IUiDispatcher ui)
    {
        _job = job;
        _service = service;
        _ui = ui;
        _job.Changed += OnJobChanged;
        Refresh();
    }

    public TransferJob Job => _job;

    public string Name => _job.Name;

    public bool IsUpload => _job.Direction == TransferDirection.Upload;

    public bool IsFinished => _job.IsFinished;

    /// <summary>该项结束（完成 / 失败 / 取消）时触发，且只触发一次；在界面线程上。</summary>
    public event EventHandler? Finished;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>进度条下方的详情：已传 / 总量、第几个文件、当前文件。</summary>
    [ObservableProperty]
    private string _detailText = string.Empty;

    /// <summary>失败 / 跳过等说明，供悬停提示与详情展示。</summary>
    [ObservableProperty]
    private string _messageText = string.Empty;

    /// <summary>0–100。</summary>
    [ObservableProperty]
    private double _progressPercent;

    /// <summary>排队 / 扫描 / 总量未知时不确定进度。</summary>
    [ObservableProperty]
    private bool _isIndeterminate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _canCancel;

    /// <summary>状态图标资源键与颜色资源键：进行中显示方向图标，结束后显示结果图标。</summary>
    [ObservableProperty]
    private string _statusIconKey = "Ui.Pending";

    [ObservableProperty]
    private string _statusBrushKey = "Text.Tertiary";

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _service.Cancel(_job);

    private void OnJobChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _ui.Post(Refresh);
    }

    private void Refresh()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var status = _job.Status;
        var total = _job.BytesTotal;
        var done = _job.BytesDone;
        var percent = _job.Progress * 100d;

        ProgressPercent = percent;
        IsIndeterminate = status is TransferStatus.Queued or TransferStatus.Scanning
                          || (status == TransferStatus.Running && total <= 0 && _job.FilesTotal == 0);
        CanCancel = !_job.IsFinished;
        MessageText = _job.Message;

        (StatusText, StatusIconKey, StatusBrushKey) = status switch
        {
            TransferStatus.Queued => ("排队中", DirectionIcon(), "Text.Tertiary"),
            TransferStatus.Scanning => ("正在统计文件…", DirectionIcon(), "Brand.Default"),
            TransferStatus.Running => ($"{percent:0}%", DirectionIcon(), "Brand.Default"),
            TransferStatus.Completed when _job.HasIssues => (CompletedWithIssuesText(), "Ui.Warning.Filled", "Status.Warning"),
            TransferStatus.Completed => ("已完成", "Ui.Success.Filled", "Status.Success"),
            TransferStatus.Failed => ("失败", "Ui.Error.Filled", "Status.Danger"),
            _ => ("已取消", "Ui.Pending", "Text.Tertiary")
        };

        DetailText = BuildDetail(status, done, total);
        OnPropertyChanged(nameof(IsFinished));

        if (_job.IsFinished && !_finishedRaised)
        {
            _finishedRaised = true;
            Finished?.Invoke(this, EventArgs.Empty);
        }
    }

    private string DirectionIcon() => IsUpload ? "Ui.Upload" : "Ui.Download";

    private string CompletedWithIssuesText()
    {
        var parts = new List<string>();
        if (_job.FailedCount > 0)
        {
            parts.Add($"{_job.FailedCount} 个失败");
        }

        if (_job.SkippedCount > 0)
        {
            parts.Add($"{_job.SkippedCount} 项跳过");
        }

        return $"已完成（{string.Join("，", parts)}）";
    }

    private string BuildDetail(TransferStatus status, long done, long total)
    {
        switch (status)
        {
            case TransferStatus.Failed:
            case TransferStatus.Cancelled:
                return _job.Message;

            case TransferStatus.Completed:
                return _job.IsDirectory
                    ? $"{ByteSizeFormatter.Format(total)} · {_job.FilesDone} 个文件"
                    : ByteSizeFormatter.Format(total);

            case TransferStatus.Running:
                var text = total > 0
                    ? $"{ByteSizeFormatter.Format(done)} / {ByteSizeFormatter.Format(total)}"
                    : ByteSizeFormatter.Format(done);
                if (_job.IsDirectory && _job.FilesTotal > 0)
                {
                    text += $" · 第 {Math.Min(_job.FilesDone + 1, _job.FilesTotal)}/{_job.FilesTotal} 个文件";
                }

                var current = _job.CurrentFile;
                return current.Length > 0 ? $"{text} · {current}" : text;

            default:
                return string.Empty;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _job.Changed -= OnJobChanged;
        }
    }
}
