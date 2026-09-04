namespace RemoteFlow.Core.Models;

/// <summary>
/// 分组。用于树形组织连接资产，支持多级嵌套（如 客户A → AD → DC01）。
/// </summary>
public sealed class ConnectionGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>父分组 Id。null 表示根级分组。</summary>
    public Guid? ParentId { get; set; }

    /// <summary>同级排序值。</summary>
    public int SortOrder { get; set; }

    /// <summary>分组图标标识（UI 用，非必需）。</summary>
    public string Icon { get; set; } = string.Empty;
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
