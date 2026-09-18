using RemoteFlow.Core.Models;

namespace RemoteFlow.Core.Sessions;

/// <summary>
/// 单个远程会话实例。每个会话 Tab 对应一个 <see cref="IRemoteSession"/>。
/// <para>
/// 架构约束：会话只负责协议生命周期，不负责资产管理，也不感知 UI 布局。
/// 具体的画面/终端渲染由各协议项目提供的 WPF 宿主控件承担。
/// </para>
/// <para>
/// 生命周期语义（单次使用）：Idle → Connecting → Connected（自动重连期间进入 Reconnecting）
/// → Disconnecting → Disconnected → Closed（终态）。任意连接阶段失败进入 Failed。
/// 各出口（正常关 / 失败 / 取消 / 远端断 / 重连前 / App 退出）最终经同一套幂等清理收敛到
/// <see cref="ConnectionState.Closed"/>。
/// </para>
/// </summary>
public interface IRemoteSession : IAsyncDisposable
{
    /// <summary>会话唯一 Id。一次连接一个会话，重连 = 建新会话。</summary>
    Guid SessionId { get; }

    /// <summary>会话对应的协议类型。</summary>
    ProtocolType Protocol { get; }

    /// <summary>会话对应的连接配置快照。</summary>
    ConnectionProfile Profile { get; }

    /// <summary>当前连接状态（线程安全读取）。</summary>
    ConnectionState State { get; }

    /// <summary>会话失败 / 关闭时的标准化错误码。</summary>
    ConnectionErrorCode ErrorCode { get; }

    /// <summary>面向用户的中文错误说明。不含任何 Secret。</summary>
    string? ErrorMessage { get; }

    /// <summary>状态变化事件。仅当状态真实变化时触发，参数携带 old/new 与当前错误码 / 错误说明。</summary>
    event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 会话是否已进入关闭 / 收尾流程（SessionManager 防重复关闭）。
    /// <para>默认返回 false：三个尚未迁移到 <c>RemoteSessionBase</c> 的协议 Session 走该默认实现；
    /// 迁移到基类后由基类的状态守卫提供真实值。</para>
    /// </summary>
    bool IsClosing => false;

    /// <summary>
    /// 收尾：由 SessionManager 在从活动集合移除后调用，将会话置 <see cref="ConnectionState.Closed"/>（幂等）。
    /// <para>默认空实现：三个尚未迁移到 <c>RemoteSessionBase</c> 的协议 Session 走该默认实现；
    /// 迁移到基类后由基类覆盖为真实的终态收尾。</para>
    /// </summary>
    void MarkClosed() { }

    /// <summary>
    /// 建立连接。整个过程必须可取消，且不得阻塞 UI 主线程。
    /// <para>一次 Connect 后可进入 Connected / Reconnecting / Failed / Disconnected；
    /// 不允许在已 Closed / Disposed 的会话上再次 Connect。</para>
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 主动断开会话（幂等关闭模板）。必须可重复调用而不抛、不重复释放；
    /// 执行协议 teardown 后结束于 <see cref="ConnectionState.Disconnected"/>，
    /// 最终 Closed 由 SessionManager 移除后调用 MarkClosed() 完成。
    /// </summary>
    Task DisconnectAsync();
}

public sealed class SessionStateChangedEventArgs(
    ConnectionState oldState,
    ConnectionState newState,
    ConnectionErrorCode errorCode = ConnectionErrorCode.None,
    string? errorMessage = null) : EventArgs
{
    public ConnectionState OldState { get; } = oldState;
    public ConnectionState NewState { get; } = newState;
    public ConnectionErrorCode ErrorCode { get; } = errorCode;
    public string? ErrorMessage { get; } = errorMessage;
}
