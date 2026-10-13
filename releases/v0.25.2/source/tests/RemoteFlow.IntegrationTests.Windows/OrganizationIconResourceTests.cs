using System.ComponentModel;
using System.IO;
using System.Xml.Linq;
using RemoteFlow.App.Views.Dialogs;
using Xunit;

namespace RemoteFlow.IntegrationTests;

public sealed class OrganizationIconResourceTests
{
    [Fact]
    public void 组织图标字典包含完整分组和标签Geometry资源()
    {
        var path = FindProjectFile("src", "RemoteFlow.App", "Themes", "OrganizationIcons.xaml");
        var document = XDocument.Load(path);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var keys = document.Root!
            .Elements(presentation + "Geometry")
            .Select(element => (string?)element.Attribute(x + "Key"))
            .Where(key => key is not null)
            .Cast<string>()
            .ToArray();

        Assert.Equal(14, keys.Count(key => key.StartsWith("GroupIcon.", StringComparison.Ordinal)));
        Assert.Equal(10, keys.Count(key => key.StartsWith("TagIcon.", StringComparison.Ordinal)));
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void 应用合并组织图标且资源树使用Path而非字体文件夹()
    {
        var app = File.ReadAllText(FindProjectFile("src", "RemoteFlow.App", "App.xaml"));
        Assert.Contains("Themes/OrganizationIcons.xaml", app, StringComparison.Ordinal);
        Assert.Contains("ResourceKeyToGeometryConverter", app, StringComparison.Ordinal);

        var tree = File.ReadAllText(FindProjectFile(
            "src", "RemoteFlow.App", "Views", "Pages", "ConnectionResourceTree.xaml"));
        Assert.Contains("IconResourceKey", tree, StringComparison.Ordinal);
        Assert.Contains("KeyToGeometry", tree, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{StaticResource Icon.Folder}\"", tree, StringComparison.Ordinal);
    }

    [Fact]
    public void 分组和标签编辑入口包含图标选择器()
    {
        var group = File.ReadAllText(FindProjectFile(
            "src", "RemoteFlow.App", "Views", "Dialogs", "GroupEditorDialog.xaml"));
        var tag = File.ReadAllText(FindProjectFile(
            "src", "RemoteFlow.App", "Views", "Dialogs", "TagEditorDialog.xaml"));

        Assert.Contains("IconOptions", group, StringComparison.Ordinal);
        Assert.Contains("IconOptions", tag, StringComparison.Ordinal);
        Assert.Contains("KeyToGeometry", group, StringComparison.Ordinal);
        Assert.Contains("KeyToGeometry", tag, StringComparison.Ordinal);
    }

    [Fact]
    public void 图标编辑器在选择变化时通知实时预览()
    {
        Assert.True(typeof(INotifyPropertyChanged).IsAssignableFrom(typeof(GroupEditorDialog)));
        Assert.True(typeof(INotifyPropertyChanged).IsAssignableFrom(typeof(TagEditorDialog)));
    }

    [Fact]
    public void 一级导航只通过CurrentPage绑定触发刷新()
    {
        var mainWindow = File.ReadAllText(FindProjectFile(
            "src", "RemoteFlow.App", "Views", "MainWindow.xaml"));

        Assert.DoesNotContain("Command=\"{Binding NavigateToCommand}\"", mainWindow, StringComparison.Ordinal);

        // 已选中的导航钮再次点击不会改变 CurrentPage：必须另行处理，才能从会话 Tab 回到当前页并刷新。
        var railButtons = System.Text.RegularExpressions.Regex.Matches(
            mainWindow, "<RadioButton Style=\"\\{StaticResource Nav.RailItem\\}\"[^>]*>");
        Assert.Equal(6, railButtons.Count);
        Assert.All(railButtons, m => Assert.Contains("PreviewMouseLeftButtonUp=\"OnNavRailSameItemClick\"", m.Value));
    }

    [Fact]
    public void 会话标签与测试连接对话框使用设备图标而非协议字形()
    {
        var model = new TestConnectionDialogModel(new RemoteFlow.Core.Models.ConnectionProfile
        {
            Name = "dc",
            Host = "dc.example.com",
            Port = 3389,
            Protocol = RemoteFlow.Core.Models.ProtocolType.Rdp,
            DeviceType = RemoteFlow.Core.Models.DeviceType.DomainController,
        });
        var expected = RemoteFlow.Presentation.ViewModels.DeviceTypeCatalog.Resolve(
            RemoteFlow.Core.Models.DeviceType.DomainController)!;
        Assert.Equal(expected.IconResourceKey, model.DeviceIconKey);

        foreach (var file in new[]
                 {
                     FindProjectFile("src", "RemoteFlow.App", "Views", "MainWindow.xaml"),
                     FindProjectFile("src", "RemoteFlow.App", "Views", "Sessions", "SessionTabSelector.xaml"),
                     FindProjectFile("src", "RemoteFlow.App", "Views", "Dialogs", "TestConnectionDialog.xaml"),
                 })
        {
            Assert.Contains("DeviceIconKey", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    private static string FindProjectFile(params string[] segments)
    {
        foreach (var root in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory }.Distinct())
        {
            for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine([directory.FullName, .. segments]);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException($"未找到 {Path.Combine(segments)}。");
    }
}
