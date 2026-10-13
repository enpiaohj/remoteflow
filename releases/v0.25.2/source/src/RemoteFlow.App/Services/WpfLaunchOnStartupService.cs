using Microsoft.Win32;
using RemoteFlow.Presentation.Host;

namespace RemoteFlow.App.Services;

/// <summary>
/// Windows 实现：开机自启 = 在 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>
/// 写 / 删当前可执行文件路径。
/// <para>
/// 启动项带 <see cref="StartupArgument"/> 参数：随系统启动时应用直接驻留托盘、不弹主窗口；
/// 用户手动打开（不带该参数）则正常显示窗口。
/// </para>
/// <para>从 SettingsPageViewModel 移出，使 VM 保持平台无关（技术方案 §6.6）。</para>
/// </summary>
public sealed class WpfLaunchOnStartupService : ILaunchOnStartupService
{
    /// <summary>开机启动项带的参数：表示本次是随系统启动，而不是用户手动打开。</summary>
    public const string StartupArgument = "--startup";

    private const string StartupRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "RemoteFlow";

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, writable: true);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executablePath))
            {
                return;
            }

            key.SetValue(StartupValueName, BuildCommand(executablePath));
        }
        else
        {
            key.DeleteValue(StartupValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// 把旧版本写入的、不带启动参数的启动项升级为带参数的形式。
    /// 只处理「指向当前这个可执行文件」的启动项：从别的目录运行的副本（试用包等）不会改动已有注册。
    /// </summary>
    public static void UpgradeExistingRegistration()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executablePath))
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, writable: true);
        if (key?.GetValue(StartupValueName) is not string current)
        {
            return;
        }

        if (NeedsUpgrade(current, executablePath))
        {
            key.SetValue(StartupValueName, BuildCommand(executablePath));
        }
    }

    /// <summary>
    /// 应用内更新改了可执行文件名之后调用：启动项仍指向旧文件时改指向新文件（带启动参数）。
    /// 没有启动项、或启动项指向别的文件时不做任何事。
    /// </summary>
    public static void RetargetRegistration(string oldExecutablePath, string newExecutablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, writable: true);
        if (key?.GetValue(StartupValueName) is string current && PointsTo(current, oldExecutablePath))
        {
            key.SetValue(StartupValueName, BuildCommand(newExecutablePath));
        }
    }

    internal static string BuildCommand(string executablePath) => $"\"{executablePath}\" {StartupArgument}";

    /// <summary>已有启动项指向当前可执行文件、但没有启动参数时返回 true。</summary>
    internal static bool NeedsUpgrade(string registered, string executablePath)
        => PointsTo(registered, executablePath)
           && !registered.Contains(StartupArgument, StringComparison.OrdinalIgnoreCase);

    /// <summary>启动项命令行里的可执行文件路径是否就是给定路径（不区分大小写，带不带引号都认）。</summary>
    internal static bool PointsTo(string registered, string executablePath)
    {
        var trimmed = registered.Trim();
        var path = trimmed.StartsWith('"')
            ? trimmed[1..].Split('"')[0]
            : trimmed.Split(' ')[0];

        return string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase);
    }
}
