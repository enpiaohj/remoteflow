using RemoteFlow.Presentation.Terminal;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>PowerShell 会话的终端外观固定取 Windows 11 终端默认值，改 SSH 主题时不能连带改掉。</summary>
public sealed class WindowsTerminalLookTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rf-wt-look-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void 终端页面提供原生外观_数值与Windows终端默认一致()
    {
        TerminalAssetStore.ExtractTo(_directory);
        var page = File.ReadAllText(Path.Combine(_directory, "terminal.html"));
        var start = page.IndexOf("useWindowsTerminalLook()", StringComparison.Ordinal);
        Assert.True(start > 0);
        var look = page[start..page.IndexOf("setFont(family, size)", start, StringComparison.Ordinal)];

        // Campbell 配色（背景 / 前景 / 黄 / 亮红）、Cascadia Mono 12pt = 16px、8px 内边距、竖线光标。
        foreach (var expected in new[] { "'#0C0C0C'", "'#CCCCCC'", "'#C19C00'", "'#E74856'", "Cascadia Mono", "fontSize = 16", "padding = '8px'", "cursorStyle = 'bar'" })
        {
            Assert.Contains(expected, look, StringComparison.Ordinal);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
