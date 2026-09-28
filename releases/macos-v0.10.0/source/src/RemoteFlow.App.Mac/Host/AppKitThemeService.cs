using AppKit;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.Host;
using RemoteFlow.App.Mac;

namespace RemoteFlow.App.Mac.Host;

/// <summary>
/// AppKit 主题服务：浅/深映射到 <see cref="NSAppearance"/>；跟随系统时清空覆盖。
/// <see cref="IsDark"/> 反映当前实际外观，供 SSH 终端等自绘区域联动。
/// </summary>
public sealed class AppKitThemeService : IThemeService
{
    public bool IsDark { get; private set; } = IsSystemDark();

    public event EventHandler? EffectiveThemeChanged;

    /// <summary>窗口材质 / 透明度变化（设置页拖动即触发）。<see cref="MainWindowController"/>
    /// 订阅它来实时重铺窗口底的磨砂并刷新已构建页面的底衬颜色。</summary>
    public event EventHandler? GlassAppearanceChanged;

    public void SetMaterial(WindowMaterial material)
    {
        Palette.Material = material;
        GlassAppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetTransparency(int transparency)
    {
        Palette.Transparency = transparency;
        GlassAppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Apply(AppTheme theme)
    {
        NSApplication.SharedApplication.Appearance = theme switch
        {
            AppTheme.Dark => NSAppearance.GetAppearance(NSAppearance.NameDarkAqua),
            AppTheme.Light => NSAppearance.GetAppearance(NSAppearance.NameAqua),
            _ => null, // FollowSystem
        };

        var dark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark(),
        };

        if (dark != IsDark)
        {
            IsDark = dark;
            EffectiveThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool IsSystemDark()
    {
        var name = NSApplication.SharedApplication.EffectiveAppearance.Name;
        return name == NSAppearance.NameDarkAqua
            || name == NSAppearance.NameVibrantDark
            || name == NSAppearance.NameAccessibilityHighContrastDarkAqua
            || name == NSAppearance.NameAccessibilityHighContrastVibrantDark;
    }
}
