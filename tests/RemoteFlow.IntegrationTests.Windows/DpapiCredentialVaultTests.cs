using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Security;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 凭据保险库的端到端验证。
/// <para>
/// 核心 P0 验收点：<b>Secret 不明文落入磁盘</b>——保险库文件里不应能搜到明文密码。
/// </para>
/// </summary>
public sealed class DpapiCredentialVaultTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly DpapiCredentialVault _vault;

    public DpapiCredentialVaultTests()
    {
        _vault = new DpapiCredentialVault(_workspace.VaultPath, NullLogger<DpapiCredentialVault>.Instance);
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task 存入的Secret可以原样取回()
    {
        const string secret = "P@ssw0rd-北京-2026!";
        var reference = DpapiCredentialVault.CreateReference("test");

        await _vault.StoreSecretAsync(reference, secret);

        Assert.Equal(secret, await _vault.RetrieveSecretAsync(reference));
    }

    [Fact]
    public async Task 保险库文件里搜不到明文Secret()
    {
        const string secret = "SuperSecretValue_ShouldNeverAppearInFile";
        var reference = DpapiCredentialVault.CreateReference("leak-check");

        await _vault.StoreSecretAsync(reference, secret);

        Assert.True(File.Exists(_workspace.VaultPath));

        var rawBytes = await File.ReadAllBytesAsync(_workspace.VaultPath);
        var secretBytes = Encoding.UTF8.GetBytes(secret);

        Assert.False(ContainsSubsequence(rawBytes, secretBytes),
            "保险库文件中出现了明文 Secret 字节序列——DPAPI 加密未生效。");

        // 文本形式也不应包含
        var text = Encoding.UTF8.GetString(rawBytes);
        Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public async Task 删除Secret后取回为空且文件中不再包含()
    {
        const string secret = "TransientSecret_9f3a";
        var reference = DpapiCredentialVault.CreateReference("delete-test");

        await _vault.StoreSecretAsync(reference, secret);
        await _vault.DeleteSecretAsync(reference);

        Assert.Null(await _vault.RetrieveSecretAsync(reference));

        var text = await File.ReadAllTextAsync(_workspace.VaultPath);
        Assert.DoesNotContain(reference, text);
    }

    [Fact]
    public async Task 不存在的引用键返回null而非抛异常()
    {
        Assert.Null(await _vault.RetrieveSecretAsync("cred/deadbeef/password"));
    }

    [Fact]
    public async Task 并发写入多个Secret互不覆盖()
    {
        var pairs = Enumerable.Range(0, 20)
            .Select(i => (Ref: DpapiCredentialVault.CreateReference($"c{i}"), Secret: $"secret-{i}-{Guid.NewGuid():N}"))
            .ToArray();

        await Task.WhenAll(pairs.Select(p => _vault.StoreSecretAsync(p.Ref, p.Secret)));

        foreach (var (reference, secret) in pairs)
        {
            Assert.Equal(secret, await _vault.RetrieveSecretAsync(reference));
        }
    }

    [Fact]
    public async Task Resolve能把凭据元数据解析为可用凭据()
    {
        var passwordRef = DpapiCredentialVault.CreateReference("pw");
        await _vault.StoreSecretAsync(passwordRef, "root-password");

        var credential = new Credential
        {
            Name = "Linux-Root",
            Type = CredentialType.SshPassword,
            Username = "root",
            SecretReference = passwordRef
        };

        using var resolved = await _vault.ResolveAsync(credential);

        Assert.Equal("root", resolved.Username);
        Assert.Equal("root-password", resolved.Password);
        Assert.Null(resolved.PrivateKey);

        // 解析结果的字符串表示不能泄露密码
        Assert.DoesNotContain("root-password", resolved.ToString());
    }

    [Fact]
    public async Task 私钥凭据的私钥与Passphrase分别解析()
    {
        var keyRef = DpapiCredentialVault.CreateReference("key");
        var phraseRef = DpapiCredentialVault.CreateReference("phrase");
        await _vault.StoreSecretAsync(keyRef, "-----BEGIN OPENSSH PRIVATE KEY-----\nfake\n-----END-----");
        await _vault.StoreSecretAsync(phraseRef, "key-passphrase");

        var credential = new Credential
        {
            Name = "Git-Deploy",
            Type = CredentialType.SshPrivateKey,
            Username = "git",
            KeyReference = keyRef,
            SecretReference = phraseRef
        };

        using var resolved = await _vault.ResolveAsync(credential);

        Assert.Contains("BEGIN OPENSSH PRIVATE KEY", resolved.PrivateKey);
        Assert.Equal("key-passphrase", resolved.Password);
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
