namespace RemoteFlow.Presentation.Host;

/// <summary>
/// 平台相关的更新落地：替换当前可执行文件、重启、打开网页。
/// 没有实现的平台不注册更新视图模型，关于页不显示更新区。
/// </summary>
public interface IUpdateInstaller
{
    /// <summary>当前运行形态能否原地自更新（例如开发态多文件运行不能）。不能时只提供「打开下载页」。</summary>
    bool CanSelfUpdate { get; }

    /// <summary>
    /// 登记已校验的更新包，程序退出时用它替换当前可执行文件（运行期间不能换）。
    /// <b>路径保持不变</b>——开机启动项、快捷方式、任务栏固定都继续有效。
    /// 所在目录不可写等能提前发现的问题在此抛异常。
    /// </summary>
    void Apply(string verifiedPackagePath);

    /// <summary>退出当前进程，完成替换后启动新版本。</summary>
    void Restart();

    void OpenUrl(string url);
}
