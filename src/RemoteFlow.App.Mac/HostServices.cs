using Avalonia.Styling;
using Avalonia.Threading;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.Host;

namespace RemoteFlow.App.Mac;

/// <summary>Avalonia 实现：把工作排到 UI 线程（Dispatcher.UIThread）。</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public bool IsOnUiThread => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}

/// <summary>创建基于 Avalonia DispatcherTimer 的 <see cref="IUiTimer"/>。</summary>
public sealed class AvaloniaUiTimerFactory : IUiTimerFactory
{
    public IUiTimer Create(TimeSpan interval) => new AvaloniaUiTimer { Interval = interval };
}

internal sealed class AvaloniaUiTimer : IUiTimer
{
    private readonly DispatcherTimer _timer = new();

    public TimeSpan Interval { get => _timer.Interval; set => _timer.Interval = value; }

    public event EventHandler? Tick
    {
        add => _timer.Tick += value;
        remove => _timer.Tick -= value;
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public void Dispose() => _timer.Stop();
}

/// <summary>
/// Avalonia 主题服务：映射浅/深到 <see cref="Avalonia.Styling.ThemeVariant"/>，
/// 记录 IsDark 供 SSH 终端等自绘区联动。跟随系统走 Avalonia 的 RequestedThemeVariant。
/// </summary>
public sealed class AvaloniaThemeService : IThemeService
{
    private readonly Avalonia.Application _app;

    public AvaloniaThemeService(Avalonia.Application app) => _app = app;

    public bool IsDark { get; private set; }

    public event EventHandler? EffectiveThemeChanged;

    public void Apply(AppTheme theme)
    {
        _app.RequestedThemeVariant = theme switch
        {
            AppTheme.Dark => Avalonia.Styling.ThemeVariant.Dark,
            AppTheme.Light => Avalonia.Styling.ThemeVariant.Light,
            _ => Avalonia.Styling.ThemeVariant.Default, // FollowSystem
        };

        var dark = _app.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        if (dark != IsDark)
        {
            IsDark = dark;
            EffectiveThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
