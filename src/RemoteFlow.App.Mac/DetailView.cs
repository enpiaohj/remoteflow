using System.ComponentModel;
using AppKit;
using RemoteFlow.Core.Models;
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

    private ConnectionsPageViewModel? _detailVm;
    private bool _wiredDetailVm;
    private bool _showingDetail;

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

    /// <summary>连接详情。绑 <see cref="ConnectionsPageViewModel"/> 的 SelectedItem 及其
    /// 异步加载的历史 / 迷你图 / 会话状态；相关属性变化时重建。</summary>
    public void ShowConnection(ConnectionsPageViewModel vm)
    {
        _detailVm = vm;
        if (!_wiredDetailVm)
        {
            vm.PropertyChanged += OnDetailVmChanged;
            _wiredDetailVm = true;
        }

        RebuildDetail();
    }

    private void OnDetailVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (
            nameof(ConnectionsPageViewModel.SelectedItemHasHistory)
            or nameof(ConnectionsPageViewModel.SelectedConnectionStatusText)
            or nameof(ConnectionsPageViewModel.SelectedLastConnectedText)
            or nameof(ConnectionsPageViewModel.SelectedLastDurationText)
            or nameof(ConnectionsPageViewModel.SelectedTotalConnectionsText)))
        {
            return;
        }

        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            if (_showingDetail)
            {
                RebuildDetail();
            }
        });
    }

    private void RebuildDetail()
    {
        if (_detailVm?.SelectedItem is { } c)
        {
            SafeSwap(() => BuildDetail(_detailVm, c), "连接详情");
            _showingDetail = true;
        }
    }

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
        SafeSwap(() => BuildHome(vm), "首页");
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

        NSApplication.SharedApplication.BeginInvokeOnMainThread(() => SafeSwap(() => BuildHome(vm), "首页"));
    }

    /// <summary>构造视图时若抛异常，退化为错误占位并把异常写日志，而不是让 ObjC 回调静默吞掉、页面空白。</summary>
    private void SafeSwap(Func<NSView> build, string what)
    {
        try
        {
            Swap(build());
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"[DetailView] 构造「{what}」失败: {ex}");
            Swap(Centered(Icon("exclamationmark.triangle", 40, NSColor.SystemOrange),
                $"「{what}」渲染失败", ex.Message));
        }
    }

    public void ShowCredentials(CredentialsPageViewModel vm) => Swap(BuildCredentials(vm));

    // ── 连接详情 ────────────────────────────────────────────────

    private NSView BuildDetail(ConnectionsPageViewModel vm, ConnectionItemViewModel c)
    {
        var col = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 0,
            EdgeInsets = new NSEdgeInsets(26, 0, 32, 0),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        // ── 头部：图标 + 名称 + 收藏；状态点；连接 / 编辑 ──
        var tile = IconTile(c.Profile.Protocol, c.IsConnected, c.HasActiveSession);
        var name = Big(c.Name, 22);
        name.LineBreakMode = NSLineBreakMode.TruncatingTail;

        var fav = NSButton.CreateButton(string.Empty, () => { vm.ToggleFavoriteCommand.Execute(c); });
        fav.Bordered = false;
        fav.Image = NSImage.GetSystemSymbol(c.IsFavorite ? "star.fill" : "star", null);
        fav.ContentTintColor = c.IsFavorite ? NSColor.SystemYellow : NSColor.TertiaryLabel;
        fav.SymbolConfiguration = NSImageSymbolConfiguration.Create(15, NSFontWeight.Regular);
        fav.ToolTip = "收藏 / 取消收藏";

        var head = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = 12,
            Distribution = NSStackViewDistribution.Fill,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var headSpacer = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        head.AddArrangedSubview(tile);
        head.AddArrangedSubview(name);
        head.AddArrangedSubview(headSpacer);
        head.AddArrangedSubview(fav);
        headSpacer.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        AddFill(col, head);

        col.AddArrangedSubview(Gap(8));
        col.AddArrangedSubview(StatusRow(vm.SelectedConnectionStatusBrushKey, vm.SelectedConnectionStatusText));

        col.AddArrangedSubview(Gap(16));
        var connect = NSButton.CreateButton("连接", () => ConnectRequested?.Invoke(this, c));
        connect.BezelStyle = NSBezelStyle.Rounded;
        connect.ControlSize = NSControlSize.Large;
        connect.KeyEquivalent = "\r";
        var edit = NSButton.CreateButton(string.Empty, () => { vm.EditCommand.Execute(c); });
        edit.BezelStyle = NSBezelStyle.Rounded;
        edit.ControlSize = NSControlSize.Large;
        edit.Image = NSImage.GetSystemSymbol("pencil", null);
        edit.ToolTip = "编辑连接";

        var actions = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = 8,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var actSpacer = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        actions.AddArrangedSubview(connect);
        actions.AddArrangedSubview(edit);
        actions.AddArrangedSubview(actSpacer);
        connect.WidthAnchor.ConstraintEqualTo(200).Active = true;
        edit.WidthAnchor.ConstraintEqualTo(44).Active = true;
        actSpacer.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        AddFill(col, actions);

        // ── 连接信息 ──
        col.AddArrangedSubview(Gap(24));
        col.AddArrangedSubview(SectionLabel("连接信息"));
        col.AddArrangedSubview(Gap(8));

        var infoRows = new List<(string, NSView)>
        {
            ("主机", HostValue(c.HostDisplay)),
            ("端口", Plain(c.PortDisplay, 12)),
            ("协议", Plain(c.ProtocolName, 12)),
            ("分组", Plain(string.IsNullOrEmpty(c.GroupName) ? "未分组" : c.GroupName, 12)),
            ("凭据", CredentialValue(c.CredentialName)),
        };
        if (c.Tags.Count > 0)
        {
            infoRows.Add(("标签", TagChips(c.Tags)));
        }
        if (!string.IsNullOrWhiteSpace(c.Notes))
        {
            infoRows.Add(("备注", WrapValue(c.Notes)));
        }
        AddFill(col, FieldCard(infoRows));

        // ── 使用信息 ──
        col.AddArrangedSubview(Gap(20));
        col.AddArrangedSubview(SectionLabel("使用信息"));
        col.AddArrangedSubview(Gap(8));
        AddFill(col, FieldCard(new List<(string, NSView)>
        {
            ("创建时间", Muted(c.CreatedAtDisplay, 12)),
            ("最近连接", Muted(vm.SelectedLastConnectedText, 12)),
            ("上次时长", Muted(vm.SelectedLastDurationText, 12)),
            ("连接次数", Muted(vm.SelectedTotalConnectionsText, 12)),
        }));

        // ── 连接活动 ──
        col.AddArrangedSubview(Gap(20));
        col.AddArrangedSubview(SectionLabel("连接活动"));
        col.AddArrangedSubview(Gap(10));

        if (vm.SelectedItemHasHistory)
        {
            col.AddArrangedSubview(Muted("最近 10 次连接（柱高 = 时长）", 11));
            col.AddArrangedSubview(Gap(8));
            AddFill(col, SparklineCard(vm.SelectedItemSparkline.ToArray()));
            col.AddArrangedSubview(Gap(16));
            col.AddArrangedSubview(Muted("连接历史", 11));
            col.AddArrangedSubview(Gap(6));

            var history = vm.SelectedItemHistory.Take(8).ToArray();
            var hcard = Card();
            var hstack = new NSStackView
            {
                Orientation = NSUserInterfaceLayoutOrientation.Vertical,
                Alignment = NSLayoutAttribute.Leading,
                Spacing = 0,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            hcard.AddSubview(hstack);
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                hstack.LeadingAnchor.ConstraintEqualTo(hcard.LeadingAnchor),
                hstack.TrailingAnchor.ConstraintEqualTo(hcard.TrailingAnchor),
                hstack.TopAnchor.ConstraintEqualTo(hcard.TopAnchor),
                hstack.BottomAnchor.ConstraintEqualTo(hcard.BottomAnchor),
            });
            for (var i = 0; i < history.Length; i++)
            {
                if (i > 0)
                {
                    var sep = Hairline();
                    hstack.AddArrangedSubview(sep);
                    sep.WidthAnchor.ConstraintEqualTo(hstack.WidthAnchor).Active = true;
                    sep.HeightAnchor.ConstraintEqualTo(1).Active = true;
                }

                var hr = HistoryRow(history[i]);
                hstack.AddArrangedSubview(hr);
                hr.WidthAnchor.ConstraintEqualTo(hstack.WidthAnchor).Active = true;
            }
            AddFill(col, hcard);

            col.AddArrangedSubview(Gap(10));
            var all = NSButton.CreateButton("查看全部历史", () => { vm.ViewAllHistoryCommand.Execute(null); });
            all.Bordered = false;
            all.ContentTintColor = NSColor.ControlAccent;
            all.Font = NSFont.SystemFontOfSize(12);
            col.AddArrangedSubview(all);
        }
        else
        {
            col.AddArrangedSubview(Muted("还没有连接历史。连接成功后这里会显示最近记录与时长。", 12));
        }

        col.AddArrangedSubview(Gap(24));

        col.Menu = DetailMenu(vm, c);
        return ScrollHost(col, 34, 400);
    }

    /// <summary>详情页右键菜单 —— 走「我的连接」既有命令。</summary>
    private NSMenu DetailMenu(ConnectionsPageViewModel vm, ConnectionItemViewModel c)
    {
        var m = new NSMenu();
        m.AddItem(new NSMenuItem("连接", (_, _) => ConnectRequested?.Invoke(this, c)));
        m.AddItem(NSMenuItem.SeparatorItem);
        m.AddItem(new NSMenuItem("编辑…", (_, _) => vm.EditCommand.Execute(c)));
        m.AddItem(new NSMenuItem("复制", (_, _) => vm.DuplicateCommand.Execute(c)));
        m.AddItem(new NSMenuItem("测试连接…", (_, _) => vm.TestConnectionCommand.Execute(c)));
        m.AddItem(new NSMenuItem(c.IsFavorite ? "取消收藏" : "收藏", (_, _) => vm.ToggleFavoriteCommand.Execute(c)));
        m.AddItem(NSMenuItem.SeparatorItem);
        m.AddItem(new NSMenuItem("删除…", (_, _) => vm.DeleteCommand.Execute(c)));
        return m;
    }

    // ── 详情组件 ────────────────────────────────────────────────

    /// <summary>
    /// 协议图标块。<paramref name="connected"/> / <paramref name="active"/> 有值时，
    /// 右下角挂一枚在线状态徽标（绿=已连接、橙=会话活动中），与列表行保持一致。
    /// </summary>
    private static NSView IconTile(ProtocolType p, bool connected = false, bool active = false)
    {
        var tint = ProtocolStyle.Tint(p);
        var tile = new CardView(() => tint.ColorWithAlphaComponent(0.16f), cornerRadius: 9);
        tile.WidthAnchor.ConstraintEqualTo(40).Active = true;
        tile.HeightAnchor.ConstraintEqualTo(40).Active = true;

        var glyph = new NSImageView
        {
            Image = ProtocolStyle.Symbol(p),
            ContentTintColor = tint,
            TranslatesAutoresizingMaskIntoConstraints = false,
            SymbolConfiguration = NSImageSymbolConfiguration.Create(17, NSFontWeight.Medium),
        };
        tile.AddSubview(glyph);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            glyph.CenterXAnchor.ConstraintEqualTo(tile.CenterXAnchor),
            glyph.CenterYAnchor.ConstraintEqualTo(tile.CenterYAnchor),
        });

        if (connected || active)
        {
            // 40pt 图标块上 9pt 太小要凑近看，这里用 12pt。
            // 必须**完全落在**图标块内部：之前挂到 tile 外沿（+3）会溢出父视图，
            // 被祖先裁掉一角，看着就不圆了。
            var badge = ProtocolStyle.StatusBadge(12);
            tile.AddSubview(badge);
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                badge.TrailingAnchor.ConstraintEqualTo(tile.TrailingAnchor, -2),
                badge.BottomAnchor.ConstraintEqualTo(tile.BottomAnchor, -2),
            });
            ProtocolStyle.ApplyStatus(badge, connected, active);
        }

        return tile;
    }

    private static NSView StatusRow(string brushKey, string text)
    {
        var color = brushKey switch
        {
            "Status.Success" => NSColor.SystemGreen,
            "Status.Info" => NSColor.SystemBlue,
            "Status.Danger" => NSColor.SystemRed,
            _ => NSColor.TertiaryLabel,
        };
        var dot = new CardView(() => color, cornerRadius: 4);
        dot.WidthAnchor.ConstraintEqualTo(8).Active = true;
        dot.HeightAnchor.ConstraintEqualTo(8).Active = true;

        var row = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = 7,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        row.AddArrangedSubview(dot);
        row.AddArrangedSubview(Styled(text, 12, NSFontWeight.Semibold, NSColor.SecondaryLabel));
        return row;
    }

    private NSView HostValue(string host)
    {
        var label = new NSTextField
        {
            StringValue = host,
            Bordered = false,
            Editable = false,
            Selectable = true,
            DrawsBackground = false,
            Font = NSFont.MonospacedSystemFont(12, NSFontWeight.Regular),
            TextColor = NSColor.Label,
            LineBreakMode = NSLineBreakMode.TruncatingMiddle,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var copy = NSButton.CreateButton(string.Empty, () =>
        {
            var pb = NSPasteboard.GeneralPasteboard;
            pb.ClearContents();
            pb.SetStringForType(host, "public.utf8-plain-text");
        });
        copy.Bordered = false;
        copy.Image = NSImage.GetSystemSymbol("doc.on.doc", null);
        copy.ContentTintColor = NSColor.SecondaryLabel;
        copy.SymbolConfiguration = NSImageSymbolConfiguration.Create(11, NSFontWeight.Regular);
        copy.ToolTip = "复制主机 / IP";

        var row = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = 6,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        row.AddArrangedSubview(label);
        row.AddArrangedSubview(copy);
        return row;
    }

    private static NSView CredentialValue(string name)
    {
        var icon = new NSImageView
        {
            Image = NSImage.GetSystemSymbol("key", null),
            ContentTintColor = NSColor.SecondaryLabel,
            TranslatesAutoresizingMaskIntoConstraints = false,
            SymbolConfiguration = NSImageSymbolConfiguration.Create(11, NSFontWeight.Regular),
        };
        var row = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = 6,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        row.AddArrangedSubview(icon);
        row.AddArrangedSubview(Plain(string.IsNullOrEmpty(name) ? "未指定" : name, 12));
        return row;
    }

    private static NSView TagChips(IReadOnlyList<TagChip> tags)
    {
        var wrap = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = 5,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        foreach (var t in tags)
        {
            var chip = new NSTextField
            {
                StringValue = t.Name,
                Bordered = false,
                Editable = false,
                Selectable = false,
                DrawsBackground = false,
                Font = NSFont.SystemFontOfSize(10, NSFontWeight.Medium),
                TextColor = NSColor.SecondaryLabel,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            var box = new CardView(() => NSColor.QuaternaryLabel.ColorWithAlphaComponent(0.4f),
                () => NSColor.Separator, 4);
            box.AddSubview(chip);
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                chip.LeadingAnchor.ConstraintEqualTo(box.LeadingAnchor, 6),
                chip.TrailingAnchor.ConstraintEqualTo(box.TrailingAnchor, -6),
                chip.TopAnchor.ConstraintEqualTo(box.TopAnchor, 2),
                chip.BottomAnchor.ConstraintEqualTo(box.BottomAnchor, -2),
            });
            wrap.AddArrangedSubview(box);
        }
        return wrap;
    }

    private static NSView WrapValue(string text)
    {
        var f = new NSTextField
        {
            StringValue = text,
            Bordered = false,
            Editable = false,
            Selectable = true,
            DrawsBackground = false,
            Font = NSFont.SystemFontOfSize(12),
            TextColor = NSColor.Label,
            LineBreakMode = NSLineBreakMode.ByWordWrapping,
            Alignment = NSTextAlignment.Right,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        f.PreferredMaxLayoutWidth = 250;
        return f;
    }

    /// <summary>标签左（右对齐窄列）/ 值右，圆角卡容器。行高自适应。</summary>
    private static NSView FieldCard(IReadOnlyList<(string Label, NSView Value)> rows)
    {
        var grid = new NSGridView
        {
            RowSpacing = 10,
            ColumnSpacing = 16,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        foreach (var (label, value) in rows)
        {
            var l = Muted(label, 12);
            l.Alignment = NSTextAlignment.Right;
            grid.AddRow(new[] { l, value });
        }
        grid.GetColumn(0).Width = 52;
        grid.GetColumn(0).LeadingPadding = 0;
        grid.GetColumn(1).LeadingPadding = 0;

        var card = Card();
        card.AddSubview(grid);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            grid.LeadingAnchor.ConstraintEqualTo(card.LeadingAnchor, 14),
            grid.TrailingAnchor.ConstraintEqualTo(card.TrailingAnchor, -14),
            grid.TopAnchor.ConstraintEqualTo(card.TopAnchor, 12),
            grid.BottomAnchor.ConstraintEqualTo(card.BottomAnchor, -12),
        });
        return card;
    }

    private static NSView SparklineCard(IReadOnlyList<SparkBar> bars)
    {
        var card = Card();
        card.HeightAnchor.ConstraintEqualTo(72).Active = true;

        var row = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.Bottom,
            Spacing = 6,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        foreach (var b in bars)
        {
            var color = b.BrushKey == "Status.Danger" ? NSColor.SystemRed : NSColor.SystemGreen;
            var bar = new CardView(() => color, cornerRadius: 3) { ToolTip = b.Tooltip };
            bar.WidthAnchor.ConstraintEqualTo(16).Active = true;
            bar.HeightAnchor.ConstraintEqualTo((nfloat)Math.Max(5, b.Height * 44)).Active = true;
            row.AddArrangedSubview(bar);
        }

        card.AddSubview(row);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            row.LeadingAnchor.ConstraintEqualTo(card.LeadingAnchor, 14),
            row.BottomAnchor.ConstraintEqualTo(card.BottomAnchor, -12),
            row.TopAnchor.ConstraintGreaterThanOrEqualTo(card.TopAnchor, 12),
        });
        return card;
    }

    private static NSView HistoryRow(HistoryItemViewModel h)
    {
        var ok = h.ResultText == "成功";
        var dot = new CardView(() => ok ? NSColor.SystemGreen : NSColor.SystemRed, cornerRadius: 3);
        dot.WidthAnchor.ConstraintEqualTo(6).Active = true;
        dot.HeightAnchor.ConstraintEqualTo(6).Active = true;

        var when = Muted(h.StartedAtDisplay, 11);
        var dur = Muted(h.DurationDisplay, 11);

        var row = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        row.AddSubview(dot);
        row.AddSubview(when);
        row.AddSubview(dur);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            row.HeightAnchor.ConstraintEqualTo(32),
            dot.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor, 14),
            dot.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor),
            when.LeadingAnchor.ConstraintEqualTo(dot.TrailingAnchor, 8),
            when.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor),
            dur.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor, -14),
            dur.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor),
        });
        return row;
    }

    /// <summary>纵向滚动区：<paramref name="content"/>（纵向 NSStackView，无左右 EdgeInsets）
    /// 左右贴到可视区留 <paramref name="hMargin"/> 边距，随窗口伸缩；子卡片用 AddFill 一起变宽。</summary>
    private static NSView ScrollHost(NSView content, nfloat hMargin, nfloat minContentWidth)
    {
        var doc = new FlippedHost { TranslatesAutoresizingMaskIntoConstraints = false };
        doc.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            content.LeadingAnchor.ConstraintEqualTo(doc.LeadingAnchor, hMargin),
            content.TrailingAnchor.ConstraintEqualTo(doc.TrailingAnchor, -hMargin),
            content.TopAnchor.ConstraintEqualTo(doc.TopAnchor),
            content.BottomAnchor.ConstraintEqualTo(doc.BottomAnchor),
        });
        // 内容最小宽只做「建议」（低优先级）。若用必需优先级，这个值会沿
        // 内容 → 详情列 → NSSplitViewController → 窗口一路顶上去，被当成详情列的
        // fittingSize / 最大厚度，窗口就卡在 nav_max + list_max + 该值 拉不宽（"宽度锁定"）。
        var minW = content.WidthAnchor.ConstraintGreaterThanOrEqualTo(minContentWidth);
        minW.Priority = (float)NSLayoutPriority.DefaultLow;
        minW.Active = true;

        var scroll = new NSScrollView
        {
            DocumentView = doc,
            DrawsBackground = false,
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        doc.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor).Active = true;
        // 横向 hugging 降到最低：告诉 Auto Layout「我乐意被拉得比 fittingSize 宽得多」。
        // 否则 NSSplitViewController 会拿详情列的 fittingSize 当它的最大厚度，窗口拉不宽。
        content.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        doc.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        scroll.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        return scroll;
    }

    /// <summary>把 <paramref name="child"/> 加进 <paramref name="stack"/> 并让其宽度跟随
    /// stack（随窗口伸缩）。必须先 AddArrangedSubview 再约束，否则无共同祖先。</summary>
    private static void AddFill(NSStackView stack, NSView child)
    {
        stack.AddArrangedSubview(child);
        child.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
    }

    private sealed class FlippedHost : NSView
    {
        public override bool IsFlipped => true;
    }

    // ── 首页 ────────────────────────────────────────────────────


    private HomePageViewModel? _homeVm;

    /// <summary>连接项的右键菜单（首页卡片 / 行共用）。经 <see cref="HomePageViewModel.RequestConnectionAction"/>
    /// 桥接到「我的连接」既有命令。</summary>
    private NSMenu ConnMenu(ConnectionItemViewModel c)
    {
        var m = new NSMenu();
        void Add(string title, string action) =>
            m.AddItem(new NSMenuItem(title, (_, _) => _homeVm?.RequestConnectionAction(c, action)));

        Add(c.IsConnected || c.HasActiveSession ? "切换到会话" : "连接", HomeRowActions.Connect);
        if (c.HasActiveSession)
        {
            Add("断开连接", HomeRowActions.Disconnect);
        }
        m.AddItem(NSMenuItem.SeparatorItem);
        Add("编辑…", HomeRowActions.Edit);
        Add("测试连接…", HomeRowActions.Test);
        Add(c.IsFavorite ? "取消收藏" : "收藏", HomeRowActions.Favorite);
        m.AddItem(NSMenuItem.SeparatorItem);
        Add("在「我的连接」中显示", HomeRowActions.Manage);
        return m;
    }

    private NSView BuildHome(HomePageViewModel vm)
    {
        _homeVm = vm;
        var col = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 0,
            EdgeInsets = new NSEdgeInsets(34, 0, 32, 0),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        // 问候 + 日期 + 时钟 + 统计
        col.AddArrangedSubview(Big(string.IsNullOrEmpty(vm.Greeting) ? "欢迎" : vm.Greeting, 28));
        col.AddArrangedSubview(Gap(3));
        col.AddArrangedSubview(Styled(vm.DateLine ?? "", 13, NSFontWeight.Regular, NSColor.TertiaryLabel));
        if (vm.HasClockLine)
        {
            col.AddArrangedSubview(Gap(2));
            var clock = Styled(vm.ClockLine ?? "", 12, NSFontWeight.Regular, NSColor.SecondaryLabel);
            clock.Font = NSFont.MonospacedSystemFont(12, NSFontWeight.Regular);
            col.AddArrangedSubview(clock);
        }
        col.AddArrangedSubview(Gap(7));
        col.AddArrangedSubview(Muted($"{vm.TotalConnections} 个连接  ·  {vm.ConnectedSessions} 个会话已连接", 12));
        col.AddArrangedSubview(Gap(22));

        if (vm.IsFirstRun)
        {
            AddFill(col, HomeFirstRunCard());
            col.AddArrangedSubview(Gap(20));
        }

        // 最近连接：一排大卡片
        if (vm.HasRecent)
        {
            AddFill(col, HomeSectionHeader("最近连接", () => vm.ViewAllRecentCommand.Execute(null)));
            col.AddArrangedSubview(Gap(10));
            var cardsRow = new NSStackView
            {
                Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
                Alignment = NSLayoutAttribute.Top,
                Distribution = NSStackViewDistribution.FillEqually,
                Spacing = 12,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            foreach (var r in vm.RecentItems.Take(3))
            {
                cardsRow.AddArrangedSubview(HomeRecentCard(r));
            }
            AddFill(col, cardsRow);
            col.AddArrangedSubview(Gap(24));
        }

        // 收藏 / 最近活动 —— 首页已全宽（列表列在首页折叠），恢复 Windows 的两列。
        // 不用 Distribution.FillEqually：它加的等宽约束优先级顶不过列卡里文本的抗压缩
        // 优先级（750），会把一列压成 ~60px。改为两卡显式必需等宽 + Distribution.Fill。
        var twoCol = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.Top,
            Distribution = NSStackViewDistribution.Fill,
            Spacing = 24,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var favCol = HomeColumnCard("收藏", "star",
            vm.HasFavorites ? vm.FavoriteItems.Select(HomeFavRow) : null,
            "还没有收藏的连接。在「我的连接」里点星标即可加入。",
            () => vm.ViewAllFavoritesCommand.Execute(null), "管理收藏");
        var actCol = HomeColumnCard("最近活动", "clock.arrow.circlepath",
            vm.HasActivity ? vm.RecentHistory.Select(HomeActivityRow) : null,
            "还没有连接记录。",
            () => vm.ViewAllActivityCommand.Execute(null), "查看所有活动");
        twoCol.AddArrangedSubview(favCol);
        twoCol.AddArrangedSubview(actCol);
        favCol.WidthAnchor.ConstraintEqualTo(actCol.WidthAnchor).Active = true;
        AddFill(col, twoCol);

        if (vm.ShowSecurityTip)
        {
            col.AddArrangedSubview(Gap(20));
            AddFill(col, HomeSecurityCard(() => vm.DismissSecurityTipCommand.Execute(null)));
        }

        col.AddArrangedSubview(Gap(24));
        return ScrollHost(col, 40, 640);
    }

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
            var spacer = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
            spacer.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
            var link = NSButton.CreateButton("查看全部", () => viewAll());
            link.Bordered = false;
            link.ContentTintColor = NSColor.ControlAccent;
            link.Font = NSFont.SystemFontOfSize(11);
            row.AddArrangedSubview(spacer);
            row.AddArrangedSubview(link);
        }
        return row;
    }

    /// <summary>最近连接的大卡片：协议色左条 + 图标块 + 名称 / 主机 / 协议徽章。整卡可点。</summary>
    private NSView HomeRecentCard(ConnectionItemViewModel c)
    {
        var card = new TapRow(() => ConnectRequested?.Invoke(this, c)) { Menu = ConnMenu(c) };
        var border = new CardView(() => NSColor.SecondaryLabel.ColorWithAlphaComponent(0.06f),
            () => NSColor.SecondaryLabel.ColorWithAlphaComponent(0.12f), 10);
        card.AddSubview(border);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            card.HeightAnchor.ConstraintEqualTo(104),
            border.LeadingAnchor.ConstraintEqualTo(card.LeadingAnchor),
            border.TrailingAnchor.ConstraintEqualTo(card.TrailingAnchor),
            border.TopAnchor.ConstraintEqualTo(card.TopAnchor),
            border.BottomAnchor.ConstraintEqualTo(card.BottomAnchor),
        });

        var tint = ProtocolStyle.Tint(c.Profile.Protocol);
        var bar = new CardView(() => tint, cornerRadius: 1.5f);
        var tile = IconTile(c.Profile.Protocol, c.IsConnected, c.HasActiveSession);
        var name = Plain(c.Name, 14);
        name.Font = NSFont.SystemFontOfSize(14, NSFontWeight.Semibold);
        name.LineBreakMode = NSLineBreakMode.TruncatingTail;
        var host = Muted(c.HostDisplay, 11);
        host.Font = NSFont.MonospacedSystemFont(11, NSFontWeight.Regular);
        var badge = ProtocolStyle.Badge(c.Profile.Protocol, c.ProtocolName);

        var txt = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 3,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        txt.AddArrangedSubview(name);
        txt.AddArrangedSubview(host);
        txt.AddArrangedSubview(badge);

        card.AddSubview(bar);
        card.AddSubview(tile);
        card.AddSubview(txt);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            bar.LeadingAnchor.ConstraintEqualTo(card.LeadingAnchor),
            bar.TopAnchor.ConstraintEqualTo(card.TopAnchor, 16),
            bar.BottomAnchor.ConstraintEqualTo(card.BottomAnchor, -16),
            bar.WidthAnchor.ConstraintEqualTo(3),
            tile.LeadingAnchor.ConstraintEqualTo(card.LeadingAnchor, 16),
            tile.CenterYAnchor.ConstraintEqualTo(card.CenterYAnchor),
            txt.LeadingAnchor.ConstraintEqualTo(tile.TrailingAnchor, 12),
            txt.TrailingAnchor.ConstraintLessThanOrEqualTo(card.TrailingAnchor, -12),
            txt.CenterYAnchor.ConstraintEqualTo(card.CenterYAnchor),
        });
        return card;
    }

    /// <summary>收藏 / 最近活动列卡：标题行 + 「查看全部」 + 滚动列表 / 空态。</summary>
    private NSView HomeColumnCard(string title, string symbol, IEnumerable<NSView>? rows, string emptyText,
        Action viewAll, string footerText)
    {
        var card = Card();
        card.HeightAnchor.ConstraintEqualTo(420).Active = true;

        var head = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = 7,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        head.AddArrangedSubview(new NSImageView
        {
            Image = NSImage.GetSystemSymbol(symbol, null),
            ContentTintColor = NSColor.SecondaryLabel,
            TranslatesAutoresizingMaskIntoConstraints = false,
            SymbolConfiguration = NSImageSymbolConfiguration.Create(12, NSFontWeight.Regular),
        });
        head.AddArrangedSubview(SectionLabel(title));
        var hspacer = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        hspacer.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        head.AddArrangedSubview(hspacer);
        var link = NSButton.CreateButton("查看全部", () => viewAll());
        link.Bordered = false;
        link.ContentTintColor = NSColor.ControlAccent;
        link.Font = NSFont.SystemFontOfSize(11);
        head.AddArrangedSubview(link);

        NSView body;
        var rowList = rows?.ToArray();
        if (rowList is { Length: > 0 })
        {
            var listStack = new NSStackView
            {
                Orientation = NSUserInterfaceLayoutOrientation.Vertical,
                Alignment = NSLayoutAttribute.Leading,
                Spacing = 2,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            foreach (var r in rowList)
            {
                listStack.AddArrangedSubview(r);
                r.WidthAnchor.ConstraintEqualTo(listStack.WidthAnchor).Active = true;
            }

            var doc = new FlippedHost { TranslatesAutoresizingMaskIntoConstraints = false };
            doc.AddSubview(listStack);
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                listStack.LeadingAnchor.ConstraintEqualTo(doc.LeadingAnchor),
                listStack.TrailingAnchor.ConstraintEqualTo(doc.TrailingAnchor),
                listStack.TopAnchor.ConstraintEqualTo(doc.TopAnchor),
                listStack.BottomAnchor.ConstraintEqualTo(doc.BottomAnchor),
            });
            var scroll = new NSScrollView
            {
                DocumentView = doc,
                DrawsBackground = false,
                HasVerticalScroller = true,
                AutohidesScrollers = true,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            doc.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor).Active = true;

            // 用 frame 布局把这个**内层** NSScrollView 包一层：它的约束若直接接进外层，
            // 会把详情列的最大厚度压成最小厚度，窗口就拉不宽（首页"宽度锁定"的真凶）。
            var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
            scroll.TranslatesAutoresizingMaskIntoConstraints = true;
            scroll.AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable;
            host.AddSubview(scroll);
            body = host;
        }
        else
        {
            var e = Muted(emptyText, 12);
            e.LineBreakMode = NSLineBreakMode.ByWordWrapping;
            e.PreferredMaxLayoutWidth = 300;
            body = e;
        }

        var footSep = Hairline();
        var foot = NSButton.CreateButton(footerText, () => viewAll());
        // 直接 AddSubview + 约束的控件必须关掉 autoresizing 约束，否则与显式约束冲突，
        // 布局引擎会给出一个僵死的宽度 → 详情列长不大 → 窗口"宽度锁定"。
        foot.TranslatesAutoresizingMaskIntoConstraints = false;
        foot.Bordered = false;
        foot.ContentTintColor = NSColor.SecondaryLabel;
        foot.Font = NSFont.SystemFontOfSize(12);

        card.AddSubview(head);
        card.AddSubview(body);
        card.AddSubview(footSep);
        card.AddSubview(foot);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            head.LeadingAnchor.ConstraintEqualTo(card.LeadingAnchor, 16),
            head.TrailingAnchor.ConstraintEqualTo(card.TrailingAnchor, -14),
            head.TopAnchor.ConstraintEqualTo(card.TopAnchor, 14),
            body.LeadingAnchor.ConstraintEqualTo(card.LeadingAnchor, 10),
            body.TrailingAnchor.ConstraintEqualTo(card.TrailingAnchor, -8),
            body.TopAnchor.ConstraintEqualTo(head.BottomAnchor, 10),
            body.BottomAnchor.ConstraintEqualTo(footSep.TopAnchor, -6),
            footSep.LeadingAnchor.ConstraintEqualTo(card.LeadingAnchor, 12),
            footSep.TrailingAnchor.ConstraintEqualTo(card.TrailingAnchor, -12),
            footSep.HeightAnchor.ConstraintEqualTo(1),
            footSep.BottomAnchor.ConstraintEqualTo(foot.TopAnchor, -6),
            foot.CenterXAnchor.ConstraintEqualTo(card.CenterXAnchor),
            foot.BottomAnchor.ConstraintEqualTo(card.BottomAnchor, -8),
        });
        return card;
    }

    private NSView HomeFavRow(ConnectionItemViewModel c)
    {
        var tile = IconTile(c.Profile.Protocol, c.IsConnected, c.HasActiveSession);
        tile.WidthAnchor.ConstraintEqualTo(30).Active = true;
        tile.HeightAnchor.ConstraintEqualTo(30).Active = true;

        var name = Plain(c.Name, 12);
        name.LineBreakMode = NSLineBreakMode.TruncatingTail;
        var host = Muted(c.HostDisplay, 11);
        var stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 1,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        stack.AddArrangedSubview(name);
        stack.AddArrangedSubview(host);

        var row = new TapRow(() => ConnectRequested?.Invoke(this, c))
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            Menu = ConnMenu(c),
        };
        row.AddSubview(tile);
        row.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            row.HeightAnchor.ConstraintEqualTo(46),
            tile.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor, 8),
            tile.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor),
            stack.LeadingAnchor.ConstraintEqualTo(tile.TrailingAnchor, 10),
            stack.TrailingAnchor.ConstraintLessThanOrEqualTo(row.TrailingAnchor, -8),
            stack.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor),
        });
        return row;
    }

    private static NSView HomeActivityRow(HistoryItemViewModel h)
    {
        var tile = IconTile(h.Protocol);
        tile.WidthAnchor.ConstraintEqualTo(30).Active = true;
        tile.HeightAnchor.ConstraintEqualTo(30).Active = true;

        var title = Plain(h.ActivityText, 12);
        title.LineBreakMode = NSLineBreakMode.TruncatingTail;
        var sub = Muted(h.HostProtocolLine, 11);
        var stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 1,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        stack.AddArrangedSubview(title);
        stack.AddArrangedSubview(sub);

        var when = Styled(h.StartedAtDisplay, 11, NSFontWeight.Regular, NSColor.TertiaryLabel);

        var row = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        row.AddSubview(tile);
        row.AddSubview(stack);
        row.AddSubview(when);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            row.HeightAnchor.ConstraintEqualTo(46),
            tile.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor, 8),
            tile.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor),
            stack.LeadingAnchor.ConstraintEqualTo(tile.TrailingAnchor, 10),
            stack.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor),
            when.LeadingAnchor.ConstraintGreaterThanOrEqualTo(stack.TrailingAnchor, 6),
            when.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor, -8),
            when.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor),
        });
        return row;
    }

    private NSView HomeFirstRunCard()
    {
        var card = Card();

        var t = Big("欢迎使用 RemoteFlow", 15);
        t.Font = NSFont.SystemFontOfSize(15, NSFontWeight.Semibold);
        var b = Muted("把 RDP、SSH、VNC 连接统一到一个工作台。先新建一个连接，或从设置的「数据与备份」导入既有清单。", 12);
        b.LineBreakMode = NSLineBreakMode.ByWordWrapping;
        b.PreferredMaxLayoutWidth = 760;
        var s = Styled("密码与私钥由 macOS 钥匙串加密保存，不写入连接库，也不随导出文件带走。", 11, NSFontWeight.Regular, NSColor.TertiaryLabel);
        s.LineBreakMode = NSLineBreakMode.ByWordWrapping;
        s.PreferredMaxLayoutWidth = 760;
        var newBtn = NSButton.CreateButton("新建连接", () => NewConnectionRequested?.Invoke(this, EventArgs.Empty));
        newBtn.BezelStyle = NSBezelStyle.Rounded;
        newBtn.KeyEquivalent = "\r";

        var stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 8,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        stack.AddArrangedSubview(t);
        stack.AddArrangedSubview(b);
        stack.AddArrangedSubview(s);
        stack.AddArrangedSubview(Gap(4));
        stack.AddArrangedSubview(newBtn);

        card.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            stack.LeadingAnchor.ConstraintEqualTo(card.LeadingAnchor, 22),
            stack.TrailingAnchor.ConstraintEqualTo(card.TrailingAnchor, -22),
            stack.TopAnchor.ConstraintEqualTo(card.TopAnchor, 20),
            stack.BottomAnchor.ConstraintEqualTo(card.BottomAnchor, -20),
        });
        return card;
    }

    private static NSView HomeSecurityCard(Action dismiss)
    {
        var card = new CardView(() => NSColor.SystemBlue.ColorWithAlphaComponent(0.09f),
            () => NSColor.SecondaryLabel.ColorWithAlphaComponent(0.12f), 10);

        var icon = new NSImageView
        {
            Image = NSImage.GetSystemSymbol("shield.lefthalf.filled", null),
            ContentTintColor = NSColor.SystemBlue,
            TranslatesAutoresizingMaskIntoConstraints = false,
            SymbolConfiguration = NSImageSymbolConfiguration.Create(15, NSFontWeight.Regular),
        };
        var t = Plain("安全提示", 13);
        t.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold);
        var b = Styled("为保障连接安全，请定期更新密码，并为关键账号启用双因素认证。", 12,
            NSFontWeight.Regular, NSColor.SecondaryLabel);
        b.LineBreakMode = NSLineBreakMode.ByWordWrapping;
        b.PreferredMaxLayoutWidth = 620;
        var txt = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 2,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        txt.AddArrangedSubview(t);
        txt.AddArrangedSubview(b);

        var close = NSButton.CreateButton(string.Empty, () => dismiss());
        close.TranslatesAutoresizingMaskIntoConstraints = false;
        close.Bordered = false;
        close.Image = NSImage.GetSystemSymbol("xmark", null);
        close.ContentTintColor = NSColor.SecondaryLabel;
        close.SymbolConfiguration = NSImageSymbolConfiguration.Create(10, NSFontWeight.Bold);

        card.AddSubview(icon);
        card.AddSubview(txt);
        card.AddSubview(close);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            icon.LeadingAnchor.ConstraintEqualTo(card.LeadingAnchor, 16),
            icon.CenterYAnchor.ConstraintEqualTo(card.CenterYAnchor),
            txt.LeadingAnchor.ConstraintEqualTo(icon.TrailingAnchor, 12),
            txt.TopAnchor.ConstraintEqualTo(card.TopAnchor, 13),
            txt.BottomAnchor.ConstraintEqualTo(card.BottomAnchor, -13),
            close.TrailingAnchor.ConstraintEqualTo(card.TrailingAnchor, -12),
            close.CenterYAnchor.ConstraintEqualTo(card.CenterYAnchor),
        });
        return card;
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
        _showingDetail = false;
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

    // 低反差：一层极淡的中性填充 + 极淡描边，不做「白卡压灰底」的强对比。
    private static CardView Card() =>
        new(() => NSColor.SecondaryLabel.ColorWithAlphaComponent(0.06f),
            () => NSColor.SecondaryLabel.ColorWithAlphaComponent(0.12f), 8);

    private static CardView Hairline() => new(() => NSColor.SecondaryLabel.ColorWithAlphaComponent(0.12f));

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
