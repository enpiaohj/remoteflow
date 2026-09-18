namespace RemoteFlow.Core.Abstractions;

/// <summary>
/// Vault 引用键的命名规则。引用键会明文保存在 SQLite 中，
/// 因此它只能是「定位用的键」，不得包含任何凭据内容线索。
/// </summary>
public static class VaultReference
{
    /// <summary>密码 / Passphrase 的引用键。</summary>
    public static string ForPassword(Guid credentialId) => $"cred/{credentialId:N}/password";

    /// <summary>SSH 私钥的引用键。</summary>
    public static string ForPrivateKey(Guid credentialId) => $"cred/{credentialId:N}/privatekey";
}
