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
public sealed class ThemeService
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
    /// </summary>
    public void StartListeningToSystemTheme()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General || _current != AppTheme.System)
            {
                return;
            }

            // 系统主题变化通知来自非 UI 线程，切回 UI 线程再操作资源字典。
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
        };
    }

    /// <summary>
    /// 读取系统的「应用模式」设置。
    /// 注册表值 AppsUseLightTheme：1 = 浅色，0 = 深色。读取失败时按浅色处理。
    /// </summary>
    private static bool IsSystemUsingDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}
