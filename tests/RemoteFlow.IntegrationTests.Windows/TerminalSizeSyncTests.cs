using RemoteFlow.App.Views.Sessions;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 远端终端尺寸必须等于页面最后一次上报的值。曾在连接前后用「就绪时」的旧值重发，
/// 远端以为屏幕比实际大，clear 之后光标错位、多出一段滚动区。
/// </summary>
public sealed class TerminalSizeSyncTests
{
    [Fact]
    public void 套用字号后尺寸变化_之后重发的是新值而不是就绪时的旧值()
    {
        var sent = new List<(int, int)>();
        var sync = new TerminalSizeSync((c, r, _, _) => sent.Add((c, r)));

        sync.Report(121, 29, 1000, 520); // 就绪时（默认字号）
        sync.Report(110, 24, 1000, 520); // 套用终端外观后页面重新上报
        sync.Reapply();                  // 连接成功后重发

        Assert.Equal([(121, 29), (110, 24), (110, 24)], sent);
    }

    [Fact]
    public void 没有上报过尺寸时不下发_无效尺寸忽略()
    {
        var sent = new List<(int, int)>();
        var sync = new TerminalSizeSync((c, r, _, _) => sent.Add((c, r)));

        sync.Reapply();
        sync.Report(0, 24, 0, 0);
        sync.Report(80, -1, 0, 0);
        sync.Reapply();

        Assert.Empty(sent);
    }

    [Fact]
    public void 终端视图只经尺寸同步下发_不直接用就绪消息重发()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RemoteFlow.slnx")))
        {
            directory = directory.Parent;
        }

        var source = File.ReadAllText(Path.Combine(directory!.FullName, "src", "RemoteFlow.App", "Views", "Sessions", "TerminalSessionView.cs"));

        Assert.DoesNotContain("_session.Resize(", source, StringComparison.Ordinal);
    }
}
