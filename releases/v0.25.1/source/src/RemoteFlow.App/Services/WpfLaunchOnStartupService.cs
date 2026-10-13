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

    internal static string BuildCommand(string executablePath) => $"\"{executablePath}\" {StartupArgument}";

    /// <summary>已有启动项指向当前可执行文件、但没有启动参数时返回 true。</summary>
    internal static bool NeedsUpgrade(string registered, string executablePath)
    {
        var trimmed = registered.Trim();
        var path = trimmed.StartsWith('"')
            ? trimmed[1..].Split('"')[0]
            : trimmed.Split(' ')[0];

        return string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase)
            && !trimmed.Contains(StartupArgument, StringComparison.OrdinalIgnoreCase);
    }
}
