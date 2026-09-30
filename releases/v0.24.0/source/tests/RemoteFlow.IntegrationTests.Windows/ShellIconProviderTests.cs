using RemoteFlow.App.Services;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>远端文件图标取自系统（资源管理器同款）：文件夹与常见扩展名都能取到，且按扩展名缓存。</summary>
public sealed class ShellIconProviderTests
{
    [Theory]
    [InlineData("readme.txt", false)]
    [InlineData("archive.zip", false)]
    [InlineData("no-extension", false)]
    [InlineData("logs", true)]
    public void 系统图标可取到且已冻结(string name, bool isDirectory)
    {
        var icon = ShellIconProvider.Get(name, isDirectory);

        Assert.NotNull(icon);
        Assert.True(icon.IsFrozen);
        Assert.True(icon.PixelWidth > 0 && icon.PixelHeight > 0);
    }

    [Fact]
    public void 同扩展名共用缓存且不区分大小写()
    {
        Assert.Same(ShellIconProvider.Get("a.TXT", false), ShellIconProvider.Get("b.txt", false));
    }

    [Fact]
    public void 目录名带扩展名也仍取文件夹图标()
    {
        Assert.Same(ShellIconProvider.Get("dir1", true), ShellIconProvider.Get("backup.txt", true));
    }
}
