using System.IO;

namespace RemoteFlow.App.Services;

/// <summary>
/// 把内嵌在主程序中的 xterm.js 前端资源释放到本地应用数据目录，供 WebView2
/// 通过虚拟主机映射加载。这样 SSH 终端不依赖 exe 旁边额外存在 Assets/Terminal。
/// </summary>
internal static class TerminalAssetStore
{
    private const string ResourcePrefix = "RemoteFlow.App.Assets.Terminal.";

    private static readonly string[] AssetFileNames =
    [
        "terminal.html",
        "xterm.js",
        "xterm.css",
        "addon-fit.js",
        "addon-webgl.js",
        "addon-search.js",
    ];

    private static readonly Lazy<string> DefaultDirectory = new(
        MaterializeDefaultDirectory,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>确保当前版本的终端资源已落盘，并返回可供 WebView2 映射的物理目录。</summary>
    public static string EnsureAvailable() => DefaultDirectory.Value;

    private static string MaterializeDefaultDirectory()
    {
        var assemblyVersion = typeof(TerminalAssetStore).Assembly.GetName().Version?.ToString(3)
                              ?? "unknown";
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemoteFlow",
            "TerminalAssets",
            assemblyVersion);

        ExtractTo(directory);
        return directory;
    }

    /// <summary>从程序集资源释放完整的终端前端文件集。</summary>
    internal static void ExtractTo(string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);

        var assembly = typeof(TerminalAssetStore).Assembly;

        foreach (var fileName in AssetFileNames)
        {
            var resourceName = ResourcePrefix + fileName;
            using var source = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"内嵌终端资源缺失：{fileName}");

            var destinationPath = Path.Combine(destinationDirectory, fileName);
            using var destination = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);

            source.CopyTo(destination);
        }
    }
}
