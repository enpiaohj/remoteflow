using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Application.Services;

/// <summary>
/// 一次凭据导入的结果。<see cref="Failure"/> 非 <see cref="CredentialBackupImportFailure.None"/>
/// 时表示整体失败（未写入任何数据）。
/// </summary>
public sealed record CredentialImportReport(
    int Imported,
    IReadOnlyList<string> SkippedNames,
    CredentialBackupImportFailure Failure)
{
    public static CredentialImportReport Failed(CredentialBackupImportFailure failure)
        => new(0, [], failure);
}

/// <summary>
/// 凭据的加密备份导入 / 导出。编排「元数据仓储 + 保险库 + 加密包」三方。
/// <para>
/// <b>安全约束：</b>明文 Secret 只在本服务方法内、以及 <see cref="CredentialBackup"/> 的
/// 加解密缓冲中短暂存在。日志只记条数，绝不记凭据名以外的任何内容。
/// </para>
/// </summary>
public sealed class CredentialBackupService(
    ICredentialRepository repository,
    ICredentialVault vault,
    CredentialService credentialService,
    ILogger<CredentialBackupService> logger)
{
    /// <summary>把全部凭据（含密码 / 私钥）导出为一个口令加密的 <c>.rfbackup</c> 文件。</summary>
    public async Task<int> ExportAsync(string filePath, string password, CancellationToken ct = default)
    {
        var credentials = await repository.GetAllAsync(ct);
        var payload = new CredentialBackupPayload();

        foreach (var credential in credentials)
        {
            var entry = new CredentialBackupEntry
            {
                Name = credential.Name,
                Type = credential.Type,
                Username = credential.Username,
                Domain = credential.Domain,
                Description = credential.Description,
            };

            if (!string.IsNullOrEmpty(credential.SecretReference))
            {
                entry.Password = await vault.RetrieveSecretAsync(credential.SecretReference, ct);
            }

            if (!string.IsNullOrEmpty(credential.KeyReference))
            {
                entry.PrivateKey = await vault.RetrieveSecretAsync(credential.KeyReference, ct);
            }

            payload.Credentials.Add(entry);
        }

        var envelope = CredentialBackup.Export(payload, password);
        await File.WriteAllTextAsync(filePath, envelope, ct);

        logger.LogInformation("已导出 {Count} 条凭据到加密备份包", payload.Credentials.Count);
        return payload.Credentials.Count;
    }

    /// <summary>
    /// 从 <c>.rfbackup</c> 导入凭据。同名（忽略大小写）的凭据跳过，不覆盖本机已有密钥。
    /// 口令错误 / 文件损坏时返回对应失败原因，不写入任何数据。
    /// </summary>
    public async Task<CredentialImportReport> ImportAsync(string filePath, string password, CancellationToken ct = default)
    {
        string fileContent;
        try
        {
            fileContent = await File.ReadAllTextAsync(filePath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "读取凭据备份文件失败");
            return CredentialImportReport.Failed(CredentialBackupImportFailure.NotABackupFile);
        }

        var result = CredentialBackup.Import(fileContent, password);
        if (!result.Succeeded)
        {
            return CredentialImportReport.Failed(result.Failure);
        }

        var existingNames = (await repository.GetAllAsync(ct))
            .Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var imported = 0;
        var skipped = new List<string>();

        foreach (var entry in result.Payload!.Credentials)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || !existingNames.Add(entry.Name))
            {
                skipped.Add(entry.Name);
                continue;
            }

            var credential = new Credential
            {
                Name = entry.Name,
                Type = entry.Type,
                Username = entry.Username,
                Domain = entry.Domain,
                Description = entry.Description,
            };

            await credentialService.CreateAsync(credential, entry.Password, entry.PrivateKey, ct);
            imported++;
        }

        logger.LogInformation("凭据导入完成：新增 {Imported} 条，跳过 {Skipped} 条", imported, skipped.Count);
        return new CredentialImportReport(imported, skipped, CredentialBackupImportFailure.None);
    }
}
