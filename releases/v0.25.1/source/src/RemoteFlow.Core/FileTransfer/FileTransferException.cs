namespace RemoteFlow.Core.FileTransfer;

/// <summary>文件传输操作失败的分类。用于给出准确的中文提示，而不是笼统的「失败」。</summary>
public enum FileTransferErrorCode
{
    Unknown = 0,

    /// <summary>远端不支持该通道（如 SSH 服务端未启用 sftp 子系统）。</summary>
    NotSupported,

    PermissionDenied,

    NotFound,

    AlreadyExists,

    /// <summary>远端或本机磁盘空间不足。</summary>
    DiskFull,

    /// <summary>传输中连接中断。</summary>
    ConnectionLost,

    /// <summary>名称或路径不合法（含穿越、保留名等）。</summary>
    InvalidName,

    /// <summary>目录非空等无法执行的状态冲突。</summary>
    InvalidOperation
}

/// <summary>
/// 文件传输操作异常。消息面向用户（中文），<b>不包含文件内容、凭据或私钥</b>；
/// 是否包含路径由抛出方决定，日志侧仍只在 Debug 级别记录路径。
/// </summary>
public sealed class FileTransferException : Exception
{
    public FileTransferException(FileTransferErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public FileTransferErrorCode Code { get; }
}
