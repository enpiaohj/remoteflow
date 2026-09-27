using RemoteFlow.Core.Models;

namespace RemoteFlow.Presentation.ViewModels;

/// <summary>
/// 设备类型的展示信息：名称、正式矢量图标资源键与 Tile 配色资源键。
/// 图标资源由 Windows UI 层的 <c>Themes/DeviceIcons.xaml</c> 提供；
/// Presentation 只持有平台无关的语义键，不引用任何 WPF 类型。
/// </summary>
public sealed record DeviceTypeInfo(
    DeviceType Type,
    string Name,
    string IconResourceKey,
    string AccentBrushKey);

/// <summary>
/// 设备类型目录。Unknown 不是一种「类型」，而是「未指定」——
/// 界面按协议推断显示（<see cref="InferFromProtocol"/>），避免老数据升级后图标突变。
/// </summary>
public static class DeviceTypeCatalog
{
    public static IReadOnlyList<DeviceTypeInfo> All { get; } =
    [
        new(DeviceType.WindowsPc, "Windows 电脑", "DeviceIcon.WindowsPc", "Protocol.Rdp"),
        new(DeviceType.WindowsServer, "Windows 服务器", "DeviceIcon.WindowsServer", "Protocol.Rdp"),
        new(DeviceType.Linux, "Linux 服务器", "DeviceIcon.Linux", "Protocol.Ssh"),
        new(DeviceType.DomainController, "域控", "DeviceIcon.DomainController", "Protocol.Vnc"),
        new(DeviceType.FileServer, "文件服务器", "DeviceIcon.FileServer", "Brand.Default"),
        new(DeviceType.Mac, "macOS 设备", "DeviceIcon.Mac", "Status.Idle"),
        new(DeviceType.WebHost, "Web / 应用主机", "DeviceIcon.WebHost", "Status.Info"),
        new(DeviceType.Database, "数据库服务器", "DeviceIcon.Database", "Status.Danger"),
        new(DeviceType.NetworkDevice, "网络设备", "DeviceIcon.NetworkDevice", "Status.Success"),
        new(DeviceType.VirtualMachine, "虚拟机", "DeviceIcon.VirtualMachine", "Protocol.Vnc"),
        new(DeviceType.Container, "容器", "DeviceIcon.Container", "Status.Warning"),
        new(DeviceType.CloudHost, "云主机", "DeviceIcon.CloudHost", "Status.Info"),
    ];

    public static DeviceTypeInfo? Resolve(DeviceType type) =>
        type == DeviceType.Unknown ? null : All.FirstOrDefault(i => i.Type == type);

    /// <summary>未指定设备类型时按协议推断：RDP → Windows 电脑，SSH → Linux 服务器，VNC → macOS 设备。</summary>
    public static DeviceTypeInfo InferFromProtocol(ProtocolType protocol) => protocol switch
    {
        ProtocolType.Rdp => Resolve(DeviceType.WindowsPc)!,
        ProtocolType.Ssh => Resolve(DeviceType.Linux)!,
        _ => Resolve(DeviceType.Mac)!,
    };

    /// <summary>条目的显示文案（编辑器下拉、详情面板）。Unknown 显示「自动（按协议）」。</summary>
    public static string DisplayName(DeviceType type) =>
        type == DeviceType.Unknown ? "自动（按协议）" : Resolve(type)?.Name ?? "自动（按协议）";
}
