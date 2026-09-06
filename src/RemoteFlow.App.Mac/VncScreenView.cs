using System.Runtime.InteropServices;
using AppKit;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using MarcusW.VncClient;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Protocol.Vnc;
using ConnectionState = RemoteFlow.Core.Models.ConnectionState;
using VncPosition = MarcusW.VncClient.Position;

namespace RemoteFlow.App.Mac;

/// <summary>
/// VNC 会话视图：把 <see cref="VncRenderTarget"/>（IFrameSource，BGRA32 缓冲）按显示帧率
/// 取帧、经 <see cref="CGImage"/> 贴到图层；键鼠事件经
/// <see cref="VncSession.SendPointerEvent"/> / <see cref="VncSession.SendKeyEvent"/> 发回远端。
/// </summary>
public sealed class VncScreenView : NSView
{
    private const double FrameIntervalMs = 33; // ≈30fps

    private readonly VncSession _session;
    private readonly IFrameSource _frames;
    private readonly CALayer _screen = new() { ContentsGravity = CALayer.GravityResizeAspect };
    private readonly NSTextField _overlay;
    private readonly NSButton _reconnect;

    /// <summary>会话断开后用户点「重新连接」。</summary>
    public event EventHandler? ReconnectRequested;

    public RemoteFlow.Core.Models.ConnectionProfile Profile => _session.Profile;

    private NSTimer? _timer;
    private byte[] _buffer = Array.Empty<byte>();
    private int _fw;
    private int _fh;
    private FrameSize _pendingSize;
    private uint _prevFlags;
    private bool _detached;

    public VncScreenView(VncSession session)
    {
        _session = session;
        _frames = session.RenderTarget;

        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        Layer!.BackgroundColor = NSColor.Black.CGColor;
        Layer.AddSublayer(_screen);

        _overlay = new NSTextField
        {
            StringValue = "正在连接…",
            Bordered = false,
            Editable = false,
            Selectable = false,
            DrawsBackground = false,
            Alignment = NSTextAlignment.Center,
            TextColor = NSColor.SecondaryLabel,
            Font = NSFont.SystemFontOfSize(14),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _reconnect = NSButton.CreateButton("重新连接", () => ReconnectRequested?.Invoke(this, EventArgs.Empty));
        _reconnect.BezelStyle = NSBezelStyle.Rounded;
        _reconnect.Hidden = true;
        _reconnect.TranslatesAutoresizingMaskIntoConstraints = false;

        AddSubview(_overlay);
        AddSubview(_reconnect);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            _overlay.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            _overlay.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            _reconnect.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            _reconnect.TopAnchor.ConstraintEqualTo(_overlay.BottomAnchor, 12),
        });

        _frames.FrameSizeChanged += OnFrameSizeChanged;
        _session.StateChanged += OnStateChanged;
        _session.ClipboardTextReceived += OnClipboardTextReceived;

        _pendingSize = _frames.FrameSize;
        StartLoop();
    }

    public void Detach()
    {
        _detached = true;
        StopLoop();
        _frames.FrameSizeChanged -= OnFrameSizeChanged;
        _session.StateChanged -= OnStateChanged;
        _session.ClipboardTextReceived -= OnClipboardTextReceived;
    }

    // 远端复制 → 本机剪贴板（连接级开关 Profile.Vnc.ClipboardToLocal 已在会话层把关）。
    private void OnClipboardTextReceived(object? sender, string text)
        => NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            var pb = NSPasteboard.GeneralPasteboard;
            pb.ClearContents();
            pb.SetStringForType(text, "public.utf8-plain-text");
        });

    public override bool AcceptsFirstResponder() => true;

    public override void ViewDidMoveToWindow()
    {
        base.ViewDidMoveToWindow();
        if (Window is not null)
        {
            Window.MakeFirstResponder(this);
        }
    }

    public override void Layout()
    {
        base.Layout();
        _screen.Frame = Bounds;
    }

    // ── 渲染循环 ────────────────────────────────────────────────
    private void StartLoop()
    {
        StopLoop();
        _timer = NSTimer.CreateRepeatingScheduledTimer(
            TimeSpan.FromMilliseconds(FrameIntervalMs), _ => PumpFrame());
        NSRunLoop.Main.AddTimer(_timer, NSRunLoopMode.Common);
    }

    private void StopLoop()
    {
        _timer?.Invalidate();
        _timer = null;
    }

    private void PumpFrame()
    {
        if (_detached || _session.State != ConnectionState.Connected)
        {
            return;
        }

        if (!_pendingSize.IsEmpty)
        {
            RecreateBuffer(_pendingSize);
            _pendingSize = FrameSize.Empty;
        }

        if (_buffer.Length == 0)
        {
            return;
        }

        var handle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        bool copied;
        try
        {
            copied = _frames.TryCopyLatestFrame(
                handle.AddrOfPinnedObject(), _buffer.Length, _fw * 4, _fw, _fh);
        }
        finally
        {
            handle.Free();
        }

        if (copied)
        {
            _overlay.Hidden = true;
            _screen.Contents = MakeImage();
        }
    }

    private void RecreateBuffer(FrameSize size)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        _fw = size.Width;
        _fh = size.Height;
        _buffer = new byte[_fw * _fh * 4];
    }

    private CGImage? MakeImage()
    {
        using var provider = new CGDataProvider(_buffer, 0, _buffer.Length);
        using var space = CGColorSpace.CreateDeviceRGB();
        return new CGImage(
            _fw, _fh, 8, 32, _fw * 4, space,
            CGBitmapFlags.ByteOrder32Little | CGBitmapFlags.NoneSkipFirst,
            provider, null, false, CGColorRenderingIntent.Default);
    }

    private void OnFrameSizeChanged(object? sender, FrameSize size) => _pendingSize = size;

    private void OnStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            if (e.NewState is ConnectionState.Failed or ConnectionState.Disconnected or ConnectionState.Closed)
            {
                var what = e.NewState == ConnectionState.Failed ? "会话失败" : "会话已断开";
                var reason = _session.ErrorMessage
                    ?? (_session.ErrorCode == RemoteFlow.Core.Models.ConnectionErrorCode.None
                        ? null
                        : _session.ErrorCode.ToString());
                _overlay.StringValue = reason is null ? what : $"{what}：{reason}";
                _overlay.Hidden = false;
                _reconnect.Hidden = false;
            }
        });
    }

    // ── 指针 ────────────────────────────────────────────────────
    public override void MouseDown(NSEvent e) => Pointer(e);
    public override void MouseUp(NSEvent e) => Pointer(e);
    public override void MouseDragged(NSEvent e) => Pointer(e);
    public override void RightMouseDown(NSEvent e) => Pointer(e);
    public override void RightMouseUp(NSEvent e) => Pointer(e);
    public override void RightMouseDragged(NSEvent e) => Pointer(e);
    public override void OtherMouseDown(NSEvent e) => Pointer(e);
    public override void OtherMouseUp(NSEvent e) => Pointer(e);
    public override void OtherMouseDragged(NSEvent e) => Pointer(e);
    public override void MouseMoved(NSEvent e) => Pointer(e);

    public override void ScrollWheel(NSEvent e)
    {
        if (ToRemote(e) is not { } pos)
        {
            return;
        }

        var wheel = e.ScrollingDeltaY >= 0 ? MouseButtons.WheelUp : MouseButtons.WheelDown;
        _session.SendPointerEvent(pos, wheel);
        _session.SendPointerEvent(pos, MouseButtons.None);
    }

    private void Pointer(NSEvent e)
    {
        if (ToRemote(e) is not { } pos)
        {
            return;
        }

        _session.SendPointerEvent(pos, ButtonsOf(e));
    }

    private static MouseButtons ButtonsOf(NSEvent e)
    {
        var mask = (int)NSEvent.CurrentPressedMouseButtons;
        var b = MouseButtons.None;
        if ((mask & 1) != 0) b |= MouseButtons.Left;
        if ((mask & 2) != 0) b |= MouseButtons.Right;
        if ((mask & 4) != 0) b |= MouseButtons.Middle;
        return b;
    }

    /// <summary>视图坐标 → 远端画面坐标（按 aspect-fit 缩放与居中，视图为非翻转）。</summary>
    private VncPosition? ToRemote(NSEvent e)
    {
        if (_fw == 0 || _fh == 0)
        {
            return null;
        }

        var p = ConvertPointFromView(e.LocationInWindow, null);
        var avail = Bounds.Size;
        var scale = Math.Min(avail.Width / _fw, avail.Height / _fh);
        if (scale <= 0)
        {
            return null;
        }

        var offX = (avail.Width - _fw * scale) / 2;
        var offY = (avail.Height - _fh * scale) / 2;

        // AppKit 视图坐标原点在左下；远端画面原点在左上 → Y 翻转。
        var x = (int)Math.Round((p.X - offX) / scale);
        var y = (int)Math.Round((avail.Height - p.Y - offY) / scale);

        if (x < 0 || y < 0 || x >= _fw || y >= _fh)
        {
            return null;
        }

        return new VncPosition(x, y);
    }

    // ── 键盘 ────────────────────────────────────────────────────
    public override void KeyDown(NSEvent e)
    {
        if (AppKitVncKeyMapper.Map(e) is { } sym)
        {
            _session.SendKeyEvent(sym, true);
        }
    }

    public override void KeyUp(NSEvent e)
    {
        if (AppKitVncKeyMapper.Map(e) is { } sym)
        {
            _session.SendKeyEvent(sym, false);
        }
    }

    public override void FlagsChanged(NSEvent e)
    {
        if (!AppKitVncKeyMapper.IsModifier(e.KeyCode)
            || AppKitVncKeyMapper.MapModifier(e.KeyCode) is not { } sym)
        {
            _prevFlags = (uint)e.ModifierFlags;
            return;
        }

        // 该修饰位从无到有 = 按下，从有到无 = 抬起。
        var bit = MaskFor(e.KeyCode);
        var nowDown = ((uint)e.ModifierFlags & bit) != 0;
        _session.SendKeyEvent(sym, nowDown);
        _prevFlags = (uint)e.ModifierFlags;
    }

    private static uint MaskFor(ushort keyCode) => keyCode switch
    {
        0x38 or 0x3C => (uint)NSEventModifierMask.ShiftKeyMask,
        0x3B or 0x3E => (uint)NSEventModifierMask.ControlKeyMask,
        0x3A or 0x3D => (uint)NSEventModifierMask.AlternateKeyMask,
        0x37 or 0x36 => (uint)NSEventModifierMask.CommandKeyMask,
        _ => 0,
    };
}
