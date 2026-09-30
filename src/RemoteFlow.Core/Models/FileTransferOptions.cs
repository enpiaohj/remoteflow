namespace RemoteFlow.Core.Models;

/// <summary>
/// 文件传输通道。RDP / VNC 协议本身没有文件传输通道，所以「不建立会话直接传文件」
/// 需要借用别的协议；这里记录每个连接用哪一种。
/// </summary>
public enum FileTransferChannel
{
    /// <summary>按连接协议自动选：SSH → SFTP，RDP → SMB。</summary>
    Auto = 0,

    /// <summary>SFTP（SSH 子系统）。RDP 主机需要目标 Windows 安装并启用 OpenSSH Server。</summary>
    Sftp = 1,

    /// <summary>SMB 管理共享（<c>\\主机\C$</c> 等）。仅 Windows 版可用，固定 445 端口。</summary>
    Smb = 2
}

/// <summary>
/// 文件传输专项参数（按连接保存）。字段都是主机侧信息（通道 / 端口），不含本机路径，随连接同步无风险。
/// <para>
/// 缺字段时（老连接、旧客户端同步来的记录）取类型默认值，即 <see cref="FileTransferChannel.Auto"/>，
/// 行为与「未配置」一致，因此新增本类型不需要数据库迁移，也不需要升级同步 Schema。
/// </para>
/// </summary>
public sealed class FileTransferOptions
{
    /// <summary>传输通道。<see cref="FileTransferChannel.Auto"/> 由 <see cref="ResolveChannel"/> 按协议解析。</summary>
    public FileTransferChannel Channel { get; set; } = FileTransferChannel.Auto;

    /// <summary>
    /// 通道为 SFTP 且连接协议不是 SSH（即 RDP 主机走 OpenSSH）时使用的 SSH 端口。
    /// SSH 连接自身使用连接的端口，忽略此项。
    /// </summary>
    public int SftpPort { get; set; } = 22;

    /// <summary>
    /// 解析出实际通道。VNC 没有可用通道，返回 <c>null</c>；显式选择的通道原样返回
    /// （是否可用由各通道工厂的 <c>IsAvailable</c> 判断）。
    /// </summary>
    public FileTransferChannel? ResolveChannel(ProtocolType protocol)
    {
        if (protocol == ProtocolType.Vnc)
        {
            return null;
        }

        if (protocol == ProtocolType.Ssh)
        {
            // SSH 主机只有 SFTP 一条路；保存的是 Smb 也没有意义，按 SFTP 处理。
            return FileTransferChannel.Sftp;
        }

        return Channel == FileTransferChannel.Auto ? FileTransferChannel.Smb : Channel;
    }

    /// <summary>字段均为值类型或不可变字符串，浅拷贝即完整拷贝。</summary>
    public FileTransferOptions Clone() => (FileTransferOptions)MemberwiseClone();
}
