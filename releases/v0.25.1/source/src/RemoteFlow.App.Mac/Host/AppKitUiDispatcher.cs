using AppKit;
using RemoteFlow.Presentation.Host;

namespace RemoteFlow.App.Mac.Host;

/// <summary>AppKit 实现：把工作排到主线程。</summary>
public sealed class AppKitUiDispatcher : IUiDispatcher
{
    public bool IsOnUiThread => NSThread.IsMain;

    public void Post(Action action) => NSApplication.SharedApplication.BeginInvokeOnMainThread(action);
}
