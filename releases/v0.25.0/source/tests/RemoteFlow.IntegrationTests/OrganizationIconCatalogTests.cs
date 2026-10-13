using RemoteFlow.Core.Models;
using Xunit;

namespace RemoteFlow.IntegrationTests;

public sealed class OrganizationIconCatalogTests
{
    [Fact]
    public void 分组图标目录包含两个内置图标和十二个自定义图标()
    {
        Assert.Equal(14, GroupIconCatalog.All.Count);
        Assert.Equal(12, GroupIconCatalog.CustomOptions.Count);
        Assert.Equal("GroupIcon.MyDevices", GroupIconCatalog.MyDevicesKey);
        Assert.Equal("GroupIcon.Ungrouped", GroupIconCatalog.UngroupedKey);
        Assert.Equal(GroupIconCatalog.All.Count, GroupIconCatalog.All.Select(x => x.Key).Distinct().Count());
        Assert.DoesNotContain(GroupIconCatalog.CustomOptions, x => x.Key == GroupIconCatalog.MyDevicesKey);
        Assert.DoesNotContain(GroupIconCatalog.CustomOptions, x => x.Key == GroupIconCatalog.UngroupedKey);
    }

    [Theory]
    [InlineData("", "GroupIcon.Folder")]
    [InlineData("legacy-folder", "GroupIcon.Folder")]
    [InlineData("GroupIcon.Cloud", "GroupIcon.Cloud")]
    public void 自定义分组图标坏值回退文件夹(string value, string expected)
        => Assert.Equal(expected, GroupIconCatalog.NormalizeCustom(value));

    [Fact]
    public void 标签图标目录包含十个唯一选项()
    {
        Assert.Equal(10, TagIconCatalog.All.Count);
        Assert.Equal("TagIcon.Tag", TagIconCatalog.DefaultKey);
        Assert.Equal(TagIconCatalog.All.Count, TagIconCatalog.All.Select(x => x.Key).Distinct().Count());
    }

    [Theory]
    [InlineData("", "TagIcon.Tag")]
    [InlineData("unknown", "TagIcon.Tag")]
    [InlineData("TagIcon.Security", "TagIcon.Security")]
    public void 标签图标坏值回退普通标签(string value, string expected)
        => Assert.Equal(expected, TagIconCatalog.Normalize(value));
}
