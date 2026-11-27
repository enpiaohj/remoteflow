using RemoteFlow.Core.Models;

namespace RemoteFlow.Core.Sessions;

/// <summary>
/// 会话种类。同一台主机除了按其协议建立的默认会话外，还可以开出别的会话
/// （如 Windows 主机的 PowerShell 远程会话）。它只存在于运行期：不写入连接数据，也不参与云同步。
/// </summary>
public enum SessionKind
{
    /// <summary>按连接配置的协议建立（RDP / SSH / VNC）。</summary>
    Default = 0,

    /// <summary>PowerShell 远程会话（WinRM）。目前仅 Windows 版提供。</summary>
    PowerShell
}

/// <summary>
/// 以字节流与终端界面对接的会话。终端视图只依赖本接口，不关心底下是 SSH 还是 PowerShell。
/// 数据一律用原始字节：多字节字符被分包切断时由终端侧解码器拼接。
/// </summary>
public interface ITerminalSession : IRemoteSession
{
    /// <summary>远端输出。可能在后台线程触发。</summary>
    event EventHandler<byte[]>? DataReceived;

    void SendInput(byte[] data);

    void SendInput(string text);

    /// <summary>终端尺寸变化。可能在连接建立之前调用，实现应记住尺寸并在连接时使用。</summary>
    void Resize(int columns, int rows, int pixelWidth, int pixelHeight);
}

/// <summary>连接过程中需要告诉用户在发生什么（例如即将弹出系统确认框）的会话。</summary>
public interface IConnectNoticeSource
{
    /// <summary>当前提示；没有时为空字符串。</summary>
    string ConnectNotice { get; }

    event EventHandler? ConnectNoticeChanged;
}

/// <summary>默认会话之外的会话工厂。按 <see cref="Kind"/> 注册，平台不支持时不注册。</summary>
public interface ISessionKindProvider
{
    SessionKind Kind { get; }

    /// <summary>该连接能否开出这种会话（决定入口是否出现）。</summary>
    bool CanOpen(ConnectionProfile profile);

    /// <summary>本机是否具备运行条件。</summary>
    bool IsAvailable(out string? unavailableReason);

    IRemoteSession CreateSession(SessionRequest request);
}

/// <summary>PowerShell 远程的网络端点约定。</summary>
public static class PowerShellEndpoint
{
    /// <summary>WinRM 的 HTTP 默认端口。与连接里填的 RDP 端口无关。</summary>
    public const int DefaultPort = 5985;
}
