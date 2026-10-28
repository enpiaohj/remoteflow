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
/// 路径因此会变：开机启动项与桌面 / 开始菜单 / 任务栏上指向旧文件的快捷方式一并改指向新文件；
/// 旧文件由新版本启动后删除。文件名是用户自己改过的（如 <c>RemoteFlow.exe</c>）时保持原名，原地替换。
/// </para>
/// <para>
/// <b>失败处理约定</b>：新文件先写成 <c>.new</c> 临时名、核对大小后再改名到位，任何时刻目录里都不会出现
/// 名字正确但内容不完整的 exe；落地失败时原文件保持可用，并留下失败标记，下次启动提示用户；
/// 启动项与快捷方式的改指向在退出时做一次、新版本启动时再补一次，之后才删除旧文件。
/// </para>
/// </summary>
public sealed partial class WpfUpdateInstaller(ILogger<WpfUpdateInstaller> logger) : IUpdateInstaller
{
    /// <summary>更新后重启时带的参数：新进程据此等待旧进程退出并释放单实例互斥体。</summary>
    public const string AfterUpdateArgument = "--after-update";

    /// <summary>退出时落地失败、原版本被重新拉起时带的参数：与 <see cref="AfterUpdateArgument"/> 一样需要等待旧进程退出。</summary>
    public const string UpdateFailedArgument = "--update-failed";

    private const string BackupSuffix = ".old";

    /// <summary>新文件旁的标记文件后缀：内容是被它取代的旧文件名，新版本启动后据此善后并删除旧文件。</summary>
    private const string ReplacesSuffix = ".replaces";

    /// <summary>落地失败标记：放在仍在使用的原文件旁，下次启动时提示一次。</summary>
    private const string FailedSuffix = ".update-failed";

    /// <summary>写入中的新文件临时后缀：只有完整写完并核对大小后才改名到位。</summary>
    private const string StagingSuffix = ".new";

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
        if (System.Windows.Application.Current.MainWindow is not Views.MainWindow window)
        {
            System.Windows.Application.Current.Shutdown(0);
            return;
        }

        window.RequestExit();

        // 关闭可能被别的确认拦下（例如还有文件在传输时用户选了取消）。窗口还在就说明没有退出：
        // 撤销「待重启」，否则之后用户正常退出时程序会莫名其妙自己又启动起来。更新包仍然登记着，退出时照常落地。
        if (window.IsLoaded && PresentationSourceAlive(window))
        {
            s_restartPending = false;
            logger.LogInformation("重启被取消，更新将在下次退出时落地");
        }
    }

    private static bool PresentationSourceAlive(System.Windows.Window window)
        => System.Windows.PresentationSource.FromVisual(window) is not null;

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
    /// 此时日志已关闭，任何失败都保持原文件可用，并留下失败标记供下次启动提示。
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
            succeeded = Install(current, newPath, package);
            if (succeeded)
            {
                launchPath = newPath;
            }
            else
            {
                launchPath = ResolveUsablePath(current);
                TryRun(() => File.WriteAllText(launchPath + FailedSuffix, DateTimeOffset.Now.ToString("O")));
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
    /// 把更新包落地到 <paramref name="newPath"/>。成功返回 true；失败时 <paramref name="current"/> 保持可用。
    /// </summary>
    internal static bool Install(string current, string newPath, string package)
    {
        var sideBySide = !string.Equals(newPath, current, StringComparison.OrdinalIgnoreCase);

        // 1) 先把新文件完整写成临时名并核对大小。磁盘满、被强杀都只会留下一个 .new 临时文件，
        //    不会出现「名字正确但内容不完整」的 exe。
        var staging = newPath + StagingSuffix;
        try
        {
            File.Copy(package, staging, overwrite: true);
            if (new FileInfo(staging).Length != new FileInfo(package).Length)
            {
                TryDelete(staging);
                return false;
            }
        }
        catch (Exception)
        {
            TryDelete(staging);
            return false;
        }

        if (sideBySide)
        {
            // 2a) 文件名跟随版本：旧文件原样不动（它还在运行，之后由新版本删除），新文件改名到位。
            try
            {
                File.Move(staging, newPath, overwrite: true);
            }
            catch (Exception)
            {
                TryDelete(staging);
                return false;
            }

            // 新文件已就位。以下是「尽力而为」：这里没改成的，新版本启动时还会按标记再补一次。
            TryRun(() => File.WriteAllText(newPath + ReplacesSuffix, Path.GetFileName(current)));
            TryRun(() => WpfLaunchOnStartupService.RetargetRegistration(current, newPath));
            TryRun(() => ShortcutRetargeter.Retarget(current, newPath));
        }
        else
        {
            // 2b) 原地替换：旧文件改名为 .old，新文件改名到原路径；第二步失败就把旧文件改回去。
            var backup = current + BackupSuffix;
            try
            {
                TryDelete(backup);
                File.Move(current, backup);
            }
            catch (Exception)
            {
                TryDelete(staging);
                return false;
            }

            try
            {
                File.Move(staging, current);
            }
            catch (Exception)
            {
                TryRun(() => File.Move(backup, current));
                TryDelete(staging);
                return false;
            }
        }

        TryDelete(package);
        return true;
    }

    /// <summary>
    /// 落地失败后还能用来启动的文件：正常是原路径；原地替换连回滚都失败的极端情况下，原文件以 .old 留着。
    /// </summary>
    private static string ResolveUsablePath(string current)
        => File.Exists(current) || !File.Exists(current + BackupSuffix) ? current : current + BackupSuffix;

    /// <summary>
    /// 上次退出时更新是否落地失败（只报告一次：读取后即清除标记）。
    /// 覆盖「不重启、直接退出」的路径——那时没有进程可以带参数回来提示。
    /// </summary>
    public static bool ConsumeFailureMarker()
    {
        if (Environment.ProcessPath is not { Length: > 0 } path)
        {
            return false;
        }

        return ConsumeFailureMarker(path);
    }

    internal static bool ConsumeFailureMarker(string path)
    {
        var marker = path + FailedSuffix;
        if (!File.Exists(marker))
        {
            return false;
        }

        TryDelete(marker);
        return true;
    }

    /// <summary>
    /// 启动时善后上一次更新：清掉遗留的临时文件；原地替换留下的 <c>.old</c>；
    /// 文件名变了的更新，按标记把启动项与快捷方式再改指向一次（退出时可能没改成），然后删除旧文件。
    /// 旧进程可能还在退出途中，删不掉就保留标记，下次再试。
    /// </summary>
    public static void CleanupPreviousVersion()
    {
        if (Environment.ProcessPath is { Length: > 0 } path)
        {
            CleanupPreviousVersion(path, retarget: true);
        }
    }

    internal static void CleanupPreviousVersion(string path, bool retarget = false)
    {
        TryDelete(path + BackupSuffix);
        TryDelete(path + StagingSuffix);

        var marker = path + ReplacesSuffix;
        if (!File.Exists(marker))
        {
            return;
        }

        try
        {
            var oldName = File.ReadAllText(marker).Trim();
            var directory = Path.GetDirectoryName(path) ?? string.Empty;

            // 只处理同目录下、符合发布产物命名、且不是自己的那个文件：标记内容不可信时什么都不动。
            if (oldName.Length > 0
                && oldName == Path.GetFileName(oldName)
                && ReleaseArtifactName().IsMatch(oldName)
                && !string.Equals(oldName, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
            {
                var oldPath = Path.Combine(directory, oldName);

                if (retarget)
                {
                    // 第二次机会：旧文件删除之前，确保启动项与快捷方式已经指向新文件。
                    TryRun(() => WpfLaunchOnStartupService.RetargetRegistration(oldPath, path));
                    TryRun(() => ShortcutRetargeter.Retarget(oldPath, path));
                }

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

    /// <summary>文件名是不是发布产物的命名形式（<c>RemoteFlow-vX.Y.Z-win-x64.exe</c>）。</summary>
    internal static bool IsReleaseArtifactName(string fileName) => ReleaseArtifactName().IsMatch(fileName);

    /// <summary>
    /// <paramref name="target"/> 是不是「以前某次更新漏改的、已经不存在的旧版本产物」：与 <paramref name="currentPath"/> 同目录、
    /// 同为发布产物命名、不是它自己、且文件已不存在。启动项 / 快捷方式指向这样的目标一定是失效的，可以安全改指向当前文件。
    /// </summary>
    internal static bool IsStaleSibling(string target, string currentPath)
    {
        if (string.IsNullOrWhiteSpace(target)
            || string.Equals(target, currentPath, StringComparison.OrdinalIgnoreCase)
            || !IsReleaseArtifactName(Path.GetFileName(currentPath))
            || !IsReleaseArtifactName(Path.GetFileName(target)))
        {
            return false;
        }

        var sameDirectory = string.Equals(
            Path.GetDirectoryName(target), Path.GetDirectoryName(currentPath), StringComparison.OrdinalIgnoreCase);
        return sameDirectory && !File.Exists(target);
    }

    /// <summary>
    /// 启动时的兜底：以前的更新没来得及改的启动项 / 快捷方式（指向已被删除的旧版本文件）改指向当前文件。
    /// 不依赖 <c>.replaces</c> 标记，所以更新后的首次启动之后任何时候都能自愈。
    /// </summary>
    public static void RepairStaleEntries()
    {
        if (Environment.ProcessPath is not { Length: > 0 } current || !IsReleaseArtifactName(Path.GetFileName(current)))
        {
            return;
        }

        TryRun(() => WpfLaunchOnStartupService.RepairStaleRegistration(current));
        TryRun(() => ShortcutRetargeter.RepairStale(current));
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
