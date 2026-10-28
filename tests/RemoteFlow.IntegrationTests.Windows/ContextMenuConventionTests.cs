using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 全应用右键菜单的统一约定（读取源码标记，不启动窗口）：
/// 破坏性操作用警示色并放在菜单最后一组；同一动作在各入口叫法一致；不保留永远禁用的占位项。
/// </summary>
public sealed partial class ContextMenuConventionTests
{
    private static readonly string[] MenuFiles =
    [
        "Views/MainWindow.xaml",
        "Views/Pages/ConnectionsPage.xaml",
        "Views/Pages/ConnectionsPage.xaml.cs",
        "Views/Pages/ConnectionDetailPanel.xaml.cs",
        "Views/Pages/ConnectionResourceTree.xaml",
        "Views/Pages/CredentialsPage.xaml",
        "Views/Pages/HomePage.xaml",
        "Views/Pages/VmwarePage.xaml",
        "Views/Sessions/FileTransferPanel.xaml",
        "Services/TrayService.cs",
    ];

    [Theory]
    [InlineData("Views/Pages/ConnectionsPage.xaml", "ConnectionRowMenu")]
    [InlineData("Views/Pages/CredentialsPage.xaml", "CredentialRowMenu")]
    [InlineData("Views/Pages/ConnectionResourceTree.xaml", "GroupMenu")]
    [InlineData("Views/Sessions/FileTransferPanel.xaml", "FileRowMenu")]
    [InlineData("Views/MainWindow.xaml", "SessionTabContextMenu")]
    public void 破坏性操作用警示色且是菜单最后一项(string file, string menuKey)
    {
        var items = MenuItemsOf(ReadApp(file), menuKey);

        Assert.NotEmpty(items);
        var danger = items.Where(i => i.Contains("MenuItemAssist.IsDanger=\"True\"", StringComparison.Ordinal)).ToList();
        Assert.Single(danger);
        Assert.Same(danger[0], items[^1]);
    }

    [Theory]
    [InlineData("PowerMenu")]
    [InlineData("VmRowMenu")]
    public void 虚拟机菜单的危险项集中在最后(string menuKey)
    {
        var items = MenuItemsOf(ReadApp("Views/Pages/VmwarePage.xaml"), menuKey);

        var dangerIndexes = items.Select((item, index) => (item, index))
            .Where(x => x.item.Contains("MenuItemAssist.IsDanger=\"True\"", StringComparison.Ordinal))
            .Select(x => x.index).ToList();

        Assert.NotEmpty(dangerIndexes);
        // 危险项必须是末尾连续的一段。
        Assert.Equal(Enumerable.Range(items.Count - dangerIndexes.Count, dangerIndexes.Count), dangerIndexes);
    }

    [Fact]
    public void 虚拟机行菜单的每一项都带图标()
    {
        foreach (var item in MenuItemsOf(ReadApp("Views/Pages/VmwarePage.xaml"), "VmRowMenu"))
        {
            Assert.Contains("MenuItemAssist.IconKey=", item, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 所有删除项都标了警示色()
    {
        foreach (var file in MenuFiles.Where(f => f.EndsWith(".xaml", StringComparison.Ordinal)))
        {
            foreach (Match item in MenuItemTag().Matches(ReadApp(file)))
            {
                if (HeaderOf(item.Value).StartsWith("删除", StringComparison.Ordinal))
                {
                    Assert.Contains("MenuItemAssist.IsDanger=\"True\"", item.Value, StringComparison.Ordinal);
                }
            }
        }
    }

    [Theory]
    [InlineData("Header=\"编辑\"")]
    [InlineData("\"复制连接\"")]
    [InlineData("\"管理连接\"")]
    [InlineData("\"复制 IP\"")]
    [InlineData("测试连接（Ping）")]
    [InlineData("\"Traceroute\"")]
    [InlineData("\"打开 Web\"")]
    public void 不再出现旧叫法与占位项(string legacy)
    {
        foreach (var file in MenuFiles)
        {
            Assert.DoesNotContain(legacy, ReadApp(file), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Views/Pages/ConnectionsPage.xaml", "ConnectionRowMenu")]
    [InlineData("Views/Pages/HomePage.xaml", "HomeRowMenu")]
    public void 连接行菜单在两个入口提供同样的常用动作(string file, string menuKey)
    {
        var headers = MenuItemsOf(ReadApp(file), menuKey).Select(HeaderOf).ToList();

        foreach (var expected in new[] { "连接", "新建会话", "重新连接", "断开连接", "测试连接", "文件传输…", "复制地址", "编辑连接", "收藏", "在工作台中显示" })
        {
            Assert.Contains(expected, headers);
        }
    }

    [Fact]
    public void 托盘菜单使用应用自己的菜单样式()
    {
        var source = ReadApp("Services/TrayService.cs");

        Assert.DoesNotContain("ContextMenuStrip", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolStripMenuItem", source, StringComparison.Ordinal);
        Assert.Contains("PlacementMode.MousePoint", source, StringComparison.Ordinal);
    }

    private static List<string> MenuItemsOf(string markup, string menuKey)
    {
        var start = markup.IndexOf($"<ContextMenu x:Key=\"{menuKey}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"未找到菜单 {menuKey}");
        var end = markup.IndexOf("</ContextMenu>", start, StringComparison.Ordinal);
        var body = markup[start..end];

        // 只取顶层菜单项：子菜单占位项（缩进更深的禁用项）不参与排序约定。
        return MenuItemTag().Matches(body)
            .Select(m => m.Value)
            .Where(v => !v.Contains("IsEnabled=\"False\"", StringComparison.Ordinal))
            .ToList();
    }

    private static string HeaderOf(string item)
    {
        var match = HeaderAttribute().Match(item);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string ReadApp(string relative)
        => File.ReadAllText(Path.Combine(RepoRoot(), "src", "RemoteFlow.App", relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RemoteFlow.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("未找到仓库根目录。");
    }

    [GeneratedRegex("<MenuItem\\b[^>]*>", RegexOptions.Singleline)]
    private static partial Regex MenuItemTag();

    [GeneratedRegex("Header=\"([^\"]*)\"")]
    private static partial Regex HeaderAttribute();
}
