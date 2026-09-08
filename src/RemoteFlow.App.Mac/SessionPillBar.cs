using AppKit;
using CoreAnimation;
using CoreGraphics;
using RemoteFlow.Core.Models;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 全屏（窗口内全屏 / 完全全屏）时的悬浮药丸工具条，对齐 Windows 版 SessionHostView 的 PillBar：
/// 拖动柄 · 会话切换 · 固定 │ 状态 + 主机 │ 协议工具 │ 最小化 · 退出一档 · 关闭会话。
/// 默认自动隐藏，鼠标移到画面顶沿唤出；「固定」后常驻。整条可左右拖动。
/// </summary>
public sealed class SessionPillBar : NSView
{
    private readonly NSTextField _title;
    private readonly NSTextField _host;
    private readonly NSView _dot;
    private readonly NSView _stateDot;
    private readonly NSButton _pin;
    private readonly NSButton _screenFull;
    private readonly NSStackView _tools;
    private readonly NSStackView _row;
    private readonly DragGrip _grip;

    private bool _pinned;
    private bool _hovering;
    private bool _menuOpen;       // 药丸弹出的菜单开着时不收（否则移到菜单上就整条消失）
    private NSTimer? _hideTimer;  // 延迟隐藏：鼠标离开后给一段宽限，避开热区↔药丸↔子菜单之间的空隙
    private NSTrackingArea? _tracking;

    private const double HideDelaySeconds = 2.5;

    /// <summary>「切换会话」被点开时向宿主要当前会话清单。</summary>
    public Func<IReadOnlyList<(Guid Id, string Title, ProtocolType Protocol)>>? SessionsProvider { get; set; }

    public event EventHandler<Guid>? SessionPicked;

    /// <summary>逐档退出：完全全屏 → 窗口内全屏 → 常规三栏。</summary>
    public event EventHandler? ExitOneLevelRequested;

    /// <summary>切换「完全全屏」（macOS 原生全屏）。</summary>
    public event EventHandler? ToggleScreenFullRequested;

    public event EventHandler? MinimizeRequested;
    public event EventHandler? CloseSessionRequested;

    /// <summary>整条相对画面中心的水平偏移（拖动用），由宿主绑到约束上。</summary>
    public NSLayoutConstraint? CenterOffset { get; set; }

    public bool Pinned => _pinned;

    public SessionPillBar()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        Hidden = true;

        // 顶角切平、底角圆 —— 「从屏幕顶沿拉出」的观感（与 Windows 版一致）。
        Layer!.CornerRadius = 11;
        Layer.MaskedCorners = CACornerMask.MinXMaxYCorner | CACornerMask.MaxXMaxYCorner;
        Layer.BorderWidth = 1;
        Layer.ShadowOpacity = 0.28f;
        Layer.ShadowRadius = 12;
        Layer.ShadowOffset = new CGSize(0, -2);

        _grip = new DragGrip(this);

        _dot = Dot(6);
        _title = Label(12, NSFontWeight.Medium, NSColor.Label);
        _title.WidthAnchor.ConstraintLessThanOrEqualTo(170).Active = true;

        var switchBtn = SessionTabBar.IconButton("chevron.down", "切换会话");
        switchBtn.Activated += (_, _) => ShowSessionMenu(switchBtn);

        _pin = SessionTabBar.IconButton("pin", "固定工具条（不自动隐藏）");
        _pin.Activated += (_, _) => TogglePin();

        _stateDot = Dot(6);
        _host = Label(11, NSFontWeight.Regular, NSColor.SecondaryLabel);
        _host.Font = NSFont.MonospacedSystemFont(11, NSFontWeight.Regular);
        _host.WidthAnchor.ConstraintLessThanOrEqualTo(190).Active = true;

        // 协议工具区：按会话协议填充（无可用工具则整段隐藏）。
        _tools = Row(2);

        var minimize = SessionTabBar.IconButton("minus", "最小化窗口");
        minimize.Activated += (_, _) => MinimizeRequested?.Invoke(this, EventArgs.Empty);

        _screenFull = SessionTabBar.IconButton("arrow.up.left.and.arrow.down.right", "完全全屏（⌃⌘F）");
        _screenFull.Activated += (_, _) => ToggleScreenFullRequested?.Invoke(this, EventArgs.Empty);

        var exit = SessionTabBar.IconButton("rectangle.on.rectangle", "退出全屏（回到上一档）");
        exit.Activated += (_, _) => ExitOneLevelRequested?.Invoke(this, EventArgs.Empty);

        var close = SessionTabBar.IconButton("xmark", "关闭当前会话");
        close.Activated += (_, _) => CloseSessionRequested?.Invoke(this, EventArgs.Empty);

        _row = Row(4);
        _row.EdgeInsets = new NSEdgeInsets(5, 6, 5, 6);
        _row.AddArrangedSubview(_grip);
        _row.AddArrangedSubview(_dot);
        _row.AddArrangedSubview(_title);
        _row.AddArrangedSubview(switchBtn);
        _row.AddArrangedSubview(_pin);
        _row.AddArrangedSubview(Sep());
        _row.AddArrangedSubview(_stateDot);
        _row.AddArrangedSubview(_host);
        _row.AddArrangedSubview(Sep());
        _row.AddArrangedSubview(_tools);
        _row.AddArrangedSubview(Sep());
        _row.AddArrangedSubview(minimize);
        _row.AddArrangedSubview(_screenFull);
        _row.AddArrangedSubview(exit);
        _row.AddArrangedSubview(close);

        AddSubview(_row);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            _row.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _row.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            _row.TopAnchor.ConstraintEqualTo(TopAnchor),
            _row.BottomAnchor.ConstraintEqualTo(BottomAnchor),
        });

        RefreshChrome();
    }

    // ── 宿主接口 ────────────────────────────────────────────────

    /// <summary>切换会话时刷新标题 / 主机 / 协议工具。</summary>
    public void SetSession(string title, ProtocolType protocol, string host, bool connected)
    {
        _title.StringValue = title;
        _title.ToolTip = title;
        _dot.Layer!.BackgroundColor = ProtocolStyle.Tint(protocol).CGColor;

        _host.StringValue = host;
        _host.ToolTip = host;
        _stateDot.Layer!.BackgroundColor = (connected ? NSColor.SystemGreen : NSColor.SystemOrange).CGColor;

        BuildTools(protocol);
    }

    /// <summary>当前是否处于完全全屏，决定右侧那颗按钮的图标 / 提示。</summary>
    public void SetScreenFull(bool on)
    {
        _screenFull.Image = NSImage.GetSystemSymbol(
            on ? "arrow.down.right.and.arrow.up.left" : "arrow.up.left.and.arrow.down.right", null);
        _screenFull.ToolTip = on ? "退出完全全屏（⌃⌘F）" : "完全全屏（⌃⌘F）";
    }

    public void Reveal()
    {
        CancelHideTimer();
        Hidden = false;
        AlphaValue = 1;
    }

    /// <summary>鼠标离开等场景调用：不立刻收，排一个宽限定时器；期间再 Reveal 就取消。</summary>
    public void MaybeHide()
    {
        if (_pinned || _hovering || _menuOpen)
        {
            return;
        }

        CancelHideTimer();
        _hideTimer = NSTimer.CreateScheduledTimer(HideDelaySeconds, _ =>
        {
            _hideTimer = null;
            if (!_pinned && !_hovering && !_menuOpen)
            {
                Hidden = true;
            }
        });
    }

    /// <summary>立刻收起（切换会话 / 退出全屏等确定性场景），不走宽限。</summary>
    public void ForceHide()
    {
        CancelHideTimer();
        _hovering = false;
        _menuOpen = false;
        Hidden = true;
    }

    /// <summary>
    /// 用户在画面本身（非药丸、非其子菜单）按下鼠标 —— 视为「回到远端操作」，立刻收起，
    /// 不走 <see cref="HideDelaySeconds"/> 宽限；已「固定」时保持常驻。
    /// </summary>
    public void DismissForContentClick()
    {
        if (_pinned || Hidden)
        {
            return;
        }

        CancelHideTimer();
        _hovering = false;
        _menuOpen = false;
        Hidden = true;
    }

    private void CancelHideTimer()
    {
        _hideTimer?.Invalidate();
        _hideTimer = null;
    }

    // ── 内部 ────────────────────────────────────────────────────

    /// <summary>
    /// 协议工具区。只放**确有实现**的操作 —— 宁可少一个按钮，也不放点了没反应的假按钮。
    /// 各协议的画面内工具（RDP 的 Ctrl+Alt+Del / 缩放、SSH 的复制粘贴清屏查找、VNC 的缩放）
    /// 待对应 SessionView 暴露能力后在这里补上。
    /// </summary>
    private void BuildTools(ProtocolType protocol)
    {
        foreach (var v in _tools.ArrangedSubviews.ToArray())
        {
            _tools.RemoveArrangedSubview(v);
            v.RemoveFromSuperview();
        }

        // 目前三种协议的画面视图都还没暴露可调用的工具命令，先整段隐藏，
        // 保持药丸在 RDP / SSH / VNC 下形态一致（不会一个协议宽一个协议窄）。
        _tools.Hidden = _tools.ArrangedSubviews.Length == 0;
    }

    private void TogglePin()
    {
        _pinned = !_pinned;
        _pin.Image = NSImage.GetSystemSymbol(_pinned ? "pin.fill" : "pin", null);
        _pin.ToolTip = _pinned ? "取消固定（自动隐藏）" : "固定工具条（不自动隐藏）";
        _pin.ContentTintColor = _pinned ? NSColor.ControlAccent : NSColor.SecondaryLabel;
        if (!_pinned)
        {
            MaybeHide();
        }
    }

    private void ShowSessionMenu(NSView anchor)
    {
        var list = SessionsProvider?.Invoke();
        if (list is null || list.Count == 0)
        {
            return;
        }

        var menu = new NSMenu { Delegate = new PillMenuDelegate(this) };
        foreach (var (id, title, protocol) in list)
        {
            menu.AddItem(new NSMenuItem(title, (_, _) => SessionPicked?.Invoke(this, id))
            {
                Image = ProtocolStyle.Symbol(protocol),
            });
        }

        // 菜单开着期间不收药丸；关闭后按宽限收。
        _menuOpen = true;
        CancelHideTimer();
        menu.PopUpMenu(null, new CGPoint(0, anchor.Bounds.Height + 4), anchor);
    }

    private sealed class PillMenuDelegate(SessionPillBar owner) : NSMenuDelegate
    {
        public override void MenuDidClose(NSMenu menu)
        {
            owner._menuOpen = false;
            owner.MaybeHide();
        }
    }

    private static NSStackView Row(nfloat spacing) => new()
    {
        Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
        Alignment = NSLayoutAttribute.CenterY,
        Spacing = spacing,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    private static NSView Dot(nfloat size)
    {
        var v = new NSView { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false };
        v.Layer!.CornerRadius = size / 2;
        v.WidthAnchor.ConstraintEqualTo(size).Active = true;
        v.HeightAnchor.ConstraintEqualTo(size).Active = true;
        return v;
    }

    private static NSTextField Label(nfloat size, nfloat weight, NSColor color) => new()
    {
        Bordered = false,
        Editable = false,
        Selectable = false,
        DrawsBackground = false,
        Font = NSFont.SystemFontOfSize(size, weight),
        TextColor = color,
        LineBreakMode = NSLineBreakMode.TruncatingTail,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    private static NSBox Sep()
    {
        var b = new NSBox { BoxType = NSBoxType.NSBoxSeparator, TranslatesAutoresizingMaskIntoConstraints = false };
        b.WidthAnchor.ConstraintEqualTo(1).Active = true;
        b.HeightAnchor.ConstraintEqualTo(16).Active = true;
        return b;
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        RefreshChrome();
    }

    private void RefreshChrome()
    {
        var prev = NSAppearance.CurrentAppearance;
        NSAppearance.CurrentAppearance = EffectiveAppearance;
        Layer!.BackgroundColor = NSColor.WindowBackground.CGColor;
        Layer.BorderColor = NSColor.SecondaryLabel.ColorWithAlphaComponent(0.18f).CGColor;
        Layer.ShadowColor = NSColor.Black.CGColor;
        NSAppearance.CurrentAppearance = prev;
    }

    public override void UpdateTrackingAreas()
    {
        base.UpdateTrackingAreas();
        if (_tracking is not null)
        {
            RemoveTrackingArea(_tracking);
        }

        _tracking = new NSTrackingArea(Bounds,
            NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveInKeyWindow,
            this, null);
        AddTrackingArea(_tracking);
    }

    public override void MouseEntered(NSEvent theEvent)
    {
        _hovering = true;
        Reveal();
    }

    public override void MouseExited(NSEvent theEvent)
    {
        _hovering = false;
        MaybeHide();
    }

    /// <summary>左侧拖动柄：按住左右拖动整条（对齐 Windows 版整条可拖）。</summary>
    private sealed class DragGrip : NSView
    {
        private readonly SessionPillBar _owner;
        private nfloat _startOffset;
        private nfloat _startX;

        public DragGrip(SessionPillBar owner)
        {
            _owner = owner;
            TranslatesAutoresizingMaskIntoConstraints = false;
            WantsLayer = true;
            ToolTip = "拖动工具条";
            WidthAnchor.ConstraintEqualTo(14).Active = true;
            HeightAnchor.ConstraintEqualTo(22).Active = true;
        }

        public override void DrawRect(CGRect dirtyRect)
        {
            base.DrawRect(dirtyRect);
            NSColor.TertiaryLabel.Set();
            // 两列共六个小点，标准的「可拖」提示。
            for (var col = 0; col < 2; col++)
            {
                for (var r = 0; r < 3; r++)
                {
                    var x = Bounds.Width / 2 - 3 + col * 4;
                    var y = Bounds.Height / 2 - 6 + r * 5;
                    NSBezierPath.FromOvalInRect(new CGRect(x, y, 2, 2)).Fill();
                }
            }
        }

        public override void ResetCursorRects()
            => AddCursorRect(Bounds, NSCursor.OpenHandCursor);

        public override void MouseDown(NSEvent theEvent)
        {
            _startOffset = _owner.CenterOffset?.Constant ?? 0;
            _startX = Window?.ConvertPointToScreen(theEvent.LocationInWindow).X ?? 0;
        }

        public override void MouseDragged(NSEvent theEvent)
        {
            if (_owner.CenterOffset is not { } c || Window is null)
            {
                return;
            }

            var x = Window.ConvertPointToScreen(theEvent.LocationInWindow).X;
            c.Constant = _startOffset + (x - _startX);
        }
    }
}
