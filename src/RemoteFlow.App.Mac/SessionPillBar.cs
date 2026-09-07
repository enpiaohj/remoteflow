using AppKit;
using CoreAnimation;
using CoreGraphics;
using RemoteFlow.Core.Models;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 全屏时的悬浮药丸工具条（对齐 Windows 版 SessionHostView 的 PillBar）。
/// 全屏隐藏了 Tab 条，这条药丸承担会话切换、固定、退出全屏。
/// 默认自动隐藏，鼠标移到画面顶沿唤出；「固定」后常驻。整条可拖动左右位置。
/// </summary>
public sealed class SessionPillBar : NSView
{
    private readonly NSTextField _title;
    private readonly NSView _dot;
    private readonly NSButton _pin;
    private readonly NSStackView _row;

    private bool _pinned;
    private bool _hovering;
    private NSTrackingArea? _tracking;

    /// <summary>「切换会话」被点开时向宿主要当前会话清单。</summary>
    public Func<IReadOnlyList<(Guid Id, string Title, ProtocolType Protocol)>>? SessionsProvider { get; set; }

    public event EventHandler<Guid>? SessionPicked;
    public event EventHandler? ExitFullScreenRequested;

    public bool Pinned => _pinned;

    public SessionPillBar()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        Hidden = true;

        // 顶角切平、底角圆 —— 呈现「从屏幕顶沿拉出」的观感（与 Windows 版一致）。
        Layer!.CornerRadius = 11;
        Layer.MaskedCorners = CACornerMask.MinXMaxYCorner | CACornerMask.MaxXMaxYCorner;
        Layer.BorderWidth = 1;
        Layer.ShadowOpacity = 0.28f;
        Layer.ShadowRadius = 12;
        Layer.ShadowOffset = new CGSize(0, -2);

        _dot = new NSView { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false };
        _dot.Layer!.CornerRadius = 3;

        _title = new NSTextField
        {
            StringValue = string.Empty,
            Bordered = false,
            Editable = false,
            Selectable = false,
            DrawsBackground = false,
            Font = NSFont.SystemFontOfSize(12, NSFontWeight.Medium),
            LineBreakMode = NSLineBreakMode.TruncatingTail,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _title.WidthAnchor.ConstraintLessThanOrEqualTo(180).Active = true;

        var switchBtn = SessionTabBar.IconButton("chevron.down", "切换会话");
        switchBtn.Activated += (_, _) => ShowSessionMenu(switchBtn);

        _pin = SessionTabBar.IconButton("pin", "固定工具条（不自动隐藏）");
        _pin.Activated += (_, _) =>
        {
            _pinned = !_pinned;
            _pin.Image = NSImage.GetSystemSymbol(_pinned ? "pin.fill" : "pin", null);
            _pin.ToolTip = _pinned ? "取消固定（自动隐藏）" : "固定工具条（不自动隐藏）";
            _pin.ContentTintColor = _pinned ? NSColor.ControlAccent : NSColor.SecondaryLabel;
            if (!_pinned)
            {
                MaybeHide();
            }
        };

        var exit = SessionTabBar.IconButton("arrow.down.right.and.arrow.up.left", "退出全屏（⌃⌘F）");
        exit.Activated += (_, _) => ExitFullScreenRequested?.Invoke(this, EventArgs.Empty);

        var sep = new NSBox { BoxType = NSBoxType.NSBoxSeparator, TranslatesAutoresizingMaskIntoConstraints = false };
        sep.WidthAnchor.ConstraintEqualTo(1).Active = true;
        sep.HeightAnchor.ConstraintEqualTo(16).Active = true;

        _row = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = 4,
            EdgeInsets = new NSEdgeInsets(5, 11, 5, 6),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _row.AddArrangedSubview(_dot);
        _row.AddArrangedSubview(_title);
        _row.AddArrangedSubview(switchBtn);
        _row.AddArrangedSubview(sep);
        _row.AddArrangedSubview(_pin);
        _row.AddArrangedSubview(exit);

        AddSubview(_row);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            _dot.WidthAnchor.ConstraintEqualTo(6),
            _dot.HeightAnchor.ConstraintEqualTo(6),
            _row.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _row.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            _row.TopAnchor.ConstraintEqualTo(TopAnchor),
            _row.BottomAnchor.ConstraintEqualTo(BottomAnchor),
        });

        RefreshChrome();
    }

    public void SetSession(string title, ProtocolType protocol)
    {
        _title.StringValue = title;
        _title.ToolTip = title;
        _dot.Layer!.BackgroundColor = ProtocolStyle.Tint(protocol).CGColor;
    }

    /// <summary>顶沿唤出（鼠标进入热区，或宿主主动要求）。</summary>
    public void Reveal()
    {
        Hidden = false;
        AlphaValue = 1;
    }

    /// <summary>未固定且鼠标不在条上时收起。</summary>
    public void MaybeHide()
    {
        if (!_pinned && !_hovering)
        {
            Hidden = true;
        }
    }

    public void ForceHide()
    {
        _hovering = false;
        Hidden = true;
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

    private void ShowSessionMenu(NSView anchor)
    {
        var list = SessionsProvider?.Invoke();
        if (list is null || list.Count == 0)
        {
            return;
        }

        var menu = new NSMenu();
        foreach (var (id, title, protocol) in list)
        {
            var item = new NSMenuItem(title, (_, _) => SessionPicked?.Invoke(this, id))
            {
                Image = ProtocolStyle.Symbol(protocol),
            };
            menu.AddItem(item);
        }

        menu.PopUpMenu(null, new CGPoint(0, anchor.Bounds.Height + 4), anchor);
    }
}
