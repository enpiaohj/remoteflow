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
    /// 登记已校验的更新包，程序退出时落地（运行期间不能动可执行文件）。
    /// 平台实现负责让开机启动项、快捷方式在更新后继续有效（文件名随版本变化时一并改指向）。
    /// 所在目录不可写等能提前发现的问题在此抛异常。
    /// </summary>
    void Apply(string verifiedPackagePath);

    /// <summary>退出当前进程，完成落地后启动新版本。</summary>
    void Restart();

    void OpenUrl(string url);
}
