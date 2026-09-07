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
}
