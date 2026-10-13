using System.Text.Json;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Cloud;

namespace RemoteFlow.Infrastructure.Sync;

/// <summary>
/// 把同步实体（类型 + Id）翻成给用户看的名称，用于冲突列表等 UI，
/// 避免只显示裸 GUID 让用户无法判断在问谁。
/// </summary>
public sealed class SyncEntityLabeler(
    IConnectionRepository connections,
    ICredentialRepository credentials,
    IGroupRepository groups,
    ITagRepository tags)
{
    /// <summary>实体类型的中文名（用于分组标题 / 冲突类型）。</summary>
    public static string TypeLabel(string entityType) => entityType switch
    {
        SyncEntityTypes.Connection => "连接",
        SyncEntityTypes.Credential => "凭据",
        SyncEntityTypes.CredentialSecret => "密码",
        SyncEntityTypes.Group => "分组",
        SyncEntityTypes.Tag => "标签",
        _ => entityType,
    };

    /// <summary>取实体的人类可读名称；找不到时回退为短 Id。</summary>
    public async Task<string> DescribeAsync(string entityType, string entityId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(entityId, out var id))
        {
            return entityId;
        }

        switch (entityType)
        {
            case SyncEntityTypes.Connection:
            {
                var connection = await connections.GetByIdAsync(id, ct);
                return connection is null ? "（已删除的连接）" : $"{connection.Name}（{connection.Host}）";
            }

            case SyncEntityTypes.Credential:
            {
                var credential = await credentials.GetByIdAsync(id, ct);
                return credential is null ? "（已删除的凭据）" : credential.Name;
            }

            case SyncEntityTypes.Group:
            {
                var group = (await groups.GetAllAsync(ct)).FirstOrDefault(g => g.Id == id);
                return group is null ? "（已删除的分组）" : group.Name;
            }

            case SyncEntityTypes.Tag:
            {
                var tag = (await tags.GetAllAsync(ct)).FirstOrDefault(t => t.Id == id);
                return tag is null ? "（已删除的标签）" : tag.Name;
            }

            case SyncEntityTypes.CredentialSecret:
            {
                // 密码本体与其凭据同 Id —— 用凭据名标识，并明确是「密码」。
                var credential = await credentials.GetByIdAsync(id, ct);
                return credential is null ? "（已删除凭据的密码）" : $"{credential.Name} 的密码";
            }

            default:
                return id.ToString("D")[..8];
        }
    }

    /// <summary>
    /// 从已解密的 Payload 里取实体名称（本地已被删除、只能靠云端密文识别时用）。
    /// 解析失败返回 null，由调用方回退。
    /// </summary>
    public static string? DescribePayload(string entityType, byte[] plaintext)
    {
        try
        {
            return entityType switch
            {
                SyncEntityTypes.Connection => SyncSerializer.Deserialize<Core.Models.ConnectionProfile>(plaintext) is var (_, c)
                    ? $"{c.Name}（{c.Host}）" : null,
                SyncEntityTypes.Credential => SyncSerializer.Deserialize<Sources.CredentialSyncSource.CredentialMetadata>(plaintext) is var (_, k)
                    ? k.Name : null,
                SyncEntityTypes.Group => SyncSerializer.Deserialize<Core.Models.ConnectionGroup>(plaintext) is var (_, g)
                    ? g.Name : null,
                SyncEntityTypes.Tag => SyncSerializer.Deserialize<Core.Models.Tag>(plaintext) is var (_, t)
                    ? t.Name : null,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
