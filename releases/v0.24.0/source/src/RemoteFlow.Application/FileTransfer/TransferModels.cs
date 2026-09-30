using System.Diagnostics;

namespace RemoteFlow.Application.FileTransfer;

public enum TransferDirection
{
    Upload,
    Download
}

public enum TransferStatus
{
    /// <summary>排队中（并发槽位已满）。</summary>
    Queued,

    /// <summary>正在枚举文件夹内容以统计总量。</summary>
    Scanning,

    Running,

    /// <summary>完成。<see cref="TransferJob.HasIssues"/> 为 true 表示有文件被跳过或失败。</summary>
    Completed,

    /// <summary>整项失败（如无法列目录、无权限）；详情见 <see cref="TransferJob.Message"/>。</summary>
    Failed,

    Cancelled
}

/// <summary>覆盖冲突的处理动作。</summary>
public enum ConflictAction
{
    Overwrite,
    Skip,

    /// <summary>保留两者：新文件改名为「名称 (1).扩展名」。</summary>
    Rename,

    /// <summary>取消整批传输（同一次添加的全部项目）。</summary>
    Cancel
}

/// <param name="Action">处理动作。</param>
/// <param name="ApplyToAll">对同一批后续冲突沿用本次选择（不再询问）。</param>
public readonly record struct ConflictDecision(ConflictAction Action, bool ApplyToAll = false);

/// <summary>一次覆盖冲突的描述，交给界面询问用户。</summary>
/// <param name="CanOverwrite">
/// 能否覆盖：文件与文件夹同名（类型不同）时为 false——无法用一个替换另一个，只能跳过或改名。
/// </param>
public sealed record TransferConflict(
    TransferDirection Direction,
    string Name,
    bool ExistingIsDirectory,
    bool IncomingIsDirectory,
    long? ExistingSize,
    DateTimeOffset? ExistingModified,
    long? IncomingSize,
    DateTimeOffset? IncomingModified)
{
    public bool CanOverwrite => ExistingIsDirectory == IncomingIsDirectory;
}

/// <summary>冲突询问回调。由界面实现（弹窗）；Application 层不含任何 UI。</summary>
public delegate Task<ConflictDecision> ConflictResolver(TransferConflict conflict, CancellationToken cancellationToken);

/// <summary>递归删除的结果汇总。</summary>
public sealed record DeleteSummary(int Deleted, int Failed, string? FirstError);

/// <summary>
/// 队列里的一项传输（用户一次选中的一个文件或文件夹）。字段可被后台线程更新、被界面线程读取：
/// 数值用 volatile 读写，状态迁移与文本在锁内更新；<see cref="Changed"/> 在状态迁移时立即触发，
/// 进度变化则节流到约 10Hz，避免大量小文件把界面线程淹没。
/// </summary>
public sealed class TransferJob
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _gate = new();
    private readonly Stopwatch _sinceLastNotify = Stopwatch.StartNew();

    private long _bytesDone;
    private long _bytesTotal;
    private int _filesDone;
    private int _filesTotal;
    private int _skippedCount;
    private int _failedCount;
    private TransferStatus _status = TransferStatus.Queued;
    private string _currentFile = string.Empty;
    private string _message = string.Empty;

    internal TransferJob(TransferDirection direction, string name, string sourcePath, string targetDirectory, bool isDirectory)
    {
        Direction = direction;
        Name = name;
        SourcePath = sourcePath;
        TargetDirectory = targetDirectory;
        IsDirectory = isDirectory;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public TransferDirection Direction { get; }

    /// <summary>显示名（选中项的名称）。</summary>
    public string Name { get; }

    /// <summary>来源：上传为本机路径，下载为远端虚拟路径。</summary>
    public string SourcePath { get; }

    /// <summary>目标目录：上传为远端目录，下载为本机目录。</summary>
    public string TargetDirectory { get; }

    public bool IsDirectory { get; }

    public TransferStatus Status
    {
        get { lock (_gate) { return _status; } }
    }

    public long BytesDone => Volatile.Read(ref _bytesDone);

    /// <summary>总字节数；文件夹在扫描完成前为 0（此时 <see cref="Status"/> 为 <see cref="TransferStatus.Scanning"/>）。</summary>
    public long BytesTotal => Volatile.Read(ref _bytesTotal);

    public int FilesDone => Volatile.Read(ref _filesDone);

    public int FilesTotal => Volatile.Read(ref _filesTotal);

    public int SkippedCount => Volatile.Read(ref _skippedCount);

    public int FailedCount => Volatile.Read(ref _failedCount);

    public string CurrentFile
    {
        get { lock (_gate) { return _currentFile; } }
    }

    /// <summary>最近一条面向用户的说明：失败原因、跳过 / 失败汇总等。</summary>
    public string Message
    {
        get { lock (_gate) { return _message; } }
    }

    public bool HasIssues => SkippedCount > 0 || FailedCount > 0;

    public bool IsFinished => Status is TransferStatus.Completed or TransferStatus.Failed or TransferStatus.Cancelled;

    /// <summary>0–1 的完成度；总量未知时为 0。</summary>
    public double Progress => BytesTotal > 0 ? Math.Min(1d, (double)BytesDone / BytesTotal) : (IsFinished && Status == TransferStatus.Completed ? 1d : 0d);

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>状态或进度变化。可能在任意线程触发，订阅方需自行切回界面线程。</summary>
    public event EventHandler? Changed;

    internal CancellationTokenSource? Cts { get; set; }

    internal void SetStatus(TransferStatus status)
    {
        lock (_gate)
        {
            _status = status;
            if (status is TransferStatus.Scanning or TransferStatus.Running)
            {
                StartedAt ??= DateTimeOffset.Now;
            }

            if (status is TransferStatus.Completed or TransferStatus.Failed or TransferStatus.Cancelled)
            {
                FinishedAt = DateTimeOffset.Now;
                _currentFile = string.Empty;
            }
        }

        RaiseChanged(force: true);
    }

    internal void SetMessage(string message)
    {
        lock (_gate)
        {
            _message = message;
        }

        RaiseChanged(force: true);
    }

    internal void SetTotals(long bytes, int files)
    {
        Volatile.Write(ref _bytesTotal, bytes);
        Volatile.Write(ref _filesTotal, files);
        RaiseChanged(force: true);
    }

    internal void SetCurrentFile(string name)
    {
        lock (_gate)
        {
            _currentFile = name;
        }

        RaiseChanged(force: false);
    }

    internal void AddBytes(long delta)
    {
        if (delta != 0)
        {
            Interlocked.Add(ref _bytesDone, delta);
        }

        RaiseChanged(force: false);
    }

    internal void FileDone() => Interlocked.Increment(ref _filesDone);

    internal void FileSkipped(int count = 1) => Interlocked.Add(ref _skippedCount, count);

    internal void FileFailed() => Interlocked.Increment(ref _failedCount);

    private void RaiseChanged(bool force)
    {
        if (!force)
        {
            lock (_gate)
            {
                if (_sinceLastNotify.Elapsed < ProgressInterval)
                {
                    return;
                }

                _sinceLastNotify.Restart();
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
