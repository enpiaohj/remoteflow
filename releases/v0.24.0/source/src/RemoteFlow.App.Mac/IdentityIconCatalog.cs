using AppKit;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 彩色身份图标（凭据类型 / 设置分区 / 首页区块等）。图源是 Windows 版
/// <c>IdentityIcons.xaml</c> 里 25 个原创矢量图标，经 <c>scripts/build-identity-icons-mac.py</c>
/// 转成 <c>Resources/IdentityIcons/*.png</c>；键沿用 Windows 那边的 <c>"Id.XXX"</c> 命名
/// （如 <see cref="RemoteFlow.Presentation.ViewModels.CredentialItemViewModel.TypeIconKey"/>
/// 已经在共享层直接返回这些键），这样两端不用各建一套映射表。
/// </summary>
internal static class IdentityIconCatalog
{
    private static readonly Dictionary<string, NSImage?> Cache = new();

    /// <summary>按 <c>"Id.XXX"</c> 键取图；没有对应资源（本轮未接线的其余键）时返回 null，
    /// 调用方应回落到 SF Symbol。</summary>
    public static NSImage? Get(string key)
    {
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // 不强制 Size：调用方的 NSImageView 都是固定宽高约束 + 默认
        // ProportionallyUpOrDown 缩放，256×256 源图缩小到十几到二十几 pt 都清晰。
        NSImage? image = null;
        var name = key.StartsWith("Id.", StringComparison.Ordinal) ? key["Id.".Length..] : key;
        var path = NSBundle.MainBundle.PathForResource(name, "png", "IdentityIcons");
        if (path is not null)
        {
            image = new NSImage(path);
        }

        Cache[key] = image;
        return image;
    }
}
