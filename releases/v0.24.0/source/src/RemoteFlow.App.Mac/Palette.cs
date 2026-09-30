using AppKit;
using RemoteFlow.Core.Models;

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
    /// <summary>当前「窗口材质」设置（设置 → 常规 → 外观与行为），由
    /// <see cref="Host.AppKitThemeService"/> 在启动 / 设置变化时写入。只影响
    /// <see cref="PageGround"/> 的透过率——卡片（<see cref="CardSurface"/>）
    /// 三档材质下都保持不透明，避免削弱之前专门调过的白卡可读性。</summary>
    public static WindowMaterial Material { get; set; } = WindowMaterial.Acrylic;

    /// <summary>玻璃透明度（0 最不透明 ～ 100 最透明），语义对齐 Windows 版设置。</summary>
    public static int Transparency { get; set; } = 50;

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

    /// <summary>页面底衬：卡片之外那片区域。**半透明**覆一层 —— 窗口底铺了整块磨砂
    /// （见 MainWindowController），这里留出透过率让磨砂露出来一点，同时仍比卡片（纯毛玻璃）
    /// 亮一档，两者只留**轻微色差**。用不透明色会把窗口底的磨砂完全盖死。
    /// 不透明度按 <see cref="Material"/> 定区间、<see cref="Transparency"/> 在区间内插值——
    /// 纯色恒为 1（等于关掉材质），云母区间最窄最保守，亚克力区间最宽、玻璃感最强。</summary>
    public static NSColor PageGround(NSView v)
    {
        var t = Math.Clamp(Transparency, 0, 100) / 100f;
        var dark = IsDark(v);
        var (max, min) = (Material, dark) switch
        {
            (WindowMaterial.Solid, _) => (1f, 1f),
            (WindowMaterial.Mica, false) => (0.92f, 0.55f),
            (WindowMaterial.Mica, true) => (0.62f, 0.35f),
            (WindowMaterial.Acrylic, false) => (0.65f, 0.20f),
            (WindowMaterial.Acrylic, true) => (0.45f, 0.12f),
            _ => (0.82f, 0.82f),
        };
        var opacity = max - ((max - min) * t);
        return dark
            ? NSColor.Black.ColorWithAlphaComponent((nfloat)opacity)
            : NSColor.White.ColorWithAlphaComponent((nfloat)opacity);
    }

    /// <summary>卡片 / 列表等内容表面。浅色下纯白，深色下比底衬亮一档。
    /// 注：现在「框」普遍改用**毛玻璃底**（材质与左侧栏一致），见 DetailView.CardView(glass: true)
    /// 与 SettingsPaneView.AddGlassBackground；这个纯色值留给还没换的零散场景。</summary>
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
