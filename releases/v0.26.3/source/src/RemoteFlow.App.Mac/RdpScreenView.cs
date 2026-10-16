using AppKit;
using CoreAnimation;
using Foundation;
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
    private readonly RdpSession _session;
    private readonly IFrameSource _frames;
    private readonly CALayer _screen = new() { ContentsGravity = CALayer.GravityResizeAspect };
    private readonly LayerFramePump _pump;
    private readonly NSTextField _overlay;
    private readonly NSButton _reconnect;

    public event EventHandler? ReconnectRequested;

    public RemoteFlow.Core.Models.ConnectionProfile Profile => _session.Profile;

    private int _fw;
    private int _fh;
    private bool _detached;

    // 动态分辨率：视图尺寸变了就把远程桌面改成同样的长宽比，避免画面被 letterbox 出黑边。
    private NSTimer? _resizeTimer;
    private NSTimer? _verifyTimer;
    private readonly List<NSObject> _winObservers = new();
    private int _lastReqW;
    private int _lastReqH;
    private int _verifyAttempts;

    public RdpScreenView(RdpSession session)
    {
        _session = session;
        _frames = session.Frames;

        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        RefreshMatte();
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
        _session.CursorChanged += OnCursorChanged;
        var initial = _frames.FrameSize;
        _fw = initial.Width;
        _fh = initial.Height;

        _pump = new LayerFramePump(_frames, _screen, () => _overlay.Hidden = true);
        _pump.Start();
    }

    // ── 光标本地渲染（NSCursor 构造见 CursorImage）─────────────────
    private NSCursor? _remoteCursor;

    private void OnCursorChanged(RemoteFlow.Protocol.Rdp.Mac.RdpCursor c)
        => NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            if (_detached)
            {
                return;
            }

            _remoteCursor = c.IsDefault ? null
                : c.IsHidden ? CursorImage.Hidden
                : CursorImage.FromRgba(c.Rgba, c.Width, c.Height, c.HotX, c.HotY);
            Window?.InvalidateCursorRectsForView(this);
        });

    public override void ResetCursorRects()
    {
        // 远端有光标形状就用它；否则交回系统箭头。
        AddCursorRect(Bounds, _remoteCursor ?? NSCursor.ArrowCursor);
    }

    /// <summary>
    /// 画面四周的衬底。远程桌面分辨率与视图比例对不上时必然留边，
    /// 关键是这条边要看着像「有意的留白」而不是「渲染坏了」：
    ///   窗口 / 三栏模式 → 用页面底衬色，画面像一张嵌在页面里的图；
    ///   全屏（窗口内全屏 / 完全全屏）→ 纯黑，避免亮边干扰，也是通行做法。
    /// </summary>
    public void SetCinematicMatte(bool cinematic)
    {
        _cinematicMatte = cinematic;
        RefreshMatte();
    }

    private bool _cinematicMatte;

    private void RefreshMatte() => Palette.With(this, () =>
        Layer!.BackgroundColor = (_cinematicMatte ? NSColor.Black : Palette.PageGround(this)).CGColor);

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        RefreshMatte();
    }

    public void Detach()
    {
        _detached = true;
        _resizeTimer?.Invalidate();
        _resizeTimer = null;
        _verifyTimer?.Invalidate();
        _verifyTimer = null;
        foreach (var t in _winObservers)
        {
            NSNotificationCenter.DefaultCenter.RemoveObserver(t);
        }

        _winObservers.Clear();
        _pump.Dispose();
        _frames.FrameSizeChanged -= OnFrameSizeChanged;
        _session.StateChanged -= OnStateChanged;
        _session.CursorChanged -= OnCursorChanged;
    }

    public override bool AcceptsFirstResponder() => true;

    /// <summary>拖拽改窗口结束时再确认一次尺寸（Layout 期间的去抖可能被跟踪模式压掉）。</summary>
    public override void ViewDidEndLiveResize()
    {
        base.ViewDidEndLiveResize();
        ScheduleResize();
    }

    public override void ViewDidMoveToSuperview()
    {
        base.ViewDidMoveToSuperview();
        ScheduleResize();
    }

    public override void ViewDidMoveToWindow()
    {
        base.ViewDidMoveToWindow();
        Window?.MakeFirstResponder(this);
        HookWindowNotifications();
        SyncPumpPaused();
    }

    /// <summary>
    /// 窗口最大化 / 进出全屏时**直接**重协商，不走去抖定时器。
    /// 这些切换期间 runloop 处于事件跟踪模式，定时器不可靠；而它们恰恰是
    /// 尺寸跳变最大的时刻，漏掉一次就是满屏黑边。
    /// </summary>
    private void HookWindowNotifications()
    {
        foreach (var t in _winObservers)
        {
            NSNotificationCenter.DefaultCenter.RemoveObserver(t);
        }

        _winObservers.Clear();
        if (Window is not { } win)
        {
            return;
        }

        void Watch(NSString name) => _winObservers.Add(
            NSNotificationCenter.DefaultCenter.AddObserver(name, _ =>
                NSApplication.SharedApplication.BeginInvokeOnMainThread(ApplyResize), win));

        Watch(NSWindow.DidResizeNotification);
        Watch(NSWindow.DidEnterFullScreenNotification);
        Watch(NSWindow.DidExitFullScreenNotification);
        Watch(NSWindow.DidEndLiveResizeNotification);

        // 最小化 / 遮挡 → 暂停取帧（画面重协商仍按 ApplyResize 走）。
        void WatchPaused(NSString name) => _winObservers.Add(
            NSNotificationCenter.DefaultCenter.AddObserver(name, _ => SyncPumpPaused(), win));

        WatchPaused(NSWindow.DidMiniaturizeNotification);
        WatchPaused(NSWindow.DidDeminiaturizeNotification);
    }

    public override void Layout()
    {
        base.Layout();
        _screen.Frame = Bounds;
        // 图层按背板缩放渲染，否则 Retina 上 2x 的远程画面会被降采样成一层糊。
        _screen.ContentsScale = Window?.BackingScaleFactor ?? 1;
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
        // 必须挂到 Common 模式：窗口最大化 / 进全屏期间 runloop 处于事件跟踪模式，
        // 默认模式的定时器在那段时间不会触发，重协商就被吞掉了 —— 表现为最大化 /
        // 全屏后画面还是旧分辨率，四周一圈大黑边。
        _resizeTimer = NSTimer.CreateTimer(TimeSpan.FromSeconds(0.35), _ => ApplyResize());
        NSRunLoop.Main.AddTimer(_resizeTimer, NSRunLoopMode.Common);
    }

    /// <summary>
    /// 重协商核对：native 侧对 monitor layout 做了限频（每秒 ≤5 次，社区经验：
    /// 发太快会把 Windows Server 2012 R2 发僵），被限掉的那一次不能让最终尺寸落空。
    /// 这里过一会儿比对「服务端实际帧尺寸」与最近请求值，不一致就补发，最多三次。
    /// </summary>
    private void ScheduleVerify()
    {
        _verifyTimer?.Invalidate();
        _verifyTimer = NSTimer.CreateTimer(TimeSpan.FromSeconds(1.2), _ =>
        {
            if (_detached || _session.State != ConnectionState.Connected)
            {
                return;
            }

            if ((_fw == _lastReqW && _fh == _lastReqH) || _verifyAttempts >= 3)
            {
                return;
            }

            _verifyAttempts++;
            try
            {
                _session.Resize(_lastReqW, _lastReqH);
            }
            catch
            {
                // 补发失败就算了，下次尺寸变化再试。
            }

            ScheduleVerify();
        });
        NSRunLoop.Main.AddTimer(_verifyTimer, NSRunLoopMode.Common);
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

        // 与 native 侧的 4 对齐保持一致，避免"请求 782、实到 780"被误判成没生效而反复重试。
        w -= w % 4;
        h -= h % 4;

        _lastReqW = w;
        _lastReqH = h;
        _verifyAttempts = 0;
        ScheduleVerify();
        System.Console.Error.WriteLine(
            $"[RDP] 请求远程桌面 {w}×{h}（视图 {Bounds.Width:0}×{Bounds.Height:0}pt @{scale}x，当前帧 {_fw}×{_fh}）");
        try
        {
            _session.Resize(w, h);
        }
        catch
        {
            // 重协商失败不影响现有画面，下次尺寸变化再试。
        }
    }

    // ── 渲染 ────────────────────────────────────────────────────
    // 取帧 / 贴图层 / 缓冲乒乓 / 不可见暂停 都在 LayerFramePump 里。这里只跟踪
    // 服务端实际帧尺寸（_fw/_fh），供动态分辨率重协商比对。

    /// <summary>视图从舞台移走（切 Tab）、窗口最小化、被完全遮挡时暂停取帧。</summary>
    private void SyncPumpPaused()
        => _pump.Paused = _detached
            || Window is null
            || Window.IsMiniaturized;

    private void OnFrameSizeChanged(object? sender, FrameSize size)
    {
        _fw = size.Width;
        _fh = size.Height;
        // 记录服务端最终给到的分辨率：和请求值对不上就说明对端没接受重协商，
        // 此时的黑边是协议层限制而非视图没铺满，便于区分两类问题。
        System.Console.Error.WriteLine(
            $"[RDP] 远程桌面实际 {size.Width}×{size.Height}（最近请求 {_lastReqW}×{_lastReqH}）");
    }

    private void OnStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            if (e.NewState == ConnectionState.Connected)
            {
                // 连上瞬间视图可能刚因 Tab 条出现而变高变窄，立刻按当前尺寸校一次。
                _lastReqW = 0;
                _lastReqH = 0;
                ScheduleResize();
            }

            if (e.NewState is ConnectionState.Failed or ConnectionState.Disconnected or ConnectionState.Closed)
            {
                var what = e.NewState == ConnectionState.Failed ? "连接失败" : "会话已断开";
                var reason = _session.ErrorMessage
                    ?? (_session.ErrorCode == RemoteFlow.Core.Models.ConnectionErrorCode.None
                        ? null
                        : RemoteFlow.Presentation.ConnectionErrorText.Title(_session.ErrorCode));
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
