using System.Windows.Threading;
using RemoteFlow.Presentation.Host;

namespace RemoteFlow.App.Services;

/// <summary>创建基于 WPF <see cref="DispatcherTimer"/> 的 <see cref="IUiTimer"/>。</summary>
public sealed class WpfUiTimerFactory : IUiTimerFactory
{
    public IUiTimer Create(TimeSpan interval) => new WpfUiTimer(interval);
}

internal sealed class WpfUiTimer : IUiTimer
{
    private readonly DispatcherTimer _timer;

    public WpfUiTimer(TimeSpan interval)
    {
        _timer = new DispatcherTimer { Interval = interval };
    }

    public TimeSpan Interval
    {
        get => _timer.Interval;
        set => _timer.Interval = value;
    }

    public event EventHandler? Tick
    {
        add => _timer.Tick += value;
        remove => _timer.Tick -= value;
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public void Dispose() => _timer.Stop();
}
