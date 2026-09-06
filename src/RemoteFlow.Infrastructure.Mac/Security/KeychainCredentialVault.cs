using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Mac.Interop;

namespace RemoteFlow.Infrastructure.Security;

/// <summary>
/// 基于 macOS Keychain Services 的凭据保险库。Windows 侧 <c>DpapiCredentialVault</c> 的对等实现。
/// <para>
/// <b>存储模型：</b>
/// <code>
/// SQLite   →  Credential 元数据 + SecretReference（仅引用键）
///                     │
///                     ▼
/// Keychain →  kSecClassGenericPassword 条目
///             service = "RemoteFlow"，account = 引用键，data = Secret 明文字节
/// </code>
/// Secret 由系统钥匙串持有并受用户登录态保护，与业务数据分离。
/// 因此把 <c>remoteflow.db</c> 复制走得不到任何密码。
/// </para>
/// <para>
/// <b>与 DPAPI 版的差异：</b>Windows 版把全部密文集中写在 <c>vault.dat</c> 一个文件里，
/// 每次读写都要整文件读改写（故需信号量串行化）；本实现每个 Secret 独立成条目，
/// 天然无文件损坏风险、无并发读改写竞争，也不需要 <c>vault.dat</c>。
/// 迁移路径不受影响：跨平台迁移走 <c>.rfbackup</c>（口令派生 + AES-GCM，与平台无关）。
/// </para>
/// <para>
/// <b>本类是 macOS 侧唯一接触明文 Secret 的组件</b>，任何方法都不得把 Secret 写入日志。
/// </para>
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class KeychainCredentialVault : ICredentialVault
{
    /// <summary>钥匙串条目的 service 字段。同一 service 下按 account（引用键）区分条目。</summary>
    private const string ServiceName = "RemoteFlow";

    private readonly ILogger<KeychainCredentialVault> _logger;
    private readonly string _service;

    /// <param name="logger">日志器。实现保证不向其写入任何 Secret。</param>
    /// <param name="serviceName">
    /// 钥匙串 service 名。默认 <c>RemoteFlow</c>；测试传入独立值以免污染用户真实钥匙串。
    /// </param>
    public KeychainCredentialVault(ILogger<KeychainCredentialVault> logger, string? serviceName = null)
    {
        _logger = logger;
        _service = string.IsNullOrWhiteSpace(serviceName) ? ServiceName : serviceName;
    }

    /// <summary>生成一个新的引用键。使用 GUID，避免从键名反推凭据用途。</summary>
    public static string CreateReference(string purpose) => $"{purpose}:{Guid.NewGuid():N}";

    public Task<string> StoreSecretAsync(string reference, string secret, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        var secretBytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            // 先尝试更新已有条目；不存在再新增。反过来做会在常见的「改密码」路径上
            // 白白触发一次 errSecDuplicateItem。
            var status = Update(reference, secretBytes);

            if (status == SecurityFramework.ErrSecItemNotFound)
            {
                status = Add(reference, secretBytes);
            }

            if (status != SecurityFramework.ErrSecSuccess)
            {
                throw new InvalidOperationException(
                    $"无法写入钥匙串条目：{SecurityFramework.DescribeStatus(status)}");
            }

            // 只记录引用键，绝不记录 Secret 本身。
            _logger.LogInformation("已保存凭据 Secret 至钥匙串，引用键 {Reference}", reference);
            return Task.FromResult(reference);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    public Task<string?> RetrieveSecretAsync(string reference, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return Task.FromResult<string?>(null);
        }

        byte[]? data = null;
        try
        {
            var status = CopyMatching(reference, out data);

            if (status == SecurityFramework.ErrSecItemNotFound)
            {
                return Task.FromResult<string?>(null);
            }

            if (status != SecurityFramework.ErrSecSuccess)
            {
                // 只记录引用键与失败原因，不记录任何密文或明文内容。
                _logger.LogError(
                    "无法读取引用键 {Reference} 对应的 Secret：{Reason}",
                    reference, SecurityFramework.DescribeStatus(status));
                return Task.FromResult<string?>(null);
            }

            return Task.FromResult<string?>(data is null ? null : Encoding.UTF8.GetString(data));
        }
        finally
        {
            if (data is not null)
            {
                CryptographicOperations.ZeroMemory(data);
            }
        }
    }

    public Task DeleteSecretAsync(string reference, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return Task.CompletedTask;
        }

        var status = Delete(reference);

        if (status == SecurityFramework.ErrSecSuccess)
        {
            _logger.LogInformation("已从钥匙串删除凭据 Secret，引用键 {Reference}", reference);
        }
        else if (status != SecurityFramework.ErrSecItemNotFound)
        {
            // 删除不存在的条目视为已达成目标，不算失败；其余情况记录原因。
            _logger.LogWarning(
                "删除引用键 {Reference} 对应的 Secret 失败：{Reason}",
                reference, SecurityFramework.DescribeStatus(status));
        }

        return Task.CompletedTask;
    }

    public async Task<ResolvedCredential> ResolveAsync(Credential credential, CancellationToken ct = default)
    {
        string? password = null;
        string? privateKey = null;

        if (!string.IsNullOrEmpty(credential.SecretReference))
        {
            password = await RetrieveSecretAsync(credential.SecretReference, ct);
        }

        if (!string.IsNullOrEmpty(credential.KeyReference))
        {
            privateKey = await RetrieveSecretAsync(credential.KeyReference, ct);
        }

        return new ResolvedCredential
        {
            Type = credential.Type,
            Username = credential.Username,
            Domain = credential.Domain,
            Password = password,
            PrivateKey = privateKey,
        };
    }

    // ── Keychain 原语 ─────────────────────────────────────────────

    /// <summary>
    /// 构造定位单个条目的查询字典：class = GenericPassword，service + account 唯一确定一条。
    /// </summary>
    private CoreFoundation.CFObject CreateQuery(
        CoreFoundation.CFObject service,
        CoreFoundation.CFObject account)
        => CoreFoundation.CreateDictionary(
            [SecurityFramework.SecClass, SecurityFramework.SecAttrService, SecurityFramework.SecAttrAccount],
            [SecurityFramework.SecClassGenericPassword, service.Handle, account.Handle]);

    private int Add(string reference, byte[] secretBytes)
    {
        using var service = CoreFoundation.CreateString(_service);
        using var account = CoreFoundation.CreateString(reference);
        using var data = CoreFoundation.CreateData(secretBytes);

        // kSecAttrAccessibleAfterFirstUnlock：登录后即可访问，避免后台重连时因
        // 钥匙串未解锁而失败；同时不降级到「始终可访问」这种更弱的等级。
        // kSecAttrAccess（trustedlist=NULL）：放开 app 签名限制，避免 ad-hoc 构建
        // 每次 hash 变化触发授权框（见 SecurityFramework.CreateOpenAccess 说明）。
        var access = SecurityFramework.CreateOpenAccess(_service);
        try
        {
            List<nint> keys =
            [
                SecurityFramework.SecClass,
                SecurityFramework.SecAttrService,
                SecurityFramework.SecAttrAccount,
                SecurityFramework.SecValueData,
                SecurityFramework.SecAttrAccessible,
            ];
            List<nint> values =
            [
                SecurityFramework.SecClassGenericPassword,
                service.Handle,
                account.Handle,
                data.Handle,
                SecurityFramework.SecAttrAccessibleAfterFirstUnlock,
            ];
            if (access != nint.Zero)
            {
                keys.Add(SecurityFramework.SecAttrAccess);
                values.Add(access);
            }

            using var attributes = CoreFoundation.CreateDictionary([.. keys], [.. values]);
            return SecurityFramework.SecItemAdd(attributes.Handle, nint.Zero);
        }
        finally
        {
            if (access != nint.Zero)
            {
                CoreFoundation.CFRelease(access);
            }
        }
    }

    private int Update(string reference, byte[] secretBytes)
    {
        using var service = CoreFoundation.CreateString(_service);
        using var account = CoreFoundation.CreateString(reference);
        using var query = CreateQuery(service, account);
        using var data = CoreFoundation.CreateData(secretBytes);

        using var changes = CoreFoundation.CreateDictionary(
            [SecurityFramework.SecValueData],
            [data.Handle]);

        return SecurityFramework.SecItemUpdate(query.Handle, changes.Handle);
    }

    private int CopyMatching(string reference, out byte[]? secretBytes)
    {
        secretBytes = null;

        using var service = CoreFoundation.CreateString(_service);
        using var account = CoreFoundation.CreateString(reference);

        using var query = CoreFoundation.CreateDictionary(
            [
                SecurityFramework.SecClass,
                SecurityFramework.SecAttrService,
                SecurityFramework.SecAttrAccount,
                SecurityFramework.SecReturnData,
                SecurityFramework.SecMatchLimit,
            ],
            [
                SecurityFramework.SecClassGenericPassword,
                service.Handle,
                account.Handle,
                SecurityFramework.CFBooleanTrue,
                SecurityFramework.SecMatchLimitOne,
            ]);

        var status = SecurityFramework.SecItemCopyMatching(query.Handle, out var result);

        if (status == SecurityFramework.ErrSecSuccess && result != nint.Zero)
        {
            // SecItemCopyMatching 遵循 Create Rule，返回的 CFData 由调用方释放。
            using var data = new CoreFoundation.CFObject(result);
            secretBytes = CoreFoundation.ReadData(result);
        }

        return status;
    }

    private int Delete(string reference)
    {
        using var service = CoreFoundation.CreateString(_service);
        using var account = CoreFoundation.CreateString(reference);
        using var query = CreateQuery(service, account);

        return SecurityFramework.SecItemDelete(query.Handle);
    }
}
