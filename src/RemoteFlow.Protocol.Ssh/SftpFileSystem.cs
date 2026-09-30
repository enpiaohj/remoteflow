using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using RemoteFlow.Core.FileTransfer;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Protocol.Ssh;

/// <summary>
/// 基于 SSH.NET <see cref="SftpClient"/> 的远端文件系统。构造时客户端必须<b>已连接</b>；
/// 本对象拥有客户端，释放时先取消在途操作再断开。
/// <para>
/// 只实现单项原语（见 <see cref="IRemoteFileSystem"/>）；临时文件、原子改名、递归展开、冲突处理都在上层。
/// 所有 SSH.NET 异常统一映射为 <see cref="FileTransferException"/>（面向用户的中文消息，不含路径与内容）。
/// </para>
/// </summary>
public sealed class SftpFileSystem : IRemoteFileSystem
{
    private readonly SftpClient _client;
    private readonly ILogger _logger;

    /// <summary>释放时取消所有在途操作，避免对已断开的连接继续读写而卡住。</summary>
    private readonly CancellationTokenSource _lifetime = new();

    private int _disposed;

    public SftpFileSystem(SftpClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
        InitialPath = InitialDirectory(client);
    }

    public FileTransferChannel Channel => FileTransferChannel.Sftp;

    public string InitialPath { get; }

    /// <summary>底层连接仍可用（未释放且未断开）。会话缓存据此判断是否需要重建。</summary>
    public bool IsUsable => Volatile.Read(ref _disposed) == 0 && _client.IsConnected;

    public Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken cancellationToken)
        => GuardAsync(async ct =>
        {
            var directory = RemotePath.Normalize(path);
            var entries = new List<RemoteFileEntry>();

            await foreach (var file in _client.ListDirectoryAsync(directory, ct).ConfigureAwait(false))
            {
                // 「.」「..」不是真实条目；服务端返回带分隔符的名字属于异常数据，直接丢弃（不可信输入）。
                if (!RemotePath.IsValidEntryName(file.Name))
                {
                    _logger.LogDebug("SFTP 列目录忽略非法条目名");
                    continue;
                }

                entries.Add(await ToEntryAsync(directory, file, ct).ConfigureAwait(false));
            }

            return (IReadOnlyList<RemoteFileEntry>)entries;
        }, cancellationToken);

    public Task<RemoteFileEntry?> StatAsync(string path, CancellationToken cancellationToken)
        => GuardAsync(ct => StatCoreAsync(RemotePath.Normalize(path), ct), cancellationToken);

    public Task DownloadAsync(
        string remotePath, Stream destination, IProgress<long>? progress, CancellationToken cancellationToken)
        => GuardAsync(async ct =>
        {
            await _client.DownloadFileAsync(
                RemotePath.Normalize(remotePath),
                destination,
                new InlineProgress<DownloadFileProgressReport>(r => progress?.Report((long)r.TotalBytesDownloaded)),
                ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task UploadAsync(
        Stream source, string remotePath, IProgress<long>? progress, CancellationToken cancellationToken)
        => GuardAsync(async ct =>
        {
            await _client.UploadFileAsync(
                source,
                RemotePath.Normalize(remotePath),
                canOverride: true,
                new InlineProgress<UploadFileProgressReport>(r => progress?.Report((long)r.TotalBytesUploaded)),
                ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        => GuardAsync(async ct =>
        {
            var normalized = RemotePath.Normalize(path);
            var existing = await StatCoreAsync(normalized, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.IsDirectory)
                {
                    return true; // 已存在的目录：幂等，不报错。
                }

                throw new FileTransferException(FileTransferErrorCode.AlreadyExists, "已存在同名文件，无法创建文件夹。");
            }

            await _client.CreateDirectoryAsync(normalized, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task RenameAsync(string path, string newPath, bool overwrite, CancellationToken cancellationToken)
        => GuardAsync(async ct =>
        {
            var from = RemotePath.Normalize(path);
            var to = RemotePath.Normalize(newPath);

            if (!overwrite)
            {
                if (await StatCoreAsync(to, ct).ConfigureAwait(false) is not null)
                {
                    throw new FileTransferException(FileTransferErrorCode.AlreadyExists, "目标位置已存在同名项目。");
                }

                await Task.Run(() => _client.RenameFile(from, to, isPosix: false), ct).ConfigureAwait(false);
                return true;
            }

            await ReplaceAsync(from, to, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task DeleteAsync(string path, bool isDirectory, CancellationToken cancellationToken)
        => GuardAsync(async ct =>
        {
            var normalized = RemotePath.Normalize(path);
            if (normalized == RemotePath.Root)
            {
                throw new FileTransferException(FileTransferErrorCode.InvalidOperation, "不能删除根目录。");
            }

            if (isDirectory)
            {
                await _client.DeleteDirectoryAsync(normalized, ct).ConfigureAwait(false);
            }
            else
            {
                await _client.DeleteFileAsync(normalized, ct).ConfigureAwait(false);
            }

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

        // Disconnect 是同步阻塞调用；连接已异常时可能拖延，放到线程池并设上限，
        // 避免关闭 Tab / 窗口时卡住 UI（与会话 5 秒关闭预算配合）。
        var disconnect = Task.Run(() =>
        {
            try
            {
                if (_client.IsConnected)
                {
                    _client.Disconnect();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SFTP 断开连接时出现异常");
            }
            finally
            {
                _client.Dispose();
            }
        });

        await Task.WhenAny(disconnect, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
        _lifetime.Dispose();
    }

    // ── 内部 ──────────────────────────────────────────────────────

    /// <summary>
    /// 覆盖式改名：优先用 <c>posix-rename@openssh.com</c>（原子替换，失败时目标原样保留）；
    /// 服务端不支持时退回「目标先改名为备份 → 改名 → 删备份」，任何一步失败都还原备份——
    /// 绝不「先删目标再写」，避免覆盖失败时把原文件丢了。
    /// </summary>
    private async Task ReplaceAsync(string from, string to, CancellationToken ct)
    {
        if (await StatCoreAsync(to, ct).ConfigureAwait(false) is null)
        {
            await Task.Run(() => _client.RenameFile(from, to, isPosix: false), ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await Task.Run(() => _client.RenameFile(from, to, isPosix: true), ct).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is NotSupportedException or SftpException and not SftpPermissionDeniedException)
        {
            _logger.LogDebug(ex, "SFTP 服务端不支持 posix-rename，退回备份替换");
        }

        var backup = to + ".rfbak-" + Guid.NewGuid().ToString("N")[..8];
        await Task.Run(() => _client.RenameFile(to, backup, isPosix: false), ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() => _client.RenameFile(from, to, isPosix: false), ct).ConfigureAwait(false);
        }
        catch
        {
            // 还原备份，保证原文件不丢；还原本身失败只能记录，异常继续向上抛让调用方知道覆盖没成功。
            try
            {
                await Task.Run(() => _client.RenameFile(backup, to, isPosix: false), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception restoreEx)
            {
                _logger.LogError(restoreEx, "SFTP 覆盖失败后还原备份也失败，原文件保留在备份名下");
            }

            throw;
        }

        try
        {
            await _client.DeleteFileAsync(backup, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 覆盖已成功，只是备份没删掉：记录即可，不让整个操作失败。
            _logger.LogWarning(ex, "SFTP 覆盖成功但删除备份失败");
        }
    }

    private async Task<RemoteFileEntry?> StatCoreAsync(string normalizedPath, CancellationToken ct)
    {
        if (normalizedPath == RemotePath.Root)
        {
            return new RemoteFileEntry(string.Empty, RemotePath.Root, true, false, 0, null);
        }

        try
        {
            var file = await _client.GetAsync(normalizedPath, ct).ConfigureAwait(false);
            return await ToEntryAsync(RemotePath.GetParent(normalizedPath), file, ct).ConfigureAwait(false);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
    }

    private async Task<RemoteFileEntry> ToEntryAsync(string parent, ISftpFile file, CancellationToken ct)
    {
        var fullPath = RemotePath.Combine(parent, file.Name);
        var isDirectory = file.IsDirectory;

        // 符号链接本身的类型是「链接」；要知道指向文件还是目录，得 stat 目标（stat 会跟随链接）。
        // 悬空链接（目标不存在）按文件处理。
        if (file.IsSymbolicLink)
        {
            try
            {
                var target = await _client.GetAttributesAsync(fullPath, ct).ConfigureAwait(false);
                isDirectory = target.IsDirectory;
            }
            catch (Exception ex) when (ex is SftpException or SshException)
            {
                isDirectory = false;
            }
        }

        DateTimeOffset? modified = file.LastWriteTimeUtc is { Year: > 1 } utc
            ? new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc))
            : null;

        return new RemoteFileEntry(
            file.Name,
            fullPath,
            isDirectory,
            file.IsSymbolicLink,
            isDirectory ? 0 : file.Length,
            modified,
            FormatPermissions(file));
    }

    private static string FormatPermissions(ISftpFile file)
    {
        var text = new StringBuilder(9)
            .Append(file.OwnerCanRead ? 'r' : '-')
            .Append(file.OwnerCanWrite ? 'w' : '-')
            .Append(file.OwnerCanExecute ? 'x' : '-')
            .Append(file.GroupCanRead ? 'r' : '-')
            .Append(file.GroupCanWrite ? 'w' : '-')
            .Append(file.GroupCanExecute ? 'x' : '-')
            .Append(file.OthersCanRead ? 'r' : '-')
            .Append(file.OthersCanWrite ? 'w' : '-')
            .Append(file.OthersCanExecute ? 'x' : '-');
        return text.ToString();
    }

    private static string InitialDirectory(SftpClient client)
    {
        try
        {
            return RemotePath.Normalize(client.WorkingDirectory);
        }
        catch (Exception)
        {
            return RemotePath.Root;
        }
    }

    /// <summary>
    /// 统一入口：联动释放取消、检查已释放、把 SSH.NET 异常翻译为 <see cref="FileTransferException"/>。
    /// 调用方自己的取消（<see cref="OperationCanceledException"/>）原样抛出，不当作失败。
    /// </summary>
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
            // 不是调用方取消的：连接被释放导致，表现为连接中断。
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

    /// <summary>SSH.NET / Socket / IO 异常 → 面向用户的分类异常；不认识的返回 null 让异常照常冒泡。</summary>
    internal static FileTransferException? Translate(Exception ex) => ex switch
    {
        SftpPermissionDeniedException
            => new(FileTransferErrorCode.PermissionDenied, "没有权限执行此操作。", ex),
        SftpPathNotFoundException
            => new(FileTransferErrorCode.NotFound, "路径不存在，可能已被删除或移动。", ex),
        SshConnectionException or SshOperationTimeoutException or SocketException or ObjectDisposedException
            => new(FileTransferErrorCode.ConnectionLost, "与服务器的连接已中断。", ex),
        SftpException { StatusCode: StatusCode.NoConnection or StatusCode.ConnectionLost }
            => new(FileTransferErrorCode.ConnectionLost, "与服务器的连接已中断。", ex),
        SftpException { StatusCode: StatusCode.OperationUnsupported }
            => new(FileTransferErrorCode.NotSupported, "服务器不支持该操作。", ex),
        SftpException sftp when LooksLikeDiskFull(sftp.Message)
            => new(FileTransferErrorCode.DiskFull, "远端磁盘空间不足或已超出配额。", ex),
        SftpException
            => new(FileTransferErrorCode.Unknown, "服务器拒绝了该操作。", ex),
        IOException io when IsLocalDiskFull(io)
            => new(FileTransferErrorCode.DiskFull, "本机磁盘空间不足。", ex),
        _ => null
    };

    private static bool IsLocalDiskFull(IOException io)
        => (io.HResult & 0xFFFF) is 0x27 or 0x70 || LooksLikeDiskFull(io.Message);

    private static bool LooksLikeDiskFull(string? message)
        => message is not null
           && (message.Contains("No space", StringComparison.OrdinalIgnoreCase)
               || message.Contains("quota", StringComparison.OrdinalIgnoreCase)
               || message.Contains("disk full", StringComparison.OrdinalIgnoreCase)
               || message.Contains("not enough space", StringComparison.OrdinalIgnoreCase));

    /// <summary>同步回调的 <see cref="IProgress{T}"/>：不像 <c>Progress&lt;T&gt;</c> 那样捕获上下文并异步投递。</summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
