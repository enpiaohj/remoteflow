using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.Core.FileTransfer;

/// <summary>
/// 打开文件系统所需的一切。<see cref="Credential"/> 由调用方（<c>FileTransferConnector</c>）解析并在
/// <see cref="IRemoteFileSystemFactory.OpenAsync"/> 返回后立即释放——工厂不得保留对它的引用，
/// 需要长期保留认证信息的通道（如 SFTP 的 <c>ConnectionInfo</c>）必须自行持有必要的副本。
/// </summary>
/// <param name="Profile">目标连接。</param>
/// <param name="Credential">已解析的凭据。</param>
/// <param name="HostKeyPolicy">SSH 主机密钥策略；SMB 通道不用。</param>
public sealed record FileSystemOpenRequest(
    ConnectionProfile Profile,
    ResolvedCredential Credential,
    ISshHostKeyPolicy? HostKeyPolicy);

/// <summary>
/// 某个传输通道的文件系统工厂，用于「不建立会话」的独立文件传输。
/// 会话内的 SFTP 侧栏则由 <see cref="IFileTransferSession"/> 直接复用会话认证。
/// </summary>
public interface IRemoteFileSystemFactory
{
    FileTransferChannel Channel { get; }

    /// <summary>当前环境是否可用。不可用时 <paramref name="unavailableReason"/> 给出面向用户的中文原因。</summary>
    bool IsAvailable(out string? unavailableReason);

    /// <summary>
    /// 连接并返回文件系统。失败抛 <see cref="ConnectionException"/>（认证 / 网络 / 主机密钥等连接阶段错误）。
    /// </summary>
    Task<IRemoteFileSystem> OpenAsync(FileSystemOpenRequest request, CancellationToken cancellationToken);
}
