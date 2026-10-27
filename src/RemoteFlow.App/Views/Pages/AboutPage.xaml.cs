using System.Windows.Controls;

namespace RemoteFlow.App.Views.Pages;

/// <summary>
/// 关于页（独立页面）：标题 + <see cref="AboutContent"/>。
/// 应用内的「关于」入口统一进入 设置 → 关于 选项卡，见 MainViewModel 的 NavigationPage.About 处理。
/// </summary>
public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
    }
}
