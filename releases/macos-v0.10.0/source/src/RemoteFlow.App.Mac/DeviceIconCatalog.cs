using AppKit;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 设备类型 / 连接资源智能视图的彩色身份图标。图源是 Windows 版
/// <c>DeviceIcons.xaml</c> 里的 12 个设备类型 + 3 个智能视图图标，经
/// <c>scripts/build-vector-icons-mac.py</c> 转成 <c>Resources/DeviceIcons/*.png</c>；
/// 键沿用 Windows 那边的命名（<see cref="RemoteFlow.Presentation.ViewModels.DeviceTypeCatalog"/>
/// 的 <c>IconResourceKey</c> 已经是 <c>"DeviceIcon.XXX"</c>，<c>ConnectionsPageViewModel.SmartViewOption</c>
/// 的 <c>IconResourceKey</c> 是 <c>"ResourceIcon.XXX"</c>），两端不用各建一套映射表。
/// </summary>
internal static class DeviceIconCatalog
{
    private static readonly Dictionary<string, NSImage?> Cache = new();

    /// <summary>按 <c>"DeviceIcon.XXX"</c> / <c>"ResourceIcon.XXX"</c> 键取图；
    /// 没有对应资源时返回 null，调用方应回落到协议色块 / SF Symbol。</summary>
    public static NSImage? Get(string key)
    {
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        NSImage? image = null;
        var dot = key.IndexOf('.');
        var name = dot >= 0 ? key[(dot + 1)..] : key;
        var path = NSBundle.MainBundle.PathForResource(name, "png", "DeviceIcons");
        if (path is not null)
        {
            image = new NSImage(path);
        }

        Cache[key] = image;
        return image;
    }
}
