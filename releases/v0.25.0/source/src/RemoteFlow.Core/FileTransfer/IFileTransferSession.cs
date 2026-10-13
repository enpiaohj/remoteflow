namespace RemoteFlow.Core.FileTransfer;

/// <summary>
/// 会话的文件传输能力（可选，由会话类型自行实现，视图模型通过类型探测使用）。
/// 之所以是独立的能力接口而不是 <c>IRemoteSession</c> 的成员：只有 SSH 会话有这个能力，
/// 给公共接口加成员会牵动所有协议与 macOS 端。
/// </summary>
public interface IFileTransferSession
{
    /// <summary>
    /// 在<b>已连接</b>的会话上打开文件系统，复用会话已有的认证与主机密钥信任，不再询问凭据。
    /// 返回的对象归调用方释放；会话关闭时也会一并释放其打开过的文件系统。
    /// 会话未连接时抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    Task<IRemoteFileSystem> OpenFileSystemAsync(CancellationToken cancellationToken);
}
