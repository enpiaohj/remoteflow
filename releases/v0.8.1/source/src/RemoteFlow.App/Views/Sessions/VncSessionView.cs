using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MarcusW.VncClient;
using Microsoft.Extensions.Logging;
using RemoteFlow.App.ViewModels;
using RemoteFlow.Core.Models;
using RemoteFlow.Protocol.Vnc;
using VncSize = MarcusW.VncClient.Size;

namespace RemoteFlow.App.Views.Sessions;

/// <summary>
/// VNC / macOS Screen Sharing 会话视图。
/// <para>
/// 渲染路径：协议线程写入非托管帧缓冲 → 本视图在 WPF 渲染回调中拷贝进
/// <see cref="WriteableBitmap"/>。只有真正产生新帧时才拷贝，空闲时不消耗 CPU。
/// </para>
/// </summary>
public sealed class VncSessionView : ContentControl, IDisposable
{
    private readonly VncSession _session;
    private readonly SessionTabViewModel _viewModel;
    private readonly ILogger<VncSessionView> _logger;
    private readonly Image _image;
    private readonly ScrollViewer _scroll;

    private WriteableBitmap? _bitmap;

    /// <summary>待重建位图的目标尺寸。由协议线程写入，UI 线程消费。</summary>
    private VncSize? _pendingSize;

    private bool _renderingHooked;
    private bool _connectStarted;
    private bool _disposed;

    public VncSessionView(VncSession session, SessionTabViewModel viewModel, ILogger<VncSessionView> logger)
    {
        _session = session;
        _viewModel = viewModel;
        _logger = logger;

        _image = new Image
        {
            // 远程画面是像素资产，用最近邻缩放保持文字锐利，避免插值糊化。
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = true
        };

        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);

        // 1:1 模式下远端桌面可能大于可视区，用 ScrollViewer 提供滚动；
        // 适应窗口模式下画面已缩放贴合，滚动条自动隐藏。
        _scroll = new ScrollViewer
        {
            Content = _image,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Focusable = false
        };

        Content = _scroll;
        Focusable = true;
        Background = Brushes.Black;

        // 视图铺满容器；画面本身在其中按 Uniform 居中缩放，超出部分留黑边。
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        _session.RenderTarget.FramebufferSizeChanged += OnFramebufferSizeChanged;
        _viewModel.ActionRequested += OnActionRequested;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // 按连接记住的档位初始化画面缩放（构造后 VM 已从 Profile 恢复 VncScale）。
        ApplyScaleMode();

        HookInput();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_renderingHooked)
        {
            CompositionTarget.Rendering += OnRendering;
            _renderingHooked = true;
        }

        if (_connectStarted)
        {
            return;
        }

        _connectStarted = true;
        await _session.ConnectAsync();

        _image.Focus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Tab 切走时停止取帧，避免为不可见的会话做无谓的位图拷贝。
        if (_renderingHooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingHooked = false;
        }
    }

    // ── 帧渲染 ────────────────────────────────────────────────────

    private void OnFramebufferSizeChanged(object? sender, VncSize size)
    {
        // 该事件来自协议线程，这里只登记尺寸，实际重建位图放到 UI 线程。
        _pendingSize = size;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_pendingSize is { } size)
        {
            _pendingSize = null;
            RecreateBitmap(size);
        }

        if (_bitmap is not null)
        {
            // RenderTo 内部有脏标记判断，无新帧时会直接返回，不做任何拷贝。
            _session.RenderTarget.RenderTo(_bitmap);
        }
    }

    private void RecreateBitmap(VncSize size)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        // Bgra32 与帧缓冲格式逐字节一致，拷贝时无需做像素转换。
        _bitmap = new WriteableBitmap(size.Width, size.Height, 96, 96, PixelFormats.Bgra32, null);
        _image.Source = _bitmap;
    }

    // ── 输入转发 ──────────────────────────────────────────────────

    private void HookInput()
    {
        _image.MouseMove += OnMouseMove;
        _image.MouseDown += OnMouseDown;
        _image.MouseUp += OnMouseUp;
        _image.MouseWheel += OnMouseWheel;

        // 用 PreviewKey* 抢在 WPF 焦点导航（Tab、方向键）之前拿到按键，
        // 否则这些键会被界面自身消费，永远送不到远端。
        _image.PreviewKeyDown += OnPreviewKeyDown;
        _image.PreviewKeyUp += OnPreviewKeyUp;
        _image.TextInput += OnTextInput;

        // 点击画面时把键盘焦点切到画面本身，保证后续按键能送到远端。
        // 具名 handler：Dispose 时才能退订，匿名 lambda 无法解挂会在视图释放后仍持有 _image 引用。
        _image.MouseDown += OnImageMouseDownFocus;
    }

    /// <summary>退订全部输入事件。与 <see cref="HookInput"/> 一一对应，幂等可重复调用。</summary>
    private void UnhookInput()
    {
        _image.MouseMove -= OnMouseMove;
        _image.MouseDown -= OnMouseDown;
        _image.MouseDown -= OnImageMouseDownFocus;
        _image.MouseUp -= OnMouseUp;
        _image.MouseWheel -= OnMouseWheel;
        _image.PreviewKeyDown -= OnPreviewKeyDown;
        _image.PreviewKeyUp -= OnPreviewKeyUp;
        _image.TextInput -= OnTextInput;
    }

    private void OnImageMouseDownFocus(object? sender, MouseButtonEventArgs e) => _image.Focus();

    /// <summary>
    /// 把控件坐标换算为远端桌面像素坐标。
    /// 画面按 Uniform 缩放居中显示，因此需要同时补偿缩放比例与居中留白。
    /// </summary>
    private Position? ToRemotePosition(Point point)
    {
        if (_bitmap is null || _image.ActualWidth <= 0 || _image.ActualHeight <= 0)
        {
            return null;
        }

        var scale = Math.Min(_image.ActualWidth / _bitmap.PixelWidth, _image.ActualHeight / _bitmap.PixelHeight);
        if (scale <= 0)
        {
            return null;
        }

        var renderedWidth = _bitmap.PixelWidth * scale;
        var renderedHeight = _bitmap.PixelHeight * scale;
        var offsetX = (_image.ActualWidth - renderedWidth) / 2;
        var offsetY = (_image.ActualHeight - renderedHeight) / 2;

        var x = (int)Math.Round((point.X - offsetX) / scale);
        var y = (int)Math.Round((point.Y - offsetY) / scale);

        // 画面之外（居中留白区域）的移动不应发给远端。
        if (x < 0 || y < 0 || x >= _bitmap.PixelWidth || y >= _bitmap.PixelHeight)
        {
            return null;
        }

        return new Position(x, y);
    }

    private static MouseButtons GetPressedButtons()
    {
        var buttons = MouseButtons.None;

        if (Mouse.LeftButton == MouseButtonState.Pressed) buttons |= MouseButtons.Left;
        if (Mouse.MiddleButton == MouseButtonState.Pressed) buttons |= MouseButtons.Middle;
        if (Mouse.RightButton == MouseButtonState.Pressed) buttons |= MouseButtons.Right;

        return buttons;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var local = e.GetPosition(_image);
        if (ToRemotePosition(local) is { } position)
        {
            _logger.LogInformation(
                "[VNC INPUT] MouseMove local=({lx:0},{ly:0}) remote={position} mask=0x{mask:x2}",
                local.X, local.Y, position, (int)GetPressedButtons());
            _session.SendPointerEvent(position, GetPressedButtons());
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var local = e.GetPosition(_image);
        if (ToRemotePosition(local) is { } position)
        {
            _logger.LogInformation(
                "[VNC INPUT] MouseDown button={button} remote={position} mask=0x{mask:x2}",
                e.ChangedButton, position, (int)GetPressedButtons());
            _session.SendPointerEvent(position, GetPressedButtons());
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        var local = e.GetPosition(_image);
        if (ToRemotePosition(local) is { } position)
        {
            _logger.LogInformation(
                "[VNC INPUT] MouseUp button={button} remote={position} mask=0x{mask:x2}",
                e.ChangedButton, position, (int)GetPressedButtons());
            _session.SendPointerEvent(position, GetPressedButtons());
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ToRemotePosition(e.GetPosition(_image)) is not { } position)
        {
            return;
        }

        // RFB 用「按下并抬起滚轮按钮」表达滚动，没有独立的滚动量字段。
        var wheel = e.Delta > 0 ? MouseButtons.WheelUp : MouseButtons.WheelDown;

        _session.SendPointerEvent(position, wheel);
        _session.SendPointerEvent(position, MouseButtons.None);

        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (VncKeyMapper.TryMapSpecialKey(key, out var keySymbol))
        {
            _logger.LogInformation("[VNC INPUT] KeyDown key={key} keysym={keySymbol}", key, keySymbol);
            _session.SendKeyEvent(keySymbol, isDown: true);
            e.Handled = true;
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (VncKeyMapper.TryMapSpecialKey(key, out var keySymbol))
        {
            _logger.LogInformation("[VNC INPUT] KeyUp key={key} keysym={keySymbol}", key, keySymbol);
            _session.SendKeyEvent(keySymbol, isDown: false);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 可打印字符走文本输入事件，这样输入法与各国键盘布局都能正确工作，
    /// 中文等非 ASCII 字符也能按 Unicode keysym 规则送达远端。
    /// </summary>
    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        foreach (var rune in e.Text.EnumerateRunes())
        {
            _logger.LogInformation("[VNC INPUT] TextInput char='{char}' cp=0x{cp:x4}", rune, rune.Value);
            _session.SendKeyStroke(VncKeyMapper.MapCharacter(rune.Value));
        }

        e.Handled = true;
    }

    /// <summary>按当前 VNC 缩放档位设置画面拉伸与滚动条。</summary>
    private void ApplyScaleMode()
    {
        switch (_viewModel.VncScale)
        {
            case VncScaleMode.Fill:
                _image.Stretch = Stretch.Fill;
                _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                break;
            case VncScaleMode.Original:
                _image.Stretch = Stretch.None;
                _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                break;
            default: // FitToWindow
                _image.Stretch = Stretch.Uniform;
                _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                break;
        }
    }

    private void OnActionRequested(object? sender, SessionAction action)
    {
        switch (action)
        {
            case SessionAction.ToggleScaling:
                ApplyScaleMode();
                _session.Profile.Vnc.ScaleMode = _viewModel.VncScale;
                break;

            case SessionAction.ReturnFocusToSession:
                // Flyout / 浮层关闭后把键盘焦点还给 VNC 画面（后续按键才能送向远端）。
                var window = Window.GetWindow(this);
                if (window is not null && window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                _image.Focus();
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 先退订输入事件：视图释放后 _image 不再被输入事件链持有，鼠标/键盘回调不会发给已关闭会话。
        UnhookInput();

        if (_renderingHooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingHooked = false;
        }

        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;

        _session.RenderTarget.FramebufferSizeChanged -= OnFramebufferSizeChanged;
        _viewModel.ActionRequested -= OnActionRequested;

        // 断开 WriteableBitmap 引用：即使控件仍在可视树中也不再取帧。
        _image.Source = null;
        _bitmap = null;
    }
}
