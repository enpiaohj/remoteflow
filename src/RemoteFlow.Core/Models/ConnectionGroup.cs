namespace RemoteFlow.Core.Models;

/// <summary>
/// 连接分组——用户自己的连接组织目录，支持多级嵌套（如 公司 → 生产 → DC01）。
/// <para>
/// 职责固定：<b>Group = 放在哪里</b>；协议 / 操作系统只是连接属性，不再作为分组。
/// 全应用只有一个 Group 模型，不引入「系统分组 / 协议分组 / 标签分组 / 自动分组」。
/// </para>
/// </summary>
public sealed class ConnectionGroup
{
    /// <summary>系统兜底分组「未分组」的固定 Id。不可删除、不可重命名，无连接时隐藏。</summary>
    public static readonly Guid UngroupedId = new("00000000-0000-0000-0000-0000000000FF");

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>父分组 Id。null 表示根级分组。</summary>
    public Guid? ParentId { get; set; }

    /// <summary>同级排序值。</summary>
    public int SortOrder { get; set; }

    /// <summary>分组图标标识（UI 用，非必需）。</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>
    /// 是否系统分组。当前唯一的系统分组是「未分组」：不可删除、不可重命名、不可移动。
    /// 必须显式区分，不允许只按名称字符串判断。
    /// </summary>
    public bool IsSystem { get; set; }

    /// <summary>是否默认新建连接分组。仅用户分组可为默认；至多一个，0 个 = 回落未分组。</summary>
    public bool IsDefault { get; set; }

    /// <summary>是否受保护 / 锁定（仅默认组有意义）。系统组保护由 GroupService 强制，不依赖本列。</summary>
    public bool IsProtected { get; set; }
}

/// <summary>
/// 标签。用于跨分组的多维筛选，一个连接可拥有多个标签。
/// </summary>
public sealed class Tag
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>标签色（#RRGGBB）。用于 UI 区分，不单独承担信息传达职责。</summary>
    public string Color { get; set; } = "#0F6CBD";
}
