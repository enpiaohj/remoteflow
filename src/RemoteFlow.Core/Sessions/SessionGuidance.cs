namespace RemoteFlow.Core.Sessions;

/// <summary>一条建议用户在目标或本机执行的命令。应用只展示和提供复制，不替用户执行。</summary>
/// <param name="Title">这条命令做什么、在哪台电脑上运行。</param>
/// <param name="Command">可直接复制执行的命令。</param>
/// <param name="Note">风险与撤销方法等补充说明。</param>
public sealed record SessionCommandHint(string Title, string Command, string? Note = null)
{
    public bool HasNote => !string.IsNullOrEmpty(Note);
}

/// <summary>
/// 失败时能给出结构化处理办法的会话：错误说明仍在 <see cref="IRemoteSession.ErrorMessage"/>，
/// 命令与系统原始报错单独提供，界面分开呈现。
/// </summary>
public interface ISessionGuidanceSource
{
    /// <summary>系统返回的原始错误信息（常为英文）；没有时为 null。</summary>
    string? ErrorDetail { get; }

    IReadOnlyList<SessionCommandHint> CommandHints { get; }

    /// <summary>处理办法前的原因说明（较长，界面默认折叠）；没有时为 null。</summary>
    string? GuidanceIntro => null;
}
