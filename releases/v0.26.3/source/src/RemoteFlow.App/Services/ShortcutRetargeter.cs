using System.IO;

namespace RemoteFlow.App.Services;

/// <summary>
/// 应用内更新改了可执行文件名之后，把仍指向旧文件的快捷方式改指向新文件：
/// 桌面、开始菜单、任务栏固定、快速启动栏里目标为旧路径的 <c>.lnk</c>。
/// <para>
/// 尽力而为：改不了的（没有权限、被占用、用户自己放在别处的快捷方式）跳过，不影响更新本身。
/// 只改「目标完全等于旧路径」的快捷方式，不碰其它任何文件。
/// </para>
/// </summary>
public static class ShortcutRetargeter
{
    /// <summary>返回成功改指向的快捷方式数量。</summary>
    public static int Retarget(string oldTarget, string newTarget)
        => Retarget(oldTarget, newTarget, EnumerateShortcuts());

    /// <summary>对给定的快捷方式文件执行改指向（测试用入口）。</summary>
    internal static int Retarget(string oldTarget, string newTarget, IEnumerable<string> shortcutFiles)
    {
        var updated = 0;
        dynamic? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null)
            {
                return 0;
            }

            shell = Activator.CreateInstance(type);
            if (shell is null)
            {
                return 0;
            }

            foreach (var link in shortcutFiles)
            {
                try
                {
                    var shortcut = shell.CreateShortcut(link);
                    string target = shortcut.TargetPath;
                    if (!string.Equals(target, oldTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    shortcut.TargetPath = newTarget;
                    shortcut.WorkingDirectory = Path.GetDirectoryName(newTarget) ?? string.Empty;
                    shortcut.Save();
                    updated++;
                }
                catch (Exception)
                {
                    // 单个快捷方式改不了就跳过。
                }
            }
        }
        catch (Exception)
        {
            // 没有脚本宿主等情况：整体放弃，不影响更新。
        }
        finally
        {
            if (shell is not null)
            {
                try
                {
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
                }
                catch (Exception)
                {
                    // 释放失败无关紧要。
                }
            }
        }

        return updated;
    }

    private static IEnumerable<string> EnumerateShortcuts()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roots = new (string Path, bool Recursive)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), false),
            (Environment.GetFolderPath(Environment.SpecialFolder.Programs), true),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), true),
            (Environment.GetFolderPath(Environment.SpecialFolder.Startup), false),
            (Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch"), true),
        };

        foreach (var (root, recursive) in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                continue;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(
                    root,
                    "*.lnk",
                    new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true });
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }
}
