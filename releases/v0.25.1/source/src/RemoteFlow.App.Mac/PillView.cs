using AppKit;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 列表行里的彩色胶囊标签，对齐 Windows 版的协议徽章 / 标签 chip / 在线状态药丸——
/// 三处长得一样：淡色圆角底 + 同色前景（可选一个前导小图标或圆点）+ 文字。
///
/// 和 <see cref="ProtocolStyle.Badge"/> 的区别：那个是一次性构造好就不再变的（详情卡用），
/// 这个可以反复 <see cref="Apply"/> 换内容，才能配合 NSTableView 的 cell 复用。
/// </summary>
internal sealed class PillView : NSView
{
    private readonly NSImageView _icon;
    private readonly NSView _dot;
    private readonly NSTextField _label;
    private readonly NSLayoutConstraint _labelLeadingToIcon;
    private readonly NSLayoutConstraint _labelLeadingToDot;
    private readonly NSLayoutConstraint _labelLeadingToEdge;

    // nfloat 不能做默认参数值（CS1750），用重载代替。
    public PillView() : this(10f)
    {
    }

    public PillView(nfloat fontSize)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        Layer!.CornerRadius = 5;

        _icon = new NSImageView
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            SymbolConfiguration = NSImageSymbolConfiguration.Create(fontSize, NSFontWeight.Medium),
            Hidden = true,
        };
        _dot = new NSView { TranslatesAutoresizingMaskIntoConstraints = false, WantsLayer = true, Hidden = true };
        _dot.Layer!.CornerRadius = 3;
        _label = new NSTextField
        {
            Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
            Font = NSFont.SystemFontOfSize(fontSize, NSFontWeight.Semibold),
            LineBreakMode = NSLineBreakMode.TruncatingTail,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        AddSubview(_icon);
        AddSubview(_dot);
        AddSubview(_label);

        _labelLeadingToIcon = _label.LeadingAnchor.ConstraintEqualTo(_icon.TrailingAnchor, 3);
        _labelLeadingToDot = _label.LeadingAnchor.ConstraintEqualTo(_dot.TrailingAnchor, 4);
        _labelLeadingToEdge = _label.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 7);

        NSLayoutConstraint.ActivateConstraints(new[]
        {
            _icon.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 5),
            _icon.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            _icon.WidthAnchor.ConstraintEqualTo(fontSize + 2),

            _dot.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 6),
            _dot.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            _dot.WidthAnchor.ConstraintEqualTo(6),
            _dot.HeightAnchor.ConstraintEqualTo(6),

            _label.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -6),
            _label.TopAnchor.ConstraintEqualTo(TopAnchor, 2),
            _label.BottomAnchor.ConstraintEqualTo(BottomAnchor, -2),
        });
        _labelLeadingToEdge.Active = true;
    }

    /// <summary>换一套内容：文字 + 主色；<paramref name="icon"/> 与 <paramref name="showDot"/>
    /// 二选一（都不给就是纯文字胶囊）。</summary>
    public void Apply(string text, NSColor tint, NSImage? icon = null, bool showDot = false)
    {
        _label.StringValue = text;
        _label.TextColor = tint;
        Layer!.BackgroundColor = tint.ColorWithAlphaComponent(0.14f).CGColor;

        _icon.Hidden = icon is null;
        _icon.Image = icon;
        _icon.ContentTintColor = tint;
        _dot.Hidden = !showDot;
        _dot.Layer!.BackgroundColor = tint.CGColor;

        _labelLeadingToIcon.Active = false;
        _labelLeadingToDot.Active = false;
        _labelLeadingToEdge.Active = false;
        if (icon is not null)
        {
            _labelLeadingToIcon.Active = true;
        }
        else if (showDot)
        {
            _labelLeadingToDot.Active = true;
        }
        else
        {
            _labelLeadingToEdge.Active = true;
        }
    }
}
