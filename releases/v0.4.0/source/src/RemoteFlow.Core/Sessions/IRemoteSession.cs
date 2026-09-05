using RemoteFlow.Core.Models;

namespace RemoteFlow.Core.Sessions;

/// <summary>
/// 单个远程会话实例。每个会话 Tab 对应一个 <see cref="IRemoteSession"/>。
/// <para>
/// 架构约束：会话只负责协议生命周期，不负责资产管理，也不感知 UI 布局。
/// 具体的画面/终端渲染由各协议项目提供的 WPF 宿主控件承担。
/// </para>
/// </summary>
public interface IRemoteSession : IAsyncDisposable
{
    Guid SessionId { get; }

    ProtocolType Protocol { get; }

    /// <summary>会话对应的连接配置快照。</summary>
    ConnectionProfile Profile { get; }

    ConnectionState State { get; }

    /// <summary>会话失败时的标准化错误码。</summary>
    ConnectionErrorCode ErrorCode { get; }

    /// <summary>面向用户的中文错误说明。不含任何 Secret。</summary>
    string? ErrorMessage { get; }

    event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 建立连接。整个过程必须可取消，且不得阻塞 UI 主线程。
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>主动断开会话。实现必须保证可重复调用而不抛异常。</summary>
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
