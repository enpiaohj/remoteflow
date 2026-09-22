using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Application.Services;

/// <summary>分组树的一个节点。子节点已按 <see cref="ConnectionGroup.SortOrder"/> 排好序。</summary>
public sealed class GroupTreeNode
{
    public required ConnectionGroup Group { get; init; }
    public List<GroupTreeNode> Children { get; } = [];
}

/// <summary>
/// 分组应用服务。分组是「用户自己的连接组织目录」——
/// 创建 / 重命名 / 删除 / 移动 / 取树，以及第一次启动时的默认分组种子。
/// <para>
/// 唯一的系统分组是「未分组」（<see cref="ConnectionGroup.UngroupedId"/>）：不可删除、
/// 不可重命名、不可移动；新连接未指定分组、或删除普通分组时组内连接都归入这里。
/// </para>
/// </summary>
public sealed class GroupService(
    IGroupRepository groups, IConnectionRepository connections, ISyncChangeTracker? syncTracker = null)
{
    private readonly ISyncChangeTracker _sync = syncTracker ?? NoOpSyncChangeTracker.Instance;

    /// <summary>首次启动时自动创建的默认用户分组名。</summary>
    public const string DefaultGroupName = "我的设备";

    private Task TrackGroupAsync(Guid id, CancellationToken ct) =>
        _sync.TrackUpsertAsync(SyncEntityTypes.Group, id.ToString(), ct);

    public Task<IReadOnlyList<ConnectionGroup>> GetAllAsync(CancellationToken ct = default)
        => groups.GetAllAsync(ct);

    /// <summary>取分组树（不含「未分组」——它由 UI 按需单独呈现）。</summary>
    public async Task<IReadOnlyList<GroupTreeNode>> GetTreeAsync(CancellationToken ct = default)
    {
        var all = await groups.GetAllAsync(ct);
        return BuildTree(all.Where(g => !g.IsSystem));
    }

    /// <summary>从扁平列表构建树，按 SortOrder 再按名称排序。</summary>
    public static IReadOnlyList<GroupTreeNode> BuildTree(IEnumerable<ConnectionGroup> groups)
    {
        var nodes = groups.ToDictionary(g => g.Id, g => new GroupTreeNode { Group = g });
        var roots = new List<GroupTreeNode>();

        foreach (var node in nodes.Values)
        {
            if (node.Group.ParentId is { } parentId && nodes.TryGetValue(parentId, out var parent))
            {
                parent.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        Sort(roots);
        return roots;
    }

    private static void Sort(List<GroupTreeNode> level)
    {
        level.Sort((a, b) => a.Group.SortOrder != b.Group.SortOrder
            ? a.Group.SortOrder.CompareTo(b.Group.SortOrder)
            : string.Compare(a.Group.Name, b.Group.Name, StringComparison.CurrentCultureIgnoreCase));

        foreach (var node in level)
        {
            Sort(node.Children);
        }
    }

    /// <summary>
    /// 保证「未分组」存在，并返回默认新建连接分组（is_default=true 的用户组）。
    /// <para>
    /// <paramref name="createIfEmpty"/> = true（首启，App 层按 <c>DefaultGroupSeedDone</c> 决定）：
    /// 库里没有默认组时，<b>始终新建（或复用同名的）「我的设备」</b>作为默认 + 保护，
    /// <b>不借用户已有分组顶上</b>——老用户升级也照建，现有分组一个不动。
    /// </para>
    /// <para>
    /// <paramref name="createIfEmpty"/> = false（已种过）：无默认组时直接返回 null——
    /// 说明用户自己删掉了默认组（删默认组时已强制改选或回落未分组），不再自动复活。
    /// </para>
    /// 返回 null 表示无默认组（新连接回落未分组）。
    /// </summary>
    public async Task<Guid?> EnsureSeedAsync(CancellationToken ct = default, bool createIfEmpty = true)
    {
        var all = await groups.GetAllAsync(ct);

        if (all.All(g => g.Id != ConnectionGroup.UngroupedId))
        {
            await groups.AddAsync(new ConnectionGroup
            {
                Id = ConnectionGroup.UngroupedId,
                Name = "未分组",
                SortOrder = int.MaxValue,
                // 系统组保护由 GroupService 按 IsSystem 强制，不需要也不能依赖本列。
                IsSystem = true
            }, ct);
        }

        // 之后的判断都基于同一份快照：上面的 Add 只影响系统「未分组」，
        // 不会改变「非系统组」集合，故无需再查库。
        var existingDefault = all.FirstOrDefault(g => !g.IsSystem && g.IsDefault);
        if (existingDefault is not null)
        {
            return existingDefault.Id;
        }

        if (!createIfEmpty)
        {
            return null;
        }

        // 首次为该库补默认组。用户可能碰巧已有一个叫「我的设备」的普通组——复用它，别造重名。
        var existingMine = all.FirstOrDefault(g => !g.IsSystem
            && string.Equals(g.Name, DefaultGroupName, StringComparison.CurrentCultureIgnoreCase));
        if (existingMine is not null)
        {
            existingMine.IsDefault = true;
            existingMine.IsProtected = true;
            await groups.UpdateAsync(existingMine, ct);
            return existingMine.Id;
        }

        // 新建「我的设备」，排在所有现有分组之前，现有分组不动。
        var userGroups = all.Where(g => !g.IsSystem).ToList();
        var defaultGroup = new ConnectionGroup
        {
            Name = DefaultGroupName,
            SortOrder = userGroups.Count > 0 ? userGroups.Min(g => g.SortOrder) - 1 : 0,
            IsDefault = true,
            IsProtected = true
        };
        await groups.AddAsync(defaultGroup, ct);
        return defaultGroup.Id;
    }

    public async Task<ConnectionGroup> CreateAsync(string name, Guid? parentId, CancellationToken ct = default)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("分组名称不能为空。", nameof(name));
        }

        var all = await groups.GetAllAsync(ct);

        if (parentId is { } pid && all.All(g => g.Id != pid))
        {
            throw new InvalidOperationException("上级分组不存在。");
        }

        if (HasSiblingNamed(all, parentId, trimmed, excludeId: null))
        {
            throw new InvalidOperationException($"同一层级下已存在分组「{trimmed}」。");
        }

        var nextOrder = all.Where(g => g.ParentId == parentId && !g.IsSystem)
            .Select(g => g.SortOrder)
            .DefaultIfEmpty(-1)
            .Max() + 1;

        var group = new ConnectionGroup { Name = trimmed, ParentId = parentId, SortOrder = nextOrder };
        await groups.AddAsync(group, ct);
        await TrackGroupAsync(group.Id, ct);
        return group;
    }

    public async Task RenameAsync(Guid id, string newName, CancellationToken ct = default)
    {
        var trimmed = (newName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("分组名称不能为空。", nameof(newName));
        }

        var all = await groups.GetAllAsync(ct);
        var group = all.FirstOrDefault(g => g.Id == id)
            ?? throw new InvalidOperationException("分组不存在。");

        if (group.IsSystem)
        {
            throw new InvalidOperationException("系统分组不能重命名。");
        }

        if (group.IsProtected)
        {
            throw new InvalidOperationException("默认分组受保护，无法重命名。请先在「设置 → 常规 → 分组」关闭保护。");
        }

        if (HasSiblingNamed(all, group.ParentId, trimmed, excludeId: id))
        {
            throw new InvalidOperationException($"同一层级下已存在分组「{trimmed}」。");
        }

        group.Name = trimmed;
        await groups.UpdateAsync(group, ct);
        await TrackGroupAsync(group.Id, ct);
    }

    /// <summary>删除分组：组内连接 → 未分组，子分组 → 被删除分组的父级（§16）。</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var all = await groups.GetAllAsync(ct);
        var group = all.FirstOrDefault(g => g.Id == id)
            ?? throw new InvalidOperationException("分组不存在。");

        if (group.IsSystem)
        {
            throw new InvalidOperationException("系统分组不能删除。");
        }

        if (group.IsProtected)
        {
            throw new InvalidOperationException("默认分组受保护，无法删除。请先在「设置 → 常规 → 分组」关闭保护。");
        }

        // 「未分组」以 group_id = null 表示；传 null 让仓储把组内连接置空。
        await groups.DeleteAsync(id, moveConnectionsTo: null, ct);
        await _sync.TrackDeleteAsync(SyncEntityTypes.Group, id.ToString(), ct);
    }

    /// <summary>把分组移动到新的父级（null = 根级）。</summary>
    public async Task MoveAsync(Guid id, Guid? newParentId, CancellationToken ct = default)
    {
        if (id == newParentId)
        {
            throw new InvalidOperationException("不能把分组移动到自身。");
        }

        var all = await groups.GetAllAsync(ct);
        var group = all.FirstOrDefault(g => g.Id == id)
            ?? throw new InvalidOperationException("分组不存在。");

        if (group.IsSystem)
        {
            throw new InvalidOperationException("系统分组不能移动。");
        }

        if (group.IsProtected)
        {
            throw new InvalidOperationException("默认分组受保护，无法移动。请先在「设置 → 常规 → 分组」关闭保护。");
        }

        if (newParentId is { } target)
        {
            if (all.All(g => g.Id != target))
            {
                throw new InvalidOperationException("目标分组不存在。");
            }

            if (IsDescendant(all, ancestorId: id, candidateId: target))
            {
                throw new InvalidOperationException("不能把分组移动到它自己的子分组下。");
            }
        }

        if (HasSiblingNamed(all, newParentId, group.Name, excludeId: id))
        {
            throw new InvalidOperationException($"目标层级下已存在分组「{group.Name}」。");
        }

        group.ParentId = newParentId;
        await groups.UpdateAsync(group, ct);
        await TrackGroupAsync(group.Id, ct);
    }

    /// <summary>把连接移动到指定分组（null / 未分组 都归入「未分组」）。</summary>
    public async Task MoveConnectionAsync(Guid connectionId, Guid? groupId, CancellationToken ct = default)
    {
        var profile = await connections.GetByIdAsync(connectionId, ct)
            ?? throw new InvalidOperationException("连接不存在。");

        profile.GroupId = groupId == ConnectionGroup.UngroupedId ? null : groupId;
        profile.UpdatedAt = DateTimeOffset.Now;
        await connections.UpdateAsync(profile, ct);
        await _sync.TrackUpsertAsync(SyncEntityTypes.Connection, profile.Id.ToString(), ct);
    }

    /// <summary>更新同级排序值（拖拽排序落库）。</summary>
    public async Task SetSortOrderAsync(Guid id, int sortOrder, CancellationToken ct = default)
    {
        var all = await groups.GetAllAsync(ct);
        var group = all.FirstOrDefault(g => g.Id == id);
        if (group is null || group.IsSystem)
        {
            return;
        }

        group.SortOrder = sortOrder;
        await groups.UpdateAsync(group, ct);
        await TrackGroupAsync(group.Id, ct);
    }

    /// <summary>返回默认新建连接分组（is_default=true 的非系统组）；无则 null。</summary>
    public async Task<ConnectionGroup?> GetDefaultGroupAsync(CancellationToken ct = default)
    {
        var all = await groups.GetAllAsync(ct);
        return all.FirstOrDefault(g => !g.IsSystem && g.IsDefault);
    }

    /// <summary>
    /// 把默认身份移给另一普通组。若当前存在受保护默认组则拒绝（防止借换默认绕过保护）；
    /// 成功后旧默认组 is_default/is_protected 均清 0，目标组 is_default=true 且 is_protected=false。
    /// </summary>
    public async Task SetDefaultAsync(Guid groupId, CancellationToken ct = default)
    {
        var all = await groups.GetAllAsync(ct);
        var target = all.FirstOrDefault(g => g.Id == groupId)
            ?? throw new InvalidOperationException("分组不存在。");
        if (target.IsSystem)
        {
            throw new InvalidOperationException("系统分组不能作为默认分组。");
        }

        var currentDefault = all.FirstOrDefault(g => !g.IsSystem && g.IsDefault);
        if (currentDefault is { IsProtected: true })
        {
            throw new InvalidOperationException("当前默认分组受保护，请先在「设置 → 常规 → 分组」关闭保护后再更换默认分组。");
        }

        if (currentDefault is not null && currentDefault.Id != groupId)
        {
            currentDefault.IsDefault = false;
            currentDefault.IsProtected = false;
            await groups.UpdateAsync(currentDefault, ct);
            await TrackGroupAsync(currentDefault.Id, ct);
        }

        target.IsDefault = true;
        target.IsProtected = false;
        await groups.UpdateAsync(target, ct);
        await TrackGroupAsync(target.Id, ct);
    }

    /// <summary>开 / 关当前默认分组的保护。无默认组时无操作。</summary>
    public async Task SetDefaultProtectionAsync(bool isProtected, CancellationToken ct = default)
    {
        // GetDefaultGroupAsync 已过滤系统组，无需再判 IsSystem。
        var currentDefault = await GetDefaultGroupAsync(ct);
        if (currentDefault is null)
        {
            return;
        }

        currentDefault.IsProtected = isProtected;
        await groups.UpdateAsync(currentDefault, ct);
        await TrackGroupAsync(currentDefault.Id, ct);
    }

    private static bool HasSiblingNamed(IEnumerable<ConnectionGroup> all, Guid? parentId, string name, Guid? excludeId)
        => all.Any(g => g.ParentId == parentId
            && g.Id != excludeId
            && !g.IsSystem
            && string.Equals(g.Name, name, StringComparison.CurrentCultureIgnoreCase));

    private static bool IsDescendant(IReadOnlyList<ConnectionGroup> all, Guid ancestorId, Guid candidateId)
    {
        var current = all.FirstOrDefault(g => g.Id == candidateId);
        var guard = 0;
        while (current is not null && guard++ < 64)
        {
            if (current.ParentId == ancestorId)
            {
                return true;
            }
            current = current.ParentId is { } pid ? all.FirstOrDefault(g => g.Id == pid) : null;
        }
        return false;
    }
}
