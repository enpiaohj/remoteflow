using AppKit;
using RemoteFlow.Core.Models;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 分组 / 标签语义图标键到 SF Symbols 的映射。与 Windows 的 OrganizationIcons.xaml 共用同一套键，
/// 共享层只存键，平台各自选择最贴近的原生符号。
/// </summary>
internal static class OrganizationStyle
{
    private static readonly IReadOnlyDictionary<string, string> GroupSymbols = new Dictionary<string, string>
    {
        ["GroupIcon.MyDevices"] = "laptopcomputer.and.iphone",
        ["GroupIcon.Ungrouped"] = "tray",
        ["GroupIcon.Folder"] = "folder.fill",
        ["GroupIcon.Company"] = "building.2.fill",
        ["GroupIcon.DataCenter"] = "server.rack",
        ["GroupIcon.ServerRack"] = "externaldrive.fill",
        ["GroupIcon.Cloud"] = "cloud.fill",
        ["GroupIcon.Network"] = "network",
        ["GroupIcon.Database"] = "cylinder.split.1x2.fill",
        ["GroupIcon.Web"] = "globe",
        ["GroupIcon.Security"] = "lock.shield.fill",
        ["GroupIcon.Development"] = "chevron.left.forwardslash.chevron.right",
        ["GroupIcon.Lab"] = "flask.fill",
        ["GroupIcon.Archive"] = "archivebox.fill",
    };

    private static readonly IReadOnlyDictionary<string, string> TagSymbols = new Dictionary<string, string>
    {
        ["TagIcon.Tag"] = "tag.fill",
        ["TagIcon.Production"] = "bolt.fill",
        ["TagIcon.Test"] = "testtube.2",
        ["TagIcon.Important"] = "star.fill",
        ["TagIcon.Security"] = "shield.fill",
        ["TagIcon.Location"] = "mappin.and.ellipse",
        ["TagIcon.Project"] = "briefcase.fill",
        ["TagIcon.Team"] = "person.2.fill",
        ["TagIcon.Network"] = "point.3.connected.trianglepath.dotted",
        ["TagIcon.Maintenance"] = "wrench.and.screwdriver.fill",
    };

    public static NSImage? GroupSymbol(string? key)
        => Symbol(GroupSymbols.TryGetValue(key ?? string.Empty, out var name) ? name : "folder.fill", "folder");

    public static NSImage? TagSymbol(string? key)
        => Symbol(TagSymbols.TryGetValue(TagIconCatalog.Normalize(key), out var name) ? name : "tag.fill", "tag");

    /// <summary>分组图标着色：与 Windows FolderBrushKey 同语义。</summary>
    public static NSColor GroupTint(string brushKey) => brushKey switch
    {
        "Brand.Default" => NSColor.SystemBlue,
        "Protocol.Vnc" => NSColor.SystemPurple,
        "Status.Success" => NSColor.SystemGreen,
        "Status.Warning" => NSColor.SystemOrange,
        _ => NSColor.SystemGray,
    };

    public static NSColor? ColorFromHex(string? hex)
    {
        var s = (hex ?? string.Empty).TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v))
        {
            return null;
        }

        return NSColor.FromRgb(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
    }

    /// <summary>构造「图标 + 名称」弹出菜单，用于 AppKit 分组 / 标签编辑器。</summary>
    public static NSPopUpButton IconPopup(
        IReadOnlyList<OrganizationIconInfo> options,
        string selectedKey,
        Func<string, NSImage?> symbol)
    {
        var popup = new NSPopUpButton(new CoreGraphics.CGRect(0, 0, 220, 26), pullsDown: false);
        foreach (var option in options)
        {
            popup.AddItem(option.Name);
            if (popup.LastItem is { } item)
            {
                item.Image = symbol(option.Key);
            }
        }

        var index = options.ToList().FindIndex(o => o.Key == selectedKey);
        popup.SelectItem(index < 0 ? 0 : index);
        return popup;
    }

    private static NSImage? Symbol(string name, string fallback)
        => NSImage.GetSystemSymbol(name, null) ?? NSImage.GetSystemSymbol(fallback, null);
}
