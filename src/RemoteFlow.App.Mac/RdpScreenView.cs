using System.Runtime.InteropServices;
using AppKit;
using CoreAnimation;
using CoreGraphics;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Protocol.Rdp.Mac;
using ConnectionState = RemoteFlow.Core.Models.ConnectionState;

namespace RemoteFlow.App.Mac;

/// <summary>
/// RDP 会话视图（应用内嵌入式）：<see cref="RdpSession.Frames"/>（IFrameSource，BGRX32）
/// 按显示帧率取帧 → <see cref="CGImage"/> → 图层；键鼠经 <see cref="RdpSession"/> 输入方法回传。
/// 结构与 <see cref="VncScreenView"/> 对齐。
/// </summary>
public sealed class RdpScreenView : NSView
{
    private const double FrameIntervalMs = 33;

    private readonly RdpSession _session;
    private readonly IFrameSource _frames;
    private readonly CALayer _screen = new() { ContentsGravity = CALayer.GravityResizeAspect };
    private readonly NSTextField _overlay;
    private readonly NSButton _reconnect;

    public event EventHandler? ReconnectRequested;

    public RemoteFlow.Core.Models.ConnectionProfile Profile => _session.Profile;

    private NSTimer? _timer;
    private byte[] _buffer = [];
    private int _fw;
    private int _fh;
    private FrameSize _pendingSize;
    private bool _detached;

    // 动态分辨率：视图尺寸变了就把远程桌面改成同样的长宽比，避免画面被 letterbox 出黑边。
    private NSTimer? _resizeTimer;
    private int _lastReqW;
    private int _lastReqH;

    public RdpScreenView(RdpSession session)
    {
        _session = session;
        _frames = session.Frames;

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
        _pendingSize = _frames.FrameSize;
        StartLoop();
    }

    public void Detach()
    {
        _detached = true;
        _resizeTimer?.Invalidate();
        _resizeTimer = null;
        StopLoop();
        _frames.FrameSizeChanged -= OnFrameSizeChanged;
        _session.StateChanged -= OnStateChanged;
    }

    public override bool AcceptsFirstResponder() => true;

    public override void ViewDidMoveToWindow()
    {
        base.ViewDidMoveToWindow();
        Window?.MakeFirstResponder(this);
    }

    public override void Layout()
    {
        base.Layout();
        _screen.Frame = Bounds;
        ScheduleResize();
    }

    /// <summary>
    /// 视图尺寸变化 → 请求远程桌面改成同样尺寸（FreeRDP DynamicResolutionUpdate）。
    /// 不这么做的话，远程桌面是连接时定死的一个分辨率，一旦和当前视图长宽比不一致，
    /// GravityResizeAspect 就会在上下（或左右）留黑边 —— 三栏模式下尤其明显。
    /// 拖窗口会连续触发 Layout，这里做 0.35s 去抖，避免刷屏式重协商。
    /// </summary>
    private void ScheduleResize()
    {
        if (_detached || _session.Profile.Rdp is not { } rdp
            || rdp.DisplayMode == RemoteFlow.Core.Models.RdpDisplayMode.FixedResolution)
        {
            return;
        }

        _resizeTimer?.Invalidate();
        _resizeTimer = NSTimer.CreateScheduledTimer(0.35, _ => ApplyResize());
    }

    private void ApplyResize()
    {
        if (_detached || _session.State != ConnectionState.Connected)
        {
            return;
        }

        // 按背板像素请求，Retina 下画面才是清晰的 1:1；再给个上限别让桌面大得离谱。
        var scale = Window?.BackingScaleFactor ?? 1;
        var w = (int)Math.Round(Bounds.Width * scale);
        var h = (int)Math.Round(Bounds.Height * scale);
        const int maxW = 2560;
        if (w > maxW)
        {
            h = (int)Math.Round(h * (double)maxW / w);
            w = maxW;
        }

        // FreeRDP 要求偶数宽高。
        w -= w % 2;
        h -= h % 2;
        if (w < 640 || h < 480 || (w == _lastReqW && h == _lastReqH))
        {
            return;
        }

        _lastReqW = w;
        _lastReqH = h;
        try
        {
            _session.Resize(w, h);
        }
        catch
        {
            // 重协商失败不影响现有画面，下次尺寸变化再试。
        }
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
            _fw = _pendingSize.Width;
            _fh = _pendingSize.Height;
            _buffer = new byte[_fw * _fh * 4];
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
            using var provider = new CGDataProvider(_buffer, 0, _buffer.Length);
            using var cs = CGColorSpace.CreateDeviceRGB();
            _screen.Contents = new CGImage(
                _fw, _fh, 8, 32, _fw * 4, cs,
                CGBitmapFlags.ByteOrder32Little | CGBitmapFlags.NoneSkipFirst,
                provider, null, false, CGColorRenderingIntent.Default);
        }
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
        if (ToRemote(e) is not var (x, y))
        {
            return;
        }

        _ = (x, y); // 光标位置已由 SendPointer 的 move 记录
        var delta = (int)Math.Round(e.ScrollingDeltaY * 40);
        if (delta != 0)
        {
            _session.SendWheel(delta);
        }
    }

    private void Pointer(NSEvent e)
    {
        if (ToRemote(e) is not var (x, y))
        {
            return;
        }

        var mask = (int)NSEvent.CurrentPressedMouseButtons;
        _session.SendPointer(x, y, (mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
    }

    /// <summary>视图坐标 → 远端画面坐标（aspect-fit 居中，视图非翻转 → Y 翻转）。null 表示画外。</summary>
    private (int X, int Y)? ToRemote(NSEvent e)
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
        var x = (int)Math.Round((p.X - offX) / scale);
        var y = (int)Math.Round((avail.Height - p.Y - offY) / scale);

        if (x < 0 || y < 0 || x >= _fw || y >= _fh)
        {
            return null;
        }

        return (x, y);
    }

    // ── 键盘 ────────────────────────────────────────────────────
    public override void KeyDown(NSEvent e) => Key(e, true);

    public override void KeyUp(NSEvent e) => Key(e, false);

    private void Key(NSEvent e, bool down)
    {
        if (AppKitRdpKeyMapper.MapSpecial(e.KeyCode) is { } sp)
        {
            _session.SendKey(sp.Code, down, sp.Ext);
            return;
        }

        // 可打印字符走 Unicode 通道（不依赖布局 / scancode 表）。
        var chars = e.CharactersIgnoringModifiers;
        if (!string.IsNullOrEmpty(chars) && chars[0] >= 0x20 && chars[0] != 0x7F)
        {
            _session.SendUnicode(chars[0], down);
        }
    }

    public override void FlagsChanged(NSEvent e)
    {
        if (AppKitRdpKeyMapper.MapModifier(e.KeyCode) is not { } m)
        {
            return;
        }

        var bit = (uint)AppKitRdpKeyMapper.MaskFor(e.KeyCode);
        var nowDown = ((uint)e.ModifierFlags & bit) != 0;
        _session.SendKey(m.Code, nowDown, m.Ext);
    }
}
