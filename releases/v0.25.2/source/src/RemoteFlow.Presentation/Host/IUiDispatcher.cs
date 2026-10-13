namespace RemoteFlow.Presentation.Host;

/// <summary>
/// UI 线程调度抽象。让 ViewModel 无需引用具体 UI 框架即可把工作切回 UI 线程。
/// <para>
/// 协议库的状态 / 数据事件常在后台线程触发，ViewModel 更新绑定属性前须切回 UI 线程。
/// WPF 下由 <c>Dispatcher</c> 实现，Avalonia 下由 <c>Dispatcher.UIThread</c> 实现，
/// 测试 / 无头环境下由同步实现（直接执行）。
/// </para>
/// </summary>
public interface IUiDispatcher
{
    /// <summary>当前调用是否已在 UI 线程上。同步实现恒返回 true。</summary>
    bool IsOnUiThread { get; }

    /// <summary>把 <paramref name="action"/> 异步排到 UI 线程执行（不等待完成）。</summary>
    void Post(Action action);

    /// <summary>
    /// 便捷模板：不在 UI 线程时把 <paramref name="action"/> 重新排到 UI 线程并返回 true
    /// （调用方应随即 return）；已在 UI 线程时返回 false（调用方继续同步执行）。
    /// <para>对应此前散落各处的
    /// <c>if (d is not null &amp;&amp; !d.CheckAccess()) { d.BeginInvoke(...); return; }</c> 写法。</c></para>
    /// </summary>
    bool RequeueIfNeeded(Action action)
    {
        if (IsOnUiThread)
        {
            return false;
        }

        Post(action);
        return true;
    }
}

/// <summary>
/// 同步 <see cref="IUiDispatcher"/>：一切立即在当前线程执行。
/// 用于单元测试与无 UI 线程的场景。
/// </summary>
public sealed class SynchronousUiDispatcher : IUiDispatcher
{
    public static SynchronousUiDispatcher Instance { get; } = new();

    public bool IsOnUiThread => true;

    public void Post(Action action) => action();
}
