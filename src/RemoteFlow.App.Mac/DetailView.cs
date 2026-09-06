using AppKit;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 详情 / 会话区。当前三态：空态、连接信息卡、连接中占位。
/// 会话画面（SSH 终端 / VNC）在 8.D 接入本区域。
/// </summary>
public sealed class DetailView : NSView
{
    private readonly NSTextField _title = Label(22, NSFontWeight.Semibold);
    private readonly NSTextField _subtitle = Label(13, NSFontWeight.Regular, secondary: true);
    private readonly NSGridView _grid = new()
    {
        RowSpacing = 8,
        ColumnSpacing = 16,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };
    private readonly NSButton _connectButton = NSButton.CreateButton("连接", () => { });
    private readonly NSStackView _card;
    private readonly NSView _empty;

    public DetailView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;

        _connectButton.BezelStyle = NSBezelStyle.Rounded;
        _connectButton.ControlSize = NSControlSize.Large;

        _card = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 14,
            EdgeInsets = new NSEdgeInsets(36, 40, 36, 40),
            TranslatesAutoresizingMaskIntoConstraints = false,
            Hidden = true,
        };
        _card.AddArrangedSubview(_title);
        _card.AddArrangedSubview(_subtitle);
        _card.AddArrangedSubview(Spacer(6));
        _card.AddArrangedSubview(_grid);
        _card.AddArrangedSubview(Spacer(10));
        _card.AddArrangedSubview(_connectButton);
        AddSubview(_card);

        _empty = BuildEmpty();
        AddSubview(_empty);

        NSLayoutConstraint.ActivateConstraints(new[]
        {
            _card.LeadingAnchor.ConstraintEqualTo(SafeAreaLayoutGuide.LeadingAnchor),
            _card.TrailingAnchor.ConstraintEqualTo(SafeAreaLayoutGuide.TrailingAnchor),
            _card.TopAnchor.ConstraintEqualTo(SafeAreaLayoutGuide.TopAnchor),
            _empty.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            _empty.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
        });
    }

    public override bool IsFlipped => true;

    public void ShowConnection(ConnectionItemViewModel c)
    {
        _empty.Hidden = true;
        _card.Hidden = false;

        _title.StringValue = c.Name;
        _subtitle.StringValue = $"{c.ProtocolName}  ·  {c.HostDisplay}";

        while (_grid.RowCount > 0)
        {
            _grid.RemoveRow(0);
        }

        AddRow("主机", c.Host);
        AddRow("端口", c.PortDisplay);
        AddRow("协议", c.ProtocolName);
        AddRow("最近连接", c.LastConnectedDisplay);
        AddRow("创建于", c.CreatedAtDisplay);
        if (!string.IsNullOrWhiteSpace(c.Notes))
        {
            AddRow("备注", c.Notes);
        }

        _connectButton.Title = $"连接到 {c.Name}";
    }

    public void ShowConnecting(ConnectionItemViewModel c)
    {
        _empty.Hidden = true;
        _card.Hidden = false;
        _title.StringValue = c.Name;
        _subtitle.StringValue = "正在连接…（会话画面将在 8.D 接入本区域）";
    }

    private void AddRow(string label, string value)
    {
        var l = Label(12, NSFontWeight.Medium, secondary: true);
        l.StringValue = label;
        l.Alignment = NSTextAlignment.Right;
        var v = Label(13, NSFontWeight.Regular);
        v.StringValue = string.IsNullOrEmpty(value) ? "—" : value;
        v.Selectable = true;
        _grid.AddRow(new NSView[] { l, v });
    }

    private static NSView BuildEmpty()
    {
        var stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.CenterX,
            Spacing = 8,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        var icon = new NSImageView
        {
            Image = NSImage.GetSystemSymbol("rectangle.connected.to.line.below", null),
            TranslatesAutoresizingMaskIntoConstraints = false,
            ContentTintColor = NSColor.TertiaryLabel,
            SymbolConfiguration = NSImageSymbolConfiguration.Create(44, NSFontWeight.Regular),
        };
        var t = Label(17, NSFontWeight.Medium);
        t.StringValue = "选择一个连接";
        t.Alignment = NSTextAlignment.Center;
        var s = Label(13, NSFontWeight.Regular, secondary: true);
        s.StringValue = "从左侧列表选择，或用工具栏「＋」新建连接。";
        s.Alignment = NSTextAlignment.Center;

        stack.AddArrangedSubview(icon);
        stack.AddArrangedSubview(t);
        stack.AddArrangedSubview(s);
        return stack;
    }

    private static NSTextField Label(nfloat size, nfloat weight, bool secondary = false) => new()
    {
        Bordered = false,
        Editable = false,
        Selectable = false,
        DrawsBackground = false,
        Font = NSFont.SystemFontOfSize(size, weight),
        TextColor = secondary ? NSColor.SecondaryLabel : NSColor.Label,
        TranslatesAutoresizingMaskIntoConstraints = false,
        LineBreakMode = NSLineBreakMode.TruncatingTail,
    };

    private static NSView Spacer(nfloat h)
    {
        var v = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        v.HeightAnchor.ConstraintEqualTo(h).Active = true;
        return v;
    }
}
