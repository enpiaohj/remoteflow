using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RemoteFlow.App.Views.Pages;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 「关于」（含软件更新）放进 设置 → 关于 选项卡：用户去设置里找更新入口就能找到，
/// 而不是只藏在导航栏底部的 ℹ 图标和托盘菜单里。
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class SettingsAboutTabTests
{
    [Fact]
    public void SettingsPage_HasAboutTabThatShowsAboutContentOnlyWhenActive()
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            try
            {
                // 只驱动选项卡切换与绑定：不构造完整的设置页依赖树。
                var viewModel = (SettingsPageViewModel)RuntimeHelpers.GetUninitializedObject(typeof(SettingsPageViewModel));
                viewModel.About = new AboutPageViewModel();
                var page = new SettingsPage { DataContext = viewModel };

                Assert.Contains(Descendants(page).OfType<RadioButton>(), r => Equals(r.Content, "关于"));

                Settle(page);
                var about = Descendants(page).OfType<AboutContent>().Single();
                Assert.Same(viewModel.About, about.DataContext);

                viewModel.ActiveTab = SettingsTab.General;
                Settle(page);
                Assert.Equal(Visibility.Collapsed, about.Visibility);

                viewModel.ActiveTab = SettingsTab.About;
                Settle(page);
                Assert.Equal(Visibility.Visible, about.Visibility);

                viewModel.ActiveTab = SettingsTab.Cloud;
                Settle(page);
                Assert.Equal(Visibility.Collapsed, about.Visibility);
            }
            finally
            {
                try { UiTestHost.RestoreAppResources(); } catch (NullReferenceException) { }
            }
        });
    }

    private static void Settle(FrameworkElement element)
    {
        element.Measure(new Size(1200, 800));
        element.Arrange(new Rect(0, 0, 1200, 800));
        element.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }
}
