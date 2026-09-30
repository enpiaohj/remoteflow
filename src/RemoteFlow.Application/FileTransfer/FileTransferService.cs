using Microsoft.Extensions.Logging;
using RemoteFlow.Core.FileTransfer;

namespace RemoteFlow.Application.FileTransfer;

/// <summary>
/// 文件传输队列：在一个 <see cref="IRemoteFileSystem"/> 上按队列执行上传 / 下载 / 递归删除。
/// <para>
/// 由通道无关的原语组合出所有安全行为，所以 SFTP 与 SMB 共用同一份逻辑，也能用内存假实现完整测试：
/// </para>
/// <list type="bullet">
///   <item>下载先写 <c>.rfpart</c> 临时文件，成功后原子改名；上传先写远端临时名再改名。
///         取消 / 失败一律清理临时文件——最终路径上不会出现半截文件，覆盖失败时原文件保持原样。</item>
///   <item>远端名字是不可信输入：落本机前逐段清洗（<see cref="LocalNameSanitizer"/>），落盘前再校验路径不越出目标目录。</item>
///   <item>递归时不跟随符号链接 / 联接点（防环，也避免一路下载到根），并限制深度与条目数。</item>
///   <item>同一次添加的项目共享一个冲突作用域：可「对全部应用」，也可一键取消整批。</item>
///   <item>并发上限默认 2，单个文件失败不中断其余；文件夹内的失败只计数并汇总，不让整项失败。</item>
/// </list>
/// 本类不含任何 UI：冲突询问通过 <see cref="ConflictResolver"/> 回调交给界面。
/// </summary>
public sealed class FileTransferService : IAsyncDisposable
{
    /// <summary>递归深度上限。</summary>
    public const int MaxDepth = 64;

    /// <summary>单次递归（扫描 / 删除）处理的条目数上限，防止误选根目录把界面与服务器拖垮。</summary>
    public const int MaxEntries = 200_000;

    private const int MaxRenameAttempts = 1000;
    private const int StreamBufferSize = 81920;

    private readonly IRemoteFileSystem _fileSystem;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly List<TransferJob> _jobs = [];
    private readonly List<Task> _running = [];

    private int _disposed;

    public FileTransferService(IRemoteFileSystem fileSystem, ILogger logger, int maxConcurrency = 2)
    {
        _fileSystem = fileSystem;
        _logger = logger;
        _slots = new SemaphoreSlim(Math.Max(1, maxConcurrency));
    }

    /// <summary>当前队列（含已结束、尚未清除的项）的快照。</summary>
    public IReadOnlyList<TransferJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                return [.. _jobs];
            }
        }
    }

    /// <summary>是否有未结束的传输（排队 / 扫描 / 进行中）。关闭窗口 / 退出前据此确认。</summary>
    public bool HasActiveJobs
    {
        get
        {
            lock (_gate)
            {
                return _jobs.Any(j => !j.IsFinished);
            }
        }
    }

    /// <summary>新项入队。在调用 Enqueue 的线程上触发。</summary>
    public event EventHandler<TransferJob>? JobAdded;

    /// <summary>项被 <see cref="ClearFinished"/> 移出队列。</summary>
    public event EventHandler<TransferJob>? JobRemoved;

    // ── 入队 ──────────────────────────────────────────────────────

    /// <summary>把本机文件 / 文件夹上传到远端目录。每个选中项成为队列里的一项。</summary>
    public IReadOnlyList<TransferJob> EnqueueUploads(
        IEnumerable<string> localPaths, string remoteDirectory, ConflictResolver resolver)
    {
        ThrowIfDisposed();

        var remoteDir = RemotePath.Normalize(remoteDirectory);
        var scope = new ConflictScope(resolver);
        var created = new List<TransferJob>();

        foreach (var path in localPaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var isDirectory = Directory.Exists(path);
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name))
            {
                name = path; // 盘符根（C:\）没有名称，用路径本身做显示名。
            }

            var job = new TransferJob(TransferDirection.Upload, name, path, remoteDir, isDirectory);
            created.Add(job);
            Start(job, scope, ct => RunUploadAsync(job, scope, ct));
        }

        return created;
    }

    /// <summary>把远端条目下载到本机目录。每个选中项成为队列里的一项。</summary>
    public IReadOnlyList<TransferJob> EnqueueDownloads(
        IEnumerable<RemoteFileEntry> entries, string localDirectory, ConflictResolver resolver)
    {
        ThrowIfDisposed();

        var localDir = Path.GetFullPath(localDirectory);
        var scope = new ConflictScope(resolver);
        var created = new List<TransferJob>();

        foreach (var entry in entries.DistinctBy(e => e.FullPath))
        {
            var job = new TransferJob(TransferDirection.Download, entry.Name, entry.FullPath, localDir, entry.IsDirectory);
            created.Add(job);
            Start(job, scope, ct => RunDownloadAsync(job, entry, scope, ct));
        }

        return created;
    }

    /// <summary>取消一项（排队中的直接出队，进行中的尽快停下并清理临时文件）。</summary>
    public void Cancel(TransferJob job)
    {
        try
        {
            job.Cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 任务恰好已结束并释放了自己的 CTS：无需取消。
        }
    }

    /// <summary>取消队列里全部未结束的项。</summary>
    public void CancelAll()
    {
        foreach (var job in Jobs.Where(j => !j.IsFinished))
        {
            Cancel(job);
        }
    }

    /// <summary>移除已结束的项。</summary>
    public void ClearFinished()
    {
        List<TransferJob> removed;
        lock (_gate)
        {
            removed = _jobs.Where(j => j.IsFinished).ToList();
            _jobs.RemoveAll(j => j.IsFinished);
        }

        foreach (var job in removed)
        {
            JobRemoved?.Invoke(this, job);
        }
    }

    // ── 递归删除 ──────────────────────────────────────────────────

    /// <summary>
    /// 删除所选条目；目录由本方法逐项展开、自底向上删除（各通道只需实现单项删除）。
    /// 符号链接只删除链接本身，绝不进入其指向的目录。每个选中项独立成败：某项失败不影响其余，汇总返回。
    /// </summary>
    public async Task<DeleteSummary> DeleteAsync(IEnumerable<RemoteFileEntry> entries, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        var deleted = 0;
        var failed = 0;
        string? firstError = null;
        var budget = new Budget(MaxEntries);

        foreach (var entry in entries)
        {
            try
            {
                deleted += await DeleteEntryAsync(entry, 0, budget, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                firstError ??= Describe(ex);
                _logger.LogWarning("文件传输：删除一项失败，类型 {ExceptionType}", ex.GetType().Name);
            }
        }

        return new DeleteSummary(deleted, failed, firstError);
    }

    private async Task<int> DeleteEntryAsync(RemoteFileEntry entry, int depth, Budget budget, CancellationToken ct)
    {
        if (depth > MaxDepth)
        {
            throw new FileTransferException(FileTransferErrorCode.InvalidOperation, "目录层级过深，已停止删除。");
        }

        budget.Take();
        ct.ThrowIfCancellationRequested();

        if (entry.IsSymlink || !entry.IsDirectory)
        {
            // 链接 / 文件：单项删除。链接的 isDirectory 沿用条目类型，由通道决定如何只摘除链接本身。
            await _fileSystem.DeleteAsync(entry.FullPath, entry.IsDirectory, ct).ConfigureAwait(false);
            return 1;
        }

        var count = 0;
        foreach (var child in await _fileSystem.ListAsync(entry.FullPath, ct).ConfigureAwait(false))
        {
            count += await DeleteEntryAsync(child, depth + 1, budget, ct).ConfigureAwait(false);
        }

        await _fileSystem.DeleteAsync(entry.FullPath, true, ct).ConfigureAwait(false);
        return count + 1;
    }

    // ── 队列调度 ──────────────────────────────────────────────────

    private void Start(TransferJob job, ConflictScope scope, Func<CancellationToken, Task> run)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, scope.Token);
        job.Cts = cts;

        lock (_gate)
        {
            _jobs.Add(job);
        }

        JobAdded?.Invoke(this, job);

        var task = Task.Run(async () =>
        {
            try
            {
                try
                {
                    await _slots.WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    job.SetStatus(TransferStatus.Cancelled);
                    return;
                }

                try
                {
                    await run(cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    _slots.Release();
                }
            }
            finally
            {
                job.Cts = null;
                cts.Dispose();
            }
        });

        lock (_gate)
        {
            _running.RemoveAll(t => t.IsCompleted);
            _running.Add(task);
        }
    }

    // ── 上传 ──────────────────────────────────────────────────────

    private async Task RunUploadAsync(TransferJob job, ConflictScope scope, CancellationToken ct)
    {
        var stats = new RunStats();
        try
        {
            job.SetStatus(job.IsDirectory ? TransferStatus.Scanning : TransferStatus.Running);

            if (!job.IsDirectory)
            {
                var info = new FileInfo(job.SourcePath);
                job.SetTotals(info.Length, 1);
                job.SetStatus(TransferStatus.Running);
                await UploadFileAsync(
                    job, info.FullName, info.Name, info.Length, job.TargetDirectory, stats, scope, ct).ConfigureAwait(false);
            }
            else
            {
                await UploadFolderAsync(job, stats, scope, ct).ConfigureAwait(false);
            }

            Finish(job, stats);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.SetStatus(TransferStatus.Cancelled);
        }
        catch (Exception ex)
        {
            Fail(job, ex);
        }
    }

    private async Task UploadFolderAsync(TransferJob job, RunStats stats, ConflictScope scope, CancellationToken ct)
    {
        var plan = ScanLocal(job.SourcePath, stats, ct);
        job.FileSkipped(plan.Skipped);
        job.SetTotals(plan.TotalBytes, plan.Files.Count);
        job.SetStatus(TransferStatus.Running);

        // 根目录：远端已有同名目录 → 合并；同名文件 → 冲突（只能跳过 / 改名）。
        var remoteRootName = job.Name;
        var remoteRoot = RemotePath.Combine(job.TargetDirectory, remoteRootName);
        var existing = await _fileSystem.StatAsync(remoteRoot, ct).ConfigureAwait(false);
        if (existing is { IsDirectory: false })
        {
            var action = await scope.DecideAsync(
                new TransferConflict(TransferDirection.Upload, remoteRootName, false, true, existing.Size, existing.Modified, null, null),
                ct).ConfigureAwait(false);
            if (action == ConflictAction.Rename)
            {
                remoteRootName = await NextFreeRemoteNameAsync(job.TargetDirectory, remoteRootName, ct).ConfigureAwait(false);
                remoteRoot = RemotePath.Combine(job.TargetDirectory, remoteRootName);
            }
            else
            {
                job.AddBytes(plan.TotalBytes);
                job.FileSkipped(Math.Max(1, plan.Files.Count));
                return;
            }
        }

        await _fileSystem.CreateDirectoryAsync(remoteRoot, ct).ConfigureAwait(false);

        var failedDirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segments in plan.Dirs)
        {
            ct.ThrowIfCancellationRequested();
            var key = string.Join('/', segments);
            if (HasFailedAncestor(failedDirs, segments))
            {
                failedDirs.Add(key);
                continue;
            }

            try
            {
                await _fileSystem.CreateDirectoryAsync(JoinRemote(remoteRoot, segments), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedDirs.Add(key);
                stats.NoteFailure(ex);
            }
        }

        foreach (var file in plan.Files)
        {
            ct.ThrowIfCancellationRequested();
            var parent = file.Segments[..^1];
            if (HasFailedAncestor(failedDirs, parent, includeSelf: true))
            {
                job.FileFailed();
                job.AddBytes(file.Size);
                continue;
            }

            try
            {
                await UploadFileAsync(
                    job, file.SourcePath, file.Segments[^1], file.Size, JoinRemote(remoteRoot, parent), stats, scope, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                job.FileFailed();
                stats.NoteFailure(ex);
                _logger.LogWarning("文件传输：上传一个文件失败，类型 {ExceptionType}", ex.GetType().Name);
            }
        }
    }

    private async Task UploadFileAsync(
        TransferJob job, string localFile, string name, long size, string remoteDir,
        RunStats stats, ConflictScope scope, CancellationToken ct)
    {
        job.SetCurrentFile(name);

        if (!RemotePath.IsValidEntryName(name))
        {
            job.FileSkipped();
            job.AddBytes(size);
            return;
        }

        var target = RemotePath.Combine(remoteDir, name);
        var overwrite = false;

        var existing = await _fileSystem.StatAsync(target, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            var modified = TryGetLocalModified(localFile);
            var action = await scope.DecideAsync(
                new TransferConflict(
                    TransferDirection.Upload, name, existing.IsDirectory, false,
                    existing.Size, existing.Modified, size, modified),
                ct).ConfigureAwait(false);

            switch (action)
            {
                case ConflictAction.Overwrite when !existing.IsDirectory:
                    overwrite = true;
                    break;
                case ConflictAction.Rename:
                    name = await NextFreeRemoteNameAsync(remoteDir, name, ct).ConfigureAwait(false);
                    target = RemotePath.Combine(remoteDir, name);
                    break;
                default: // Skip、类型冲突下的 Overwrite
                    job.FileSkipped();
                    job.AddBytes(size);
                    return;
            }
        }

        var temp = RemotePath.Combine(remoteDir, TempName($".{name}"));
        long reported = 0;
        var progress = new InlineProgress(total =>
        {
            job.AddBytes(total - reported);
            reported = total;
        });

        var completed = false;
        try
        {
            await using (var stream = new FileStream(
                localFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                StreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await _fileSystem.UploadAsync(stream, temp, progress, ct).ConfigureAwait(false);
            }

            await _fileSystem.RenameAsync(temp, target, overwrite, ct).ConfigureAwait(false);
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                await TryDeleteRemoteAsync(temp).ConfigureAwait(false);
            }
        }

        if (reported < size)
        {
            job.AddBytes(size - reported); // 进度回调未必上报最后一段；补齐让进度条走满。
        }

        job.FileDone();
    }

    // ── 下载 ──────────────────────────────────────────────────────

    private async Task RunDownloadAsync(TransferJob job, RemoteFileEntry entry, ConflictScope scope, CancellationToken ct)
    {
        var stats = new RunStats();
        try
        {
            job.SetStatus(entry.IsDirectory ? TransferStatus.Scanning : TransferStatus.Running);

            if (entry.IsDirectory && entry.IsSymlink)
            {
                // 明确选中的符号链接目录也不递归：它可能指向 / 或形成环，进入目录后再选其中的内容更可控。
                job.SetMessage("符号链接目录不会被递归下载，请进入该目录后选择其中的内容。");
                job.FileSkipped();
                job.SetStatus(TransferStatus.Completed);
                return;
            }

            if (!entry.IsDirectory)
            {
                job.SetTotals(entry.Size, 1);
                job.SetStatus(TransferStatus.Running);
                await DownloadFileAsync(job, entry, job.TargetDirectory, stats, scope, ct).ConfigureAwait(false);
            }
            else
            {
                await DownloadFolderAsync(job, entry, stats, scope, ct).ConfigureAwait(false);
            }

            Finish(job, stats);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.SetStatus(TransferStatus.Cancelled);
        }
        catch (Exception ex)
        {
            Fail(job, ex);
        }
    }

    private async Task DownloadFolderAsync(
        TransferJob job, RemoteFileEntry root, RunStats stats, ConflictScope scope, CancellationToken ct)
    {
        var rootName = LocalNameSanitizer.Sanitize(root.Name);
        if (rootName is null)
        {
            job.SetMessage("文件夹名称无法安全保存到本机，已跳过。");
            job.FileSkipped();
            job.SetStatus(TransferStatus.Completed);
            return;
        }

        if (rootName.Value.Changed)
        {
            stats.Renamed++;
        }

        var plan = await ScanRemoteAsync(root, stats, ct).ConfigureAwait(false);
        job.FileSkipped(plan.Skipped);
        job.SetTotals(plan.TotalBytes, plan.Files.Count);
        job.SetStatus(TransferStatus.Running);

        var localRootName = rootName.Value.Name;
        var localRoot = LocalNameSanitizer.ResolveUnder(job.TargetDirectory, localRootName);
        if (File.Exists(localRoot))
        {
            var action = await scope.DecideAsync(
                new TransferConflict(
                    TransferDirection.Download, localRootName, false, true,
                    new FileInfo(localRoot).Length, TryGetLocalModified(localRoot), null, root.Modified),
                ct).ConfigureAwait(false);
            if (action != ConflictAction.Rename)
            {
                job.AddBytes(plan.TotalBytes);
                job.FileSkipped(Math.Max(1, plan.Files.Count));
                return;
            }

            localRootName = NextFreeLocalName(job.TargetDirectory, localRootName);
            localRoot = LocalNameSanitizer.ResolveUnder(job.TargetDirectory, localRootName);
        }

        Directory.CreateDirectory(localRoot);

        var failedDirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segments in plan.Dirs)
        {
            ct.ThrowIfCancellationRequested();
            var key = string.Join('/', segments);
            if (HasFailedAncestor(failedDirs, segments))
            {
                failedDirs.Add(key);
                continue;
            }

            try
            {
                Directory.CreateDirectory(LocalNameSanitizer.ResolveUnder(localRoot, segments));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedDirs.Add(key);
                stats.NoteFailure(ex);
            }
        }

        foreach (var file in plan.Files)
        {
            ct.ThrowIfCancellationRequested();
            var parent = file.Segments[..^1];
            if (HasFailedAncestor(failedDirs, parent, includeSelf: true))
            {
                job.FileFailed();
                job.AddBytes(file.Size);
                continue;
            }

            try
            {
                var localDir = parent.Length == 0 ? localRoot : LocalNameSanitizer.ResolveUnder(localRoot, parent);
                await DownloadFileAsync(job, file.Entry!, localDir, stats, scope, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                job.FileFailed();
                stats.NoteFailure(ex);
                _logger.LogWarning("文件传输：下载一个文件失败，类型 {ExceptionType}", ex.GetType().Name);
            }
        }
    }

    private async Task DownloadFileAsync(
        TransferJob job, RemoteFileEntry entry, string localDir,
        RunStats stats, ConflictScope scope, CancellationToken ct)
    {
        job.SetCurrentFile(entry.Name);

        var sanitized = LocalNameSanitizer.Sanitize(entry.Name);
        if (sanitized is null)
        {
            job.FileSkipped();
            job.AddBytes(entry.Size);
            return;
        }

        var name = sanitized.Value.Name;
        if (sanitized.Value.Changed)
        {
            stats.Renamed++;
        }

        var target = LocalNameSanitizer.ResolveUnder(localDir, name);
        var overwrite = false;

        var existsAsFile = File.Exists(target);
        var existsAsDirectory = Directory.Exists(target);
        if (existsAsFile || existsAsDirectory)
        {
            var existingInfo = existsAsFile ? new FileInfo(target) : null;
            var action = await scope.DecideAsync(
                new TransferConflict(
                    TransferDirection.Download, name, existsAsDirectory, false,
                    existingInfo?.Length, TryGetLocalModified(target), entry.Size, entry.Modified),
                ct).ConfigureAwait(false);

            switch (action)
            {
                case ConflictAction.Overwrite when existsAsFile:
                    overwrite = true;
                    break;
                case ConflictAction.Rename:
                    name = NextFreeLocalName(localDir, name);
                    target = LocalNameSanitizer.ResolveUnder(localDir, name);
                    break;
                default:
                    job.FileSkipped();
                    job.AddBytes(entry.Size);
                    return;
            }
        }

        var temp = LocalNameSanitizer.ResolveUnder(localDir, TempName(name));
        long reported = 0;
        var progress = new InlineProgress(total =>
        {
            job.AddBytes(total - reported);
            reported = total;
        });

        var completed = false;
        try
        {
            await using (var stream = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                StreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await _fileSystem.DownloadAsync(entry.FullPath, stream, progress, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(temp, target, overwrite);
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                TryDeleteLocal(temp);
            }
        }

        if (entry.Modified is { } modified)
        {
            try
            {
                File.SetLastWriteTimeUtc(target, modified.UtcDateTime);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
            {
                // 保留修改时间是锦上添花，失败不影响传输结果。
            }
        }

        if (reported < entry.Size)
        {
            job.AddBytes(entry.Size - reported);
        }

        job.FileDone();
    }

    // ── 扫描 ──────────────────────────────────────────────────────

    /// <summary>枚举本机文件夹：跳过联接点 / 符号链接，限制深度与条目数。</summary>
    private static TransferPlan ScanLocal(string root, RunStats stats, CancellationToken ct)
    {
        var plan = new TransferPlan();
        var budget = new Budget(MaxEntries);
        var pending = new Stack<(string Path, string[] Segments)>();
        pending.Push((root, []));

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (directory, segments) = pending.Pop();
            if (segments.Length > MaxDepth)
            {
                plan.Skipped++;
                continue;
            }

            IEnumerable<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                stats.NoteFailure(ex);
                continue;
            }

            foreach (var child in children)
            {
                budget.Take();
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    plan.Skipped++; // 联接点 / 符号链接：不跟随。
                    continue;
                }

                var childSegments = new string[segments.Length + 1];
                segments.CopyTo(childSegments, 0);
                childSegments[^1] = child.Name;

                if ((child.Attributes & FileAttributes.Directory) != 0)
                {
                    plan.Dirs.Add(childSegments);
                    pending.Push((child.FullName, childSegments));
                }
                else if (child is FileInfo file)
                {
                    plan.Files.Add(new PlanFile(childSegments, file.FullName, file.Length, null));
                    plan.TotalBytes += file.Length;
                }
            }
        }

        return plan;
    }

    /// <summary>枚举远端目录树：不跟随符号链接，名称先逐段清洗，限制深度与条目数。</summary>
    private async Task<TransferPlan> ScanRemoteAsync(RemoteFileEntry root, RunStats stats, CancellationToken ct)
    {
        var plan = new TransferPlan();
        var budget = new Budget(MaxEntries);
        var pending = new Stack<(string Path, string[] Segments)>();
        pending.Push((root.FullPath, []));

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (directory, segments) = pending.Pop();
            if (segments.Length > MaxDepth)
            {
                plan.Skipped++;
                continue;
            }

            IReadOnlyList<RemoteFileEntry> children;
            try
            {
                children = await _fileSystem.ListAsync(directory, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileTransferException)
            {
                stats.NoteFailure(ex);
                continue;
            }

            foreach (var child in children)
            {
                budget.Take();
                if (child.IsSymlink)
                {
                    plan.Skipped++; // 符号链接：不跟随（文件链接也跳过，行为统一可预期）。
                    continue;
                }

                var safe = LocalNameSanitizer.Sanitize(child.Name);
                if (safe is null)
                {
                    plan.Skipped++;
                    continue;
                }

                var childSegments = new string[segments.Length + 1];
                segments.CopyTo(childSegments, 0);
                childSegments[^1] = safe.Value.Name;

                if (child.IsDirectory)
                {
                    plan.Dirs.Add(childSegments);
                    pending.Push((child.FullPath, childSegments));
                }
                else
                {
                    plan.Files.Add(new PlanFile(childSegments, child.FullPath, child.Size, child));
                    plan.TotalBytes += child.Size;
                }
            }
        }

        return plan;
    }

    // ── 小工具 ────────────────────────────────────────────────────

    private void Finish(TransferJob job, RunStats stats)
    {
        var parts = new List<string>();
        if (job.FailedCount > 0)
        {
            parts.Add($"{job.FailedCount} 个文件失败" + (stats.FirstError is { } e ? $"（{e}）" : string.Empty));
        }

        if (job.SkippedCount > 0)
        {
            parts.Add($"{job.SkippedCount} 项已跳过（同名冲突、符号链接或名称无法保存）");
        }

        if (stats.Renamed > 0)
        {
            parts.Add($"{stats.Renamed} 个名称含非法字符，已自动改名");
        }

        if (parts.Count > 0)
        {
            job.SetMessage(string.Join("；", parts));
        }

        // 一个成功的都没有、却有失败：整项判为失败，别让「完成」误导用户。
        var allFailed = job.FailedCount > 0 && job.FilesDone == 0 && job.SkippedCount == 0;
        job.SetStatus(allFailed ? TransferStatus.Failed : TransferStatus.Completed);
    }

    private void Fail(TransferJob job, Exception ex)
    {
        _logger.LogWarning("文件传输：一项传输失败，类型 {ExceptionType}", ex.GetType().Name);
        job.SetMessage(Describe(ex));
        job.SetStatus(TransferStatus.Failed);
    }

    /// <summary>异常 → 面向用户的一句话。只用异常类型与我们自己写的消息，不回显系统消息（可能带路径）。</summary>
    internal static string Describe(Exception ex) => ex switch
    {
        FileTransferException transfer => transfer.Message,
        UnauthorizedAccessException => "本机没有权限访问该文件或目录。",
        DirectoryNotFoundException or FileNotFoundException => "本机文件或目录不存在。",
        PathTooLongException => "路径过长。",
        IOException => "读写本机文件时出错。",
        _ => "传输失败。"
    };

    private async Task TryDeleteRemoteAsync(string path)
    {
        try
        {
            // 取消场景下原 token 已失效，用独立的短超时清理临时文件。
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _fileSystem.DeleteAsync(path, false, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("文件传输：清理远端临时文件失败，类型 {ExceptionType}", ex.GetType().Name);
        }
    }

    private void TryDeleteLocal(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("文件传输：清理本机临时文件失败，类型 {ExceptionType}", ex.GetType().Name);
        }
    }

    private async Task<string> NextFreeRemoteNameAsync(string remoteDir, string name, CancellationToken ct)
    {
        for (var i = 1; i <= MaxRenameAttempts; i++)
        {
            var candidate = NumberedName(name, i);
            if (await _fileSystem.StatAsync(RemotePath.Combine(remoteDir, candidate), ct).ConfigureAwait(false) is null)
            {
                return candidate;
            }
        }

        throw new FileTransferException(FileTransferErrorCode.AlreadyExists, "同名项目过多，无法自动改名。");
    }

    private static string NextFreeLocalName(string localDir, string name)
    {
        for (var i = 1; i <= MaxRenameAttempts; i++)
        {
            var candidate = NumberedName(name, i);
            var path = LocalNameSanitizer.ResolveUnder(localDir, candidate);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return candidate;
            }
        }

        throw new FileTransferException(FileTransferErrorCode.AlreadyExists, "同名项目过多，无法自动改名。");
    }

    /// <summary>临时文件名：<c>基名.8位随机.rfpart</c>。同名并发与残留互不冲突；传输成功后改回目标名。</summary>
    private static string TempName(string baseName) => $"{baseName}.{Guid.NewGuid().ToString("N")[..8]}.rfpart";

    /// <summary>「a.txt」→「a (1).txt」；无扩展名或点文件（.bashrc）→ 在末尾加序号。</summary>
    internal static string NumberedName(string name, int number)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? $"{name[..dot]} ({number}){name[dot..]}" : $"{name} ({number})";
    }

    private static DateTimeOffset? TryGetLocalModified(string path)
    {
        try
        {
            var utc = File.GetLastWriteTimeUtc(path);
            return utc.Year > 1601 ? new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string JoinRemote(string root, string[] segments)
    {
        var path = root;
        foreach (var segment in segments)
        {
            path = RemotePath.Combine(path, segment);
        }

        return path;
    }

    private static bool HasFailedAncestor(HashSet<string> failedDirs, string[] segments, bool includeSelf = false)
    {
        var limit = includeSelf ? segments.Length : segments.Length - 1;
        for (var i = 1; i <= limit; i++)
        {
            if (failedDirs.Contains(string.Join('/', segments[..i])))
            {
                return true;
            }
        }

        return false;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);

        Task[] running;
        lock (_gate)
        {
            running = [.. _running];
        }

        // 给在途任务一个清理临时文件的机会，但设上限，避免关闭窗口卡住。
        await Task.WhenAny(Task.WhenAll(running), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        _lifetime.Dispose();
        _slots.Dispose();
    }

    // ── 内部类型 ──────────────────────────────────────────────────

    private sealed class TransferPlan
    {
        public List<string[]> Dirs { get; } = [];

        public List<PlanFile> Files { get; } = [];

        public long TotalBytes { get; set; }

        /// <summary>扫描时被跳过的项（符号链接 / 联接点、名称无法安全保存、层级过深）。</summary>
        public int Skipped { get; set; }
    }

    private sealed record PlanFile(string[] Segments, string SourcePath, long Size, RemoteFileEntry? Entry);

    private sealed class RunStats
    {
        public int Renamed { get; set; }

        public string? FirstError { get; private set; }

        public void NoteFailure(Exception ex) => FirstError ??= Describe(ex);
    }

    private sealed class Budget(int limit)
    {
        private int _used;

        public void Take()
        {
            if (++_used > limit)
            {
                throw new FileTransferException(
                    FileTransferErrorCode.InvalidOperation, "条目数量过多，已停止。请缩小所选范围后重试。");
            }
        }
    }

    private sealed class InlineProgress(Action<long> handler) : IProgress<long>
    {
        public void Report(long value) => handler(value);
    }

    /// <summary>
    /// 同一次添加的项目共享的冲突作用域：串行化询问（同一时刻只弹一个）、记住「对全部应用」的选择、
    /// 选「取消」时取消整批。
    /// </summary>
    private sealed class ConflictScope(ConflictResolver resolver)
    {
        private readonly SemaphoreSlim _promptGate = new(1, 1);
        private readonly CancellationTokenSource _cancelAll = new();
        private ConflictAction? _remembered;

        public CancellationToken Token => _cancelAll.Token;

        public async Task<ConflictAction> DecideAsync(TransferConflict conflict, CancellationToken ct)
        {
            if (_remembered is { } early)
            {
                return Coerce(early, conflict);
            }

            await _promptGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_remembered is { } late)
                {
                    return Coerce(late, conflict);
                }

                var decision = await resolver(conflict, ct).ConfigureAwait(false);
                if (decision.Action == ConflictAction.Cancel)
                {
                    await _cancelAll.CancelAsync().ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    return ConflictAction.Skip; // 不可达：上一行必抛；保底不写任何东西。
                }

                if (decision.ApplyToAll)
                {
                    _remembered = decision.Action;
                }

                return Coerce(decision.Action, conflict);
            }
            finally
            {
                _promptGate.Release();
            }
        }

        /// <summary>类型冲突（文件对文件夹）无法覆盖，记住的「覆盖」对它退化为跳过。</summary>
        private static ConflictAction Coerce(ConflictAction action, TransferConflict conflict)
            => action == ConflictAction.Overwrite && !conflict.CanOverwrite ? ConflictAction.Skip : action;
    }
}
