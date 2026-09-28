using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 测试用的内存版凭据保险库。
/// <para>
/// 用途：让「需要一个 Vault」但并不检验加密实现的测试（凭据备份、会话生命周期等）
/// 摆脱平台绑定，从而能在 Windows 与 macOS 双端运行。
/// 各平台真实 Vault 的加密行为由其专属测试覆盖
/// （Windows：<c>DpapiCredentialVaultTests</c>；macOS：后续的 Keychain 测试）。
/// </para>
/// <para>
/// 语义与真实实现保持一致：引用键不存在时返回 null；删除不存在的键不抛异常；
/// <see cref="ResolveAsync"/> 同时解析密码与私钥两个引用。
/// </para>
/// </summary>
public sealed class InMemoryCredentialVault : ICredentialVault
{
    private readonly Dictionary<string, string> _secrets = [];
    private readonly Lock _gate = new();

    /// <summary>已保存的 Secret 条目数，供测试断言「删除凭据时未残留密文」。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _secrets.Count;
            }
        }
    }

    /// <summary>生成引用键。与真实实现一致，使用 GUID，避免从键名反推凭据用途。</summary>
    public static string CreateReference(string purpose) => $"{purpose}:{Guid.NewGuid():N}";

    public Task<string> StoreSecretAsync(string reference, string secret, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        lock (_gate)
        {
            _secrets[reference] = secret;
        }

        return Task.FromResult(reference);
    }

    public Task<string?> RetrieveSecretAsync(string reference, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return Task.FromResult<string?>(null);
        }

        lock (_gate)
        {
            return Task.FromResult(_secrets.GetValueOrDefault(reference));
        }
    }

    public Task DeleteSecretAsync(string reference, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(reference))
        {
            lock (_gate)
            {
                _secrets.Remove(reference);
            }
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
}
