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

    /// <summary>首页空态的「新建连接」。</summary>
    public event EventHandler? NewConnectionRequested;

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

    // 会话视图的生命周期与多 Tab 由 MainWindowController 管理，这里只负责构造。
    public SshTerminalView MakeSshTerminal(SshSession session) => new(session);

    public VncScreenView MakeVncScreen(VncSession session) => new(session);

    public RdpScreenView MakeRdpScreen(RemoteFlow.Protocol.Rdp.Mac.RdpSession session) => new(session);

    public NSView MakeSessionPlaceholder(string name)
        => Centered(Icon("checkmark.circle", 40, NSColor.SystemGreen), $"已连接 {name}",
            "该协议的会话画面尚未接入本区域。");

    public void ShowError(string message)
        => Swap(Centered(Icon("exclamationmark.triangle", 40, NSColor.SystemOrange), "连接失败", message));

    public void ShowSessionInfo(string name, string title, string body)
        => Swap(Centered(Icon("display", 40, NSColor.SystemBlue), title, body));

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

        var subtitle = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = 8,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        subtitle.AddArrangedSubview(ProtocolStyle.Badge(c.Profile.Protocol, c.ProtocolName));
        subtitle.AddArrangedSubview(Muted(c.HostDisplay, 13));

        var connect = NSButton.CreateButton("连接", () => ConnectRequested?.Invoke(this, c));
        connect.BezelStyle = NSBezelStyle.Rounded;
        connect.ControlSize = NSControlSize.Large;
        connect.KeyEquivalent = "\r"; // 默认按钮 → 系统强调色填充
        connect.WidthAnchor.ConstraintEqualTo(112).Active = true;

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
        stack.AddArrangedSubview(Gap(10));
        stack.AddArrangedSubview(connect);
        stack.AddArrangedSubview(Gap(20));
        stack.AddArrangedSubview(InfoPanel(
            ("主机", c.Host),
            ("端口", c.PortDisplay),
            ("协议", c.ProtocolName),
            ("创建时间", c.CreatedAtDisplay),
            ("最近连接", c.LastConnectedDisplay)));
        stack.AddArrangedSubview(Gap(12));
        stack.AddArrangedSubview(Styled("右键连接可编辑、复制或删除", 11, NSFontWeight.Regular, NSColor.TertiaryLabel));

        return TopAligned(stack);
    }

    // ── 首页 ────────────────────────────────────────────────────

    private NSView BuildHome(HomePageViewModel vm)
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
        stack.AddArrangedSubview(Gap(20));

        if (!vm.HasFavorites && !vm.HasRecent)
        {
            var newBtn = NSButton.CreateButton("新建连接", () => NewConnectionRequested?.Invoke(this, EventArgs.Empty));
            newBtn.BezelStyle = NSBezelStyle.Rounded;
            newBtn.ControlSize = NSControlSize.Large;
            newBtn.KeyEquivalent = "\r";
            stack.AddArrangedSubview(Muted(vm.IsFirstRun ? "还没有连接。新建一个开始。" : "收藏或连接过的设备会出现在这里。", 13));
            stack.AddArrangedSubview(Gap(6));
            stack.AddArrangedSubview(newBtn);
            return TopAligned(stack);
        }

        if (vm.HasFavorites)
        {
            stack.AddArrangedSubview(HomeSectionHeader("收藏",
                vm.FavoriteItems.Count > 6 ? () => vm.ViewAllFavoritesCommand.Execute(null) : null));
            stack.AddArrangedSubview(HomeListPanel(vm.FavoriteItems.Take(6)));
            stack.AddArrangedSubview(Gap(18));
        }

        if (vm.HasRecent)
        {
            stack.AddArrangedSubview(HomeSectionHeader("最近连接",
                vm.RecentItems.Count > 6 ? () => vm.ViewAllRecentCommand.Execute(null) : null));
            stack.AddArrangedSubview(HomeListPanel(vm.RecentItems.Take(6)));
        }

        return TopAligned(stack);
    }

    private const int PanelWidth = 380;

    private static NSView HomeSectionHeader(string title, Action? viewAll)
    {
        var row = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = 8,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        row.AddArrangedSubview(SectionLabel(title));
        if (viewAll is not null)
        {
            var link = NSButton.CreateButton("查看全部", () => viewAll());
            link.Bordered = false;
            link.ContentTintColor = NSColor.ControlAccent;
            link.Font = NSFont.SystemFontOfSize(11);
            row.AddArrangedSubview(new NSView());
            row.AddArrangedSubview(link);
        }
        row.WidthAnchor.ConstraintEqualTo(PanelWidth).Active = true;
        return row;
    }

    /// <summary>首页分组卡：圆角浅底 + 细分隔线，每行整行可点打开、悬停浅底。</summary>
    private NSView HomeListPanel(IEnumerable<ConnectionItemViewModel> items)
    {
        const int rowH = 46;
        var list = items.ToArray();

        var panel = Card();
        panel.WidthAnchor.ConstraintEqualTo(PanelWidth).Active = true;
        panel.HeightAnchor.ConstraintEqualTo(rowH * list.Length).Active = true;

        for (var i = 0; i < list.Length; i++)
        {
            var c = list[i];
            var row = BuildHomeRow(c);
            panel.AddSubview(row);
            var top = i * rowH;
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                row.LeadingAnchor.ConstraintEqualTo(panel.LeadingAnchor, 4),
                row.TrailingAnchor.ConstraintEqualTo(panel.TrailingAnchor, -4),
                row.TopAnchor.ConstraintEqualTo(panel.TopAnchor, top + 3),
                row.HeightAnchor.ConstraintEqualTo(rowH - 6),
            });

            if (i > 0)
            {
                var sep = Hairline();
                panel.AddSubview(sep);
                NSLayoutConstraint.ActivateConstraints(new[]
                {
                    sep.LeadingAnchor.ConstraintEqualTo(panel.LeadingAnchor, 42),
                    sep.TrailingAnchor.ConstraintEqualTo(panel.TrailingAnchor),
                    sep.TopAnchor.ConstraintEqualTo(panel.TopAnchor, top),
                    sep.HeightAnchor.ConstraintEqualTo(1),
                });
            }
        }

        return panel;
    }

    private NSView BuildHomeRow(ConnectionItemViewModel c)
    {
        var icon = new NSImageView
        {
            Image = ProtocolStyle.Symbol(c.Profile.Protocol),
            TranslatesAutoresizingMaskIntoConstraints = false,
            ContentTintColor = ProtocolStyle.Tint(c.Profile.Protocol),
            SymbolConfiguration = NSImageSymbolConfiguration.Create(15, NSFontWeight.Regular),
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

        var chevron = new NSImageView
        {
            Image = NSImage.GetSystemSymbol("chevron.right", null),
            ContentTintColor = NSColor.TertiaryLabel,
            TranslatesAutoresizingMaskIntoConstraints = false,
            SymbolConfiguration = NSImageSymbolConfiguration.Create(11, NSFontWeight.Semibold),
        };

        var row = new TapRow(() => ConnectRequested?.Invoke(this, c)) { TranslatesAutoresizingMaskIntoConstraints = false };
        row.AddSubview(icon);
        row.AddSubview(col);
        row.AddSubview(chevron);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            icon.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor, 10),
            icon.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor),
            icon.WidthAnchor.ConstraintEqualTo(20),
            col.LeadingAnchor.ConstraintEqualTo(icon.TrailingAnchor, 9),
            col.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor),
            chevron.LeadingAnchor.ConstraintGreaterThanOrEqualTo(col.TrailingAnchor, 8),
            chevron.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor, -12),
            chevron.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor),
        });
        return row;
    }

    /// <summary>整行可点（打开连接）+ 悬停浅底。</summary>
    private sealed class TapRow : NSView
    {
        private readonly Action _onTap;
        private NSTrackingArea? _tracking;

        public TapRow(Action onTap)
        {
            _onTap = onTap;
            WantsLayer = true;
            Layer!.CornerRadius = 6;
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
            => Layer!.BackgroundColor = NSColor.QuaternaryLabel.ColorWithAlphaComponent(0.5f).CGColor;

        public override void MouseExited(NSEvent theEvent)
            => Layer!.BackgroundColor = null;

        public override void MouseDown(NSEvent theEvent) => _onTap();
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

    /// <summary>
    /// 圆角分组卡 / 分隔线容器。裸 CALayer 的 CGColor 在构造时就冻结，明暗切换不跟随
    /// （深色模式下会白底黑字看不见）—— 本类在 <c>ViewDidChangeEffectiveAppearance</c> 时
    /// 按当前外观重刷 layer 颜色。<paramref name="fill"/> 为 null 时只作分隔线（描边色填充）。
    /// </summary>
    private sealed class CardView : NSView
    {
        private readonly Func<NSColor>? _fill;
        private readonly Func<NSColor>? _border;

        public CardView(Func<NSColor>? fill, Func<NSColor>? border = null, nfloat cornerRadius = default)
        {
            _fill = fill;
            _border = border;
            WantsLayer = true;
            TranslatesAutoresizingMaskIntoConstraints = false;
            Layer!.CornerRadius = cornerRadius;
            if (border is not null)
            {
                Layer.BorderWidth = 1;
            }
        }

        public override void ViewDidChangeEffectiveAppearance()
        {
            base.ViewDidChangeEffectiveAppearance();
            Refresh();
        }

        public override void ViewDidMoveToWindow()
        {
            base.ViewDidMoveToWindow();
            Refresh();
        }

        private void Refresh()
        {
            var prev = NSAppearance.CurrentAppearance;
            NSAppearance.CurrentAppearance = EffectiveAppearance;
            if (_fill is not null)
            {
                Layer!.BackgroundColor = _fill().CGColor;
            }
            if (_border is not null)
            {
                Layer!.BorderColor = _border().CGColor;
            }
            NSAppearance.CurrentAppearance = prev;
        }
    }

    private static CardView Card() =>
        new(() => NSColor.ControlBackground, () => NSColor.Separator, 8);

    private static CardView Hairline() => new(() => NSColor.Separator);

    /// <summary>System Settings 风格的分组信息卡：圆角浅底 + 细分隔线，标签左 / 值右。</summary>
    private static NSView InfoPanel(params (string Label, string Value)[] rows)
    {
        const int width = 380;
        const int rowH = 34;

        var panel = Card();
        panel.WidthAnchor.ConstraintEqualTo(width).Active = true;
        panel.HeightAnchor.ConstraintEqualTo(rowH * rows.Length).Active = true;

        for (var i = 0; i < rows.Length; i++)
        {
            var (label, value) = rows[i];
            var l = Muted(label, 12);
            var v = Plain(string.IsNullOrEmpty(value) ? "—" : value, 12);
            v.Alignment = NSTextAlignment.Right;
            v.Selectable = true;
            v.TextColor = NSColor.SecondaryLabel;

            panel.AddSubview(l);
            panel.AddSubview(v);
            var top = i * rowH;
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                l.LeadingAnchor.ConstraintEqualTo(panel.LeadingAnchor, 12),
                l.TopAnchor.ConstraintEqualTo(panel.TopAnchor, top),
                l.HeightAnchor.ConstraintEqualTo(rowH),
                v.TrailingAnchor.ConstraintEqualTo(panel.TrailingAnchor, -12),
                v.LeadingAnchor.ConstraintGreaterThanOrEqualTo(l.TrailingAnchor, 12),
                v.TopAnchor.ConstraintEqualTo(panel.TopAnchor, top),
                v.HeightAnchor.ConstraintEqualTo(rowH),
            });

            if (i > 0)
            {
                var sep = Hairline();
                panel.AddSubview(sep);
                NSLayoutConstraint.ActivateConstraints(new[]
                {
                    sep.LeadingAnchor.ConstraintEqualTo(panel.LeadingAnchor, 12),
                    sep.TrailingAnchor.ConstraintEqualTo(panel.TrailingAnchor),
                    sep.TopAnchor.ConstraintEqualTo(panel.TopAnchor, top),
                    sep.HeightAnchor.ConstraintEqualTo(1),
                });
            }
        }

        return panel;
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
