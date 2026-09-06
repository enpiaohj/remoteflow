namespace RemoteFlow.Infrastructure;

/// <summary>
/// 应用数据路径。保证「本地优先、不依赖云服务」，且免安装运行时也能正常写入。
/// <para>
/// 默认目录由 <see cref="Environment.SpecialFolder.LocalApplicationData"/> 决定，
/// 各平台自然落到其惯例位置：
/// <list type="bullet">
///   <item>Windows：<c>%LOCALAPPDATA%\RemoteFlow</c></item>
///   <item>macOS：<c>~/Library/Application Support/RemoteFlow</c></item>
/// </list>
/// 无需按平台分支——.NET 已做正确映射，这一点由 <c>AppPathsTests</c> 锁定。
/// </para>
/// </summary>
public sealed class AppPaths
{
    public const string AppFolderName = "RemoteFlow";

    public AppPaths(string? overrideDataDirectory = null)
    {
        DataDirectory = string.IsNullOrWhiteSpace(overrideDataDirectory)
            ? ResolveDefaultDataDirectory()
            : overrideDataDirectory;

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    /// <summary>
    /// 计算默认数据目录，<b>不创建任何目录</b>。
    /// 拆成独立方法是为了让路径解析可被测试覆盖，而不必在测试中真的往用户目录里写东西。
    /// </summary>
    public static string ResolveDefaultDataDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);

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
