namespace RemoteFlow.Core.FileTransfer;

/// <summary>
/// 远端文件系统里的一项（文件、目录或共享）。<see cref="FullPath"/> 是统一的 POSIX 风格虚拟路径
/// （以 <c>/</c> 分隔、根为 <c>/</c>），与具体通道无关——SFTP 直接对应远端路径，
/// SMB 则把 <c>/C$/Users</c> 映射为 <c>\\主机\C$\Users</c>。
/// </summary>
/// <param name="Name">不含路径的名称。</param>
/// <param name="FullPath">规范化后的完整虚拟路径。</param>
/// <param name="IsDirectory">是目录（SMB 根下的共享也算目录）。</param>
/// <param name="IsSymlink">是符号链接 / 重解析点。递归传输不会跟随它。</param>
/// <param name="Size">文件字节数；目录为 0。</param>
/// <param name="Modified">最后修改时间；取不到为 null。</param>
/// <param name="Permissions">权限文本（如 <c>rwxr-xr-x</c>）；通道不提供则为 null。</param>
public sealed record RemoteFileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    bool IsSymlink,
    long Size,
    DateTimeOffset? Modified,
    string? Permissions = null);
