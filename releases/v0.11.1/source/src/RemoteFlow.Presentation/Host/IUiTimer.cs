namespace RemoteFlow.Presentation.Host;

/// <summary>
/// UI 线程定时器抽象。<see cref="Tick"/> 在 UI 线程上触发。
/// <para>
/// 目前仅用于会话时长「每秒 +1」的展示刷新。WPF 下由 <c>DispatcherTimer</c> 实现，
/// Avalonia 下由 <c>DispatcherTimer</c> 实现，测试下可用手动触发实现。
/// </para>
/// </summary>
public interface IUiTimer : IDisposable
{
    TimeSpan Interval { get; set; }

    /// <summary>定时触发。始终在 UI 线程上。</summary>
    event EventHandler Tick;

    void Start();

    void Stop();
}

/// <summary>
/// 创建 <see cref="IUiTimer"/> 的工厂。注入到 ViewModel，避免 ViewModel 直接 new 平台类型。
/// </summary>
public interface IUiTimerFactory
{
    IUiTimer Create(TimeSpan interval);
}
