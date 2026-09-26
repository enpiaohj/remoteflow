using AppKit;
using RemoteFlow.Core.Models;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 协议的统一视觉标识：SF Symbol + 强调色。全 UI（列表 / 详情卡 / 首页 / Tab）共用一处，
/// 让 SSH / RDP / VNC 一眼可辨。此前各 cell 各写一份 switch。
/// </summary>
internal static class ProtocolStyle
{
    public static NSColor Tint(ProtocolType p) => p switch
    {
        ProtocolType.Ssh => NSColor.SystemGreen,
        ProtocolType.Rdp => NSColor.SystemBlue,
        ProtocolType.Vnc => NSColor.SystemPurple,
        _ => NSColor.SecondaryLabel,
    };

    public static string SymbolName(ProtocolType p) => p switch
    {
        ProtocolType.Ssh => "apple.terminal",
        ProtocolType.Rdp => "display",
        ProtocolType.Vnc => "rectangle.on.rectangle",
        _ => "network",
    };

    public static NSImage? Symbol(ProtocolType p)
        => NSImage.GetSystemSymbol(SymbolName(p), null)
           ?? (p == ProtocolType.Ssh ? NSImage.GetSystemSymbol("terminal", null) : null)
           ?? NSImage.GetSystemSymbol("network", null);

    /// <summary>协议短名的胶囊标签（详情卡用）：着色描边 + 淡填充。</summary>
    public static NSView Badge(ProtocolType p, string text)
    {
        var tint = Tint(p);
        var label = new NSTextField
        {
            StringValue = text,
            Bordered = false,
            Editable = false,
            Selectable = false,
            DrawsBackground = false,
            Font = NSFont.SystemFontOfSize(11, NSFontWeight.Semibold),
            TextColor = tint,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        var pill = new NSView { TranslatesAutoresizingMaskIntoConstraints = false, WantsLayer = true };
        pill.Layer!.CornerRadius = 5;
        pill.Layer.BackgroundColor = tint.ColorWithAlphaComponent(0.14f).CGColor;
        pill.AddSubview(label);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            label.LeadingAnchor.ConstraintEqualTo(pill.LeadingAnchor, 7),
            label.TrailingAnchor.ConstraintEqualTo(pill.TrailingAnchor, -7),
            label.TopAnchor.ConstraintEqualTo(pill.TopAnchor, 2),
            label.BottomAnchor.ConstraintEqualTo(pill.BottomAnchor, -2),
        });
        return pill;
    }

    /// <summary>
    /// 在线状态小圆点（贴在协议图标右下角的存在感徽标）：
    /// 绿=已连接，橙=会话活动中（连接中 / 失败未清理），无会话则整个隐藏。
    /// 外圈用窗口底色描一圈，压在图标上也看得清。
    /// </summary>
    /// <param name="size">直径。列表行的小图标用 9；首页 / 详情页的 40pt 图标块用 13 才看得清。</param>
    public static NSView StatusBadge(nfloat size = default)
    {
        if (size <= 0)
        {
            size = 9;
        }

        var v = new BadgeView(size) { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        v.WidthAnchor.ConstraintEqualTo(size).Active = true;
        v.HeightAnchor.ConstraintEqualTo(size).Active = true;
        return v;
    }

    /// <summary>按连接的会话状态刷新徽标。</summary>
    public static void ApplyStatus(NSView badge, bool connected, bool active)
    {
        if (badge is not BadgeView b)
        {
            return;
        }

        b.Hidden = !(connected || active);
        b.Fill = connected ? NSColor.SystemGreen : NSColor.SystemOrange;
        b.ToolTip = connected ? "已连接" : active ? "连接中" : null;
        b.Refresh();
    }

    private sealed class BadgeView : NSView
    {
        public NSColor Fill = NSColor.SystemGreen;

        public BadgeView(nfloat size)
        {
            WantsLayer = true;
            Layer!.CornerRadius = size / 2;
            Layer.BorderWidth = size >= 12 ? 2 : 1.5f;
        }

        public void Refresh()
        {
            var prev = NSAppearance.CurrentAppearance;
            NSAppearance.CurrentAppearance = EffectiveAppearance;
            Layer!.BackgroundColor = Fill.CGColor;
            Layer.BorderColor = NSColor.WindowBackground.CGColor;
            NSAppearance.CurrentAppearance = prev;
        }

        public override void ViewDidChangeEffectiveAppearance()
        {
            base.ViewDidChangeEffectiveAppearance();
            Refresh();
        }
    }
}
