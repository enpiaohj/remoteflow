using RemoteFlow.Presentation.Terminal;
using Xunit;

namespace RemoteFlow.IntegrationTests;

public sealed class TerminalAssetStoreTests
{
    private static readonly string[] ExpectedFiles =
    [
        "terminal.html",
        "xterm.js",
        "xterm.css",
        "addon-fit.js",
        "addon-webgl.js",
        "addon-search.js",
    ];

    [Fact]
    public void ExtractTo_WritesEveryBundledTerminalAsset()
    {
        using var workspace = new TempWorkspace();

        TerminalAssetStore.ExtractTo(workspace.Root);

        foreach (var fileName in ExpectedFiles)
        {
            var path = Path.Combine(workspace.Root, fileName);
            Assert.True(File.Exists(path), $"缺少终端资源：{fileName}");
            Assert.True(new FileInfo(path).Length > 0, $"终端资源为空：{fileName}");
        }
    }

    [Fact]
    public void EnsureAvailable_Reextracts_WhenDirectoryWasDeletedExternally()
    {
        // 磁盘清理工具 / 误删把资产目录删掉后，新会话必须自愈重建——
        // 否则 WebView 加载不到 terminal.html，会话闪断、信任窗口被吞
        // （v0.17.2 发布后真实发生：清理工具删目录 → 新连 SSH 闪断）。
        var directory = TerminalAssetStore.EnsureAvailable();
        Directory.Delete(directory, recursive: true);
        Assert.False(Directory.Exists(directory));

        var restored = TerminalAssetStore.EnsureAvailable();
        Assert.Equal(directory, restored);
        Assert.True(Directory.Exists(restored));
        foreach (var fileName in ExpectedFiles)
        {
            Assert.True(File.Exists(Path.Combine(restored, fileName)), $"自愈后缺少：{fileName}");
        }
    }
}
