using System.IO;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>连接工作台筛选胶囊与详情面板动作行的标记约定（与设计稿一致）。</summary>
public sealed class FilterPillMarkupTests
{
    [Fact]
    public void 筛选胶囊带字段前缀_协议标签状态排序四个齐全()
    {
        var styles = Read("Themes", "Controls.Collections.xaml");
        Assert.Contains("x:Key=\"Filter.Pill.ComboBox\"", styles, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FieldLabel\"", styles, StringComparison.Ordinal);
        Assert.Contains("Text=\"{TemplateBinding Tag}\"", styles, StringComparison.Ordinal);

        var page = Read("Views", "Pages", "ConnectionsPage.xaml");
        foreach (var field in new[] { "协议", "标签", "状态", "排序" })
        {
            Assert.Contains($"Tag=\"{field}\"", page, StringComparison.Ordinal);
        }

        // 字段名已在胶囊前缀里，下拉首项只写「全部」，不再是「全部协议 / 全部标签 / 全部状态」。
        Assert.DoesNotContain("全部协议", page, StringComparison.Ordinal);
        Assert.DoesNotContain("全部状态", page, StringComparison.Ordinal);
    }

    [Fact]
    public void 凭据页类型筛选与工作台同款胶囊()
    {
        var page = Read("Views", "Pages", "CredentialsPage.xaml");
        Assert.Contains("Style=\"{StaticResource Filter.Pill.ComboBox}\"", page, StringComparison.Ordinal);
        Assert.Contains("Tag=\"类型\"", page, StringComparison.Ordinal);
        Assert.Contains("ClearFiltersCommand", page, StringComparison.Ordinal);

        // 旧的 34 高方框下拉不应再用于筛选。
        Assert.DoesNotContain("Input.ComboBox", page, StringComparison.Ordinal);

        // 批量选择开关与工作台一样在筛选条里（Row 1 的搜索行），不在页头。
        var toggle = page.IndexOf("Style=\"{StaticResource MultiSelectToggle}\" Width=\"30\"", StringComparison.Ordinal);
        var filterRow = page.IndexOf("搜索框限宽 440", StringComparison.Ordinal);
        Assert.True(toggle > filterRow && filterRow > 0, "批量选择开关应位于筛选条内。");
    }

    [Fact]
    public void 最近连接的时间范围是同款胶囊而不是分段按钮()
    {
        var page = Read("Views", "Pages", "ConnectionsPage.xaml");
        Assert.Contains("Tag=\"时间\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("GroupName=\"RecentRange\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void 详情面板动作行是一行三个等宽的图标加短文字按钮()
    {
        var detail = Read("Views", "Pages", "ConnectionDetailPanel.xaml");
        foreach (var label in new[] { "Text=\"编辑\"", "Text=\"测试\"", "Text=\"传输\"" })
        {
            Assert.Contains(label, detail, StringComparison.Ordinal);
        }

        // 栏宽约 250、每个按钮约 80：图标加两个汉字放得下，四个汉字放不下（曾被切字）。
        // 完整名称留在悬停提示里。
        Assert.Contains("ToolTip=\"测试连接：DNS", detail, StringComparison.Ordinal);
        foreach (var icon in new[] { "Ui.Edit", "Ui.Pending", "Ui.FileTransfer", "Ui.Connect" })
        {
            Assert.Contains($"{{StaticResource {icon}}}", detail, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Text=\"Ping\"", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void 批量栏按钮都带图标()
    {
        var page = Read("Views", "Pages", "ConnectionsPage.xaml");
        Assert.Contains("x:Key=\"Batch.ButtonIcon\"", page, StringComparison.Ordinal);
        Assert.True(
            System.Text.RegularExpressions.Regex.Matches(page, "Style=\"\\{StaticResource Batch\\.ButtonIcon\\}\"").Count >= 7,
            "批量连接 / 移动到分组 / 添加标签 / 收藏 / 更多 / 删除 / 取消选择 都应带图标。");
    }

    private static string Read(params string[] segments)
    {
        var relative = new[] { "src", "RemoteFlow.App" }.Concat(segments).ToArray();
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
