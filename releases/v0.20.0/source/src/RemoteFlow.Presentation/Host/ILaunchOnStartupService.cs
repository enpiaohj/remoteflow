namespace RemoteFlow.Presentation.Host;

/// <summary>
/// 「开机自启」注册。各平台机制不同：
/// Windows 写 <c>HKCU\...\Run</c>；macOS 用 <c>SMAppService</c> / LaunchAgent plist。
/// <para>ViewModel 只表达「打开 / 关闭自启」，具体落地交给平台实现。</para>
/// </summary>
public interface ILaunchOnStartupService
{
    /// <summary>设置是否开机自启。失败应抛异常，由调用方回滚开关并提示。</summary>
    void SetEnabled(bool enabled);
}

/// <summary>无操作实现：平台未提供自启能力时使用（设置仍会持久化到 AppSettings）。</summary>
public sealed class NoOpLaunchOnStartupService : ILaunchOnStartupService
{
    public void SetEnabled(bool enabled) { }
}
