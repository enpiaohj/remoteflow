using AppKit;
using CoreGraphics;
using RemoteFlow.Core.Models;

namespace RemoteFlow.App.Mac;

/// <summary>新建 / 编辑连接的原生 sheet。8.C 会绑到共享 ConnectionEditorViewModel。</summary>
public sealed class ConnectionEditorSheet : NSWindowController
{
    private readonly NSTextField _name = Field();
    private readonly NSPopUpButton _protocol = new(new CGRect(0, 0, 120, 24), pullsDown: false);
    private readonly NSTextField _host = Field();
    private readonly NSTextField _port = Field();
    private readonly NSTextField _user = Field();
    private readonly NSSecureTextField _password = new()
    {
        TranslatesAutoresizingMaskIntoConstraints = false,
        Bordered = true,
        Bezeled = true,
        Font = NSFont.SystemFontOfSize(13),
    };

    public ConnectionDraft? Result { get; private set; }

    public ConnectionEditorSheet()
        : base(NewPanel())
    {
        _protocol.AddItems(new[] { "SSH", "VNC", "RDP" });
        _protocol.Activated += (_, _) => _port.StringValue = DefaultPort();

        var grid = new NSGridView
        {
            RowSpacing = 10,
            ColumnSpacing = 12,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        grid.AddRow(new NSView[] { Caption("名称"), _name });
        grid.AddRow(new NSView[] { Caption("协议"), _protocol });
        grid.AddRow(new NSView[] { Caption("主机"), _host });
        grid.AddRow(new NSView[] { Caption("端口"), _port });
        grid.AddRow(new NSView[] { Caption("账号"), _user });
        grid.AddRow(new NSView[] { Caption("口令"), _password });
        grid.GetColumn(0).LeadingPadding = 0;
        grid.GetColumn(1).LeadingPadding = 0;

        var save = NSButton.CreateButton("创建", Save);
        save.KeyEquivalent = "\r";
        save.BezelStyle = NSBezelStyle.Rounded;
        var cancel = NSButton.CreateButton("取消", () => End(NSModalResponse.Cancel));
        cancel.KeyEquivalent = "";
        cancel.BezelStyle = NSBezelStyle.Rounded;

        var buttons = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Spacing = 10,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        buttons.AddArrangedSubview(cancel);
        buttons.AddArrangedSubview(save);

        var heading = new NSTextField
        {
            StringValue = "新建连接",
            Font = NSFont.SystemFontOfSize(15, NSFontWeight.Semibold),
            Bordered = false,
            Editable = false,
            DrawsBackground = false,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        var content = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        content.AddSubview(heading);
        content.AddSubview(grid);
        content.AddSubview(buttons);

        NSLayoutConstraint.ActivateConstraints(new[]
        {
            heading.TopAnchor.ConstraintEqualTo(content.TopAnchor, 20),
            heading.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor, 20),

            grid.TopAnchor.ConstraintEqualTo(heading.BottomAnchor, 18),
            grid.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor, 20),
            grid.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor, -20),

            buttons.TopAnchor.ConstraintEqualTo(grid.BottomAnchor, 22),
            buttons.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor, -20),
            buttons.BottomAnchor.ConstraintLessThanOrEqualTo(content.BottomAnchor, -20),

            _name.WidthAnchor.ConstraintGreaterThanOrEqualTo(260),
        });

        Window.ContentView = content;
        _port.StringValue = "22";
    }

    private static NSWindow NewPanel() => new NSPanel(
        new CGRect(0, 0, 440, 342),
        NSWindowStyle.Titled,
        NSBackingStore.Buffered,
        deferCreation: false);

    private string DefaultPort() => _protocol.IndexOfSelectedItem switch { 1 => "5900", 2 => "3389", _ => "22" };

    private void Save()
    {
        var name = _name.StringValue.Trim();
        var host = _host.StringValue.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(host))
        {
            NSSound.FromName("Funk")?.Play();
            return;
        }

        if (!int.TryParse(_port.StringValue.Trim(), out var port) || port <= 0)
        {
            port = int.Parse(DefaultPort());
        }

        var proto = _protocol.IndexOfSelectedItem switch
        {
            1 => ProtocolType.Vnc,
            2 => ProtocolType.Rdp,
            _ => ProtocolType.Ssh,
        };

        Result = new ConnectionDraft(name, host, port, proto, _user.StringValue.Trim(), _password.StringValue);
        End(NSModalResponse.OK);
    }

    private void End(NSModalResponse response)
        => Window.SheetParent?.EndSheet(Window, response);

    private static NSTextField Field() => new()
    {
        TranslatesAutoresizingMaskIntoConstraints = false,
        Bordered = true,
        Bezeled = true,
        Font = NSFont.SystemFontOfSize(13),
    };

    private static NSTextField Caption(string text)
    {
        var f = new NSTextField
        {
            StringValue = text,
            Bordered = false,
            Editable = false,
            Selectable = false,
            DrawsBackground = false,
            Alignment = NSTextAlignment.Right,
            Font = NSFont.SystemFontOfSize(13),
            TextColor = NSColor.SecondaryLabel,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        f.WidthAnchor.ConstraintEqualTo(52).Active = true;
        return f;
    }
}

/// <summary>连接编辑草稿（8.C 前的临时载体）。</summary>
public sealed record ConnectionDraft(
    string Name, string Host, int Port, ProtocolType Protocol, string Username, string Password);
