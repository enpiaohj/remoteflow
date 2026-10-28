using System.Text.RegularExpressions;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 安全约束：远程会话里用户敲的每个键都可能是密码，任何日志都不能记录按键内容。
/// 历史上 VNC 视图曾在 Information 级别逐字记录 TextInput / KeyDown（含 keysym），这里防止回归。
/// </summary>
public sealed class InputLoggingSafetyTests
{
    private static readonly string[] SessionSources =
    [
        "src/RemoteFlow.Protocol.Vnc/VncSession.cs",
        "src/RemoteFlow.App/Views/Sessions/VncSessionView.cs",
        "src/RemoteFlow.App/Views/Sessions/RdpSessionView.cs",
        "src/RemoteFlow.App/Views/Sessions/SshSessionView.cs",
    ];

    private static string ReadSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RemoteFlow.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    [Theory]
    [InlineData("src/RemoteFlow.Protocol.Vnc/VncSession.cs")]
    [InlineData("src/RemoteFlow.App/Views/Sessions/VncSessionView.cs")]
    public void VNC不记录按键或文本输入内容(string file)
    {
        var source = ReadSource(file);

        Assert.DoesNotContain("TextInput char", source, StringComparison.Ordinal);
        Assert.DoesNotContain("KeyDown key=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("KeyUp key=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("keysym=", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/RemoteFlow.Protocol.Vnc/VncSession.cs")]
    [InlineData("src/RemoteFlow.App/Views/Sessions/VncSessionView.cs")]
    public void 指针事件日志只允许Trace级别(string file)
    {
        var source = ReadSource(file);

        // 每秒几十条的输入日志不能出现在 Information / Debug。
        var loud = Regex.Matches(source, @"_logger\.Log(Information|Debug|Warning)\(\s*""\[VNC INPUT\]");
        Assert.Empty(loud);
    }

    [Fact]
    public void 会话视图不在日志里引用输入文本变量()
    {
        foreach (var file in SessionSources)
        {
            var source = ReadSource(file);
            // 日志调用里不得出现 e.Text（TextInput 的内容）。
            Assert.DoesNotMatch(@"_logger\.Log\w+\([^;]*\be\.Text\b", source);
        }
    }
}
