using Microsoft.Win32;
using RemoteFlow.Presentation.Host;

namespace RemoteFlow.App.Services;

/// <summary>
/// Windows 实现：开机自启 = 在 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>
/// 写 / 删当前可执行文件路径。
/// <para>从 SettingsPageViewModel 移出，使 VM 保持平台无关（技术方案 §6.6）。</para>
/// </summary>
public sealed class WpfLaunchOnStartupService : ILaunchOnStartupService
{
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

            key.SetValue(StartupValueName, $"\"{executablePath}\"");
        }
        else
        {
            key.DeleteValue(StartupValueName, throwOnMissingValue: false);
        }
    }
}
