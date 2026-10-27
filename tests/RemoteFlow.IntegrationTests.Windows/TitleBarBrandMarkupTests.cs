using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 标题栏左上角的品牌区必须是一个整体：Logo 与产品名同色、中间没有竖线。
/// 回归：图标栏外框曾跨过标题栏那一行，右边框线与栏底色把 Logo 和产品名切成两块（看起来像拼接）。
/// </summary>
public sealed class TitleBarBrandMarkupTests
{
    [Fact]
    public void 图标栏外框只从标题栏下面开始_不跨标题栏行()
    {
        var markup = ReadMainWindow();

        // 图标栏外框必须落在 Row 1，且不自带右边框线：
        // 内容区白卡片自带一圈描边（含左上圆角），再画直线会在圆角处出界、与卡片左边框叠成双线。
        var rail = Regex.Match(
            markup,
            "<Border Grid\\.Row=\"(\\d)\" Grid\\.Column=\"0\"\\s+Background=\"\\{DynamicResource Bg\\.Sidebar\\}\">",
            RegexOptions.Singleline);
        Assert.True(rail.Success, "未找到图标栏外框（应落在 Row 1，且没有 BorderThickness）。");
        Assert.Equal("1", rail.Groups[1].Value);
        Assert.DoesNotContain("Grid.RowSpan=\"2\" Grid.Column=\"0\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void 栏顶Logo在标题栏行内且与标题栏同色()
    {
        var markup = ReadMainWindow();

        var logo = Regex.Match(
            markup,
            "<Border Grid\\.Row=\"0\" Grid\\.Column=\"0\"\\s+Background=\"\\{DynamicResource Bg\\.Sidebar\\}\"",
            RegexOptions.Singleline);
        Assert.True(logo.Success, "栏顶 Logo 必须在标题栏行（Row 0），背景用 Bg.Sidebar 与标题栏、图标栏一致。");
    }

    [Fact]
    public void 标题栏与图标栏同色_外框连成L形_内容区圆角落在统一底色上()
    {
        var markup = ReadMainWindow();

        // 标题栏（Row 0, Column 1）与图标栏同用 Bg.Sidebar：否则内容区左上角的圆角会露出
        // 一块与图标栏不同的颜色，在图标栏顶端形成缺口。
        var titleBar = Regex.Match(
            markup,
            "<Grid Grid\\.Row=\"0\" Grid\\.Column=\"1\"\\s+Background=\"\\{DynamicResource Bg\\.Sidebar\\}\"",
            RegexOptions.Singleline);
        Assert.True(titleBar.Success, "标题栏背景应为 Bg.Sidebar。");
    }

    private static string ReadMainWindow()
    {
        var relative = new[] { "src", "RemoteFlow.App", "Views", "MainWindow.xaml" };
        foreach (var root in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory }.Distinct())
        {
            for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine([directory.FullName, .. relative]);
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }
            }
        }

        throw new FileNotFoundException($"未找到 {Path.Combine(relative)}。");
    }
}
