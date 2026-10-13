using RemoteFlow.Core.Models;

namespace RemoteFlow.Core.FileTransfer;

/// <summary>
/// 远端文件系统的最小原语集合。传输队列、冲突处理、临时文件与原子改名、递归展开都在 Application 层的
/// <c>FileTransferService</c> 里，由这些原语组合而成——各通道（SFTP / SMB）只需实现单项操作，
/// 也因此可以用内存假实现完整测试上层逻辑。
/// <para>
/// 所有路径都是 <see cref="RemotePath"/> 规范化后的虚拟路径。实现必须在内部再次校验路径不越界
/// （例如 SMB 不得借 <c>..</c> 逃出所选共享），不能只依赖调用方。
/// </para>
/// </summary>
public interface IRemoteFileSystem : IAsyncDisposable
{
    /// <summary>本文件系统使用的通道，供界面显示与提示。</summary>
    FileTransferChannel Channel { get; }

    /// <summary>打开时默认显示的目录（SFTP 为登录后的家目录，SMB 为根 <c>/</c>，即共享列表）。</summary>
    string InitialPath { get; }

    /// <summary>列出目录内容（不含 <c>.</c> / <c>..</c>）。不保证顺序，由调用方排序。</summary>
    Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken cancellationToken);

    /// <summary>取单项信息；不存在返回 null。用于冲突检测。</summary>
    Task<RemoteFileEntry?> StatAsync(string path, CancellationToken cancellationToken);

    /// <summary>把远端文件读入 <paramref name="destination"/>。进度按已传字节数累计上报。</summary>
    Task DownloadAsync(
        string remotePath, Stream destination, IProgress<long>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// 把 <paramref name="source"/> 写入远端 <paramref name="remotePath"/>（已存在则覆盖）。
    /// 调用方约定先写临时名再 <see cref="RenameAsync"/>，所以实现不需要自己做原子替换。
    /// </summary>
    Task UploadAsync(
        Stream source, string remotePath, IProgress<long>? progress, CancellationToken cancellationToken);

    /// <summary>新建目录（父目录须已存在）。目录已存在时不报错。</summary>
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// 改名 / 移动。<paramref name="overwrite"/> 为 true 时目标已存在则替换（尽量原子），
    /// 为 false 时目标已存在应抛 <see cref="FileTransferException"/>（<see cref="FileTransferErrorCode.AlreadyExists"/>）。
    /// </summary>
    Task RenameAsync(string path, string newPath, bool overwrite, CancellationToken cancellationToken);

    /// <summary>删除单个文件或<b>空</b>目录。递归删除由调用方逐项展开，避免实现里藏一个不受控的递归。</summary>
    Task DeleteAsync(string path, bool isDirectory, CancellationToken cancellationToken);
}
