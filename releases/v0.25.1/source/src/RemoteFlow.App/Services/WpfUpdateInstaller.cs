using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using RemoteFlow.Presentation.Host;

namespace RemoteFlow.App.Services;

/// <summary>
/// Windows 的更新落地。发布形态是单文件 exe。
/// <para>
/// <b>替换只在进程退出的最后一步进行</b>：单文件应用按需从 exe 自身读取尚未加载的程序集，
/// 运行期间把 exe 换掉会让后续的延迟加载全部失败（实测：换掉后退出清理即抛 FileNotFoundException）。
/// 所以 <see cref="Apply"/> 只做可写性检查并登记更新包，真正的「旧文件改名 .old → 新文件写到原路径」
/// 由 <see cref="CompletePendingUpdateOnExit"/> 在全部收尾完成、释放单实例互斥体之后执行。
/// </para>
/// <para>
/// <b>路径保持不变</b>是有意为之：开机启动项（注册表 Run 值）、桌面快捷方式、任务栏固定都指向这个路径，
/// 换文件名会让它们全部失效。代价是文件名里的版本号可能与实际版本不一致，以应用内显示的版本为准。
/// </para>
/// </summary>
public sealed class WpfUpdateInstaller(ILogger<WpfUpdateInstaller> logger) : IUpdateInstaller
{
    /// <summary>更新后重启时带的参数：新进程据此等待旧进程退出并释放单实例互斥体。</summary>
    public const string AfterUpdateArgument = "--after-update";

    /// <summary>退出时替换失败、原版本被重新拉起时带的参数：启动后提示用户改为手动更新。</summary>
    public const string UpdateFailedArgument = "--update-failed";

    private const string BackupSuffix = ".old";

    private static string? s_stagedPackage;
    private static bool s_restartPending;

    /// <summary>
    /// 仅单文件发布形态支持原地替换。开发态（多文件输出）下程序集是 exe 旁边的独立 dll，
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

        // 退出阶段要用到的类型此刻先加载好，避免替换文件之后再触发按需加载。
        _ = Process.GetCurrentProcess().Id;

        s_stagedPackage = verifiedPackagePath;
        logger.LogInformation("更新包已校验并登记，将在退出时替换程序文件");
    }

    public void Restart()
    {
        // 只登记意图：替换与拉起新版本都在 App.OnExit 的最后一步完成。
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
    /// 退出收尾的最后一步调用（单实例互斥体已释放之后）：有登记的更新包就替换程序文件；
    /// 用户点了「立即重启」则随后拉起程序。用户只是正常退出时也会完成替换，下次启动即为新版本。
    /// 此时日志已关闭，任何失败都回滚到原文件并保持可用。
    /// </summary>
    public static void CompletePendingUpdateOnExit()
    {
        var package = s_stagedPackage;
        var restart = s_restartPending;
        s_stagedPackage = null;
        s_restartPending = false;

        if (Environment.ProcessPath is not { Length: > 0 } target)
        {
            return;
        }

        var replaced = false;
        if (package is not null)
        {
            replaced = TryReplace(target, package);
        }

        if (!restart)
        {
            return;
        }

        try
        {
            var argument = package is null || replaced ? AfterUpdateArgument : UpdateFailedArgument;
            Process.Start(new ProcessStartInfo(target, argument) { UseShellExecute = false });
        }
        catch (Exception)
        {
            // 拉不起来用户手动再开即可。
        }
    }

    private static bool TryReplace(string target, string package)
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
            try
            {
                File.Move(backup, target);
            }
            catch (Exception)
            {
                // 极端情况：两步都失败。旧文件仍以 .old 留在原目录，可手动改回。
            }

            return false;
        }

        TryDelete(package);
        return true;
    }

    /// <summary>启动时清理上一次更新留下的旧版本文件。删不掉（仍被占用等）就留到下次。</summary>
    public static void CleanupBackup()
    {
        if (Environment.ProcessPath is { Length: > 0 } path)
        {
            TryDelete(path + BackupSuffix);
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
}
