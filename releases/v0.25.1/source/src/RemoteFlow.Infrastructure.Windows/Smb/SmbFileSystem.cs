using Microsoft.Extensions.Logging;
using RemoteFlow.Core.FileTransfer;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Infrastructure.Smb;

/// <summary>
/// SMB 管理共享的远端文件系统：根 <c>/</c> 列出磁盘共享（<c>C$</c>、<c>D$</c>…），
/// 共享内用 <c>System.IO</c> 访问 UNC 路径。认证由 <see cref="ISmbConnection"/> 持有的 WNet 会话完成。
/// <para>
/// 与 <see cref="IRemoteFileSystem"/> 的约定一致：只做单项原语，递归 / 临时文件 / 冲突在上层。
/// 所有 <c>System.IO</c> 调用都是阻塞的、不可取消，统一放线程池并在调用前后检查取消；
/// 长时间的读写用分块循环，每块检查取消，所以取消大文件传输是及时的。
/// </para>
/// </summary>
internal sealed class SmbFileSystem : IRemoteFileSystem
{
    private const int CopyBufferSize = 128 * 1024;

    private readonly SmbPathMapper _mapper;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _listShares;
    private readonly ISmbConnection? _connection;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();

    private int _disposed;

    public SmbFileSystem(
        SmbPathMapper mapper,
        Func<CancellationToken, Task<IReadOnlyList<string>>> listShares,
        ISmbConnection? connection,
        ILogger logger)
    {
        _mapper = mapper;
        _listShares = listShares;
        _connection = connection;
        _logger = logger;
    }

    public FileTransferChannel Channel => FileTransferChannel.Smb;

    public string InitialPath => RemotePath.Root;

    public Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken cancellationToken)
        => GuardAsync<IReadOnlyList<RemoteFileEntry>>(async ct =>
        {
            var normalized = RemotePath.Normalize(path);
            if (normalized == RemotePath.Root)
            {
                var shares = await _listShares(ct).ConfigureAwait(false);
                return shares
                    .Where(RemotePath.IsValidEntryName)
                    .Select(name => new RemoteFileEntry(name, RemotePath.Combine(RemotePath.Root, name), true, false, 0, null))
                    .ToList();
            }

            var physical = _mapper.ToPhysical(normalized);
            return await Task.Run<IReadOnlyList<RemoteFileEntry>>(() =>
            {
                var directory = new DirectoryInfo(physical);
                var result = new List<RemoteFileEntry>();
                foreach (var info in directory.EnumerateFileSystemInfos())
                {
                    ct.ThrowIfCancellationRequested();
                    if (!RemotePath.IsValidEntryName(info.Name))
                    {
                        continue; // 不可信输入：带分隔符的名字直接丢弃。
                    }

                    result.Add(ToEntry(normalized, info));
                }

                return result;
            }, ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task<RemoteFileEntry?> StatAsync(string path, CancellationToken cancellationToken)
        => GuardAsync<RemoteFileEntry?>(async ct =>
        {
            var normalized = RemotePath.Normalize(path);
            if (SmbPathMapper.Depth(normalized) <= 1)
            {
                // 根与共享根：不做网络访问，视为存在的目录（共享是否真实存在由后续列目录暴露）。
                return new RemoteFileEntry(
                    RemotePath.GetName(normalized), normalized, true, false, 0, null);
            }

            var physical = _mapper.ToPhysical(normalized);
            return await Task.Run<RemoteFileEntry?>(() =>
            {
                FileSystemInfo info = Directory.Exists(physical) ? new DirectoryInfo(physical) : new FileInfo(physical);
                return info.Exists ? ToEntry(RemotePath.GetParent(normalized), info) : null;
            }, ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task DownloadAsync(
        string remotePath, Stream destination, IProgress<long>? progress, CancellationToken cancellationToken)
        => GuardAsync(async ct =>
        {
            var physical = RequireInsideShare(remotePath);
            await using var source = new FileStream(
                physical, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await CopyAsync(source, destination, progress, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task UploadAsync(
        Stream source, string remotePath, IProgress<long>? progress, CancellationToken cancellationToken)
        => GuardAsync(async ct =>
        {
            var physical = RequireInsideShare(remotePath);
            await using var target = new FileStream(
                physical, FileMode.Create, FileAccess.Write, FileShare.None,
                CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await CopyAsync(source, target, progress, ct).ConfigureAwait(false);
            await target.FlushAsync(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        => GuardAsync(async ct =>
        {
            var physical = RequireInsideShare(path);
            await Task.Run(() =>
            {
                if (File.Exists(physical))
                {
                    throw new FileTransferException(FileTransferErrorCode.AlreadyExists, "已存在同名文件，无法创建文件夹。");
                }

                Directory.CreateDirectory(physical); // 已存在的目录：幂等。
            }, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task RenameAsync(string path, string newPath, bool overwrite, CancellationToken cancellationToken)
        => GuardAsync(async ct =>
        {
            var from = RequireInsideShare(path);
            var to = RequireInsideShare(newPath);
            await Task.Run(() =>
            {
                var isDirectory = Directory.Exists(from);
                var targetExists = File.Exists(to) || Directory.Exists(to);
                if (targetExists && !overwrite)
                {
                    throw new FileTransferException(FileTransferErrorCode.AlreadyExists, "目标位置已存在同名项目。");
                }

                if (isDirectory)
                {
                    if (targetExists)
                    {
                        // 目录无法被原子替换；上层的覆盖只用于文件（临时文件改名），目录同名一律不覆盖。
                        throw new FileTransferException(FileTransferErrorCode.AlreadyExists, "目标位置已存在同名项目。");
                    }

                    Directory.Move(from, to);
                }
                else
                {
                    // File.Move(overwrite:true) 在同卷内是原子替换：失败时目标保持原样，不会「先删后写」。
                    File.Move(from, to, overwrite);
                }
            }, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task DeleteAsync(string path, bool isDirectory, CancellationToken cancellationToken)
        => GuardAsync(async ct =>
        {
            var physical = RequireInsideShare(path);
            await Task.Run(() =>
            {
                if (isDirectory)
                {
                    Directory.Delete(physical, recursive: false);
                }
                else
                {
                    // 不擅自清除只读属性：只读是用户对文件的保护，交给上层报错让用户决定。
                    File.Delete(physical);
                }
            }, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // 已释放属预期。
        }

        try
        {
            // 撤销连接可能阻塞（网络已断时），放线程池并设上限，避免关闭窗口 / 退出应用时卡住。
            var release = Task.Run(() => _connection?.Dispose());
            await Task.WhenAny(release, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SMB 撤销连接时出现异常");
        }

        _lifetime.Dispose();
    }

    // ── 内部 ──────────────────────────────────────────────────────

    /// <summary>
    /// 解析并要求路径在某个共享<b>之内</b>（层数 ≥ 2）：不能在根下新建 / 删除 / 改名（那等于操作共享本身）。
    /// </summary>
    private string RequireInsideShare(string virtualPath)
    {
        if (SmbPathMapper.Depth(virtualPath) < 2)
        {
            throw new FileTransferException(
                FileTransferErrorCode.InvalidOperation, "请先进入某个共享（如 C$），共享列表本身不能修改。");
        }

        return _mapper.ToPhysical(virtualPath);
    }

    private static RemoteFileEntry ToEntry(string parentVirtual, FileSystemInfo info)
    {
        var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
        var isReparse = (info.Attributes & FileAttributes.ReparsePoint) != 0;
        var size = !isDirectory && info is FileInfo file ? file.Length : 0;

        DateTimeOffset? modified = null;
        try
        {
            var utc = info.LastWriteTimeUtc;
            if (utc.Year > 1601)
            {
                modified = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // 时间戳异常（部分共享会返回越界值）：留空即可。
        }

        return new RemoteFileEntry(
            info.Name, RemotePath.Combine(parentVirtual, info.Name), isDirectory, isReparse, size, modified);
    }

    /// <summary>分块拷贝：每块检查取消并上报累计字节数。</summary>
    private static async Task CopyAsync(Stream source, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        var buffer = new byte[CopyBufferSize];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            total += read;
            progress?.Report(total);
        }
    }

    private async Task<T> GuardAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            return await operation(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new FileTransferException(FileTransferErrorCode.ConnectionLost, "与服务器的连接已中断。", ex);
        }
        catch (FileTransferException)
        {
            throw;
        }
        catch (Exception ex) when (Translate(ex) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary><c>System.IO</c> 异常 → 面向用户的分类异常；不认识的返回 null 让异常照常冒泡。</summary>
    internal static FileTransferException? Translate(Exception ex)
    {
        switch (ex)
        {
            case UnauthorizedAccessException:
                return new(FileTransferErrorCode.PermissionDenied, "没有权限执行此操作（也可能是文件为只读）。", ex);
            case DirectoryNotFoundException or FileNotFoundException:
                return new(FileTransferErrorCode.NotFound, "路径不存在，可能已被删除或移动。", ex);
            case PathTooLongException:
                return new(FileTransferErrorCode.InvalidName, "路径过长。", ex);
            case IOException io:
                // 按 Win32 错误码（HResult 低 16 位）判断，不依赖系统语言的消息文本。
                return (io.HResult & 0xFFFF) switch
                {
                    0x27 or 0x70 => new(FileTransferErrorCode.DiskFull, "磁盘空间不足。", ex),
                    0x20 or 0x21 => new(FileTransferErrorCode.InvalidOperation, "文件正被其他程序占用。", ex),
                    0x50 or 0xB7 => new(FileTransferErrorCode.AlreadyExists, "目标位置已存在同名项目。", ex),
                    0x91 => new(FileTransferErrorCode.InvalidOperation, "目录不为空。", ex),
                    0x41 => new(FileTransferErrorCode.PermissionDenied, "没有权限访问该网络位置。", ex),
                    0x35 or 0x36 or 0x3B or 0x40 or 0x43
                        => new(FileTransferErrorCode.ConnectionLost, "与服务器的连接已中断。", ex),
                    _ => null
                };
            default:
                return null;
        }
    }
}
