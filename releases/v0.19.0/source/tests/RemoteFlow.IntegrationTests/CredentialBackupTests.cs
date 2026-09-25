using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Data;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 凭据加密备份的端到端验证。
/// <para>
/// 核心验收点：口令错误 / 文件被篡改时<b>绝不解出数据</b>；导入按名称跳过已存在。
/// </para>
/// </summary>
public sealed class CredentialBackupTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    private static CredentialBackupPayload SamplePayload() => new()
    {
        Credentials =
        {
            new CredentialBackupEntry
            {
                Name = "DomainAdmin",
                Type = CredentialType.WindowsDomain,
                Username = "piaohj",
                Domain = "cygdi",
                Description = "POC DC 管理员",
                Password = "P@ssw0rd-北京-2026!",
            },
            new CredentialBackupEntry
            {
                Name = "Git-Deploy",
                Type = CredentialType.SshPrivateKey,
                Username = "git",
                PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nfake-key-body\n-----END OPENSSH PRIVATE KEY-----",
                Password = "key-passphrase",
            },
        },
    };

    [Fact]
    public void 往返一致_导出再导入能拿回原样内容()
    {
        var envelope = CredentialBackup.Export(SamplePayload(), "correct horse battery staple");

        var result = CredentialBackup.Import(envelope, "correct horse battery staple");

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Payload!.Credentials.Count);

        var admin = result.Payload.Credentials[0];
        Assert.Equal("DomainAdmin", admin.Name);
        Assert.Equal(CredentialType.WindowsDomain, admin.Type);
        Assert.Equal("cygdi", admin.Domain);
        Assert.Equal("P@ssw0rd-北京-2026!", admin.Password);

        var git = result.Payload.Credentials[1];
        Assert.Equal(CredentialType.SshPrivateKey, git.Type);
        Assert.Contains("BEGIN OPENSSH PRIVATE KEY", git.PrivateKey);
        Assert.Equal("key-passphrase", git.Password);
    }

    [Fact]
    public void 明文Secret不出现在信封里()
    {
        var envelope = CredentialBackup.Export(SamplePayload(), "pw");

        Assert.DoesNotContain("P@ssw0rd-北京-2026!", envelope);
        Assert.DoesNotContain("key-passphrase", envelope);
        Assert.DoesNotContain("BEGIN OPENSSH PRIVATE KEY", envelope);
    }

    [Fact]
    public void 错误口令返回WrongPasswordOrCorrupted而非抛异常()
    {
        var envelope = CredentialBackup.Export(SamplePayload(), "right-password");

        var result = CredentialBackup.Import(envelope, "wrong-password");

        Assert.False(result.Succeeded);
        Assert.Equal(CredentialBackupImportFailure.WrongPasswordOrCorrupted, result.Failure);
        Assert.Null(result.Payload);
    }

    [Fact]
    public void 篡改密文一个字节即被GCM标签校验挡下()
    {
        var envelope = CredentialBackup.Export(SamplePayload(), "pw");

        // 解析信封，翻转 ciphertext 的最后一个字节，再原样塞回。
        using var doc = System.Text.Json.JsonDocument.Parse(envelope);
        var root = doc.RootElement;
        var original = Convert.FromBase64String(root.GetProperty("ciphertext").GetString()!);
        original[^1] ^= 0xFF;

        var tampered = System.Text.Json.JsonSerializer.Serialize(new
        {
            format = root.GetProperty("format").GetString(),
            version = root.GetProperty("version").GetInt32(),
            kdf = root.GetProperty("kdf").GetString(),
            iterations = root.GetProperty("iterations").GetInt32(),
            cipher = root.GetProperty("cipher").GetString(),
            salt = root.GetProperty("salt").GetString(),
            nonce = root.GetProperty("nonce").GetString(),
            tag = root.GetProperty("tag").GetString(),
            ciphertext = Convert.ToBase64String(original),
        });

        var result = CredentialBackup.Import(tampered, "pw");

        Assert.Equal(CredentialBackupImportFailure.WrongPasswordOrCorrupted, result.Failure);
    }

    [Fact]
    public void 非备份文件返回NotABackupFile()
    {
        Assert.Equal(CredentialBackupImportFailure.NotABackupFile,
            CredentialBackup.Import("just some text, not json", "pw").Failure);

        Assert.Equal(CredentialBackupImportFailure.NotABackupFile,
            CredentialBackup.Import("{\"format\":\"something-else\"}", "pw").Failure);
    }

    [Fact]
    public void 空口令导出抛ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => CredentialBackup.Export(SamplePayload(), ""));
    }

    [Fact]
    public async Task 服务端到端_导出后删除再导入_被删的回来同名的跳过()
    {
        var database = new RemoteFlowDatabase(_workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        database.Initialize();

        var repo = new SqliteCredentialRepository(database);
        var vault = new InMemoryCredentialVault();
        var credentialService = new CredentialService(repo, vault, NullLogger<CredentialService>.Instance);
        var backup = new CredentialBackupService(repo, vault, credentialService, NullLogger<CredentialBackupService>.Instance);

        await credentialService.CreateAsync(
            new Credential { Name = "Keep", Type = CredentialType.LocalPassword, Username = "admin" }, "keep-pw", null);
        await credentialService.CreateAsync(
            new Credential { Name = "Gone", Type = CredentialType.SshPassword, Username = "root" }, "gone-pw", null);

        var backupPath = Path.Combine(_workspace.Root, "creds.rfbackup");
        var exported = await backup.ExportAsync(backupPath, "backup-pw");
        Assert.Equal(2, exported);

        // 删掉 "Gone"
        var gone = (await repo.GetAllAsync()).Single(c => c.Name == "Gone");
        await credentialService.DeleteAsync(gone.Id);
        Assert.Single(await repo.GetAllAsync());

        var report = await backup.ImportAsync(backupPath, "backup-pw");

        Assert.Equal(CredentialBackupImportFailure.None, report.Failure);
        Assert.Equal(1, report.Imported);
        Assert.Equal(["Keep"], report.SkippedNames);

        var all = await repo.GetAllAsync();
        Assert.Equal(2, all.Count);

        // "Gone" 回来了，密码可解析
        var restored = all.Single(c => c.Name == "Gone");
        using var resolved = await vault.ResolveAsync(restored);
        Assert.Equal("gone-pw", resolved.Password);
    }

    [Fact]
    public async Task 服务导入错口令时不写入任何数据()
    {
        var database = new RemoteFlowDatabase(_workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        database.Initialize();
        var repo = new SqliteCredentialRepository(database);
        var vault = new InMemoryCredentialVault();
        var credentialService = new CredentialService(repo, vault, NullLogger<CredentialService>.Instance);
        var backup = new CredentialBackupService(repo, vault, credentialService, NullLogger<CredentialBackupService>.Instance);

        var backupPath = Path.Combine(_workspace.Root, "x.rfbackup");
        await File.WriteAllTextAsync(backupPath, CredentialBackup.Export(SamplePayload(), "real"));

        var report = await backup.ImportAsync(backupPath, "fake");

        Assert.Equal(CredentialBackupImportFailure.WrongPasswordOrCorrupted, report.Failure);
        Assert.Equal(0, report.Imported);
        Assert.Empty(await repo.GetAllAsync());
    }
}
