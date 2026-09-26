using RemoteFlow.Infrastructure;

namespace RemoteFlow.Presentation.Terminal;

/// <summary>
/// 把内嵌的 xterm.js 前端资源释放到本地应用数据目录，供 WebView 宿主加载。
/// 这样 SSH 终端不依赖可执行文件旁边额外存在 Terminal/Assets 目录。
/// <para>
/// 本类原先位于 <c>RemoteFlow.App</c> 且为 <c>internal</c>，资源逻辑名绑死在该程序集上，
/// macOS 侧既访问不到也读不出资源。移入 Presentation 后两端 UI 工程共用同一份实现与资产。
/// </para>
/// <para>
/// 释放目录按程序集版本分目录，使升级后不会读到旧版本残留的前端文件。
/// </para>
/// </summary>
public static class TerminalAssetStore
{
    private const string ResourcePrefix = "RemoteFlow.Presentation.Terminal.Assets.";

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
        ComputeDefaultDirectory,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>释放目录的写入锁：多会话并发触达时只释放一次（双检）。</summary>
    private static readonly object ExtractLock = new();

    /// <summary>
    /// 确保当前版本的终端资源已落盘，并返回可供 WebView 映射的物理目录。
    /// <para>
    /// 目录被外部删除（磁盘清理工具、误删）时会<b>自动重新释放</b>——否则新会话
    /// 加载不到 terminal.html，表现为连接闪断、信任窗口被吞（v0.17.2 后真实发生）。
    /// </para>
    /// </summary>
    public static string EnsureAvailable()
    {
        var directory = DefaultDirectory.Value;

        if (!Directory.Exists(directory))
        {
            lock (ExtractLock)
            {
                if (!Directory.Exists(directory))
                {
                    ExtractTo(directory);
                }
            }
        }

        return directory;
    }

    private static string ComputeDefaultDirectory()
    {
        var assemblyVersion = typeof(TerminalAssetStore).Assembly.GetName().Version?.ToString(3)
                              ?? "unknown";

        // 复用 AppPaths 的默认根目录，使终端资产与其余应用数据同处一地
        // （Windows: %LOCALAPPDATA%\RemoteFlow，macOS: ~/Library/Application Support/RemoteFlow）。
        var directory = Path.Combine(
            AppPaths.ResolveDefaultDataDirectory(),
            "TerminalAssets",
            assemblyVersion);

        ExtractTo(directory);
        return directory;
    }

    /// <summary>从程序集资源释放完整的终端前端文件集。</summary>
    public static void ExtractTo(string destinationDirectory)
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
