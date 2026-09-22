using System.Text.Json;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Cloud;

namespace RemoteFlow.Infrastructure.Sync.Sources;

/// <summary>
/// credential-secret 实体的同步源：同步 Password / Private Key 本体。
/// <para>
/// 明文只在 <see cref="GetPlaintextAsync"/> / <see cref="ApplyAsync"/> 内短暂存在，
/// 上传前由 SyncCoordinator E2EE 加密，落地时只经 <see cref="ICredentialVault"/>
/// （DPAPI / Keychain）保护，<b>绝不写入 SQLite</b>（安全设计 §8）。
/// </para>
/// </summary>
public sealed class CredentialSecretSyncSource(ICredentialRepository credentials, ICredentialVault vault)
    : ISyncEntitySource
{
    public int SchemaVersion => 1;

    public IReadOnlyList<string> EntityTypes => [SyncEntityTypes.CredentialSecret];

    public bool Handles(string entityType) => entityType == SyncEntityTypes.CredentialSecret;

    public async Task<IReadOnlyList<string>> ListEntityIdsAsync(string entityType, CancellationToken ct = default) =>
        [.. (await credentials.GetAllAsync(ct))
            .Where(c => c.SecretReference is not null || c.KeyReference is not null)
            .Select(c => c.Id.ToString())];

    public async Task<byte[]?> GetPlaintextAsync(string entityType, string entityId, CancellationToken ct = default)
    {
        var credential = await credentials.GetByIdAsync(Guid.Parse(entityId), ct);
        if (credential is null)
        {
            return null;
        }

        var password = credential.SecretReference is null
            ? null
            : await vault.RetrieveSecretAsync(credential.SecretReference, ct);
        var privateKey = credential.KeyReference is null
            ? null
            : await vault.RetrieveSecretAsync(credential.KeyReference, ct);

        if (password is null && privateKey is null)
        {
            return null;
        }

        return JsonSerializer.SerializeToUtf8Bytes(new SecretPayload(password, privateKey));
    }

    public async Task ApplyAsync(
        string entityType, string entityId, byte[]? plaintext, bool deleted, int schemaVersion,
        CancellationToken ct = default)
    {
        var id = Guid.Parse(entityId);
        var credential = await credentials.GetByIdAsync(id, ct)
            ?? throw new SyncDependencyNotReadyException(
                SyncEntityTypes.CredentialSecret, entityId, SyncEntityTypes.Credential);

        if (deleted)
        {
            await ClearAsync(credential, ct);
            return;
        }

        var payload = JsonSerializer.Deserialize<SecretPayload>(plaintext!)
            ?? throw new JsonException("credential-secret payload deserialized to null.");

        var secretReference = await ReplaceAsync(
            credential.SecretReference, VaultReference.ForPassword(id), payload.Password, ct);
        var keyReference = await ReplaceAsync(
            credential.KeyReference, VaultReference.ForPrivateKey(id), payload.PrivateKey, ct);

        // 定点更新引用：不刷新 UpdatedAt，否则凭据元数据的内容哈希会被改坏（对账随即误推一次）。
        await credentials.SetSecretReferencesAsync(id, secretReference, keyReference, ct);
    }

    private async Task ClearAsync(Core.Models.Credential credential, CancellationToken ct)
    {
        if (credential.SecretReference is not null)
        {
            await vault.DeleteSecretAsync(credential.SecretReference, ct);
        }

        if (credential.KeyReference is not null)
        {
            await vault.DeleteSecretAsync(credential.KeyReference, ct);
        }

        await credentials.SetSecretReferencesAsync(credential.Id, null, null, ct);
    }

    private async Task<string?> ReplaceAsync(
        string? existingReference, string reference, string? value, CancellationToken ct)
    {
        if (existingReference is not null)
        {
            await vault.DeleteSecretAsync(existingReference, ct);
        }

        return string.IsNullOrEmpty(value) ? null : await vault.StoreSecretAsync(reference, value, ct);
    }

    private sealed record SecretPayload(string? Password, string? PrivateKey);
}
