using RemoteFlow.Core.FileTransfer;
using Xunit;

namespace RemoteFlow.Core.Tests;

/// <summary>
/// 远端文件名落到本机前的清洗。服务端返回的名字是不可信输入，
/// 这里的每条规则都对应一种真实的客户端漏洞（路径穿越、设备名、NTFS 备用数据流等）。
/// </summary>
public class LocalNameSanitizerTests
{
    [Theory]
    [InlineData("report.pdf")]
    [InlineData("文件 1.txt")]
    [InlineData(".bashrc")]
    [InlineData("a-b_c (1).tar.gz")]
    public void 正常名称原样保留且不标记已改名(string name)
    {
        var result = LocalNameSanitizer.Sanitize(name);

        Assert.NotNull(result);
        Assert.Equal(name, result.Value.Name);
        Assert.False(result.Value.Changed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    public void 空名与点目录无法安全落盘返回null(string? name)
        => Assert.Null(LocalNameSanitizer.Sanitize(name));

    [Theory]
    [InlineData("..\\..\\Startup\\x.exe", ".._.._Startup_x.exe")]
    [InlineData("a/b", "a_b")]
    [InlineData("../../etc/passwd", ".._.._etc_passwd")]
    [InlineData("a:evil", "a_evil")]
    [InlineData("a:b:$DATA", "a_b_$DATA")]
    [InlineData("we*ird?<n>|\"ame", "we_ird__n___ame")]
    public void 路径分隔符与Windows非法字符被替换成下划线(string input, string expected)
    {
        var result = LocalNameSanitizer.Sanitize(input);

        Assert.NotNull(result);
        Assert.Equal(expected, result.Value.Name);
        Assert.True(result.Value.Changed);
    }

    [Fact]
    public void 控制字符被替换()
    {
        var result = LocalNameSanitizer.Sanitize("a\tb\nc\u007fd");

        Assert.Equal("a_b_c_d", result!.Value.Name);
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("con", "_con")]
    [InlineData("NUL", "_NUL")]
    [InlineData("PRN.txt", "_PRN.txt")]
    [InlineData("aux.tar.gz", "_aux.tar.gz")]
    [InlineData("COM1", "_COM1")]
    [InlineData("LPT9.log", "_LPT9.log")]
    [InlineData("CONOUT$", "_CONOUT$")]
    public void Windows保留设备名加前缀(string input, string expected)
        => Assert.Equal(expected, LocalNameSanitizer.Sanitize(input)!.Value.Name);

    [Theory]
    [InlineData("CONSOLE")]
    [InlineData("COM10")]
    [InlineData("LPT")]
    [InlineData("nullable.txt")]
    public void 只是包含保留名前缀的普通名称不受影响(string name)
        => Assert.Equal(name, LocalNameSanitizer.Sanitize(name)!.Value.Name);

    [Theory]
    [InlineData("name.", "name_")]
    [InlineData("name ", "name_")]
    [InlineData("name..", "name._")]
    [InlineData("CON.", "CON_")]
    public void 结尾的点或空格被替换(string input, string expected)
        => Assert.Equal(expected, LocalNameSanitizer.Sanitize(input)!.Value.Name);

    [Fact]
    public void 超长名称截断且保留扩展名()
    {
        var input = new string('a', 400) + ".pdf";

        var result = LocalNameSanitizer.Sanitize(input)!.Value;

        Assert.Equal(LocalNameSanitizer.MaxNameLength, result.Name.Length);
        Assert.EndsWith(".pdf", result.Name);
        Assert.True(result.Changed);
    }

    [Fact]
    public void 超长名称无扩展名时直接截断()
    {
        var result = LocalNameSanitizer.Sanitize(new string('x', 500))!.Value;

        Assert.Equal(LocalNameSanitizer.MaxNameLength, result.Name.Length);
    }

    [Fact]
    public void 清洗后的名称不含任何路径分隔符()
    {
        string[] hostile = ["..\\a", "../a", "a/..", "a\\..\\b", "/etc/passwd", "C:\\Windows\\x", "\\\\host\\share"];

        foreach (var name in hostile)
        {
            var result = LocalNameSanitizer.Sanitize(name);

            Assert.NotNull(result);
            Assert.DoesNotContain('/', result.Value.Name);
            Assert.DoesNotContain('\\', result.Value.Name);
            Assert.DoesNotContain(':', result.Value.Name);
        }
    }

    // ── ResolveUnder：落盘路径兜底校验 ──

    [Fact]
    public void ResolveUnder在目录内拼出完整路径()
    {
        var root = Path.Combine(Path.GetTempPath(), "rf-name-test");

        var path = LocalNameSanitizer.ResolveUnder(root, "sub", "file.txt");

        Assert.Equal(Path.GetFullPath(Path.Combine(root, "sub", "file.txt")), path);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    public void ResolveUnder拒绝点段与空段(string segment)
    {
        var root = Path.Combine(Path.GetTempPath(), "rf-name-test");

        var ex = Assert.Throws<FileTransferException>(() => LocalNameSanitizer.ResolveUnder(root, "a", segment));

        Assert.Equal(FileTransferErrorCode.InvalidName, ex.Code);
    }

    [Fact]
    public void ResolveUnder即使上游漏了清洗也拦住越界()
    {
        var root = Path.Combine(Path.GetTempPath(), "rf-name-test");

        var ex = Assert.Throws<FileTransferException>(
            () => LocalNameSanitizer.ResolveUnder(root, "..", "elsewhere.txt"));
        Assert.Equal(FileTransferErrorCode.InvalidName, ex.Code);

        // 单段里夹带分隔符的穿越（Windows 与 POSIX 都算作越界）。
        var traversal = Path.Combine("..", "..", "x.txt");
        var ex2 = Assert.Throws<FileTransferException>(() => LocalNameSanitizer.ResolveUnder(root, traversal));
        Assert.Equal(FileTransferErrorCode.InvalidName, ex2.Code);
    }

    [Fact]
    public void ResolveUnder不把同前缀的兄弟目录当作目录内()
    {
        var parent = Path.Combine(Path.GetTempPath(), "rf-name-test-parent");
        var root = Path.Combine(parent, "dl");
        var sibling = Path.Combine("..", "dl-evil", "x.txt");

        var ex = Assert.Throws<FileTransferException>(() => LocalNameSanitizer.ResolveUnder(root, sibling));

        Assert.Equal(FileTransferErrorCode.InvalidName, ex.Code);
    }
}
