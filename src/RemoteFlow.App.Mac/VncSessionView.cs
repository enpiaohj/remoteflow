using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MarcusW.VncClient;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Protocol.Vnc;
using VncPosition = MarcusW.VncClient.Position;

namespace RemoteFlow.App.Mac;

/// <summary>
/// VNC 会话视图：把 <see cref="VncRenderTarget"/>（IFrameSource，BGRA32 缓冲）的画面
/// 呈现在 Avalonia <see cref="WriteableBitmap"/> 上，并把键鼠事件经
/// <see cref="VncSession.SendPointerEvent"/> / <see cref="VncSession.SendKeyEvent"/> 发回远端。
/// <para>
/// 复用共享层渲染面抽象：协议只维护缓冲 + 脏标记，本视图按显示帧率
/// <see cref="IFrameSource.TryCopyLatestFrame"/> 取帧——不引用任何 WPF，Windows 侧
/// VncSessionView 用同一接口对接 WPF 位图（技术方案 §6.5）。
/// </para>
/// </summary>
public sealed class VncSessionView : UserControl
{
    private const double FrameIntervalMs = 33; // ≈30fps 取帧

    private readonly VncSession _session;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _overlay = new()
    {
        Foreground = Brushes.Gray,
        FontSize = 14,
        IsVisible = false,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };

    private WriteableBitmap? _bitmap;
    private int _bmpW;
    private int _bmpH;
    private FrameSize _pendingSize;
    private DispatcherTimer? _timer;
    private bool _disposed;

    public VncSessionView(VncSession session)
    {
        _session = session;

        Background = Brushes.Black;
        Focusable = true;

        Content = new Grid { Children = { _image, _overlay } };

        // 指针 / 键盘输入。
        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;
        PointerMoved += OnPointerMoved;
        PointerWheelChanged += OnPointerWheel;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        PointerPressed += (_, _) => Focus();

        // 帧尺寸变化（协议线程）→ UI 线程重建位图。
        ((IFrameSource)_session.RenderTarget).FrameSizeChanged += OnFrameSizeChanged;
        _session.ClipboardTextReceived += OnClipboardTextReceived;
        _session.StateChanged += OnStateChanged;

        AttachedToVisualTree += (_, _) => StartRenderLoop();
        DetachedFromVisualTree += (_, _) => StopRenderLoop();
    }

    // ── 渲染循环 ────────────────────────────────────────────────

    private void StartRenderLoop()
    {
        StopRenderLoop();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FrameIntervalMs) };
        _timer.Tick += (_, _) => PumpFrame();
        _timer.Start();
    }

    private void StopRenderLoop()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void PumpFrame()
    {
        if (_disposed || _bitmap is null || _session.State != RemoteFlow.Core.Models.ConnectionState.Connected)
        {
            return;
        }

        if (_pendingSize is { Width: > 0, Height: > 0 } pending)
        {
            _pendingSize = default;
            RecreateBitmap(pending);
        }

        using var fb = _bitmap.Lock();
        _ = _session.RenderTarget.TryCopyLatestFrame(
            destination: fb.Address,
            destinationCapacityBytes: (long)fb.RowBytes * fb.Size.Height,
            destinationStride: fb.RowBytes,
            expectedWidth: fb.Size.Width,
            expectedHeight: fb.Size.Height);
    }

    private void OnFrameSizeChanged(object? sender, FrameSize size)
    {
        // 协议线程：登记尺寸，等渲染循环在 UI 线程重建位图。
        _pendingSize = size;
    }

    private void RecreateBitmap(FrameSize size)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        _bitmap?.Dispose();
        _bitmap = new WriteableBitmap(
            new PixelSize(size.Width, size.Height),
            new Vector(96, 96),
            PixelFormats.Bgra8888,
            AlphaFormat.Opaque);
        _bmpW = size.Width;
        _bmpH = size.Height;
        _image.Source = _bitmap;
    }

    // ── 指针 ─────────────────────────────────────────────────────

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ToRemotePosition(e) is not { } pos)
        {
            return;
        }

        var button = ToVncButton(e);
        if (button != MouseButtons.None)
        {
            _session.SendPointerEvent(pos, button);
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (ToRemotePosition(e) is not { } pos)
        {
            return;
        }

        _session.SendPointerEvent(pos, MouseButtons.None);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (ToRemotePosition(e) is not { } pos)
        {
            return;
        }

        _session.SendPointerEvent(pos, ToVncButton(e));
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (ToRemotePosition(e) is not { } pos)
        {
            return;
        }

        var wheel = e.Delta.Y > 0 ? MouseButtons.WheelUp : MouseButtons.WheelDown;
        _session.SendPointerEvent(pos, wheel);
        _session.SendPointerEvent(pos, MouseButtons.None);
    }

    private static MouseButtons ToVncButton(PointerEventArgs e)
    {
        var props = e.GetCurrentPoint(null).Properties;
        var buttons = MouseButtons.None;
        if (props.IsLeftButtonPressed) buttons |= MouseButtons.Left;
        if (props.IsMiddleButtonPressed) buttons |= MouseButtons.Middle;
        if (props.IsRightButtonPressed) buttons |= MouseButtons.Right;
        return buttons;
    }

    /// <summary>把控件内指针坐标换算为远端画面坐标（按 Image 的实际缩放与居中）。</summary>
    private VncPosition? ToRemotePosition(PointerEventArgs e)
    {
        if (_bitmap is null)
        {
            return null;
        }

        var local = e.GetPosition(_image);
        var avail = _image.Bounds.Size;
        var (scale, offsetX, offsetY) = FitScale(avail.Width, avail.Height);

        var x = (int)Math.Round((local.X - offsetX) / scale);
        var y = (int)Math.Round((local.Y - offsetY) / scale);

        if (x < 0 || y < 0 || x >= _bmpW || y >= _bmpH)
        {
            return null;
        }

        return new VncPosition(x, y);
    }

    private (double Scale, double OffsetX, double OffsetY) FitScale(double availWidth, double availHeight)
    {
        if (_bitmap is null || availWidth <= 0 || availHeight <= 0)
        {
            return (1, 0, 0);
        }

        var scale = Math.Min(availWidth / _bmpW, availHeight / _bmpH);
        var offsetX = (availWidth - _bmpW * scale) / 2;
        var offsetY = (availHeight - _bmpH * scale) / 2;
        return (scale, offsetX, offsetY);
    }

    // ── 键盘 ─────────────────────────────────────────────────────

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (AvaloniaVncKeyMapper.IsModifier(e.Key))
        {
            _session.SendKeyEvent(AvaloniaVncKeyMapper.MapModifier(e.Key), true);
            return;
        }

        if (AvaloniaVncKeyMapper.TryMap(e.Key, shift) is { } sym)
        {
            _session.SendKeyEvent(sym, true);
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (AvaloniaVncKeyMapper.IsModifier(e.Key))
        {
            _session.SendKeyEvent(AvaloniaVncKeyMapper.MapModifier(e.Key), false);
            return;
        }

        if (AvaloniaVncKeyMapper.TryMap(e.Key, shift) is { } sym)
        {
            _session.SendKeyEvent(sym, false);
        }
    }

    // ── 剪贴板 / 状态 ────────────────────────────────────────────

    private void OnClipboardTextReceived(object? sender, string text)
    {
        // 协议线程触发。正文可能含敏感内容，不写日志。
        // Avalonia 12 的剪贴板接口已改为数据流式（IAsyncDataTransfer，无 SetTextAsync），
        // 写文本需自建传输对象或直接 P/Invoke NSPasteboard——骨架阶段留 TODO，
        // 待接入系统剪贴板时实现（WPF 侧经 Clipboard.SetText，见技术方案 §7.5）。
        Dispatcher.UIThread.Post(() =>
        {
            // TODO(macos): 写 NSPasteboard —— 收到远端复制内容 {text}。
        });
    }

    private void OnStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (e.NewState is RemoteFlow.Core.Models.ConnectionState.Failed or RemoteFlow.Core.Models.ConnectionState.Disconnected)
            {
                _overlay.Text = e.NewState == RemoteFlow.Core.Models.ConnectionState.Failed
                    ? $"连接失败：{_session.ErrorMessage ?? _session.ErrorCode.ToString()}"
                    : "已断开";
                _overlay.IsVisible = true;
            }
            else if (e.NewState == RemoteFlow.Core.Models.ConnectionState.Connected)
            {
                _overlay.IsVisible = false;
            }
        });
    }

    /// <summary>释放：停表、退订、断开会话。</summary>
    public void DisposeView()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopRenderLoop();

        ((IFrameSource)_session.RenderTarget).FrameSizeChanged -= OnFrameSizeChanged;
        _session.ClipboardTextReceived -= OnClipboardTextReceived;
        _session.StateChanged -= OnStateChanged;

        _ = DisconnectQuietlyAsync();
    }

    private async Task DisconnectQuietlyAsync()
    {
        try
        {
            await _session.DisconnectAsync();
            _session.MarkClosed();
            await _session.DisposeAsync();
        }
        catch
        {
            // 断开过程中的异常静默处理：会话已释放，资源由协议层兜底。
        }
    }
}
