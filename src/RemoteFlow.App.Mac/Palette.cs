using AppKit;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 界面配色。
///
/// 为什么不直接用语义色（windowBackgroundColor / controlBackgroundColor）：
/// 那套是给系统控件用的，页面底衬偏灰、和卡片明度拉不开，成片铺开就发闷。
/// 成熟产品（Linear、Things、Craft 这一类）的做法是把值定死：
/// 页面底衬接近白但略沉一点点，卡片纯白，层次交给发丝线和极轻投影，
/// 而不是靠把底色染灰。深色模式同理 —— 卡片比页面底稍亮一档。
/// </summary>
internal static class Palette
{
    private static bool IsDark(NSView view)
        => view.EffectiveAppearance.FindBestMatch(new[]
        {
            NSAppearance.NameAqua.ToString(),
            NSAppearance.NameDarkAqua.ToString(),
        }) == NSAppearance.NameDarkAqua.ToString();

    private static NSColor Rgb(int hex) => NSColor.FromSrgb(
        ((hex >> 16) & 0xFF) / 255f,
        ((hex >> 8) & 0xFF) / 255f,
        (hex & 0xFF) / 255f,
        1f);

    /// <summary>页面底衬：卡片之外那片区域。浅色下接近白，只比卡片沉一点点。</summary>
    public static NSColor PageGround(NSView v) => IsDark(v) ? Rgb(0x1B1B1D) : Rgb(0xF7F7F9);

    /// <summary>卡片 / 列表等内容表面。浅色下纯白，深色下比底衬亮一档。</summary>
    public static NSColor CardSurface(NSView v) => IsDark(v) ? Rgb(0x252528) : Rgb(0xFFFFFF);

    /// <summary>内嵌说明底纹：比卡片更内敛的一块淡填充（设置页的「数据安全提示」这类）。
    /// 浅色下比页面底衬再沉一点，深色下比卡片再亮一点，和卡片、底衬都能拉开。</summary>
    public static NSColor InsetFill(NSView v) => IsDark(v)
        ? NSColor.White.ColorWithAlphaComponent(0.05f)
        : NSColor.Black.ColorWithAlphaComponent(0.04f);

    /// <summary>发丝线：卡片描边、分隔线。层次主要靠它，而不是靠明度差。</summary>
    public static NSColor Hairline(NSView v) => IsDark(v)
        ? NSColor.White.ColorWithAlphaComponent(0.10f)
        : NSColor.Black.ColorWithAlphaComponent(0.08f);

    /// <summary>行悬停底：用强调色的极淡染色，比中性灰有生气。</summary>
    public static NSColor RowHover(NSView v) => NSColor.ControlAccent.ColorWithAlphaComponent(
        IsDark(v) ? 0.16f : 0.08f);

    /// <summary>行选中底：比悬停再重一档，仍远轻于系统的实心蓝。</summary>
    public static NSColor RowSelected(NSView v) => NSColor.ControlAccent.ColorWithAlphaComponent(
        IsDark(v) ? 0.26f : 0.14f);

    /// <summary>在指定视图的外观下取色（NSColor 动态色需要当前 appearance 才解析得对）。</summary>
    public static void With(NSView view, Action body)
    {
        var prev = NSAppearance.CurrentAppearance;
        NSAppearance.CurrentAppearance = view.EffectiveAppearance;
        try
        {
            body();
        }
        finally
        {
            NSAppearance.CurrentAppearance = prev;
        }
    }
}
