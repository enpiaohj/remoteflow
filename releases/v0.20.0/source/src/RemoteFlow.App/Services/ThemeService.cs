using System.Windows;
using Microsoft.Win32;
using RemoteFlow.Core.Models;

namespace RemoteFlow.App.Services;

/// <summary>
/// 主题服务。默认浅色，可跟随系统深色。
/// <para>
/// 实现方式：只替换合并字典中索引 0 的调色板。
/// 所有控件模板引用的都是 <c>DynamicResource</c> 语义键，
/// 因此替换调色板即可让整个界面即时换肤，无需重建任何控件。
/// </para>
/// </summary>
public sealed class ThemeService : RemoteFlow.Presentation.Host.IThemeService
{
    private const string LightThemeUri = "Themes/Theme.Light.xaml";
    private const string DarkThemeUri = "Themes/Theme.Dark.xaml";

    /// <summary>调色板在 <see cref="Application.Resources"/> 合并字典中的固定位置。</summary>
    private const int PaletteIndex = 0;

    private AppTheme _current = AppTheme.System;

    /// <summary>当前实际生效的是否为深色。</summary>
    public bool IsDark { get; private set; }

    /// <summary>实际生效的主题变化时触发，供终端等自绘内容同步配色。</summary>
    public event EventHandler? EffectiveThemeChanged;

    public void Apply(AppTheme theme)
    {
        _current = theme;

        var shouldUseDark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemUsingDarkTheme()
        };

        // 首次调用时 IsDark 的默认值恰好可能与目标一致，用哨兵避免跳过初始化。
        if (_applied && shouldUseDark == IsDark)
        {
            return;
        }

        IsDark = shouldUseDark;
        _applied = true;

        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary
        {
            Source = new Uri(shouldUseDark ? DarkThemeUri : LightThemeUri, UriKind.Relative)
        };

        if (dictionaries.Count > PaletteIndex)
        {
            dictionaries[PaletteIndex] = palette;
        }
        else
        {
            dictionaries.Insert(PaletteIndex, palette);
        }

        EffectiveThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool _applied;

    /// <summary>
    /// 开始监听系统主题变化。仅在用户选择「跟随系统」时才实际重新应用。
    /// <para>
    /// Windows 切换浅色 / 深色时，<see cref="SystemEvents.UserPreferenceChanged"/> 的
    /// <c>Category</c> 在不同版本上并不一致（General / VisualStyle / Color 都出现过），
    /// 所以这几类都要响应；<see cref="Apply"/> 自身带幂等判断，多触发几次无副作用。
    /// </para>
    /// </summary>
    public void StartListeningToSystemTheme()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (_current != AppTheme.System)
            {
                return;
            }

            if (e.Category is not (UserPreferenceCategory.General
                or UserPreferenceCategory.VisualStyle
                or UserPreferenceCategory.Color))
            {
                return;
            }

            // 系统主题变化通知来自非 UI 线程，切回 UI 线程再操作资源字典。
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(ReapplySystemTheme);
        };

        // 应用重新获得焦点时补一次同步，兜住系统事件在后台期间丢失的情况。
        if (System.Windows.Application.Current is { } app)
        {
            app.Activated += (_, _) => ReapplySystemTheme();
        }
    }

    /// <summary>
    /// 主窗口重新获得焦点时补一次同步：极少数情况下系统在应用最小化 / 后台期间
    /// 切换了主题，而对应的系统事件没有送达。开销只是一次注册表读取。
    /// </summary>
    public void ReapplySystemTheme()
    {
        if (_current == AppTheme.System)
        {
            Apply(AppTheme.System);
        }
    }

    /// <summary>
    /// 读取系统的「应用模式」设置。
    /// <para>
    /// 注册表 <c>HKCU\...\Themes\Personalize\AppsUseLightTheme</c>：1 = 浅色，0 = 深色。
    /// 该值可能以 <c>Int32</c> 或 <c>Int64</c> 返回（不同 Windows 版本 / 写入方式），
    /// 都要能识别；键缺失时退回系统级 <c>SystemUsesLightTheme</c>；
    /// 全部无法判定时按<b>浅色</b>处理（宁可跟错也不要突然全黑）。
    /// </para>
    /// </summary>
    private static bool IsSystemUsingDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            if (key is null)
            {
                return false;
            }

            if (TryReadFlag(key, "AppsUseLightTheme", out var appsUseLight))
            {
                return appsUseLight == 0;
            }

            if (TryReadFlag(key, "SystemUsesLightTheme", out var systemUsesLight))
            {
                return systemUsesLight == 0;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadFlag(RegistryKey key, string name, out long value)
    {
        value = key.GetValue(name) switch
        {
            int i => i,
            long l => l,
            _ => -1
        };
        return value >= 0;
    }
}
