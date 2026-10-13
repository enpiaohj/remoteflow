using CoreFoundation;
using Foundation;
using RemoteFlow.Presentation.Host;

namespace RemoteFlow.App.Mac.Host;

/// <summary>创建基于 <see cref="NSTimer"/> 的 <see cref="IUiTimer"/>（Tick 在主线程 RunLoop 上）。</summary>
public sealed class AppKitUiTimerFactory : IUiTimerFactory
{
    public IUiTimer Create(TimeSpan interval) => new AppKitUiTimer(interval);
}

internal sealed class AppKitUiTimer : IUiTimer
{
    private NSTimer? _timer;

    public AppKitUiTimer(TimeSpan interval) => Interval = interval;

    public TimeSpan Interval { get; set; }

    public event EventHandler? Tick;

    public void Start()
    {
        Stop();
        _timer = NSTimer.CreateRepeatingTimer(Interval, _ => Tick?.Invoke(this, EventArgs.Empty));
        NSRunLoop.Main.AddTimer(_timer, NSRunLoopMode.Common);
    }

    public void Stop()
    {
        _timer?.Invalidate();
        _timer = null;
    }

    public void Dispose() => Stop();
}
