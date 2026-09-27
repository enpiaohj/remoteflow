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
    public const string DefaultGroupName = ConnectionGroup.MyDevicesName;

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
    /// 保证两个内置分组语义成立，并返回永久默认分组“我的设备”。
    /// <para>
    /// 升级旧库时不更换实体 Id：优先沿用已标记内置的组，其次沿用旧默认组，再次复用同名组，
    /// 最后才新建。这样连接与子分组引用无需迁移。<paramref name="createIfEmpty"/> 仅为旧调用兼容，
    /// 新规则下“我的设备”始终存在，因此不再允许通过 false 阻止创建。
    /// </para>
    /// </summary>
    public async Task<Guid?> EnsureSeedAsync(CancellationToken ct = default, bool createIfEmpty = true)
    {
        var all = (await groups.GetAllAsync(ct)).ToList();

        if (all.All(g => g.Id != ConnectionGroup.UngroupedId))
        {
            var ungrouped = new ConnectionGroup
            {
                Id = ConnectionGroup.UngroupedId,
                Name = "未分组",
                SortOrder = int.MaxValue,
                Icon = GroupIconCatalog.UngroupedKey,
                IsSystem = true
            };
            await groups.AddAsync(ungrouped, ct);
            all.Add(ungrouped);
        }
        else if (all.First(g => g.Id == ConnectionGroup.UngroupedId) is { } ungrouped
                 && (ungrouped.Name != "未分组"
                     || ungrouped.Icon != GroupIconCatalog.UngroupedKey
                     || !ungrouped.IsSystem))
        {
            ungrouped.Name = "未分组";
            ungrouped.Icon = GroupIconCatalog.UngroupedKey;
            ungrouped.IsSystem = true;
            ungrouped.IsBuiltIn = false;
            ungrouped.IsDefault = false;
            ungrouped.IsProtected = true;
            ungrouped.ParentId = null;
            ungrouped.SortOrder = int.MaxValue;
            await groups.UpdateAsync(ungrouped, ct);
        }

        var userGroups = all.Where(g => !g.IsSystem).ToList();

        // 同一层级内按 Id 排序取第一个：多台设备各自种子的“我的设备”经云同步汇合后，
        // 各端独立运行本方法也会选中同一个 Id，避免互相降级导致同步来回翻转。
        var byId = userGroups.OrderBy(g => g.Id.ToString("D"), StringComparer.Ordinal).ToList();
        var builtIn = byId.FirstOrDefault(g => g.IsBuiltIn)
            ?? byId.FirstOrDefault(g => g.IsDefault)
            ?? byId.FirstOrDefault(g =>
                string.Equals(g.Name, DefaultGroupName, StringComparison.CurrentCultureIgnoreCase));

        if (builtIn is null)
        {
            builtIn = new ConnectionGroup
            {
                Name = DefaultGroupName,
                SortOrder = userGroups.Count > 0 ? userGroups.Min(g => g.SortOrder) - 1 : 0,
                Icon = GroupIconCatalog.MyDevicesKey,
                IsBuiltIn = true,
                IsDefault = true,
                IsProtected = true
            };
            await groups.AddAsync(builtIn, ct);
            await TrackGroupAsync(builtIn.Id, ct);
            userGroups.Add(builtIn);
        }

        foreach (var group in userGroups)
        {
            var isBuiltIn = group.Id == builtIn.Id;
            var changed = false;

            if (group.IsBuiltIn != isBuiltIn)
            {
                group.IsBuiltIn = isBuiltIn;
                changed = true;
            }
            if (group.IsDefault != isBuiltIn)
            {
                group.IsDefault = isBuiltIn;
                changed = true;
            }
            if (group.IsProtected != isBuiltIn)
            {
                group.IsProtected = isBuiltIn;
                changed = true;
            }

            if (isBuiltIn)
            {
                if (group.Name != DefaultGroupName)
                {
                    group.Name = DefaultGroupName;
                    changed = true;
                }
                if (group.Icon != GroupIconCatalog.MyDevicesKey)
                {
                    group.Icon = GroupIconCatalog.MyDevicesKey;
                    changed = true;
                }
                if (group.ParentId is not null)
                {
                    group.ParentId = null;
                    changed = true;
                }
            }
            else
            {
                var normalizedIcon = GroupIconCatalog.NormalizeCustom(group.Icon);
                if (group.Icon != normalizedIcon)
                {
                    group.Icon = normalizedIcon;
                    changed = true;
                }
            }

            if (changed)
            {
                await groups.UpdateAsync(group, ct);
                await TrackGroupAsync(group.Id, ct);
            }
        }

        await MergeDuplicateMyDevicesAsync(builtIn, userGroups, ct);

        return builtIn.Id;
    }

    /// <summary>
    /// 多台设备各自种子过“我的设备”并经云同步汇合时，根级会出现同名普通组。
    /// 按“只有一个内置我的设备”规则并入：连接与子分组移入内置组（子分组同名时加序号），
    /// 再删除已清空的重复组。连接只改归属、绝不删除；每一步都登记同步，其它设备随之收敛。
    /// </summary>
    private async Task MergeDuplicateMyDevicesAsync(
        ConnectionGroup builtIn, List<ConnectionGroup> userGroups, CancellationToken ct)
    {
        var duplicates = userGroups
            .Where(g => g.Id != builtIn.Id
                        && g.ParentId is null
                        && string.Equals(g.Name.Trim(), DefaultGroupName, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        if (duplicates.Count == 0)
        {
            return;
        }

        var allConnections = await connections.GetAllAsync(ct);
        foreach (var duplicate in duplicates)
        {
            foreach (var profile in allConnections.Where(c => c.GroupId == duplicate.Id))
            {
                profile.GroupId = builtIn.Id;
                profile.UpdatedAt = DateTimeOffset.Now;
                await connections.UpdateAsync(profile, ct);
                await _sync.TrackUpsertAsync(SyncEntityTypes.Connection, profile.Id.ToString(), ct);
            }

            foreach (var child in userGroups.Where(g => g.ParentId == duplicate.Id).ToList())
            {
                child.ParentId = builtIn.Id;
                child.Name = UniqueSiblingName(userGroups, builtIn.Id, child.Name, child.Id);
                await groups.UpdateAsync(child, ct);
                await TrackGroupAsync(child.Id, ct);
            }

            // 已清空：仓储删除时不会再有连接需要迁移。
            await groups.DeleteAsync(duplicate.Id, moveConnectionsTo: builtIn.Id, ct);
            await _sync.TrackDeleteAsync(SyncEntityTypes.Group, duplicate.Id.ToString(), ct);
            userGroups.Remove(duplicate);
        }
    }

    private static string UniqueSiblingName(
        IEnumerable<ConnectionGroup> all, Guid parentId, string name, Guid selfId)
    {
        var candidate = name;
        for (var n = 2; HasSiblingNamed(all, parentId, candidate, selfId); n++)
        {
            candidate = $"{name} ({n})";
        }

        return candidate;
    }

    public Task<ConnectionGroup> CreateAsync(
        string name, Guid? parentId, CancellationToken ct = default)
        => CreateAsync(name, parentId, GroupIconCatalog.DefaultCustomKey, ct);

    public async Task<ConnectionGroup> CreateAsync(
        string name, Guid? parentId, string? icon, CancellationToken ct = default)
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

        if (parentId is { } systemParent && all.Any(g => g.Id == systemParent && g.IsSystem))
        {
            throw new InvalidOperationException("「未分组」是系统内置分组，不能在其下创建子分组。");
        }

        if (HasSiblingNamed(all, parentId, trimmed, excludeId: null))
        {
            throw new InvalidOperationException($"同一层级下已存在分组「{trimmed}」。");
        }

        var nextOrder = all.Where(g => g.ParentId == parentId && !g.IsSystem)
            .Select(g => g.SortOrder)
            .DefaultIfEmpty(-1)
            .Max() + 1;

        var group = new ConnectionGroup
        {
            Name = trimmed,
            ParentId = parentId,
            SortOrder = nextOrder,
            Icon = GroupIconCatalog.NormalizeCustom(icon)
        };
        await groups.AddAsync(group, ct);
        await TrackGroupAsync(group.Id, ct);
        return group;
    }

    public Task RenameAsync(Guid id, string newName, CancellationToken ct = default)
        => UpdateAsync(id, newName, icon: null, ct: ct);

    /// <summary>更新自定义分组名称与图标。内置 / 系统分组不可编辑。</summary>
    public async Task UpdateAsync(Guid id, string newName, string? icon, CancellationToken ct = default)
    {
        var trimmed = (newName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("分组名称不能为空。", nameof(newName));
        }

        var all = await groups.GetAllAsync(ct);
        var group = all.FirstOrDefault(g => g.Id == id)
            ?? throw new InvalidOperationException("分组不存在。");

        if (group.IsSystem || group.IsBuiltIn)
        {
            throw new InvalidOperationException("内置分组不能编辑名称或图标。");
        }

        if (group.IsProtected)
        {
            throw new InvalidOperationException("受保护分组无法编辑。");
        }

        if (HasSiblingNamed(all, group.ParentId, trimmed, excludeId: id))
        {
            throw new InvalidOperationException($"同一层级下已存在分组「{trimmed}」。");
        }

        group.Name = trimmed;
        group.Icon = icon is null
            ? GroupIconCatalog.NormalizeCustom(group.Icon)
            : GroupIconCatalog.NormalizeCustom(icon);
        await groups.UpdateAsync(group, ct);
        await TrackGroupAsync(group.Id, ct);
    }

    /// <summary>删除分组：组内连接 → 未分组，子分组 → 被删除分组的父级（§16）。</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var all = await groups.GetAllAsync(ct);
        var group = all.FirstOrDefault(g => g.Id == id)
            ?? throw new InvalidOperationException("分组不存在。");

        if (group.IsSystem || group.IsBuiltIn)
        {
            throw new InvalidOperationException("内置分组不能删除。");
        }

        if (group.IsProtected)
        {
            throw new InvalidOperationException("该分组受保护，无法删除。");
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

        if (group.IsSystem || group.IsBuiltIn)
        {
            throw new InvalidOperationException("内置分组不能移动。");
        }

        if (group.IsProtected)
        {
            throw new InvalidOperationException("该分组受保护，无法移动。");
        }

        if (newParentId is { } target)
        {
            if (all.All(g => g.Id != target))
            {
                throw new InvalidOperationException("目标分组不存在。");
            }

            if (all.Any(g => g.Id == target && g.IsSystem))
            {
                throw new InvalidOperationException("「未分组」是系统内置分组，不能包含子分组。");
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
        if (group is null || group.IsSystem || group.IsBuiltIn)
        {
            return;
        }

        group.SortOrder = sortOrder;
        await groups.UpdateAsync(group, ct);
        await TrackGroupAsync(group.Id, ct);
    }

    /// <summary>返回永久默认内置分组“我的设备”。启动校正前兼容读取旧 is_default。</summary>
    public async Task<ConnectionGroup?> GetDefaultGroupAsync(CancellationToken ct = default)
    {
        var all = await groups.GetAllAsync(ct);
        return all.FirstOrDefault(g => !g.IsSystem && g.IsBuiltIn)
            ?? all.FirstOrDefault(g => !g.IsSystem && g.IsDefault);
    }

    /// <summary>默认身份已固定为“我的设备”；保留方法只为旧调用提供明确错误。</summary>
    public async Task SetDefaultAsync(Guid groupId, CancellationToken ct = default)
    {
        var target = (await groups.GetAllAsync(ct)).FirstOrDefault(g => g.Id == groupId)
            ?? throw new InvalidOperationException("分组不存在。");
        if (target.IsBuiltIn)
        {
            return;
        }

        throw new InvalidOperationException("“我的设备”是固定默认分组，不能替换。");
    }

    /// <summary>内置默认分组永久受保护；保留方法用于兼容旧设置调用。</summary>
    public async Task SetDefaultProtectionAsync(bool isProtected, CancellationToken ct = default)
    {
        if (!isProtected)
        {
            throw new InvalidOperationException("内置分组“我的设备”不能解除保护。");
        }

        await EnsureSeedAsync(ct);
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
