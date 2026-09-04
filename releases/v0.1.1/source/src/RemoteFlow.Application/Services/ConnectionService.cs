using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Application.Services;

/// <summary>
/// 连接资产应用服务。承担连接的增删改查与检索逻辑，
/// 不直接操作协议实现，也不接触 Secret。
/// </summary>
public sealed class ConnectionService(
    IConnectionRepository connections,
    IGroupRepository groups,
    ITagRepository tags)
{
    public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken ct = default)
        => connections.GetAllAsync(ct);

    public Task<ConnectionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => connections.GetByIdAsync(id, ct);

    public Task<IReadOnlyList<ConnectionGroup>> GetGroupsAsync(CancellationToken ct = default)
        => groups.GetAllAsync(ct);

    public Task<IReadOnlyList<Tag>> GetTagsAsync(CancellationToken ct = default)
        => tags.GetAllAsync(ct);

    public async Task<ConnectionProfile> CreateAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        Validate(profile);

        profile.CreatedAt = DateTimeOffset.Now;
        profile.UpdatedAt = profile.CreatedAt;

        await connections.AddAsync(profile, ct);
        return profile;
    }

    public async Task UpdateAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        Validate(profile);
        await connections.UpdateAsync(profile, ct);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
        => connections.DeleteAsync(id, ct);

    /// <summary>复制一个连接。副本名称自动追加「- 副本」，不继承收藏状态与连接历史。</summary>
    public async Task<ConnectionProfile> DuplicateAsync(Guid id, CancellationToken ct = default)
    {
        var source = await connections.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException("要复制的连接不存在。");

        var copy = source.Clone($"{source.Name} - 副本");
        await connections.AddAsync(copy, ct);
        return copy;
    }

    public async Task SetFavoriteAsync(Guid id, bool favorite, CancellationToken ct = default)
    {
        var profile = await connections.GetByIdAsync(id, ct);
        if (profile is null)
        {
            return;
        }

        profile.Favorite = favorite;
        await connections.UpdateAsync(profile, ct);
    }

    /// <summary>
    /// 删除凭据前的影响评估：返回仍在引用该凭据的连接数量。
    /// UI 应据此提示用户，而不是静默解除引用。
    /// </summary>
    public Task<int> CountConnectionsUsingCredentialAsync(Guid credentialId, CancellationToken ct = default)
        => connections.CountByCredentialAsync(credentialId, ct);

    /// <summary>删除分组前的影响评估：返回该分组下的直属连接数量。</summary>
    public Task<int> CountConnectionsInGroupAsync(Guid groupId, CancellationToken ct = default)
        => connections.CountByGroupAsync(groupId, ct);

    private static void Validate(ConnectionProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new ArgumentException("连接名称不能为空。", nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.Host))
        {
            throw new ArgumentException("主机地址不能为空。", nameof(profile));
        }

        if (profile.Port is < 1 or > 65535)
        {
            throw new ArgumentException("端口必须在 1~65535 之间。", nameof(profile));
        }
    }
}
