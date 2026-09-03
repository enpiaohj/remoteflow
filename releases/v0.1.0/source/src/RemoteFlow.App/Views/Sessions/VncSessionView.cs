using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MarcusW.VncClient;
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
    private readonly Image _image;

    private WriteableBitmap? _bitmap;

    /// <summary>待重建位图的目标尺寸。由协议线程写入，UI 线程消费。</summary>
    private VncSize? _pendingSize;

    private bool _renderingHooked;
    private bool _connectStarted;
    private bool _disposed;

    public VncSessionView(VncSession session, SessionTabViewModel viewModel)
    {
        _session = session;
        _viewModel = viewModel;

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

        Content = _image;
        Focusable = true;
        Background = Brushes.Transparent;

        _session.RenderTarget.FramebufferSizeChanged += OnFramebufferSizeChanged;
        _viewModel.ActionRequested += OnActionRequested;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

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

        _image.MouseDown += (_, _) => _image.Focus();
    }

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
        if (ToRemotePosition(e.GetPosition(_image)) is { } position)
        {
            _session.SendPointerEvent(position, GetPressedButtons());
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ToRemotePosition(e.GetPosition(_image)) is { } position)
        {
            _session.SendPointerEvent(position, GetPressedButtons());
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (ToRemotePosition(e.GetPosition(_image)) is { } position)
        {
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
            _session.SendKeyEvent(keySymbol, isDown: true);
            e.Handled = true;
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (VncKeyMapper.TryMapSpecialKey(key, out var keySymbol))
        {
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
            _session.SendKeyStroke(VncKeyMapper.MapCharacter(rune.Value));
        }

        e.Handled = true;
    }

    private void OnActionRequested(object? sender, SessionAction action)
    {
        if (action != SessionAction.ToggleScaling)
        {
            return;
        }

        // 适应窗口用 Uniform 缩放；1:1 用原始像素，超出部分由外层滚动查看。
        _image.Stretch = _viewModel.ScaleToFit ? Stretch.Uniform : Stretch.None;
        _session.Profile.Vnc.ScaleMode = _viewModel.ScaleToFit ? VncScaleMode.FitToWindow : VncScaleMode.Original;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_renderingHooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingHooked = false;
        }

        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;

        _session.RenderTarget.FramebufferSizeChanged -= OnFramebufferSizeChanged;
        _viewModel.ActionRequested -= OnActionRequested;

        _image.Source = null;
        _bitmap = null;
    }
}
