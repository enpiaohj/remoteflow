using RemoteFlow.Core.FileTransfer;
using Xunit;

namespace RemoteFlow.Core.Tests;

/// <summary>远端虚拟路径的规范化与拼接。路径规范化是防路径穿越的第一道防线，必须永不越过根。</summary>
public class RemotePathTests
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("   ", "/")]
    [InlineData("/", "/")]
    [InlineData("a", "/a")]
    [InlineData("/a/b/", "/a/b")]
    [InlineData("//a///b//", "/a/b")]
    [InlineData("/a/./b", "/a/b")]
    [InlineData("/a/b/..", "/a")]
    [InlineData("/a/b/../c", "/a/c")]
    public void 规范化补前导斜杠并折叠多余分隔符与点段(string? input, string expected)
        => Assert.Equal(expected, RemotePath.Normalize(input));

    [Theory]
    [InlineData("/..", "/")]
    [InlineData("/../..", "/")]
    [InlineData("/a/../../..", "/")]
    [InlineData("../../etc/passwd", "/etc/passwd")]
    [InlineData("/home/u/../../../root", "/root")]
    public void 上溯永远停在根不会越界(string input, string expected)
        => Assert.Equal(expected, RemotePath.Normalize(input));

    [Fact]
    public void 反斜杠在POSIX路径里是普通字符不被当作分隔符()
    {
        Assert.Equal("/a\\b", RemotePath.Normalize("/a\\b"));
        Assert.Equal("/..\\x", RemotePath.Normalize("/..\\x"));
    }

    [Theory]
    [InlineData("/", "a", "/a")]
    [InlineData("/home/u", "file.txt", "/home/u/file.txt")]
    [InlineData("/home/u/", "文件.txt", "/home/u/文件.txt")]
    [InlineData("home", "x", "/home/x")]
    public void 拼接单层名称(string parent, string name, string expected)
        => Assert.Equal(expected, RemotePath.Combine(parent, name));

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("/etc")]
    [InlineData("../x")]
    [InlineData("a\0b")]
    public void 拼接拒绝非单层或带穿越的名称(string name)
        => Assert.Throws<ArgumentException>(() => RemotePath.Combine("/home/u", name));

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/a", "/")]
    [InlineData("/a/b", "/a")]
    [InlineData("/a/b/c/", "/a/b")]
    [InlineData("/..", "/")]
    public void 上一级目录根的上一级仍是根(string path, string expected)
        => Assert.Equal(expected, RemotePath.GetParent(path));

    [Theory]
    [InlineData("/", "")]
    [InlineData("/a", "a")]
    [InlineData("/a/b.txt", "b.txt")]
    [InlineData("/a/b/", "b")]
    public void 末段名称根返回空(string path, string expected)
        => Assert.Equal(expected, RemotePath.GetName(path));

    [Fact]
    public void 面包屑第一段是根并逐级累加完整路径()
    {
        var segments = RemotePath.GetSegments("/home/user/docs");

        Assert.Equal(["/", "home", "user", "docs"], segments.Select(s => s.Name));
        Assert.Equal(["/", "/home", "/home/user", "/home/user/docs"], segments.Select(s => s.FullPath));
    }

    [Fact]
    public void 根的面包屑只有根一段()
    {
        var segments = RemotePath.GetSegments("/");

        var only = Assert.Single(segments);
        Assert.Equal("/", only.FullPath);
    }

    [Theory]
    [InlineData("/a", "/a", true)]
    [InlineData("/a", "/a/b", true)]
    [InlineData("/a", "/a/b/c", true)]
    [InlineData("/a", "/ab", false)]
    [InlineData("/a", "/", false)]
    [InlineData("/a/b", "/a", false)]
    [InlineData("/", "/anything", true)]
    [InlineData("/a", "/a/../b", false)]
    public void 判断路径是否位于某目录之下按路径段比较(string root, string path, bool expected)
        => Assert.Equal(expected, RemotePath.IsSameOrUnder(root, path));

    [Theory]
    [InlineData("a", true)]
    [InlineData("文件 1.txt", true)]
    [InlineData(".hidden", true)]
    [InlineData("a\\b", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\0", false)]
    public void 合法条目名判定(string name, bool expected)
        => Assert.Equal(expected, RemotePath.IsValidEntryName(name));
}
