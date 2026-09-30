using RemoteFlow.Core.FileTransfer;
using RemoteFlow.Core.Models;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 内存版远端文件系统，用来完整测试传输队列 / 视图模型逻辑。
/// 刻意模拟真实通道的关键行为：上传一开始就在目标路径产生（半截）节点，
/// 所以「取消 / 失败后临时文件被清理」这类断言是有意义的；改名不覆盖时目标存在会报已存在。
/// 并提供故障注入与传输中钩子，便于构造取消 / 失败 / 并发场景。
/// </summary>
internal sealed class FakeRemoteFileSystem : IRemoteFileSystem
{
    private const int ChunkSize = 16 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal)
    {
        [RemotePath.Root] = new Node { IsDirectory = true }
    };

    public FileTransferChannel Channel { get; init; } = FileTransferChannel.Sftp;

    public string InitialPath { get; init; } = "/home/user";

    /// <summary>每传输一块数据回调一次（参数：累计字节数）。可在其中取消 / 阻塞以构造场景。</summary>
    public Func<string, long, CancellationToken, Task>? OnChunk { get; set; }

    /// <summary>上传到某路径前调用；返回异常则抛出（模拟权限不足等）。</summary>
    public Func<string, Exception?>? FailUpload { get; set; }

    public Func<string, Exception?>? FailDownload { get; set; }

    public Func<string, Exception?>? FailList { get; set; }

    public Func<string, Exception?>? FailDelete { get; set; }

    public List<string> Calls { get; } = [];

    public List<(string From, string To, bool Overwrite)> Renames { get; } = [];

    public bool Disposed { get; private set; }

    /// <summary>同一时刻正在进行的上传 / 下载数的历史最大值，用于验证并发上限。</summary>
    public int MaxActiveTransfers { get; private set; }

    private int _activeTransfers;

    // ── 测试用的数据构造与读取 ──

    public FakeRemoteFileSystem AddDirectory(string path)
    {
        lock (_gate)
        {
            var normalized = RemotePath.Normalize(path);
            EnsureParents(normalized);
            _nodes[normalized] = new Node { IsDirectory = true };
        }

        return this;
    }

    public FakeRemoteFileSystem AddFile(string path, byte[] data, DateTimeOffset? modified = null)
    {
        lock (_gate)
        {
            var normalized = RemotePath.Normalize(path);
            EnsureParents(normalized);
            _nodes[normalized] = new Node { Data = data, Modified = modified ?? DateTimeOffset.UtcNow };
        }

        return this;
    }

    public FakeRemoteFileSystem AddSymlink(string path, bool pointsToDirectory)
    {
        lock (_gate)
        {
            var normalized = RemotePath.Normalize(path);
            EnsureParents(normalized);
            _nodes[normalized] = new Node { IsSymlink = true, IsDirectory = pointsToDirectory };
        }

        return this;
    }

    /// <summary>绕过名称校验直接放入一个条目，模拟恶意服务器返回的畸形名字。</summary>
    public FakeRemoteFileSystem AddRawEntry(string parent, string rawName, byte[] data)
    {
        lock (_gate)
        {
            _rawEntries.Add((RemotePath.Normalize(parent), rawName, data));
        }

        return this;
    }

    private readonly List<(string Parent, string Name, byte[] Data)> _rawEntries = [];

    public bool Exists(string path)
    {
        lock (_gate)
        {
            return _nodes.ContainsKey(RemotePath.Normalize(path));
        }
    }

    public byte[]? ReadFile(string path)
    {
        lock (_gate)
        {
            return _nodes.TryGetValue(RemotePath.Normalize(path), out var node) && !node.IsDirectory ? node.Data : null;
        }
    }

    public IReadOnlyList<string> AllPaths()
    {
        lock (_gate)
        {
            return [.. _nodes.Keys.Where(p => p != RemotePath.Root).Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>残留的临时文件（<c>.rfpart</c>）。</summary>
    public IReadOnlyList<string> TempFiles() => [.. AllPaths().Where(p => p.EndsWith(".rfpart", StringComparison.Ordinal))];

    // ── IRemoteFileSystem ──

    public Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = RemotePath.Normalize(path);
        Record($"list {normalized}");
        if (FailList?.Invoke(normalized) is { } failure)
        {
            throw failure;
        }

        lock (_gate)
        {
            if (!_nodes.TryGetValue(normalized, out var self) || !self.IsDirectory)
            {
                throw new FileTransferException(FileTransferErrorCode.NotFound, "路径不存在。");
            }

            var result = new List<RemoteFileEntry>();
            foreach (var (childPath, node) in _nodes)
            {
                if (childPath == RemotePath.Root || RemotePath.GetParent(childPath) != normalized)
                {
                    continue;
                }

                result.Add(ToEntry(childPath, node));
            }

            foreach (var (parent, name, data) in _rawEntries.Where(r => r.Parent == normalized))
            {
                result.Add(new RemoteFileEntry(name, normalized.TrimEnd('/') + "/" + name, false, false, data.Length, DateTimeOffset.UtcNow));
            }

            return Task.FromResult<IReadOnlyList<RemoteFileEntry>>(result);
        }
    }

    public Task<RemoteFileEntry?> StatAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = RemotePath.Normalize(path);
        lock (_gate)
        {
            return Task.FromResult(_nodes.TryGetValue(normalized, out var node) ? ToEntry(normalized, node) : null);
        }
    }

    public async Task DownloadAsync(
        string remotePath, Stream destination, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var normalized = RemotePath.Normalize(remotePath);
        Record($"download {normalized}");
        if (FailDownload?.Invoke(normalized) is { } failure)
        {
            throw failure;
        }

        EnterTransfer();
        try
        {
        byte[] data;
        lock (_gate)
        {
            if (_nodes.TryGetValue(normalized, out var node) && !node.IsDirectory)
            {
                data = node.Data;
            }
            else if (_rawEntries.FirstOrDefault(r => normalized.EndsWith("/" + r.Name, StringComparison.Ordinal)) is { Name: not null } raw)
            {
                data = raw.Data;
            }
            else
            {
                throw new FileTransferException(FileTransferErrorCode.NotFound, "文件不存在。");
            }
        }

        long total = 0;
        for (var offset = 0; offset < data.Length; offset += ChunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(ChunkSize, data.Length - offset);
            await destination.WriteAsync(data.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            total += count;
            progress?.Report(total);
            if (OnChunk is not null)
            {
                await OnChunk(normalized, total, cancellationToken).ConfigureAwait(false);
            }
        }
        }
        finally
        {
            ExitTransfer();
        }
    }

    public async Task UploadAsync(
        Stream source, string remotePath, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var normalized = RemotePath.Normalize(remotePath);
        Record($"upload {normalized}");
        if (FailUpload?.Invoke(normalized) is { } failure)
        {
            throw failure;
        }

        EnterTransfer();
        try
        {
        lock (_gate)
        {
            var parent = RemotePath.GetParent(normalized);
            if (!_nodes.TryGetValue(parent, out var parentNode) || !parentNode.IsDirectory)
            {
                throw new FileTransferException(FileTransferErrorCode.NotFound, "上级目录不存在。");
            }

            // 真实通道一开始就会在目标路径创建文件：先放一个空节点，之后随传输增长。
            _nodes[normalized] = new Node { Data = [], Modified = DateTimeOffset.UtcNow };
        }

        var buffer = new byte[ChunkSize];
        using var received = new MemoryStream();
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            received.Write(buffer, 0, read);
            total += read;
            lock (_gate)
            {
                _nodes[normalized] = new Node { Data = received.ToArray(), Modified = DateTimeOffset.UtcNow };
            }

            progress?.Report(total);
            if (OnChunk is not null)
            {
                await OnChunk(normalized, total, cancellationToken).ConfigureAwait(false);
            }
        }
        }
        finally
        {
            ExitTransfer();
        }
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = RemotePath.Normalize(path);
        Record($"mkdir {normalized}");
        lock (_gate)
        {
            if (_nodes.TryGetValue(normalized, out var existing))
            {
                if (existing.IsDirectory)
                {
                    return Task.CompletedTask;
                }

                throw new FileTransferException(FileTransferErrorCode.AlreadyExists, "已存在同名文件。");
            }

            if (!_nodes.TryGetValue(RemotePath.GetParent(normalized), out var parent) || !parent.IsDirectory)
            {
                throw new FileTransferException(FileTransferErrorCode.NotFound, "上级目录不存在。");
            }

            _nodes[normalized] = new Node { IsDirectory = true };
        }

        return Task.CompletedTask;
    }

    public Task RenameAsync(string path, string newPath, bool overwrite, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var from = RemotePath.Normalize(path);
        var to = RemotePath.Normalize(newPath);
        Record($"rename {from} -> {to}");
        lock (_gate)
        {
            Renames.Add((from, to, overwrite));
            if (!_nodes.TryGetValue(from, out var node))
            {
                throw new FileTransferException(FileTransferErrorCode.NotFound, "路径不存在。");
            }

            if (_nodes.ContainsKey(to) && !overwrite)
            {
                throw new FileTransferException(FileTransferErrorCode.AlreadyExists, "目标已存在。");
            }

            _nodes.Remove(from);
            _nodes[to] = node;
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, bool isDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = RemotePath.Normalize(path);
        Record($"delete {normalized}");
        if (FailDelete?.Invoke(normalized) is { } failure)
        {
            throw failure;
        }

        lock (_gate)
        {
            if (!_nodes.TryGetValue(normalized, out var node))
            {
                throw new FileTransferException(FileTransferErrorCode.NotFound, "路径不存在。");
            }

            if (node.IsDirectory && !node.IsSymlink && _nodes.Keys.Any(k => k != normalized && RemotePath.GetParent(k) == normalized))
            {
                throw new FileTransferException(FileTransferErrorCode.InvalidOperation, "目录不为空。");
            }

            _nodes.Remove(normalized);
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    // ── 内部 ──

    private void EnterTransfer()
    {
        lock (_gate)
        {
            _activeTransfers++;
            MaxActiveTransfers = Math.Max(MaxActiveTransfers, _activeTransfers);
        }
    }

    private void ExitTransfer()
    {
        lock (_gate)
        {
            _activeTransfers--;
        }
    }

    private void Record(string call)
    {
        lock (_gate)
        {
            Calls.Add(call);
        }
    }

    private void EnsureParents(string normalized)
    {
        var parent = RemotePath.GetParent(normalized);
        if (parent != normalized && !_nodes.ContainsKey(parent))
        {
            EnsureParents(parent);
            _nodes[parent] = new Node { IsDirectory = true };
        }
    }

    private static RemoteFileEntry ToEntry(string path, Node node)
        => new(RemotePath.GetName(path), path, node.IsDirectory, node.IsSymlink,
            node.IsDirectory ? 0 : node.Data.Length, node.Modified);

    private sealed class Node
    {
        public bool IsDirectory { get; init; }

        public bool IsSymlink { get; init; }

        public byte[] Data { get; init; } = [];

        public DateTimeOffset? Modified { get; init; }
    }
}
