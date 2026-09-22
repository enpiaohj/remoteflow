using RemoteFlow.Core.Models;

namespace RemoteFlow.Core.Abstractions;

/// <summary>
/// 凭据保险库。负责 Secret 的加密存取，是<b>唯一</b>接触明文 Secret 的组件。
/// <para>
/// 实现约束：
/// <list type="number">
///   <item>Secret 不得以明文写入 SQLite；数据库被复制走时不应直接得到可读密码。</item>
///   <item>任何方法都不得把 Secret 写入日志。</item>
///   <item>Secret 不随连接导出文件导出。</item>
/// </list>
/// </para>
/// </summary>
public interface ICredentialVault
{
    /// <summary>
    /// 存入 Secret，返回引用键。该键会保存到 <see cref="Credential.SecretReference"/>。
    /// </summary>
    Task<string> StoreSecretAsync(string reference, string secret, CancellationToken ct = default);

    /// <summary>读取 Secret。不存在时返回 null。</summary>
    Task<string?> RetrieveSecretAsync(string reference, CancellationToken ct = default);

    /// <summary>删除 Secret。凭据被删除时必须同步调用，避免残留。</summary>
    Task DeleteSecretAsync(string reference, CancellationToken ct = default);

    /// <summary>
    /// 把凭据元数据解析为可直接建立连接的凭据。
    /// 返回对象生命周期极短，调用方用完必须立即 Dispose。
    /// </summary>
    Task<ResolvedCredential> ResolveAsync(Credential credential, CancellationToken ct = default);
}
