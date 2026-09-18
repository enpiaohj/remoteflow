using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Infrastructure.Sync.Sources;

/// <summary>
/// credential 实体的同步源。<b>只同步元数据</b>——<see cref="Credential.SecretReference"/> /
/// <see cref="Credential.KeyReference"/> 是本机绑定引用，不入 Payload；Secret 本体由
/// credential-secret 同步（后续），本机现有引用在 Apply 时保留。
/// </summary>
public sealed class CredentialSyncSource(ICredentialRepository credentials) : ISyncEntitySource
{
    public int SchemaVersion => 1;

    public IReadOnlyList<string> EntityTypes => [SyncEntityTypes.Credential];

    public bool Handles(string entityType) => entityType == SyncEntityTypes.Credential;

    public async Task<IReadOnlyList<string>> ListEntityIdsAsync(string entityType, CancellationToken ct = default) =>
        [.. (await credentials.GetAllAsync(ct)).Select(c => c.Id.ToString())];

    public async Task<byte[]?> GetPlaintextAsync(string entityType, string entityId, CancellationToken ct = default)
    {
        var credential = await credentials.GetByIdAsync(Guid.Parse(entityId), ct);
        return credential is null
            ? null
            : SyncSerializer.Serialize(CredentialMetadata.From(credential), SchemaVersion);
    }

    public async Task ApplyAsync(
        string entityType, string entityId, byte[]? plaintext, bool deleted, int schemaVersion,
        CancellationToken ct = default)
    {
        var id = Guid.Parse(entityId);
        if (deleted)
        {
            await credentials.DeleteAsync(id, ct);
            return;
        }

        var (_, metadata) = SyncSerializer.Deserialize<CredentialMetadata>(plaintext!);
        var existing = await credentials.GetByIdAsync(id, ct);
        var credential = metadata.ToCredential(id, existing);
        if (existing is null)
        {
            await credentials.AddAsync(credential, ct);
        }
        else
        {
            await credentials.UpdateAsync(credential, ct);
        }
    }

    /// <summary>凭据元数据冲突的 LWW 依据：内容更新时间的「最后一笔」为准。</summary>
    public DateTimeOffset? ReadContentModifiedAt(byte[] plaintext, int schemaVersion)
    {
        var (_, metadata) = SyncSerializer.Deserialize<CredentialMetadata>(plaintext);
        return metadata.UpdatedAt;
    }

    /// <summary>credential 同步 Payload：不含任何本机绑定的 Secret 引用。</summary>
    public sealed record CredentialMetadata(
        string Name,
        CredentialType Type,
        string Username,
        string Domain,
        string Description,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt)
    {
        public static CredentialMetadata From(Credential c) =>
            new(c.Name, c.Type, c.Username, c.Domain, c.Description, c.CreatedAt, c.UpdatedAt);

        public Credential ToCredential(Guid id, Credential? existing) => new()
        {
            Id = id,
            Name = Name,
            Type = Type,
            Username = Username,
            Domain = Domain,
            Description = Description,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            SecretReference = existing?.SecretReference,
            KeyReference = existing?.KeyReference,
        };
    }
}
