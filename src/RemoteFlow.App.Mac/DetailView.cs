using AppKit;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Presentation.ViewModels;
using RemoteFlow.Protocol.Ssh;
using RemoteFlow.Protocol.Vnc;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 第三列：详情 / 会话。多态：空态、连接信息卡、连接中、SSH 终端 / VNC 画面、错误、首页、凭据。
/// </summary>
public sealed class DetailView : NSView
{
    private readonly NSView _container = new() { TranslatesAutoresizingMaskIntoConstraints = false };

    public event EventHandler<ConnectionItemViewModel>? ConnectRequested;
    public event EventHandler<ConnectionItemViewModel>? EditRequested;
    public event EventHandler<ConnectionItemViewModel>? DeleteRequested;

    public DetailView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        AddSubview(_container);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            _container.LeadingAnchor.ConstraintEqualTo(SafeAreaLayoutGuide.LeadingAnchor),
            _container.TrailingAnchor.ConstraintEqualTo(SafeAreaLayoutGuide.TrailingAnchor),
            _container.TopAnchor.ConstraintEqualTo(SafeAreaLayoutGuide.TopAnchor),
            _container.BottomAnchor.ConstraintEqualTo(BottomAnchor),
        });

        ShowEmpty();
    }

    public override bool IsFlipped => true;

    public void ShowEmpty()
        => Swap(EmptyState("选择一个连接", "从左侧列表选择，或用工具栏「＋」新建连接。", "rectangle.connected.to.line.below"));

    public void ShowConnection(ConnectionItemViewModel c) => Swap(BuildInfoCard(c));

    public void ShowConnecting(string name)
        => Swap(Centered(Spinner(), $"正在连接 {name} …"));

    public void ShowSessionPlaceholder(string name, Guid sessionId)
        => Swap(Centered(
            Icon("checkmark.circle", 40, NSColor.SystemGreen),
            $"已连接 {name}",
            "该协议的会话画面尚未接入本区域。"));

    /// <summary>SSH 会话：xterm.js 终端。</summary>
    public void ShowSshTerminal(SshSession session) => Swap(new SshTerminalView(session));

    /// <summary>VNC 会话：远端画面。</summary>
    public void ShowVncScreen(VncSession session) => Swap(new VncScreenView(session));

    public void ShowError(string message)
        => Swap(Centered(Icon("exclamationmark.triangle", 40, NSColor.SystemOrange), "连接失败", message));

    public void ShowHome(HomePageViewModel vm)
    {
        Swap(BuildHome(vm));
        _ = ReloadHomeAsync(vm);
    }

    private async Task ReloadHomeAsync(HomePageViewModel vm)
    {
        try
        {
            await vm.LoadAsync();
        }
        catch
        {
            // 首页数据加载失败不阻塞界面。
        }

        NSApplication.SharedApplication.BeginInvokeOnMainThread(() => Swap(BuildHome(vm)));
    }

    public void ShowCredentials(CredentialsPageViewModel vm) => Swap(BuildCredentials(vm));

    // ── 连接信息卡 ──────────────────────────────────────────────

    private NSView BuildInfoCard(ConnectionItemViewModel c)
    {
        var title = Big(c.Name, 24);
        var subtitle = Muted($"{c.ProtocolName}   ·   {c.HostDisplay}", 13);

        var connect = NSButton.CreateButton("连接", () => ConnectRequested?.Invoke(this, c));
        connect.BezelStyle = NSBezelStyle.Rounded;
        connect.ControlSize = NSControlSize.Large;
        connect.KeyEquivalent = "\r";

        var edit = NSButton.CreateButton("编辑…", () => EditRequested?.Invoke(this, c));
        edit.BezelStyle = NSBezelStyle.Rounded;
        var delete = NSButton.CreateButton("删除…", () => DeleteRequested?.Invoke(this, c));
        delete.BezelStyle = NSBezelStyle.Rounded;

        var actions = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Spacing = 8,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        actions.AddArrangedSubview(connect);
        actions.AddArrangedSubview(edit);
        actions.AddArrangedSubview(delete);

        var stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 14,
            EdgeInsets = new NSEdgeInsets(32, 40, 32, 40),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        stack.AddArrangedSubview(title);
        stack.AddArrangedSubview(subtitle);
        stack.AddArrangedSubview(Gap(8));
        stack.AddArrangedSubview(actions);
        stack.AddArrangedSubview(Gap(12));
        stack.AddArrangedSubview(SectionLabel("连接信息"));
        stack.AddArrangedSubview(InfoGrid(new (string, string)[]
        {
            ("主机", c.Host),
            ("端口", c.PortDisplay),
            ("协议", c.ProtocolName),
        }));
        stack.AddArrangedSubview(Gap(8));
        stack.AddArrangedSubview(SectionLabel("使用信息"));
        stack.AddArrangedSubview(InfoGrid(new (string, string)[]
        {
            ("创建时间", c.CreatedAtDisplay),
            ("最近连接", c.LastConnectedDisplay),
        }));

        return TopAligned(stack);
    }

    // ── 首页 ────────────────────────────────────────────────────

    private static NSView BuildHome(HomePageViewModel vm)
    {
        var stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 6,
            EdgeInsets = new NSEdgeInsets(40, 44, 32, 44),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        stack.AddArrangedSubview(Big(string.IsNullOrEmpty(vm.Greeting) ? "欢迎" : vm.Greeting, 28));
        stack.AddArrangedSubview(Muted(vm.DateLine ?? "", 13));
        stack.AddArrangedSubview(Muted($"{vm.TotalConnections} 个连接  ·  {vm.ConnectedSessions} 个会话已连接", 12));
        stack.AddArrangedSubview(Gap(18));

        stack.AddArrangedSubview(SectionLabel("收藏"));
        foreach (var f in vm.FavoriteItems.Take(6))
        {
            stack.AddArrangedSubview(MiniRow(f));
        }
        if (vm.FavoriteItems.Count == 0)
        {
            stack.AddArrangedSubview(Muted("暂无收藏", 12));
        }

        stack.AddArrangedSubview(Gap(14));
        stack.AddArrangedSubview(SectionLabel("最近连接"));
        foreach (var r in vm.RecentItems.Take(6))
        {
            stack.AddArrangedSubview(MiniRow(r));
        }
        if (vm.RecentItems.Count == 0)
        {
            stack.AddArrangedSubview(Muted("暂无记录", 12));
        }

        return TopAligned(stack);
    }

    private static NSView MiniRow(ConnectionItemViewModel c)
    {
        var icon = new NSImageView
        {
            Image = ProtocolSymbol(c.Profile.Protocol),
            TranslatesAutoresizingMaskIntoConstraints = false,
            ContentTintColor = NSColor.SecondaryLabel,
            SymbolConfiguration = NSImageSymbolConfiguration.Create(13, NSFontWeight.Regular),
        };
        var col = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 1,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        col.AddArrangedSubview(Plain(c.Name, 13));
        col.AddArrangedSubview(Muted(c.HostDisplay, 11));

        var row = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = 8,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        row.AddArrangedSubview(icon);
        row.AddArrangedSubview(col);
        return row;
    }

    // ── 凭据 ────────────────────────────────────────────────────

    private static NSView BuildCredentials(CredentialsPageViewModel vm)
    {
        _ = vm.LoadAsync();

        var table = new NSTableView
        {
            HeaderView = null,
            RowHeight = 44,
            BackgroundColor = NSColor.Clear,
            SelectionHighlightStyle = NSTableViewSelectionHighlightStyle.Regular,
            Style = NSTableViewStyle.Inset,
        };
        table.AddColumn(new NSTableColumn("c") { ResizingMask = NSTableColumnResizing.Autoresizing });

        var editButton = new NSButton { Title = "编辑", Enabled = false, BezelStyle = NSBezelStyle.Rounded };
        var deleteButton = new NSButton { Title = "删除", Enabled = false, BezelStyle = NSBezelStyle.Rounded };
        var empty = Muted("还没有凭据。点「新建凭据」把常用账号 / 私钥交给钥匙串保管。", 12);

        _ = new CredentialListSource(table, vm.Items,
            onSelect: item =>
            {
                vm.SelectedItem = item;
                editButton.Enabled = item is not null;
                deleteButton.Enabled = item is not null;
            },
            onActivate: item => _ = vm.EditCommand.ExecuteAsync(item));

        void SyncEmpty() => empty.Hidden = vm.Items.Count > 0;
        vm.Items.CollectionChanged += (_, _) =>
            NSApplication.SharedApplication.BeginInvokeOnMainThread(SyncEmpty);
        SyncEmpty();

        var newButton = NSButton.CreateButton("新建凭据", () => _ = vm.CreateCommand.ExecuteAsync(null));
        newButton.BezelStyle = NSBezelStyle.Rounded;
        editButton.Activated += (_, _) => _ = vm.EditCommand.ExecuteAsync(null);
        deleteButton.Activated += (_, _) => _ = vm.DeleteCommand.ExecuteAsync(null);

        var actions = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Spacing = 8,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        actions.AddArrangedSubview(newButton);
        actions.AddArrangedSubview(editButton);
        actions.AddArrangedSubview(deleteButton);

        var scroll = new NSScrollView
        {
            DocumentView = table,
            DrawsBackground = false,
            HasVerticalScroller = true,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        var header = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 8,
            EdgeInsets = new NSEdgeInsets(32, 40, 10, 40),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        header.AddArrangedSubview(Big("凭据", 24));
        header.AddArrangedSubview(Muted("密码 / 私钥由 macOS 钥匙串加密保存，连接库只存引用。", 12));
        header.AddArrangedSubview(actions);

        var root = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        root.AddSubview(header);
        root.AddSubview(scroll);
        root.AddSubview(empty);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            header.LeadingAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.LeadingAnchor),
            header.TrailingAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TrailingAnchor),
            header.TopAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TopAnchor),
            scroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 26),
            scroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor, -26),
            scroll.TopAnchor.ConstraintEqualTo(header.BottomAnchor, 8),
            scroll.BottomAnchor.ConstraintEqualTo(root.BottomAnchor, -20),
            empty.TopAnchor.ConstraintEqualTo(scroll.TopAnchor, 24),
            empty.LeadingAnchor.ConstraintEqualTo(scroll.LeadingAnchor, 16),
        });
        return root;
    }

    // ── 容器切换 ────────────────────────────────────────────────

    private void Swap(NSView content)
    {
        foreach (var v in _container.Subviews.ToArray())
        {
            (v as SshTerminalView)?.Detach();
            (v as VncScreenView)?.Detach();
            v.RemoveFromSuperview();
        }

        content.TranslatesAutoresizingMaskIntoConstraints = false;
        _container.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            content.LeadingAnchor.ConstraintEqualTo(_container.LeadingAnchor),
            content.TrailingAnchor.ConstraintEqualTo(_container.TrailingAnchor),
            content.TopAnchor.ConstraintEqualTo(_container.TopAnchor),
            content.BottomAnchor.ConstraintEqualTo(_container.BottomAnchor),
        });
    }

    // ── 组件 ────────────────────────────────────────────────────

    private static NSView EmptyState(string title, string body, string symbol)
        => Centered(Icon(symbol, 44, NSColor.TertiaryLabel), title, body);

    private static NSView Centered(NSView top, string title, string? body = null)
    {
        var stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.CenterX,
            Spacing = 8,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        stack.AddArrangedSubview(top);
        var t = Plain(title, 17);
        t.Alignment = NSTextAlignment.Center;
        stack.AddArrangedSubview(t);
        if (!string.IsNullOrEmpty(body))
        {
            var b = Muted(body, 13);
            b.Alignment = NSTextAlignment.Center;
            stack.AddArrangedSubview(b);
        }

        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        host.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            stack.CenterXAnchor.ConstraintEqualTo(host.CenterXAnchor),
            stack.CenterYAnchor.ConstraintEqualTo(host.CenterYAnchor),
            stack.WidthAnchor.ConstraintLessThanOrEqualTo(360),
        });
        return host;
    }

    private static NSView TopAligned(NSView stack)
    {
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        host.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            stack.LeadingAnchor.ConstraintEqualTo(host.LeadingAnchor),
            stack.TrailingAnchor.ConstraintLessThanOrEqualTo(host.TrailingAnchor),
            stack.TopAnchor.ConstraintEqualTo(host.TopAnchor),
        });
        return host;
    }

    private static NSGridView InfoGrid((string Label, string Value)[] rows)
    {
        var grid = new NSGridView
        {
            RowSpacing = 7,
            ColumnSpacing = 20,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        foreach (var (label, value) in rows)
        {
            var l = Muted(label, 12);
            l.Alignment = NSTextAlignment.Right;
            var v = Plain(string.IsNullOrEmpty(value) ? "—" : value, 13);
            v.Selectable = true;
            grid.AddRow(new NSView[] { l, v });
        }

        return grid;
    }

    private static NSProgressIndicator Spinner()
    {
        var p = new NSProgressIndicator
        {
            Style = NSProgressIndicatorStyle.Spinning,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        p.StartAnimation(null);
        p.WidthAnchor.ConstraintEqualTo(32).Active = true;
        p.HeightAnchor.ConstraintEqualTo(32).Active = true;
        return p;
    }

    private static NSImageView Icon(string symbol, nfloat size, NSColor tint) => new()
    {
        Image = NSImage.GetSystemSymbol(symbol, null),
        ContentTintColor = tint,
        TranslatesAutoresizingMaskIntoConstraints = false,
        SymbolConfiguration = NSImageSymbolConfiguration.Create(size, NSFontWeight.Regular),
    };

    private static NSImage? ProtocolSymbol(RemoteFlow.Core.Models.ProtocolType p) => p switch
    {
        RemoteFlow.Core.Models.ProtocolType.Ssh => NSImage.GetSystemSymbol("apple.terminal", null)
            ?? NSImage.GetSystemSymbol("terminal", null),
        RemoteFlow.Core.Models.ProtocolType.Rdp => NSImage.GetSystemSymbol("display", null),
        _ => NSImage.GetSystemSymbol("rectangle.on.rectangle", null),
    };

    private static NSTextField SectionLabel(string text) => new()
    {
        StringValue = text,
        Bordered = false,
        Editable = false,
        Selectable = false,
        DrawsBackground = false,
        Font = NSFont.SystemFontOfSize(11, NSFontWeight.Semibold),
        TextColor = NSColor.SecondaryLabel,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    private static NSTextField Big(string text, nfloat size) => Styled(text, size, NSFontWeight.Bold, NSColor.Label);

    private static NSTextField Plain(string text, nfloat size) => Styled(text, size, NSFontWeight.Regular, NSColor.Label);

    private static NSTextField Muted(string text, nfloat size)
        => Styled(text, size, NSFontWeight.Regular, NSColor.SecondaryLabel);

    private static NSTextField Styled(string text, nfloat size, nfloat weight, NSColor color) => new()
    {
        StringValue = text,
        Bordered = false,
        Editable = false,
        Selectable = false,
        DrawsBackground = false,
        Font = NSFont.SystemFontOfSize(size, weight),
        TextColor = color,
        LineBreakMode = NSLineBreakMode.TruncatingTail,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    private static NSView Gap(nfloat h)
    {
        var v = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        v.HeightAnchor.ConstraintEqualTo(h).Active = true;
        return v;
    }
}
