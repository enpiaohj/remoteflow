using System.Globalization;
using AppKit;
using CoreGraphics;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 新建 / 编辑连接的原生对话框，绑共享 <see cref="ConnectionEditorViewModel"/>。
/// 顶部协议分段 + 必填字段 + 「高级设置」折叠区（按协议切换）。
/// 底部：取消 / 保存 / 保存并连接。以 App-Modal 方式运行（<see cref="Run"/>）。
/// </summary>
public sealed class ConnectionEditorSheet : NSWindowController
{
    private const int PanelWidth = 540;

    private readonly ConnectionEditorViewModel _vm;
    private readonly Func<Task>? _manageTags;

    private readonly NSStackView _form;
    private readonly NSView _advancedHost = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly NSButton _disclosure;
    private readonly NSTextField _error;
    private readonly NSStackView _tagRow;

    private readonly NSTextField _name;
    private readonly NSTextField _host;
    private readonly NSTextField _port;
    private readonly NSPopUpButton _credential;
    private readonly NSPopUpButton _group;
    private readonly NSTextView _notes;

    public ConnectionEditorResult? Result { get; private set; }

    public ConnectionEditorSheet(ConnectionEditorViewModel vm, Func<Task>? manageTags = null)
        : base(NewPanel())
    {
        _vm = vm;
        _manageTags = manageTags;
        Window.Title = vm.Title;

        // ── 协议分段 ────────────────────────────────────────────
        // 逻辑直接传进 FromLabels 的 action：叠加 .Activated += 在部分 macOS 版本上
        // 与工厂设置的 target/action 冲突，事件不触发（表现为切协议无反应）。
        NSSegmentedControl protocol = null!;
        protocol = NSSegmentedControl.FromLabels(
            new[] { "RDP", "SSH", "VNC" }, NSSegmentSwitchTracking.SelectOne, () =>
            {
                _vm.Protocol = protocol.SelectedSegment switch
                {
                    1 => ProtocolType.Ssh,
                    2 => ProtocolType.Vnc,
                    _ => ProtocolType.Rdp,
                };
                SyncPortField();
                RebuildAdvanced();
                if (_vm.IsAdvancedExpanded)
                {
                    FitWindow();
                }
            });
        protocol.SelectedSegment = ProtocolIndex(vm.Protocol);

        // ── 必填字段 ────────────────────────────────────────────
        _name = Field(vm.Name);
        _name.Changed += (_, _) => _vm.Name = _name.StringValue;

        _host = Field(vm.Host);
        _host.Changed += (_, _) => _vm.Host = _host.StringValue;

        _port = Field(vm.Port.ToString(CultureInfo.InvariantCulture));
        _port.Alignment = NSTextAlignment.Right;
        _port.WidthAnchor.ConstraintEqualTo(64).Active = true;
        _port.Changed += (_, _) =>
        {
            if (int.TryParse(_port.StringValue.Trim(), out var p))
            {
                _vm.Port = p;
            }
        };

        var hostPort = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Spacing = 10,
            Alignment = NSLayoutAttribute.CenterY,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        hostPort.AddArrangedSubview(_host);
        hostPort.AddArrangedSubview(Plain("端口", NSColor.SecondaryLabel));
        hostPort.AddArrangedSubview(_port);
        _host.WidthAnchor.ConstraintGreaterThanOrEqualTo(220).Active = true;

        _credential = Popup(vm.AvailableCredentials.Select(c => c.Name),
            IndexOf(vm.AvailableCredentials, vm.SelectedCredential));
        _credential.Activated += (_, _) =>
            _vm.SelectedCredential = _vm.AvailableCredentials[(int)_credential.IndexOfSelectedItem];

        _group = Popup(vm.AvailableGroups.Select(g => g.Name),
            IndexOf(vm.AvailableGroups, vm.SelectedGroup));
        _group.Activated += (_, _) =>
            _vm.SelectedGroup = _vm.AvailableGroups[(int)_group.IndexOfSelectedItem];

        _tagRow = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Spacing = 6,
            Alignment = NSLayoutAttribute.CenterY,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        BuildTagChips();

        _notes = new NSTextView
        {
            Font = NSFont.SystemFontOfSize(13),
            Value = vm.Notes ?? string.Empty,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _notes.TextContainerInset = new CGSize(4, 6);
        var notesScroll = new NSScrollView
        {
            DocumentView = _notes,
            HasVerticalScroller = true,
            BorderType = NSBorderType.BezelBorder,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        notesScroll.HeightAnchor.ConstraintEqualTo(56).Active = true;

        var grid = new NSGridView
        {
            RowSpacing = 10,
            ColumnSpacing = 12,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        grid.AddRow(new NSView[] { Caption("名称"), _name });
        grid.AddRow(new NSView[] { Caption("主机"), hostPort });
        grid.AddRow(new NSView[] { Caption("凭据"), _credential });
        grid.AddRow(new NSView[] { Caption("分组"), _group });
        grid.AddRow(new NSView[] { Caption("标签"), _tagRow });
        grid.AddRow(new NSView[] { Caption("备注"), notesScroll });
        grid.GetColumn(0).LeadingPadding = 0;
        grid.GetColumn(1).LeadingPadding = 0;

        // ── 高级设置折叠 ────────────────────────────────────────
        _disclosure = new NSButton
        {
            Title = string.Empty,
            BezelStyle = NSBezelStyle.Disclosure,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _disclosure.SetButtonType(NSButtonType.PushOnPushOff);
        _disclosure.State = _vm.IsAdvancedExpanded ? NSCellStateValue.On : NSCellStateValue.Off;
        _disclosure.Activated += (_, _) =>
        {
            _vm.IsAdvancedExpanded = _disclosure.State == NSCellStateValue.On;
            _advancedHost.Hidden = !_vm.IsAdvancedExpanded;
            FitWindow();
        };
        var disclosureRow = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Spacing = 4,
            Alignment = NSLayoutAttribute.CenterY,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        disclosureRow.AddArrangedSubview(_disclosure);
        disclosureRow.AddArrangedSubview(Plain("高级设置", NSColor.Label));

        _advancedHost.Hidden = !_vm.IsAdvancedExpanded;
        RebuildAdvanced();

        // ── 错误提示 ────────────────────────────────────────────
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

        // ── 按钮 ────────────────────────────────────────────────
        var cancel = TextButton("取消", () => Finish(NSModalResponse.Cancel));
        var save = TextButton("保存", () => TrySave(connect: false));
        var saveConnect = TextButton("保存并连接", () => TrySave(connect: true));
        saveConnect.KeyEquivalent = "\r";

        var buttons = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Spacing = 10,
            Alignment = NSLayoutAttribute.CenterY,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        buttons.AddArrangedSubview(cancel);
        buttons.AddArrangedSubview(save);
        buttons.AddArrangedSubview(saveConnect);

        // ── 表单 + 滚动 ─────────────────────────────────────────
        _form = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 16,
            EdgeInsets = new NSEdgeInsets(20, 22, 18, 22),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _form.AddArrangedSubview(protocol);
        _form.AddArrangedSubview(grid);
        _form.AddArrangedSubview(Separator());
        _form.AddArrangedSubview(disclosureRow);
        _form.AddArrangedSubview(_advancedHost);

        var footer = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        footer.AddSubview(_error);
        footer.AddSubview(buttons);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            _error.LeadingAnchor.ConstraintEqualTo(footer.LeadingAnchor, 22),
            _error.CenterYAnchor.ConstraintEqualTo(buttons.CenterYAnchor),
            _error.TrailingAnchor.ConstraintLessThanOrEqualTo(buttons.LeadingAnchor, -12),
            buttons.TrailingAnchor.ConstraintEqualTo(footer.TrailingAnchor, -22),
            buttons.TopAnchor.ConstraintEqualTo(footer.TopAnchor, 12),
            buttons.BottomAnchor.ConstraintEqualTo(footer.BottomAnchor, -16),
        });

        _root = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        _root.AddSubview(_form);
        _root.AddSubview(footer);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            _form.TopAnchor.ConstraintEqualTo(_root.TopAnchor),
            _form.LeadingAnchor.ConstraintEqualTo(_root.LeadingAnchor),
            _form.TrailingAnchor.ConstraintEqualTo(_root.TrailingAnchor),
            _form.WidthAnchor.ConstraintEqualTo((nfloat)PanelWidth),

            footer.TopAnchor.ConstraintEqualTo(_form.BottomAnchor),
            footer.LeadingAnchor.ConstraintEqualTo(_root.LeadingAnchor),
            footer.TrailingAnchor.ConstraintEqualTo(_root.TrailingAnchor),
            footer.BottomAnchor.ConstraintEqualTo(_root.BottomAnchor),
        });

        _name.NextKeyView = _host;
        _host.NextKeyView = _port;
        _port.NextKeyView = _credential;
        _credential.NextKeyView = _group;
        _group.NextKeyView = _notes;

        Window.ContentView = _root;
        Window.InitialFirstResponder = _name;
        SyncPortField();
    }

    private readonly NSView _root;

    /// <summary>以 App-Modal 运行对话框，返回用户结果（取消返回 null）。仅在主线程调用。</summary>
    public ConnectionEditorResult? Run()
    {
        FitWindow();
        Window.Center();
        Window.MakeKeyAndOrderFront(null);
        NSApplication.SharedApplication.RunModalForWindow(Window);
        return Result;
    }

    private void FitWindow()
    {
        _root.LayoutSubtreeIfNeeded();
        var fit = _root.FittingSize;
        Window.SetContentSize(new CGSize(PanelWidth, fit.Height));
    }

    private static NSWindow NewPanel() => new NSPanel(
        new CGRect(0, 0, PanelWidth, 480),
        NSWindowStyle.Titled | NSWindowStyle.Closable,
        NSBackingStore.Buffered,
        deferCreation: false);

    private static int ProtocolIndex(ProtocolType p) => p switch
    {
        ProtocolType.Ssh => 1,
        ProtocolType.Vnc => 2,
        _ => 0,
    };

    private void SyncPortField()
    {
        var vmPort = _vm.Port.ToString(CultureInfo.InvariantCulture);
        if (_port.StringValue.Trim() != vmPort)
        {
            _port.StringValue = vmPort;
        }
    }

    // ── 标签 chips ─────────────────────────────────────────────
    private void BuildTagChips()
    {
        foreach (var v in _tagRow.ArrangedSubviews.ToArray())
        {
            v.RemoveFromSuperview();
        }

        if (_vm.AvailableTags.Count == 0)
        {
            _tagRow.AddArrangedSubview(Plain("无标签", NSColor.SecondaryLabel));
        }

        foreach (var tag in _vm.AvailableTags)
        {
            var chip = new NSButton
            {
                Title = tag.Name,
                Bordered = true,
                BezelStyle = NSBezelStyle.RoundRect,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            chip.SetButtonType(NSButtonType.PushOnPushOff);
            chip.State = tag.IsSelected ? NSCellStateValue.On : NSCellStateValue.Off;
            var captured = tag;
            chip.Activated += (_, _) => captured.IsSelected = chip.State == NSCellStateValue.On;
            _tagRow.AddArrangedSubview(chip);
        }

        if (_manageTags is not null)
        {
            _tagRow.AddArrangedSubview(TextButton("管理…", async () =>
            {
                await _manageTags();
                BuildTagChips();
            }));
        }
    }

    // ── 高级设置（按协议） ─────────────────────────────────────
    private void RebuildAdvanced()
    {
        foreach (var v in _advancedHost.Subviews.ToArray())
        {
            v.RemoveFromSuperview();
        }

        var stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 10,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        if (_vm.IsRdp)
        {
            BuildRdpAdvanced(stack);
        }
        else if (_vm.IsSsh)
        {
            BuildSshAdvanced(stack);
        }
        else
        {
            BuildVncAdvanced(stack);
        }

        _advancedHost.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            stack.TopAnchor.ConstraintEqualTo(_advancedHost.TopAnchor, 2),
            stack.LeadingAnchor.ConstraintEqualTo(_advancedHost.LeadingAnchor),
            stack.TrailingAnchor.ConstraintEqualTo(_advancedHost.TrailingAnchor),
            stack.BottomAnchor.ConstraintEqualTo(_advancedHost.BottomAnchor),
        });
    }

    private void BuildRdpAdvanced(NSStackView stack)
    {
        var resolutions = _vm.AvailableResolutions.ToList();
        var resolution = Popup(resolutions.Select(r => r.Label),
            Math.Max(0, resolutions.FindIndex(r => r == _vm.SelectedResolution)));
        resolution.Hidden = _vm.RdpDisplayMode != RdpDisplayMode.FixedResolution;
        resolution.Activated += (_, _) =>
            _vm.SelectedResolution = resolutions[(int)resolution.IndexOfSelectedItem];

        NSSegmentedControl display = null!;
        display = NSSegmentedControl.FromLabels(
            new[] { "适应窗口", "固定分辨率" }, NSSegmentSwitchTracking.SelectOne, () =>
            {
                _vm.RdpDisplayMode = display.SelectedSegment == 1
                    ? RdpDisplayMode.FixedResolution
                    : RdpDisplayMode.FitToWindow;
                resolution.Hidden = display.SelectedSegment != 1;
            });
        display.SelectedSegment = _vm.RdpDisplayMode == RdpDisplayMode.FixedResolution ? 1 : 0;

        var qualities = _vm.ConnectionQualityOptions.ToList();
        var quality = Popup(qualities.Select(q => q.Label),
            Math.Max(0, qualities.FindIndex(q => q == _vm.SelectedConnectionQuality)));
        quality.Activated += (_, _) =>
            _vm.SelectedConnectionQuality = qualities[(int)quality.IndexOfSelectedItem];

        var domain = Field(_vm.Rdp.Domain);
        domain.Changed += (_, _) => _vm.Rdp.Domain = domain.StringValue;

        stack.AddArrangedSubview(Row("显示模式", display));
        stack.AddArrangedSubview(Row("分辨率", resolution));
        stack.AddArrangedSubview(Row("体验", quality));
        stack.AddArrangedSubview(Row("登录域", domain));
        stack.AddArrangedSubview(Check("连接后进入全屏", _vm.RdpStartFullScreen, v => _vm.RdpStartFullScreen = v));
        stack.AddArrangedSubview(Check("使用全部显示器", _vm.RdpUseMultimon, v => _vm.RdpUseMultimon = v));
        stack.AddArrangedSubview(Check("音频播放到本机", _vm.RdpRedirectAudio, v => _vm.RdpRedirectAudio = v));
        stack.AddArrangedSubview(Check("麦克风重定向到远端", _vm.RdpRedirectMicrophone, v => _vm.RdpRedirectMicrophone = v));
    }

    private void BuildSshAdvanced(NSStackView stack)
    {
        var terms = _vm.TerminalTypeOptions.ToList();
        var term = Popup(terms, Math.Max(0, terms.IndexOf(_vm.Ssh.TerminalType)));
        term.Activated += (_, _) => _vm.Ssh.TerminalType = terms[(int)term.IndexOfSelectedItem];

        var encodings = _vm.EncodingOptions.ToList();
        var enc = Popup(encodings, Math.Max(0, encodings.IndexOf(_vm.Ssh.Encoding)));
        enc.Activated += (_, _) => _vm.Ssh.Encoding = encodings[(int)enc.IndexOfSelectedItem];

        var keepAlive = Field(_vm.Ssh.KeepAliveSeconds.ToString(CultureInfo.InvariantCulture));
        keepAlive.Alignment = NSTextAlignment.Right;
        keepAlive.WidthAnchor.ConstraintEqualTo(80).Active = true;
        keepAlive.Changed += (_, _) =>
        {
            if (int.TryParse(keepAlive.StringValue.Trim(), out var s) && s >= 0)
            {
                _vm.Ssh.KeepAliveSeconds = s;
            }
        };

        var initCmd = Field(_vm.Ssh.InitialCommand);
        initCmd.Changed += (_, _) => _vm.Ssh.InitialCommand = initCmd.StringValue;

        stack.AddArrangedSubview(Row("终端类型", term));
        stack.AddArrangedSubview(Row("字符编码", enc));
        stack.AddArrangedSubview(Row("保活间隔（秒）", keepAlive));
        stack.AddArrangedSubview(Row("登录后执行", initCmd));
    }

    private void BuildVncAdvanced(NSStackView stack)
    {
        var scale = NSSegmentedControl.FromLabels(
            new[] { "适应窗口", "原始像素", "填满窗口" }, NSSegmentSwitchTracking.SelectOne, () => { });
        scale.SelectedSegment = (int)_vm.Vnc.ScaleMode;
        scale.Activated += (_, _) => _vm.Vnc.ScaleMode = (VncScaleMode)(int)scale.SelectedSegment;

        stack.AddArrangedSubview(Row("缩放", scale));
        stack.AddArrangedSubview(Check("只读（不发送键鼠）", _vm.Vnc.ViewOnly, v => _vm.Vnc.ViewOnly = v));
        stack.AddArrangedSubview(Check("共享连接（不踢掉其他客户端）", _vm.Vnc.SharedConnection, v => _vm.Vnc.SharedConnection = v));
        stack.AddArrangedSubview(Check("同步远端剪贴板到本机", _vm.Vnc.ClipboardToLocal, v => _vm.Vnc.ClipboardToLocal = v));
    }

    // ── 保存 ───────────────────────────────────────────────────
    private void TrySave(bool connect)
    {
        _vm.Notes = _notes.Value;

        if (_vm.Build() is not { } profile)
        {
            _error.StringValue = _vm.ValidationMessage;
            NSSound.FromName("Funk")?.Play();
            return;
        }

        Result = new ConnectionEditorResult(profile, connect);
        Finish(NSModalResponse.OK);
    }

    private void Finish(NSModalResponse response)
    {
        NSApplication.SharedApplication.StopModalWithCode((nint)response);
        Window.OrderOut(null);
    }

    // ── 控件工厂 ───────────────────────────────────────────────
    private static NSView Row(string label, NSView control)
    {
        var row = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Spacing = 12,
            Alignment = NSLayoutAttribute.CenterY,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        row.AddArrangedSubview(Caption(label, 120));
        row.AddArrangedSubview(control);
        return row;
    }

    private static NSButton Check(string title, bool initial, Action<bool> onChange)
    {
        var b = new NSButton { Title = title, TranslatesAutoresizingMaskIntoConstraints = false };
        b.SetButtonType(NSButtonType.Switch);
        b.State = initial ? NSCellStateValue.On : NSCellStateValue.Off;
        b.Activated += (_, _) => onChange(b.State == NSCellStateValue.On);
        return b;
    }

    private static NSPopUpButton Popup(IEnumerable<string> items, int selected)
    {
        var p = new NSPopUpButton(new CGRect(0, 0, 200, 24), pullsDown: false)
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        foreach (var i in items)
        {
            p.AddItem(i);
        }
        if (selected >= 0 && selected < p.Items().Length)
        {
            p.SelectItem(selected);
        }
        return p;
    }

    private static NSTextField Field(string? value) => new()
    {
        StringValue = value ?? string.Empty,
        TranslatesAutoresizingMaskIntoConstraints = false,
        Bordered = true,
        Bezeled = true,
        Font = NSFont.SystemFontOfSize(13),
    };

    private static NSTextField Caption(string text, int width = 52)
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
        f.WidthAnchor.ConstraintEqualTo((nfloat)width).Active = true;
        return f;
    }

    private static NSTextField Plain(string text, NSColor color) => new()
    {
        StringValue = text,
        Bordered = false,
        Editable = false,
        Selectable = false,
        DrawsBackground = false,
        Font = NSFont.SystemFontOfSize(13),
        TextColor = color,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    private static NSButton TextButton(string title, Action onClick)
    {
        var b = NSButton.CreateButton(title, onClick);
        b.BezelStyle = NSBezelStyle.Rounded;
        b.TranslatesAutoresizingMaskIntoConstraints = false;
        return b;
    }

    private static NSButton TextButton(string title, Func<Task> onClick)
        => TextButton(title, (Action)(() => { _ = onClick(); }));

    private static NSBox Separator() => new()
    {
        BoxType = NSBoxType.NSBoxSeparator,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    private static int IndexOf<T>(IReadOnlyList<T> list, T value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (Equals(list[i], value))
            {
                return i;
            }
        }

        return 0;
    }
}
