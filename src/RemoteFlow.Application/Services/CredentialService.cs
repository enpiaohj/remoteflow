using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Application.Services;

/// <summary>
/// 凭据应用服务。协调「元数据仓储」与「Secret 保险库」两侧，
/// 保证二者始终一致：新增/修改时写 Vault，删除时同步清理 Vault，不留孤儿密文。
/// </summary>
public sealed class CredentialService(
    ICredentialRepository repository,
    ICredentialVault vault,
    ILogger<CredentialService> logger,
    ISyncChangeTracker? syncTracker = null)
{
    private readonly ISyncChangeTracker _sync = syncTracker ?? NoOpSyncChangeTracker.Instance;

    public Task<IReadOnlyList<Credential>> GetAllAsync(CancellationToken ct = default)
        => repository.GetAllAsync(ct);

    public Task<Credential?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => repository.GetByIdAsync(id, ct);

    /// <summary>
    /// 新建凭据。明文 Secret 只在本方法内短暂存在，随即交给 Vault 加密，不落 SQLite。
    /// </summary>
    public async Task<Credential> CreateAsync(
        Credential credential, string? password, string? privateKey, CancellationToken ct = default)
    {
        Validate(credential);

        if (!string.IsNullOrEmpty(password))
        {
            credential.SecretReference = await vault.StoreSecretAsync(
                VaultReference.ForPassword(credential.Id), password, ct);
        }

        if (!string.IsNullOrEmpty(privateKey))
        {
            credential.KeyReference = await vault.StoreSecretAsync(
                VaultReference.ForPrivateKey(credential.Id), privateKey, ct);
        }

        credential.CreatedAt = DateTimeOffset.Now;
        credential.UpdatedAt = credential.CreatedAt;

        await repository.AddAsync(credential, ct);
        await _sync.TrackUpsertAsync(SyncEntityTypes.Credential, credential.Id.ToString(), ct);
        if (credential.SecretReference is not null || credential.KeyReference is not null)
        {
            await _sync.TrackUpsertAsync(SyncEntityTypes.CredentialSecret, credential.Id.ToString(), ct);
        }

        logger.LogInformation("已新建凭据 {CredentialName}（类型 {Type}）", credential.Name, credential.Type);

        return credential;
    }

    /// <summary>
    /// 创建副本：沿用类型 / 域 / 用户名 / 备注，Secret 从保险库取出后以副本自己的引用另存一份
    /// （与原凭据互不影响，改其一不会连带另一个）。名称为空时自动生成不冲突的「原名称 - 副本」。
    /// <para>
    /// 原凭据声明了密码 / 私钥却已从保险库丢失时拒绝复制：否则会悄悄复制出一个没有 Secret 的凭据，
    /// 连接时才表现为「认证失败」。
    /// </para>
    /// </summary>
    public async Task<Credential> DuplicateAsync(Guid sourceId, string? newName = null, CancellationToken ct = default)
    {
        var source = await repository.GetByIdAsync(sourceId, ct)
            ?? throw new InvalidOperationException("要复制的凭据不存在。");

        using var resolved = await vault.ResolveAsync(source, ct);
        if (resolved.SecretMissing)
        {
            throw new InvalidOperationException(
                "原凭据已保存的密码或私钥在本机保险库中找不到，无法复制。请先编辑原凭据重新保存。");
        }

        var name = string.IsNullOrWhiteSpace(newName)
            ? NextCopyName(source.Name, (await repository.GetAllAsync(ct)).Select(c => c.Name))
            : newName.Trim();

        var copy = new Credential
        {
            Name = name,
            Type = source.Type,
            Username = source.Username,
            Domain = source.Domain,
            Description = source.Description,
        };

        return await CreateAsync(copy, resolved.Password, resolved.PrivateKey, ct);
    }

    /// <summary>「原名称 - 副本」，已存在则依次「- 副本 2」「- 副本 3」…（不区分大小写）。</summary>
    internal static string NextCopyName(string sourceName, IEnumerable<string> existingNames)
    {
        var taken = existingNames.ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var candidate = $"{sourceName} - 副本";
        for (var n = 2; taken.Contains(candidate); n++)
        {
            candidate = $"{sourceName} - 副本 {n}";
        }

        return candidate;
    }

    /// <summary>
    /// 更新凭据。<paramref name="password"/> / <paramref name="privateKey"/> 传 null 表示
    /// 「保持原值不变」，传空字符串表示「清除该 Secret」。
    /// 这样 UI 的密码框留空即可安全地表示不修改密码。
    /// </summary>
    public async Task UpdateAsync(
        Credential credential, string? password, string? privateKey, CancellationToken ct = default)
    {
        Validate(credential);

        if (password is not null)
        {
            credential.SecretReference = await ReplaceSecretAsync(
                credential.SecretReference, VaultReference.ForPassword(credential.Id), password, ct);
        }

        if (privateKey is not null)
        {
            credential.KeyReference = await ReplaceSecretAsync(
                credential.KeyReference, VaultReference.ForPrivateKey(credential.Id), privateKey, ct);
        }

        await repository.UpdateAsync(credential, ct);
        await _sync.TrackUpsertAsync(SyncEntityTypes.Credential, credential.Id.ToString(), ct);
        if (password is not null || privateKey is not null)
        {
            await _sync.TrackUpsertAsync(SyncEntityTypes.CredentialSecret, credential.Id.ToString(), ct);
        }

        logger.LogInformation("已更新凭据 {CredentialName}", credential.Name);
    }

    /// <summary>删除凭据，并同步清除 Vault 中的 Secret 密文。</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var credential = await repository.GetByIdAsync(id, ct);
        if (credential is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(credential.SecretReference))
        {
            await vault.DeleteSecretAsync(credential.SecretReference, ct);
        }

        if (!string.IsNullOrEmpty(credential.KeyReference))
        {
            await vault.DeleteSecretAsync(credential.KeyReference, ct);
        }

        await repository.DeleteAsync(id, ct);
        await _sync.TrackDeleteAsync(SyncEntityTypes.CredentialSecret, id.ToString(), ct);
        await _sync.TrackDeleteAsync(SyncEntityTypes.Credential, id.ToString(), ct);
        logger.LogInformation("已删除凭据 {CredentialName}", credential.Name);
    }

    /// <summary>
    /// 解析出可用于建立连接的凭据。返回对象生命周期极短，调用方用完必须 Dispose。
    /// </summary>
    public async Task<ResolvedCredential?> ResolveAsync(Guid credentialId, CancellationToken ct = default)
    {
        var credential = await repository.GetByIdAsync(credentialId, ct);
        return credential is null ? null : await vault.ResolveAsync(credential, ct);
    }

    /// <summary>写入新 Secret 并删除旧密文；传入空字符串表示清除。</summary>
    private async Task<string?> ReplaceSecretAsync(
        string? existingReference, string newReference, string value, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(existingReference))
        {
            await vault.DeleteSecretAsync(existingReference, ct);
        }

        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return await vault.StoreSecretAsync(newReference, value, ct);
    }

    private static void Validate(Credential credential)
    {
        if (string.IsNullOrWhiteSpace(credential.Name))
        {
            throw new ArgumentException("凭据名称不能为空。", nameof(credential));
        }

        if (Credential.RequiresUsername(credential.Type) && string.IsNullOrWhiteSpace(credential.Username))
        {
            throw new ArgumentException("该凭据类型必须填写用户名。", nameof(credential));
        }
    }
}
