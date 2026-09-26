using AppKit;
using CoreGraphics;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 新建 / 编辑凭据的原生对话框，绑共享 <see cref="CredentialEditorViewModel"/>。
/// <para>安全：编辑已有凭据时不回填任何明文；密码框留空表示不修改。</para>
/// </summary>
public sealed class CredentialEditorSheet : NSWindowController
{
    private const int PanelWidth = 460;

    private static readonly (CredentialType Type, string Label)[] Types =
    {
        (CredentialType.WindowsDomain, "Windows 域账号"),
        (CredentialType.LocalPassword, "本地账号"),
        (CredentialType.SshPassword, "SSH 口令"),
        (CredentialType.SshPrivateKey, "SSH 私钥"),
        (CredentialType.VncPassword, "VNC 口令"),
    };

    private readonly CredentialEditorViewModel _vm;
    private readonly NSView _root;
    private readonly NSGridView _grid;
    private readonly NSTextField _error;

    private readonly NSTextField _name;
    private readonly NSPopUpButton _type;
    private readonly NSTextField _username;
    private readonly NSTextField _domain;
    private readonly NSSecureTextField _password;
    private readonly NSTextField _passwordLabel;
    private readonly NSTextField _passwordHint;
    private readonly NSTextView _privateKey;
    private readonly NSScrollView _privateKeyScroll;
    private readonly NSTextField _privateKeyHint;
    private readonly NSTextField _description;

    private NSGridRow? _usernameRow;
    private NSGridRow? _domainRow;
    private NSGridRow? _privateKeyRow;
    private NSGridRow? _privateKeyHintRow;

    public CredentialEditorResult? Result { get; private set; }

    public CredentialEditorSheet(CredentialEditorViewModel vm)
        : base(NewPanel())
    {
        _vm = vm;
        Window.Title = vm.Title;

        _name = Field(vm.Name);
        _name.Changed += (_, _) => _vm.Name = _name.StringValue;

        _type = new NSPopUpButton(new CGRect(0, 0, 220, 24), pullsDown: false)
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        foreach (var (_, label) in Types)
        {
            _type.AddItem(label);
        }
        _type.SelectItem(Math.Max(0, Array.FindIndex(Types, t => t.Type == vm.Type)));
        _type.Activated += (_, _) =>
        {
            _vm.Type = Types[(int)_type.IndexOfSelectedItem].Type;
            ApplyTypeVisibility();
        };

        _username = Field(vm.Username);
        _username.Changed += (_, _) => _vm.Username = _username.StringValue;

        _domain = Field(vm.Domain);
        _domain.Changed += (_, _) => _vm.Domain = _domain.StringValue;

        _password = new NSSecureTextField
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            Bordered = true,
            Bezeled = true,
            Font = NSFont.SystemFontOfSize(13),
        };
        _password.Changed += (_, _) => _vm.EnteredPassword = _password.StringValue;
        _passwordLabel = Caption(vm.PasswordLabel);
        _passwordHint = Hint(vm.PasswordHint);

        _privateKey = new NSTextView
        {
            Font = NSFont.MonospacedSystemFont(11, NSFontWeight.Regular),
            Value = vm.EnteredPrivateKey ?? string.Empty,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _privateKey.TextContainerInset = new CGSize(4, 6);
        _privateKeyScroll = new NSScrollView
        {
            DocumentView = _privateKey,
            HasVerticalScroller = true,
            BorderType = NSBorderType.BezelBorder,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _privateKeyScroll.HeightAnchor.ConstraintEqualTo(84).Active = true;
        _privateKeyHint = Hint(vm.PrivateKeyHint);

        _description = Field(vm.Description);
        _description.Changed += (_, _) => _vm.Description = _description.StringValue;

        _grid = new NSGridView
        {
            RowSpacing = 10,
            ColumnSpacing = 12,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _grid.AddRow(new NSView[] { Caption("名称"), _name });
        _grid.AddRow(new NSView[] { Caption("类型"), _type });
        _usernameRow = _grid.AddRow(new NSView[] { Caption("用户名"), _username });
        _domainRow = _grid.AddRow(new NSView[] { Caption("域"), _domain });
        _grid.AddRow(new NSView[] { _passwordLabel, _password });
        _grid.AddRow(new NSView[] { Spacer(), _passwordHint });
        _privateKeyRow = _grid.AddRow(new NSView[] { Caption("私钥"), _privateKeyScroll });
        _privateKeyHintRow = _grid.AddRow(new NSView[] { Spacer(), _privateKeyHint });
        _grid.AddRow(new NSView[] { Caption("描述"), _description });
        _grid.GetColumn(0).LeadingPadding = 0;
        _grid.GetColumn(1).LeadingPadding = 0;

        _error = new NSTextField
        {
            Bordered = false,
            Editable = false,
            Selectable = false,
            DrawsBackground = false,
            TextColor = NSColor.SystemRed,
            Font = NSFont.SystemFontOfSize(12),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        var cancel = TextButton("取消", () => Finish(NSModalResponse.Cancel));
        var save = TextButton("保存", TrySave);
        save.KeyEquivalent = "\r";
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
            StringValue = vm.Title,
            Font = NSFont.SystemFontOfSize(15, NSFontWeight.Semibold),
            Bordered = false,
            Editable = false,
            DrawsBackground = false,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        _root = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        _root.AddSubview(heading);
        _root.AddSubview(_grid);
        _root.AddSubview(_error);
        _root.AddSubview(buttons);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            heading.TopAnchor.ConstraintEqualTo(_root.TopAnchor, 20),
            heading.LeadingAnchor.ConstraintEqualTo(_root.LeadingAnchor, 22),

            _grid.TopAnchor.ConstraintEqualTo(heading.BottomAnchor, 16),
            _grid.LeadingAnchor.ConstraintEqualTo(_root.LeadingAnchor, 22),
            _grid.TrailingAnchor.ConstraintEqualTo(_root.TrailingAnchor, -22),

            _error.LeadingAnchor.ConstraintEqualTo(_root.LeadingAnchor, 22),
            _error.CenterYAnchor.ConstraintEqualTo(buttons.CenterYAnchor),
            _error.TrailingAnchor.ConstraintLessThanOrEqualTo(buttons.LeadingAnchor, -12),

            buttons.TopAnchor.ConstraintEqualTo(_grid.BottomAnchor, 20),
            buttons.TrailingAnchor.ConstraintEqualTo(_root.TrailingAnchor, -22),
            buttons.BottomAnchor.ConstraintEqualTo(_root.BottomAnchor, -18),

            _name.WidthAnchor.ConstraintGreaterThanOrEqualTo(260),
        });

        _name.NextKeyView = _username;
        _username.NextKeyView = _domain;
        _domain.NextKeyView = _password;
        _password.NextKeyView = _description;

        Window.ContentView = _root;
        Window.InitialFirstResponder = _name;

        ApplyTypeVisibility();
    }

    public CredentialEditorResult? Run()
    {
        FitWindow();
        Window.Center();
        Window.MakeKeyAndOrderFront(null);
        NSApplication.SharedApplication.RunModalForWindow(Window);
        return Result;
    }

    private void ApplyTypeVisibility()
    {
        if (_usernameRow is not null)
        {
            _usernameRow.Hidden = !_vm.NeedsUsername;
        }
        if (_domainRow is not null)
        {
            _domainRow.Hidden = !_vm.NeedsDomain;
        }
        if (_privateKeyRow is not null)
        {
            _privateKeyRow.Hidden = !_vm.NeedsPrivateKey;
        }
        if (_privateKeyHintRow is not null)
        {
            _privateKeyHintRow.Hidden = !_vm.NeedsPrivateKey;
        }

        _passwordLabel.StringValue = _vm.PasswordLabel;
        _passwordHint.StringValue = _vm.PasswordHint;
        _privateKeyHint.StringValue = _vm.PrivateKeyHint;

        FitWindow();
    }

    private void FitWindow()
    {
        _root.LayoutSubtreeIfNeeded();
        Window.SetContentSize(new CGSize(PanelWidth, _root.FittingSize.Height));
    }

    private void TrySave()
    {
        if (_vm.NeedsPrivateKey)
        {
            _vm.EnteredPrivateKey = _privateKey.Value;
        }

        if (_vm.Build() is not { } credential)
        {
            _error.StringValue = _vm.ValidationMessage;
            NSSound.FromName("Funk")?.Play();
            return;
        }

        Result = new CredentialEditorResult(credential, _vm.EnteredPassword, _vm.EnteredPrivateKey);
        Finish(NSModalResponse.OK);
    }

    private void Finish(NSModalResponse response)
    {
        NSApplication.SharedApplication.StopModalWithCode((nint)response);
        Window.OrderOut(null);
    }

    private static NSWindow NewPanel() => new NSPanel(
        new CGRect(0, 0, PanelWidth, 420),
        NSWindowStyle.Titled | NSWindowStyle.Closable,
        NSBackingStore.Buffered,
        deferCreation: false);

    private static NSTextField Field(string? value) => new()
    {
        StringValue = value ?? string.Empty,
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
        f.WidthAnchor.ConstraintEqualTo((nfloat)64).Active = true;
        return f;
    }

    private static NSTextField Hint(string text) => new()
    {
        StringValue = text,
        Bordered = false,
        Editable = false,
        Selectable = false,
        DrawsBackground = false,
        Font = NSFont.SystemFontOfSize(11),
        TextColor = NSColor.TertiaryLabel,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    private static NSView Spacer() => new() { TranslatesAutoresizingMaskIntoConstraints = false };

    private static NSButton TextButton(string title, Action onClick)
    {
        var b = NSButton.CreateButton(title, onClick);
        b.BezelStyle = NSBezelStyle.Rounded;
        b.TranslatesAutoresizingMaskIntoConstraints = false;
        return b;
    }
}
