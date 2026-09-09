using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Application.Services;

/// <summary>
/// 连接资产应用服务。承担连接的增删改查与检索逻辑，
/// 不直接操作协议实现，也不接触 Secret。
/// </summary>
public sealed class ConnectionService(
    IConnectionRepository connections,
    IGroupRepository groups,
    ITagRepository tags,
    ISyncChangeTracker? syncTracker = null)
{
    private readonly ISyncChangeTracker _sync = syncTracker ?? NoOpSyncChangeTracker.Instance;

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
        await _sync.TrackUpsertAsync(SyncEntityTypes.Connection, profile.Id.ToString(), ct);
        return profile;
    }

    public async Task UpdateAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        Validate(profile);
        await connections.UpdateAsync(profile, ct);
        await _sync.TrackUpsertAsync(SyncEntityTypes.Connection, profile.Id.ToString(), ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await connections.DeleteAsync(id, ct);
        await _sync.TrackDeleteAsync(SyncEntityTypes.Connection, id.ToString(), ct);
    }

    /// <summary>复制一个连接。副本名称自动追加「- 副本」，不继承收藏状态与连接历史。</summary>
    public async Task<ConnectionProfile> DuplicateAsync(Guid id, CancellationToken ct = default)
    {
        var source = await connections.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException("要复制的连接不存在。");

        var copy = source.Clone($"{source.Name} - 副本");
        await connections.AddAsync(copy, ct);
        await _sync.TrackUpsertAsync(SyncEntityTypes.Connection, copy.Id.ToString(), ct);
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
        await _sync.TrackUpsertAsync(SyncEntityTypes.Connection, profile.Id.ToString(), ct);
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

    // ── 标签 CRUD ────────────────────────────────────────────────
    // 标签模型本身早就支持任意名称/颜色，只是此前没有从 UI 接出创建/编辑入口，
    // 用户看到的是「已有那几个标签、名称和颜色都是固定的」。这里补上服务层方法，
    // 校验规则参照分组（GroupService）：名称去空白、不能为空、同名不允许重复。

    public async Task<Tag> CreateTagAsync(string name, string color, string description, CancellationToken ct = default)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("标签名称不能为空。", nameof(name));
        }

        var all = await tags.GetAllAsync(ct);
        if (all.Any(t => string.Equals(t.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"已存在同名标签「{trimmed}」。");
        }

        var tag = new Tag
        {
            Name = trimmed,
            Color = ValidateColor(color),
            Description = (description ?? string.Empty).Trim(),
        };

        await tags.AddAsync(tag, ct);
        await _sync.TrackUpsertAsync(SyncEntityTypes.Tag, tag.Id.ToString(), ct);
        return tag;
    }

    public async Task UpdateTagAsync(Guid id, string name, string color, string description, CancellationToken ct = default)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("标签名称不能为空。", nameof(name));
        }

        var all = await tags.GetAllAsync(ct);
        var tag = all.FirstOrDefault(t => t.Id == id)
            ?? throw new InvalidOperationException("标签不存在。");

        if (all.Any(t => t.Id != id && string.Equals(t.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"已存在同名标签「{trimmed}」。");
        }

        tag.Name = trimmed;
        tag.Color = ValidateColor(color);
        tag.Description = (description ?? string.Empty).Trim();
        await tags.UpdateAsync(tag, ct);
        await _sync.TrackUpsertAsync(SyncEntityTypes.Tag, tag.Id.ToString(), ct);
    }

    /// <summary>删除标签：连接上的引用由外键 CASCADE 一并清理，连接本身不受影响。</summary>
    public async Task DeleteTagAsync(Guid id, CancellationToken ct = default)
    {
        await tags.DeleteAsync(id, ct);
        await _sync.TrackDeleteAsync(SyncEntityTypes.Tag, id.ToString(), ct);
    }

    private static string ValidateColor(string color)
    {
        var trimmed = (color ?? string.Empty).Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^#[0-9A-Fa-f]{6}$"))
        {
            throw new ArgumentException("标签颜色必须是 #RRGGBB 格式。", nameof(color));
        }

        return trimmed;
    }

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
