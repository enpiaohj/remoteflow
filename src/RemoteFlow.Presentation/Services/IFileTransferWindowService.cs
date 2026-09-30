using RemoteFlow.Core.Models;

namespace RemoteFlow.Presentation.Services;

/// <summary>
/// 独立文件传输窗口的宿主服务：不建立终端 / RDP 会话，直接对某台主机打开文件传输。
/// 可选依赖——没有实现的平台（macOS 本版未做界面）传 null，入口命令随之不可用。
/// </summary>
public interface IFileTransferWindowService
{
    /// <summary>打开该连接的文件传输窗口；已存在则激活，不重复连接。</summary>
    void Open(ConnectionProfile profile);

    /// <summary>是否有窗口存在进行中的传输。主窗口退出前据此确认。</summary>
    bool HasActiveTransfers { get; }

    /// <summary>关闭全部窗口并有序释放连接（退出应用时调用）。</summary>
    Task CloseAllAsync();
}
