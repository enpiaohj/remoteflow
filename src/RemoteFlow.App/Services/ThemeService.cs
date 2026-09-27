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
    /// <summary>
    /// 进程内唯一实例（DI 单例）。供不经 DI 创建的对话框接入玻璃外观（<see cref="WindowBackdrop.Attach"/>）。
    /// </summary>
    public static ThemeService? Instance { get; private set; }

    public ThemeService() => Instance = this;

    // 组件 pack URI 显式指明本程序集：不依赖入口程序集 / Application.ResourceAssembly，
    // 在应用与测试宿主中解析结果一致。
    private static readonly string Component = $"/{typeof(ThemeService).Assembly.GetName().Name};component/";
    private static readonly string LightThemeUri = Component + "Themes/Theme.Light.xaml";
    private static readonly string DarkThemeUri = Component + "Themes/Theme.Dark.xaml";
    private static readonly string LightGlassUri = Component + "Themes/Glass.Light.xaml";
    private static readonly string DarkGlassUri = Component + "Themes/Glass.Dark.xaml";

    /// <summary>调色板在 <see cref="Application.Resources"/> 合并字典中的固定位置。</summary>
    private const int PaletteIndex = 0;

    private AppTheme _current = AppTheme.System;

    /// <summary>当前合并在调色板之后的玻璃覆盖层（未启用时为 null）。</summary>
    private ResourceDictionary? _glassOverlay;

    /// <summary>当前实际生效的是否为深色。</summary>
    public bool IsDark { get; private set; }

    /// <summary>
    /// 实际生效的窗口材质。系统不支持时恒为 <see cref="WindowMaterial.Solid"/>。
    /// </summary>
    public WindowMaterial Material { get; private set; } = WindowMaterial.Solid;

    /// <summary>是否启用玻璃外观（系统材质 + 半透明面）。</summary>
    public bool IsGlass => Material != WindowMaterial.Solid;

    /// <summary>主窗口应使用的系统材质。</summary>
    public BackdropKind MainBackdrop => Material == WindowMaterial.Mica ? BackdropKind.Mica : BackdropKind.Acrylic;

    /// <summary>实际生效的主题变化时触发，供终端等自绘内容同步配色。</summary>
    public event EventHandler? EffectiveThemeChanged;

    /// <summary>玻璃外观开关或深浅色变化时触发，窗口据此重新设置系统材质。</summary>
    public event EventHandler? AppearanceChanged;

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

        ApplyGlassOverlay();

        EffectiveThemeChanged?.Invoke(this, EventArgs.Empty);
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 设置窗口材质。系统不支持（Windows 11 22H2 以下）时恒为纯色；
    /// 变化后窗口在 <see cref="AppearanceChanged"/> 中重设系统材质。
    /// </summary>
    public void SetMaterial(WindowMaterial material)
    {
        var effective = WindowBackdrop.IsSupported ? material : WindowMaterial.Solid;
        if (effective == Material && (_glassOverlay is not null) == IsGlass)
        {
            return;
        }

        Material = effective;
        if (_applied)
        {
            ApplyGlassOverlay();
        }

        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 玻璃覆盖层紧跟调色板（索引 1），只覆盖「面」的语义键；
    /// 后续的控件字典不定义这些键，因此覆盖层对调色板生效、不影响控件样式。
    /// </summary>
    private void ApplyGlassOverlay()
    {
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        if (_glassOverlay is not null)
        {
            dictionaries.Remove(_glassOverlay);
            _glassOverlay = null;
        }

        if (!IsGlass)
        {
            return;
        }

        _glassOverlay = new ResourceDictionary
        {
            Source = new Uri(IsDark ? DarkGlassUri : LightGlassUri, UriKind.Relative)
        };

        // 按透明度重算三层半透明面的 Alpha（RGB 沿用覆盖层文件）。在插入前替换，
        // 整个覆盖层随后一次性生效，所有 DynamicResource 同步刷新。
        foreach (var key in GlassTransparency.ScaledKeys)
        {
            if (_glassOverlay[key] is System.Windows.Media.SolidColorBrush designed)
            {
                var color = designed.Color;
                color.A = GlassTransparency.Alpha(key, IsDark, Transparency);
                var brush = new System.Windows.Media.SolidColorBrush(color);
                brush.Freeze();
                _glassOverlay[key] = brush;
            }
        }

        dictionaries.Insert(PaletteIndex + 1, _glassOverlay);
    }

    /// <summary>玻璃外观的透明度（0–100）。</summary>
    public int Transparency { get; private set; } = GlassTransparency.Default;

    /// <summary>设置玻璃透明度并即时重建覆盖层；纯色外观下只记录，切回玻璃时生效。</summary>
    public void SetTransparency(int transparency)
    {
        var value = Math.Clamp(transparency, 0, 100);
        if (value == Transparency)
        {
            return;
        }

        Transparency = value;
        if (_applied && IsGlass)
        {
            ApplyGlassOverlay();
        }
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
