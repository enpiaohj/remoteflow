using RemoteFlow.Core.Models;

namespace RemoteFlow.Presentation.Host;

/// <summary>
/// 主题服务抽象。应用浅 / 深 / 跟随系统三种主题，并对外暴露「当前实际是否为深色」。
/// <para>
/// 具体的资源字典切换与系统主题探测由各 UI 框架实现
/// （WPF：合并 ResourceDictionary + 注册表；Avalonia：<c>RequestedThemeVariant</c> +
/// <c>PlatformSettings</c>）。ViewModel 只关心 <see cref="Apply"/> 与 <see cref="IsDark"/>。
/// </para>
/// </summary>
public interface IThemeService
{
    /// <summary>当前生效的主题是否为深色（跟随系统时反映系统当前值）。</summary>
    bool IsDark { get; }

    /// <summary>实际生效主题变化（浅↔深）时触发，用于联动 SSH 终端等自绘区域。</summary>
    event EventHandler? EffectiveThemeChanged;

    /// <summary>应用指定主题。</summary>
    void Apply(AppTheme theme);
}
