using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Security;
using Xunit;

namespace RemoteFlow.IntegrationTests.Mac;

/// <summary>
/// 仅在 macOS 上执行的 Fact。其他平台自动跳过，使解决方案在 Windows 上依然可全量跑测试。
/// </summary>
public sealed class MacOnlyFactAttribute : FactAttribute
{
    public MacOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Skip = "仅在 macOS 上执行（依赖 Security.framework 钥匙串）。";
        }
    }
}

/// <summary>
/// <see cref="KeychainCredentialVault"/> 针对真实系统钥匙串的验证。
/// 与 Windows 侧 <c>DpapiCredentialVaultTests</c> 对等。
/// <para>
/// 每个测试用独立的 service 名（含 GUID），避免污染用户真实钥匙串，
/// 并在 Dispose 时清理自己写入的全部条目。
/// </para>
/// </summary>
/// <remarks>
/// 标注 <see cref="SupportedOSPlatformAttribute"/> 是为了让平台兼容性分析器（CA1416）
/// 理解这些调用只在 macOS 上发生——实际的运行时保障由 <see cref="MacOnlyFactAttribute"/>
/// 提供，分析器无法看懂那层守卫。构造函数本身不触碰任何平台 API，因此在其他平台上
/// 被 xUnit 实例化也是安全的。
/// </remarks>
[SupportedOSPlatform("macos")]
[Collection("Keychain")]
public sealed class KeychainCredentialVaultTests : IDisposable
{
    private readonly string _service = $"RemoteFlow.Test.{Guid.NewGuid():N}";
    private readonly KeychainCredentialVault _vault;
    private readonly List<string> _written = [];

    public KeychainCredentialVaultTests()
    {
        _vault = new KeychainCredentialVault(NullLogger<KeychainCredentialVault>.Instance, _service);
    }

    private async Task<string> StoreAsync(string purpose, string secret)
    {
        var reference = KeychainCredentialVault.CreateReference(purpose);
        await _vault.StoreSecretAsync(reference, secret);
        _written.Add(reference);
        return reference;
    }

    [MacOnlyFact]
    public async Task 存入后可原样读回()
    {
        const string secret = "P@ssw0rd-往返测试-🔐";

        var reference = await StoreAsync("round-trip", secret);
        var actual = await _vault.RetrieveSecretAsync(reference);

        Assert.Equal(secret, actual);
    }

    [MacOnlyFact]
    public async Task 读取不存在的引用键返回null()
    {
        var actual = await _vault.RetrieveSecretAsync(
            KeychainCredentialVault.CreateReference("never-stored"));

        Assert.Null(actual);
    }

    [MacOnlyFact]
    public async Task 重复存入同一引用键覆盖旧值()
    {
        var reference = await StoreAsync("overwrite", "第一版");
        await _vault.StoreSecretAsync(reference, "第二版");

        Assert.Equal("第二版", await _vault.RetrieveSecretAsync(reference));
    }

    [MacOnlyFact]
    public async Task 删除后读不到()
    {
        var reference = await StoreAsync("delete", "要被删掉的");
        Assert.NotNull(await _vault.RetrieveSecretAsync(reference));

        await _vault.DeleteSecretAsync(reference);

        Assert.Null(await _vault.RetrieveSecretAsync(reference));
    }

    [MacOnlyFact]
    public async Task 删除不存在的引用键不抛异常()
    {
        // 幂等：凭据删除流程可能重复调用，不得因此失败。
        await _vault.DeleteSecretAsync(KeychainCredentialVault.CreateReference("absent"));
        await _vault.DeleteSecretAsync(string.Empty);
    }

    [MacOnlyFact]
    public async Task 多条凭据互不干扰()
    {
        var entries = Enumerable.Range(0, 8)
            .Select(i => (Purpose: $"multi-{i}", Secret: $"secret-{i}-{Guid.NewGuid():N}"))
            .ToArray();

        var references = new List<string>();
        foreach (var (purpose, secret) in entries)
        {
            references.Add(await StoreAsync(purpose, secret));
        }

        for (var i = 0; i < entries.Length; i++)
        {
            Assert.Equal(entries[i].Secret, await _vault.RetrieveSecretAsync(references[i]));
        }
    }

    [MacOnlyFact]
    public async Task 解析凭据同时取回密码与私钥()
    {
        var passwordRef = await StoreAsync("pw", "密码正文");
        var keyRef = await StoreAsync("key", "-----BEGIN OPENSSH PRIVATE KEY-----\nfake\n-----END-----");

        var credential = new Credential
        {
            Name = "测试凭据",
            Type = CredentialType.SshPrivateKey,
            Username = "root",
            Domain = "example",
            SecretReference = passwordRef,
            KeyReference = keyRef,
        };

        using var resolved = await _vault.ResolveAsync(credential);

        Assert.Equal(CredentialType.SshPrivateKey, resolved.Type);
        Assert.Equal("root", resolved.Username);
        Assert.Equal("example", resolved.Domain);
        Assert.Equal("密码正文", resolved.Password);
        Assert.StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----", resolved.PrivateKey);
    }

    [MacOnlyFact]
    public async Task 解析缺少引用的凭据得到null字段而非抛异常()
    {
        var credential = new Credential
        {
            Name = "无 Secret",
            Type = CredentialType.LocalPassword,
            Username = "guest",
        };

        using var resolved = await _vault.ResolveAsync(credential);

        Assert.Null(resolved.Password);
        Assert.Null(resolved.PrivateKey);
    }

    [MacOnlyFact]
    public async Task 不同service之间互相隔离()
    {
        var reference = await StoreAsync("isolation", "属于本 service");

        var otherService = new KeychainCredentialVault(
            NullLogger<KeychainCredentialVault>.Instance, $"RemoteFlow.Test.{Guid.NewGuid():N}");

        Assert.Null(await otherService.RetrieveSecretAsync(reference));
    }

    [MacOnlyFact]
    public async Task ToString不泄露Secret内容()
    {
        var reference = await StoreAsync("leak-check", "绝不能出现在任何输出里");

        using var resolved = await _vault.ResolveAsync(new Credential
        {
            Name = "泄露检查",
            Type = CredentialType.LocalPassword,
            Username = "admin",
            SecretReference = reference,
        });

        var text = resolved.ToString();
        Assert.DoesNotContain("绝不能出现在任何输出里", text);
        Assert.Contains("admin", text);
    }

    public void Dispose()
    {
        // 清理本测试写入钥匙串的全部条目，不留残迹。
        foreach (var reference in _written)
        {
            try
            {
                _vault.DeleteSecretAsync(reference).GetAwaiter().GetResult();
            }
            catch
            {
                // 清理失败不应掩盖测试本身的结果。
            }
        }
    }
}
