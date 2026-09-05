namespace RemoteFlow.Infrastructure;

/// <summary>
/// 应用数据路径。默认全部位于 <c>%LOCALAPPDATA%\RemoteFlow</c>，
/// 保证「本地优先、不依赖云服务」，且免安装运行时也能正常写入。
/// </summary>
public sealed class AppPaths
{
    public const string AppFolderName = "RemoteFlow";

    public AppPaths(string? overrideDataDirectory = null)
    {
        DataDirectory = string.IsNullOrWhiteSpace(overrideDataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName)
            : overrideDataDirectory;

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    /// <summary>数据根目录。</summary>
    public string DataDirectory { get; }

    /// <summary>SQLite 数据库文件。存放连接、分组、标签、历史与凭据元数据。</summary>
    public string DatabasePath => Path.Combine(DataDirectory, "remoteflow.db");

    /// <summary>凭据保险库文件。存放 DPAPI 加密后的 Secret 密文，与数据库分离。</summary>
    public string VaultPath => Path.Combine(DataDirectory, "vault.dat");

    /// <summary>应用设置文件。</summary>
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>日志目录。</summary>
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>日志文件模板（按天滚动）。</summary>
    public string LogFileTemplate => Path.Combine(LogDirectory, "remoteflow-.log");
}
