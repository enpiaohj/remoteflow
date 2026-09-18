using AppKit;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.Host;

namespace RemoteFlow.App.Mac.Host;

/// <summary>
/// AppKit 主题服务：浅/深映射到 <see cref="NSAppearance"/>；跟随系统时清空覆盖。
/// <see cref="IsDark"/> 反映当前实际外观，供 SSH 终端等自绘区域联动。
/// </summary>
public sealed class AppKitThemeService : IThemeService
{
    public bool IsDark { get; private set; } = IsSystemDark();

    public event EventHandler? EffectiveThemeChanged;

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
