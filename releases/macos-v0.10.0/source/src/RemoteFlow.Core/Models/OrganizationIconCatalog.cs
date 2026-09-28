namespace RemoteFlow.Core.Models;

/// <summary>可持久化组织图标的稳定键与显示名。</summary>
public sealed record OrganizationIconInfo(string Key, string Name);

/// <summary>分组图标目录。内置分组与自定义分组使用不同集合，避免系统身份被伪装。</summary>
public static class GroupIconCatalog
{
    public const string MyDevicesKey = "GroupIcon.MyDevices";
    public const string UngroupedKey = "GroupIcon.Ungrouped";
    public const string DefaultCustomKey = "GroupIcon.Folder";
    public const string CompanyKey = "GroupIcon.Company";
    public const string DataCenterKey = "GroupIcon.DataCenter";
    public const string ServerRackKey = "GroupIcon.ServerRack";
    public const string CloudKey = "GroupIcon.Cloud";
    public const string NetworkKey = "GroupIcon.Network";
    public const string DatabaseKey = "GroupIcon.Database";
    public const string WebKey = "GroupIcon.Web";
    public const string SecurityKey = "GroupIcon.Security";
    public const string DevelopmentKey = "GroupIcon.Development";
    public const string LabKey = "GroupIcon.Lab";
    public const string ArchiveKey = "GroupIcon.Archive";

    public static IReadOnlyList<OrganizationIconInfo> CustomOptions { get; } =
    [
        new(DefaultCustomKey, "文件夹"),
        new(CompanyKey, "公司"),
        new(DataCenterKey, "数据中心"),
        new(ServerRackKey, "服务器机架"),
        new(CloudKey, "云环境"),
        new(NetworkKey, "网络"),
        new(DatabaseKey, "数据库"),
        new(WebKey, "Web 服务"),
        new(SecurityKey, "安全"),
        new(DevelopmentKey, "开发"),
        new(LabKey, "实验室"),
        new(ArchiveKey, "归档"),
    ];

    public static IReadOnlyList<OrganizationIconInfo> All { get; } =
    [
        new(MyDevicesKey, "我的设备"),
        new(UngroupedKey, "未分组"),
        .. CustomOptions,
    ];

    private static readonly HashSet<string> CustomKeys =
        CustomOptions.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);

    public static string NormalizeCustom(string? key)
        => key is not null && CustomKeys.Contains(key) ? key : DefaultCustomKey;
}

/// <summary>标签图标目录。标签颜色独立保存，图标只承担语义。</summary>
public static class TagIconCatalog
{
    public const string DefaultKey = "TagIcon.Tag";
    public const string ProductionKey = "TagIcon.Production";
    public const string TestKey = "TagIcon.Test";
    public const string ImportantKey = "TagIcon.Important";
    public const string SecurityKey = "TagIcon.Security";
    public const string LocationKey = "TagIcon.Location";
    public const string ProjectKey = "TagIcon.Project";
    public const string TeamKey = "TagIcon.Team";
    public const string NetworkKey = "TagIcon.Network";
    public const string MaintenanceKey = "TagIcon.Maintenance";

    public static IReadOnlyList<OrganizationIconInfo> All { get; } =
    [
        new(DefaultKey, "标签"),
        new(ProductionKey, "生产"),
        new(TestKey, "测试"),
        new(ImportantKey, "重要"),
        new(SecurityKey, "安全"),
        new(LocationKey, "位置"),
        new(ProjectKey, "项目"),
        new(TeamKey, "团队"),
        new(NetworkKey, "网络"),
        new(MaintenanceKey, "维护"),
    ];

    private static readonly HashSet<string> Keys =
        All.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);

    public static string Normalize(string? key)
        => key is not null && Keys.Contains(key) ? key : DefaultKey;
}
