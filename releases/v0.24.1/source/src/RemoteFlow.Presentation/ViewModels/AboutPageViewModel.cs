using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteFlow.Presentation.ViewModels;

/// <summary>
/// 关于页（内嵌）：版本、开发者、源码仓库、许可证与隐私说明。
/// 无交互逻辑；外链由视图层处理（打浏览器是平台行为，不进共享层）。
/// </summary>
public sealed class AboutPageViewModel : ObservableObject
{
    /// <summary>展示用版本号，如 "v0.15.0"。与主窗口状态栏同源（程序集版本）。</summary>
    public string Version { get; } =
        "v" + (typeof(AboutPageViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");
}
