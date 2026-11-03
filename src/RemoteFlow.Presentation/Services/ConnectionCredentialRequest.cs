using RemoteFlow.Core.Models;

namespace RemoteFlow.Presentation.Services;

/// <summary>连接前向用户索取凭据的对话框参数。不含 Secret。</summary>
/// <param name="ConnectionName">连接名称，用于提示「正在连接谁」。</param>
/// <param name="Target">主机:端口，辅助确认连接目标。</param>
/// <param name="Requirement">要求用户补充什么：只密码 / 账号和密码；账户名已知时只读展示。</param>
/// <param name="SavedCredentials">「更多选项」里可直接选用的已保存凭据（只含保险库里有密码 / 私钥的）。为 null 或空则不显示该入口。</param>
public sealed record ConnectionCredentialRequest(
    string ConnectionName,
    string Target,
    ConnectionCredentialRequirement Requirement,
    IReadOnlyList<SavedCredentialChoice>? SavedCredentials = null);

/// <summary>「更多选项」列表里的一条已保存凭据。<paramref name="Account"/> 为「域\用户」展示串，不含 Secret。</summary>
public sealed record SavedCredentialChoice(Guid Id, string Name, string Account);

/// <summary>
/// 用户在连接前当场输入的凭据，只在发起这一次连接的调用栈里短暂存在：
/// 不入库、不缓存、不进日志，用完即转成一次性 <see cref="ResolvedCredential"/>。
/// </summary>
/// <param name="SavedCredentialId">在「更多选项」里选了已保存的凭据时的 Id；此时 Username / Password 为空，由调用方取保存的凭据。</param>
public sealed record ConnectionCredentialInput(
    string Username,
    string Domain,
    string Password,
    bool Remember = false,
    Guid? SavedCredentialId = null,
    string? CredentialName = null,
    CredentialType? CredentialType = null)
{
    public override string ToString() => $"ConnectionCredentialInput(Username={Username})";
}
