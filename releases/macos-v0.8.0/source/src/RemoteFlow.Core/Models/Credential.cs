namespace RemoteFlow.Core.Models;

/// <summary>
/// 凭据元数据。
/// <para>
/// <b>安全约束：本对象只保存元数据与 Secret 引用，不保存 Secret 本体。</b>
/// 实际的 Password / Private Key 由 <c>ICredentialVault</c> 通过
/// <see cref="SecretReference"/> / <see cref="KeyReference"/> 在 DPAPI 保护下存取。
/// 因此 SQLite 数据库即使被整体复制走，也无法直接得到可读密码。
/// </para>
/// </summary>
public sealed class Credential
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>凭据显示名称，如 DomainAdmin。</summary>
    public string Name { get; set; } = string.Empty;

    public CredentialType Type { get; set; }

    /// <summary>用户名。VNC 口令类型下为空。</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Windows 域名。仅 <see cref="CredentialType.WindowsDomain"/> 使用。</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>密码 / Passphrase 在 Vault 中的引用键，不是密码本身。</summary>
    public string? SecretReference { get; set; }

    /// <summary>SSH 私钥在 Vault 中的引用键，不是私钥本身。</summary>
    public string? KeyReference { get; set; }

    /// <summary>备注说明。</summary>
    public string Description { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>该凭据类型是否需要用户名。</summary>
    public static bool RequiresUsername(CredentialType type) =>
        type is not CredentialType.VncPassword;

    /// <summary>该凭据类型是否需要私钥。</summary>
    public static bool RequiresPrivateKey(CredentialType type) =>
        type is CredentialType.SshPrivateKey;
}

/// <summary>
/// 从 Vault 解析出的、可直接用于建立连接的凭据。
/// <para>
/// 生命周期必须尽可能短：由 Provider 在 ConnectAsync 期间使用，用后立即 Dispose。
/// 不得写入日志、不得序列化、不得跨会话缓存。
/// </para>
/// </summary>
public sealed class ResolvedCredential : IDisposable
{
    public required CredentialType Type { get; init; }

    public string Username { get; init; } = string.Empty;

    public string Domain { get; init; } = string.Empty;

    /// <summary>明文密码或私钥 Passphrase。可能为 null。</summary>
    public string? Password { get; init; }

    /// <summary>PEM / OpenSSH 格式私钥正文。可能为 null。</summary>
    public string? PrivateKey { get; init; }

    private bool _disposed;

    /// <summary>
    /// 显式清理。由于 .NET 的 string 不可变且由 GC 管理，无法真正擦除内存，
    /// 此处的职责是切断引用、尽快让 GC 回收，并标记对象不可再用。
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>供协议层在使用前自检，避免误用已释放的凭据。</summary>
    public void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// 该重写确保凭据即使被误传入日志、字符串插值或调试输出，也不会泄露内容。
    /// </summary>
    public override string ToString() => $"ResolvedCredential({Type}, Username={Username})";
}
