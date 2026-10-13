using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RemoteFlow.Presentation.Host;

namespace RemoteFlow.App.Services;

/// <summary>
/// Windows 的更新落地。发布形态是单文件 exe。
/// <para>
/// <b>一切只在进程退出的最后一步进行</b>：单文件应用按需从 exe 自身读取尚未加载的程序集，
/// 运行期间动了 exe 会让后续的延迟加载全部失败（实测：换掉后退出清理即抛 FileNotFoundException）。
/// 所以 <see cref="Apply"/> 只做可写性检查并登记更新包，落地由 <see cref="CompletePendingUpdateOnExit"/>
/// 在全部收尾完成、释放单实例互斥体之后执行。
/// </para>
/// <para>
/// <b>文件名跟随版本</b>：当前文件名是发布产物的命名形式（<c>RemoteFlow-v0.25.0-win-x64.exe</c>）时，
/// 新版本以它自己的产物名（<c>RemoteFlow-v0.25.1-win-x64.exe</c>）写到同一目录，文件名与版本始终一致。
/// 路径因此会变：开机启动项与桌面 / 开始菜单 / 任务栏上指向旧文件的快捷方式在此一并改指向新文件；
/// 旧文件由新版本启动后删除。文件名是用户自己改过的（如 <c>RemoteFlow.exe</c>）时保持原名，原地替换。
/// </para>
/// </summary>
public sealed partial class WpfUpdateInstaller(ILogger<WpfUpdateInstaller> logger) : IUpdateInstaller
{
    /// <summary>更新后重启时带的参数：新进程据此等待旧进程退出并释放单实例互斥体。</summary>
    public const string AfterUpdateArgument = "--after-update";

    /// <summary>退出时落地失败、原版本被重新拉起时带的参数：启动后提示用户改为手动更新。</summary>
    public const string UpdateFailedArgument = "--update-failed";

    private const string BackupSuffix = ".old";

    /// <summary>新文件旁的标记文件后缀：内容是被它取代的旧文件名，新版本启动后据此删除旧文件。</summary>
    private const string ReplacesSuffix = ".replaces";

    private const string DownloadSuffix = ".download";

    private static string? s_stagedPackage;
    private static bool s_restartPending;

    /// <summary>
    /// 仅单文件发布形态支持应用内更新。开发态（多文件输出）下程序集是 exe 旁边的独立 dll，
    /// 只替换 apphost 会弄坏运行目录，因此不允许。
    /// </summary>
    public bool CanSelfUpdate =>
        Environment.ProcessPath is { Length: > 0 } path
        && !File.Exists(Path.ChangeExtension(path, ".dll"));

    public void Apply(string verifiedPackagePath)
    {
        var target = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前可执行文件路径。");

        // 现在就确认所在目录可写：退出时已经没有界面可以报错，权限问题必须提前暴露给用户。
        var probe = target + ".update-probe";
        using (File.Create(probe, 1, FileOptions.DeleteOnClose))
        {
        }

        s_stagedPackage = verifiedPackagePath;
        logger.LogInformation(
            "更新包已校验并登记，将在退出时落地为 {NewName}",
            Path.GetFileName(ResolveNewPath(target, verifiedPackagePath)));
    }

    public void Restart()
    {
        // 只登记意图：落地与拉起新版本都在 App.OnExit 的最后一步完成。
        s_restartPending = true;
        if (System.Windows.Application.Current.MainWindow is Views.MainWindow window)
        {
            window.RequestExit();
        }
        else
        {
            System.Windows.Application.Current.Shutdown(0);
        }
    }

    public void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "打开下载页失败");
        }
    }

    /// <summary>
    /// 新版本落在哪个路径：当前文件名是发布产物命名时，用更新包自己的产物名（同目录）；否则保持原路径。
    /// </summary>
    internal static string ResolveNewPath(string currentPath, string packagePath)
    {
        var packageName = Path.GetFileName(packagePath);
        if (packageName.EndsWith(DownloadSuffix, StringComparison.OrdinalIgnoreCase))
        {
            packageName = packageName[..^DownloadSuffix.Length];
        }

        if (!ReleaseArtifactName().IsMatch(Path.GetFileName(currentPath)) || !ReleaseArtifactName().IsMatch(packageName))
        {
            return currentPath;
        }

        return Path.Combine(Path.GetDirectoryName(currentPath) ?? string.Empty, packageName);
    }

    /// <summary>
    /// 退出收尾的最后一步调用（单实例互斥体已释放之后）：有登记的更新包就落地；
    /// 用户点了「立即重启」则随后拉起程序。用户只是正常退出时也会完成落地，下次启动即为新版本。
    /// 此时日志已关闭，任何失败都保持原文件可用。
    /// </summary>
    public static void CompletePendingUpdateOnExit()
    {
        var package = s_stagedPackage;
        var restart = s_restartPending;
        s_stagedPackage = null;
        s_restartPending = false;

        if (Environment.ProcessPath is not { Length: > 0 } current)
        {
            return;
        }

        var launchPath = current;
        var succeeded = true;
        if (package is not null)
        {
            var newPath = ResolveNewPath(current, package);
            succeeded = string.Equals(newPath, current, StringComparison.OrdinalIgnoreCase)
                ? TryReplaceInPlace(current, package)
                : TryInstallSideBySide(current, newPath, package);
            if (succeeded)
            {
                launchPath = newPath;
            }
        }

        if (!restart)
        {
            return;
        }

        try
        {
            var argument = succeeded ? AfterUpdateArgument : UpdateFailedArgument;
            Process.Start(new ProcessStartInfo(launchPath, argument) { UseShellExecute = false });
        }
        catch (Exception)
        {
            // 拉不起来用户手动再开即可。
        }
    }

    /// <summary>
    /// 文件名跟随版本的落地方式：新文件写到同目录的新名字，旧文件原样不动（它还在运行，之后由新版本删除）。
    /// 需要用到延迟加载的步骤（注册表、快捷方式）都发生在旧文件仍完好的时候。
    /// </summary>
    private static bool TryInstallSideBySide(string current, string newPath, string package)
    {
        try
        {
            File.Copy(package, newPath, overwrite: true);
        }
        catch (Exception)
        {
            return false;
        }

        // 新文件已就位。以下都是「尽力而为」：失败不回滚，新旧文件都在，用户仍可手动处理。
        TryRun(() => File.WriteAllText(newPath + ReplacesSuffix, Path.GetFileName(current)));
        TryRun(() => WpfLaunchOnStartupService.RetargetRegistration(current, newPath));
        TryRun(() => ShortcutRetargeter.Retarget(current, newPath));
        TryDelete(package);
        return true;
    }

    /// <summary>文件名不是发布产物命名时的落地方式：旧文件改名为 .old，新文件写到原路径。</summary>
    private static bool TryReplaceInPlace(string target, string package)
    {
        var backup = target + BackupSuffix;
        try
        {
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            File.Move(target, backup);
        }
        catch (Exception)
        {
            return false;
        }

        try
        {
            File.Copy(package, target, overwrite: false);
        }
        catch (Exception)
        {
            // 新文件没写成：把旧文件改回去，当前安装保持可用。
            TryDelete(target);
            TryRun(() => File.Move(backup, target));
            return false;
        }

        TryDelete(package);
        return true;
    }

    /// <summary>
    /// 启动时清理上一次更新留下的旧版本：原地替换留下的 <c>.old</c>，以及标记文件指明的旧文件。
    /// 旧进程可能还在退出途中，删不掉就留到下次启动。
    /// </summary>
    public static void CleanupPreviousVersion()
    {
        if (Environment.ProcessPath is { Length: > 0 } path)
        {
            CleanupPreviousVersion(path);
        }
    }

    internal static void CleanupPreviousVersion(string path)
    {
        TryDelete(path + BackupSuffix);

        var marker = path + ReplacesSuffix;
        if (!File.Exists(marker))
        {
            return;
        }

        try
        {
            var oldName = File.ReadAllText(marker).Trim();
            var directory = Path.GetDirectoryName(path) ?? string.Empty;

            // 只删同目录下、符合发布产物命名、且不是自己的那个文件：标记内容不可信时什么都不删。
            if (oldName.Length > 0
                && oldName == Path.GetFileName(oldName)
                && ReleaseArtifactName().IsMatch(oldName)
                && !string.Equals(oldName, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
            {
                var oldPath = Path.Combine(directory, oldName);
                if (File.Exists(oldPath))
                {
                    File.Delete(oldPath);
                }
            }

            File.Delete(marker);
        }
        catch (Exception)
        {
            // 旧文件仍被占用等：保留标记，下次启动再试。
        }
    }

    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // 尽力而为的步骤，失败不影响更新结果。
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // 清理失败不影响功能。
        }
    }

    [GeneratedRegex(@"^RemoteFlow-v\d+\.\d+\.\d+-win-x64\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseArtifactName();
}
