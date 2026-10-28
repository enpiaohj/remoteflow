using System.IO;

namespace RemoteFlow.App.Services;

/// <summary>
/// 应用内更新改了可执行文件名之后，把仍指向旧文件的快捷方式改指向新文件：
/// 桌面、开始菜单、任务栏固定、快速启动栏里目标为旧路径的 <c>.lnk</c>。
/// <para>
/// 尽力而为：改不了的（没有权限、被占用、用户自己放在别处的快捷方式）跳过，不影响更新本身。
/// 只改「目标完全等于旧路径」、或指向「同目录里已不存在的旧版本产物」（以前的更新漏改的）的快捷方式，不碰其它任何文件。
/// </para>
/// <para>
/// 改指向之后，快捷方式自己的文件名若就是旧文件名（如 <c>RemoteFlow-v0.27.3-win-x64.lnk</c>）则一并改成新文件名，
/// 免得开始菜单里一直显示过期的版本号；显式指定了图标路径且指向旧文件的，图标也跟着改。
/// 任务栏固定项只改目标、不改名（改名会让固定失效）。
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
                    var isOld = oldTarget.Length > 0 && string.Equals(target, oldTarget, StringComparison.OrdinalIgnoreCase);
                    if (!isOld && !WpfUpdateInstaller.IsStaleSibling(target, newTarget))
                    {
                        // 目标早已指向新文件、只是快捷方式名字还带着旧版本号（以前的更新只改了目标）：补改名。
                        if (string.Equals(target, newTarget, StringComparison.OrdinalIgnoreCase)
                            && WpfUpdateInstaller.IsReleaseArtifactName(Path.GetFileNameWithoutExtension(link) + ".exe"))
                        {
                            TryRenameToNewVersion(link, Path.Combine(
                                Path.GetDirectoryName(newTarget) ?? string.Empty,
                                Path.GetFileNameWithoutExtension(link) + ".exe"), newTarget);
                        }

                        continue;
                    }

                    shortcut.TargetPath = newTarget;
                    shortcut.WorkingDirectory = Path.GetDirectoryName(newTarget) ?? string.Empty;
                    string icon = shortcut.IconLocation;
                    if (icon.StartsWith(target, StringComparison.OrdinalIgnoreCase))
                    {
                        shortcut.IconLocation = newTarget + icon[target.Length..];
                    }

                    shortcut.Save();
                    updated++;
                    TryRenameToNewVersion(link, target, newTarget);
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

    /// <summary>启动时的兜底：指向已被删除的旧版本产物的快捷方式改指向当前文件。</summary>
    public static int RepairStale(string currentTarget)
        => Retarget(string.Empty, currentTarget, EnumerateShortcuts());

    /// <summary>快捷方式文件名就是旧产物名（不含扩展名）时改成新产物名；新名字已被占用或是任务栏固定项则不改。</summary>
    private static void TryRenameToNewVersion(string link, string oldTarget, string newTarget)
    {
        try
        {
            var linkName = Path.GetFileNameWithoutExtension(link);
            if (!string.Equals(linkName, Path.GetFileNameWithoutExtension(oldTarget), StringComparison.OrdinalIgnoreCase)
                || link.Contains(@"\User Pinned\", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var renamed = Path.Combine(
                Path.GetDirectoryName(link) ?? string.Empty, Path.GetFileNameWithoutExtension(newTarget) + ".lnk");
            if (!File.Exists(renamed))
            {
                File.Move(link, renamed);
            }
        }
        catch (Exception)
        {
            // 改名失败不影响已经改好的目标。
        }
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
