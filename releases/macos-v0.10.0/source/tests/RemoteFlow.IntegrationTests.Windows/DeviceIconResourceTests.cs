using System.IO;
using System.Xml.Linq;
using Xunit;

namespace RemoteFlow.IntegrationTests;

public sealed class DeviceIconResourceTests
{
    private static readonly string[] ExpectedKeys =
    [
        "DeviceIcon.WindowsPc",
        "DeviceIcon.WindowsServer",
        "DeviceIcon.Linux",
        "DeviceIcon.DomainController",
        "DeviceIcon.FileServer",
        "DeviceIcon.Mac",
        "DeviceIcon.WebHost",
        "DeviceIcon.Database",
        "DeviceIcon.NetworkDevice",
        "DeviceIcon.VirtualMachine",
        "DeviceIcon.Container",
        "DeviceIcon.CloudHost",
    ];

    private static readonly string[] ExpectedProtocolKeys =
    [
        "ProtocolIcon.Rdp",
        "ProtocolIcon.Ssh",
        "ProtocolIcon.Vnc",
    ];

    private static readonly string[] ExpectedResourceKeys =
    [
        "ResourceIcon.AllDevices",
        "ResourceIcon.Favorites",
        "ResourceIcon.Recent",
    ];

    [Fact]
    public void 设备图标资源字典包含十二枚独立矢量图标()
    {
        var path = FindProjectFile("src", "RemoteFlow.App", "Themes", "DeviceIcons.xaml");
        var document = XDocument.Load(path);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var images = document.Root!
            .Elements(presentation + "DrawingImage")
            .Where(element => ((string?)element.Attribute(x + "Key"))?.StartsWith("DeviceIcon.", StringComparison.Ordinal) == true)
            .ToDictionary(
                element => (string)element.Attribute(x + "Key")!,
                StringComparer.Ordinal);

        Assert.Equal(ExpectedKeys.Length, images.Count);
        Assert.Equal(ExpectedKeys.Order(), images.Keys.Order());
        Assert.All(images.Values, image =>
            Assert.NotEmpty(image.Descendants(presentation + "GeometryDrawing")));
    }

    [Fact]
    public void 协议徽章和连接资源使用独立矢量图标()
    {
        var path = FindProjectFile("src", "RemoteFlow.App", "Themes", "DeviceIcons.xaml");
        var document = XDocument.Load(path);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var images = document.Root!
            .Elements(presentation + "DrawingImage")
            .ToDictionary(
                element => (string)element.Attribute(x + "Key")!,
                StringComparer.Ordinal);

        Assert.All(ExpectedProtocolKeys, key => Assert.True(images.ContainsKey(key), $"缺少 {key}"));
        Assert.All(ExpectedResourceKeys, key => Assert.True(images.ContainsKey(key), $"缺少 {key}"));
        Assert.All(ExpectedProtocolKeys.Concat(ExpectedResourceKeys), key =>
            Assert.NotEmpty(images[key].Descendants(presentation + "GeometryDrawing")));
    }

    [Fact]
    public void 协议徽章和状态指示由统一模板接入各页面()
    {
        var controls = File.ReadAllText(FindProjectFile("src", "RemoteFlow.App", "Themes", "Controls.Base.xaml"));
        Assert.Contains("x:Key=\"ProtocolBadge.Template\"", controls, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ConnectionStatus.Template\"", controls, StringComparison.Ordinal);
        Assert.Contains("ProtocolIconKey", controls, StringComparison.Ordinal);
        Assert.Contains("ConnectionStatusDisplay", controls, StringComparison.Ordinal);
        Assert.Contains("ConnectionStatusBrushKey", controls, StringComparison.Ordinal);

        foreach (var segments in new[]
                 {
                     new[] { "src", "RemoteFlow.App", "Views", "Pages", "ConnectionsPage.xaml" },
                     new[] { "src", "RemoteFlow.App", "Views", "Pages", "ConnectionDetailPanel.xaml" },
                     new[] { "src", "RemoteFlow.App", "Views", "Pages", "HomePage.xaml" },
                 })
        {
            var markup = File.ReadAllText(FindProjectFile(segments));
            Assert.Contains("ProtocolBadge.Template", markup, StringComparison.Ordinal);
        }

        var connections = File.ReadAllText(FindProjectFile("src", "RemoteFlow.App", "Views", "Pages", "ConnectionsPage.xaml"));
        Assert.Contains("ConnectionStatus.Template", connections, StringComparison.Ordinal);
    }

    [Fact]
    public void 连接资源智能视图使用语义矢量图标而非字体字符()
    {
        var markup = File.ReadAllText(FindProjectFile(
            "src", "RemoteFlow.App", "Views", "Pages", "ConnectionResourceTree.xaml"));

        Assert.Contains("IconResourceKey", markup, StringComparison.Ordinal);
        Assert.Contains("KeyToImageSource", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding IconGlyph}\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void 应用合并设备图标资源且各页面使用ImageSource绑定()
    {
        var app = File.ReadAllText(FindProjectFile("src", "RemoteFlow.App", "App.xaml"));
        Assert.Contains("Themes/DeviceIcons.xaml", app, StringComparison.Ordinal);
        Assert.Contains("ResourceKeyToImageSourceConverter", app, StringComparison.Ordinal);

        foreach (var segments in new[]
                 {
                     new[] { "src", "RemoteFlow.App", "Views", "Pages", "ConnectionsPage.xaml" },
                     new[] { "src", "RemoteFlow.App", "Views", "Pages", "ConnectionDetailPanel.xaml" },
                     new[] { "src", "RemoteFlow.App", "Views", "Pages", "HomePage.xaml" },
                 })
        {
            var markup = File.ReadAllText(FindProjectFile(segments));
            Assert.Contains("DeviceIconKey", markup, StringComparison.Ordinal);
            Assert.Contains("KeyToImageSource", markup, StringComparison.Ordinal);
            Assert.DoesNotContain("DeviceIconGlyph", markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Segoe UI Emoji", markup, StringComparison.Ordinal);
        }
    }

    private static string FindProjectFile(params string[] segments)
    {
        foreach (var root in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory }.Distinct())
        {
            for (DirectoryInfo? directory = new(root);
                 directory is not null;
                 directory = directory.Parent)
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
