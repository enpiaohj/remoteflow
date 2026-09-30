using RemoteFlow.Core.Models;

namespace RemoteFlow.Presentation.Services;

/// <summary>
/// 「不建立会话直接传文件」的入口：页面（连接工作台 / 首页 / 详情面板）只表达意图，
/// 由主窗口把它呈现为工作区里的一个文件传输 Tab（与 SSH / RDP 会话 Tab 同处一条 Tab 条）。
/// 可选依赖——没有订阅者的平台（macOS 本版未做界面）传 null，入口命令随之不可用。
/// </summary>
public interface IFileTransferLauncher
{
    /// <summary>请求打开某连接的文件传输 Tab；已存在则由订阅方聚焦，不重复连接。</summary>
    event EventHandler<ConnectionProfile>? OpenRequested;

    void Open(ConnectionProfile profile);
}

/// <summary>默认实现：仅转发意图，不含任何界面逻辑。</summary>
public sealed class FileTransferLauncher : IFileTransferLauncher
{
    public event EventHandler<ConnectionProfile>? OpenRequested;

    public void Open(ConnectionProfile profile) => OpenRequested?.Invoke(this, profile);
}
