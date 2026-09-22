using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using RemoteFlow.App.Views.Pages;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

public sealed class ConnectionsPageMenuTests
{
    [Fact]
    public void ConnectionContextMenu_InitializesMoveToGroupSubmenu()
    {
        var markup = File.ReadAllText(FindConnectionsPageMarkup());

        Assert.Contains(
            "<MenuItem Tag=\"move\" Header=\"移动到分组\" SubmenuOpened=\"OnMoveToGroupSubmenuOpened\">",
            markup,
            StringComparison.Ordinal);
        Assert.Contains("<MenuItem Header=\"加载分组…\" IsEnabled=\"False\" />", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void MenuItemTemplate_RendersSubmenus()
    {
        var markup = File.ReadAllText(FindControlCollectionsMarkup());

        Assert.Contains("<Popup x:Name=\"SubmenuPopup\"", markup, StringComparison.Ordinal);
        Assert.Contains("IsOpen=\"{Binding IsSubmenuOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}\"", markup, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SubmenuArrow\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateMoveTargetMenuItem_BindsTargetToOpeningConnection()
    {
        RunInSta(() =>
        {
            var source = new ConnectionItemViewModel(new ConnectionProfile
            {
                Name = "测试连接",
                Host = "host"
            });
            var target = new GroupTargetOption(Guid.NewGuid(), "目标分组", 0);
            var factory = typeof(ConnectionsPage).GetMethod(
                "CreateMoveTargetMenuItem",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(factory);

            var item = Assert.IsType<MenuItem>(factory.Invoke(null, [target, source, null]));
            Assert.Same(source, item.DataContext);
            Assert.Same(target, item.Tag);
            Assert.True(item.IsEnabled);
        });
    }

    private static string FindConnectionsPageMarkup()
        => FindProjectFile("src", "RemoteFlow.App", "Views", "Pages", "ConnectionsPage.xaml");

    private static string FindControlCollectionsMarkup()
        => FindProjectFile("src", "RemoteFlow.App", "Themes", "Controls.Collections.xaml");

    private static string FindProjectFile(params string[] segments)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"未找到 {Path.Combine(segments)}。");
    }

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
