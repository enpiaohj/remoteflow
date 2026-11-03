namespace RemoteFlow.Core.Models;

/// <summary>建立连接前，是否需要用户当场补充凭据。</summary>
public enum ConnectionCredentialPrompt
{
    /// <summary>凭据已齐全（或该连接用私钥登录），直接使用保存的凭据。</summary>
    None = 0,

    /// <summary>凭据只保存了账户名，每次连接由用户输入密码。</summary>
    PasswordOnly = 1,

    /// <summary>连接没有分配凭据，每次连接由用户输入账号和密码。</summary>
    UsernameAndPassword = 2,
}

/// <summary>
/// 一次连接对凭据的要求。账户名 / 域来自已保存的凭据（<see cref="ConnectionCredentialPrompt.PasswordOnly"/> 时），
/// 其余字段由用户输入；不含任何 Secret。
/// </summary>
public sealed record ConnectionCredentialRequirement(
    ConnectionCredentialPrompt Prompt,
    CredentialType Type,
    string Username = "",
    string Domain = "")
{
    public static ConnectionCredentialRequirement NotRequired { get; } = new(ConnectionCredentialPrompt.None, default);

    /// <summary>该类型的凭据是否需要账户名（VNC 口令只有密码）。</summary>
    public bool NeedsUsername => Credential.RequiresUsername(Type);
}
