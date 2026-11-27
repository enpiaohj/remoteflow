namespace RemoteFlow.Core.Sessions;

/// <summary>修改本机 PowerShell 信任列表的结果。</summary>
public enum TrustedHostChange
{
    Done,

    /// <summary>用户在系统管理员确认框中取消。</summary>
    Declined,

    Failed
}

/// <summary>
/// 查看与移除本机 PowerShell 远程的信任列表（TrustedHosts）。仅 Windows 提供实现；
/// 列表是整台电脑共用的系统设置，移除需要管理员确认。
/// </summary>
public interface IPowerShellTrustedHosts
{
    /// <summary>当前列表中的条目，按原顺序。读取失败时返回空列表。</summary>
    IReadOnlyList<string> GetAll();

    Task<TrustedHostChange> RemoveAsync(string host, CancellationToken ct = default);
}
