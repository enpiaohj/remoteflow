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
}
