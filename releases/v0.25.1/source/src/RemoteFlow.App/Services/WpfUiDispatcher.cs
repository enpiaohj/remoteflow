using RemoteFlow.Presentation.Host;

namespace RemoteFlow.App.Services;

/// <summary>
/// WPF 实现：把工作排到 WPF <see cref="System.Windows.Threading.Dispatcher"/>（UI 线程）。
/// 与原有 CheckAccess / BeginInvoke 语义一致：无 Application 时直接在当前线程执行。
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    public bool IsOnUiThread
    {
        get
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            return dispatcher is null || dispatcher.CheckAccess();
        }
    }

    public void Post(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }
}
