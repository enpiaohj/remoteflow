using System.Collections.Specialized;
using AppKit;
using CoreGraphics;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 偏好设置窗口（⌘,）—— macOS 惯例的独立窗口 + 工具栏分页，绑共享
/// <see cref="SettingsPageViewModel"/>。分页：常规 / RDP / SSH / VNC / 安全 / 数据与备份。
/// </summary>
/// <summary>
/// 设置页（内嵌主窗口详情区，对齐 Windows 版）：标题「设置」+ 下划线式横向标签条
/// （常规 / RDP / SSH / VNC / 安全 / 数据与备份）+ 分页内容。
/// 原先是独立偏好窗口，改到主窗口内以保持与 Windows 版一致的导航模型。
/// </summary>
public sealed class SettingsPaneView : NSView
{
    private static readonly (string Title, string Symbol)[] Tabs =
    {
        ("常规", "gearshape"),
        ("RDP", "display"),
        ("SSH", "terminal"),
        ("VNC", "rectangle.on.rectangle"),
        ("安全", "lock.shield"),
        ("数据与备份", "externaldrive"),
    };

    private readonly SettingsPageViewModel _vm;
    private readonly NSView _content = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly Lazy<NSView>[] _pages;
    private readonly List<TabButton> _tabButtons = new();

    public SettingsPaneView(SettingsPageViewModel vm)
    {
        _vm = vm;
        TranslatesAutoresizingMaskIntoConstraints = false;

        _pages = new[]
        {
            new Lazy<NSView>(BuildGeneral),
            new Lazy<NSView>(BuildRdp),
            new Lazy<NSView>(BuildSsh),
            new Lazy<NSView>(BuildVnc),
            new Lazy<NSView>(BuildSecurity),
            new Lazy<NSView>(BuildData),
        };

        var title = new NSTextField
        {
            StringValue = "设置",
            Bordered = false,
            Editable = false,
            Selectable = false,
            DrawsBackground = false,
            Font = NSFont.SystemFontOfSize(24, NSFontWeight.Bold),
            TextColor = NSColor.Label,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        var tabs = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.Bottom,
            Spacing = 4,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        for (var i = 0; i < Tabs.Length; i++)
        {
            var idx = i;
            var b = new TabButton(Tabs[i].Title, () => Select(idx));
            _tabButtons.Add(b);
            tabs.AddArrangedSubview(b);
        }

        var sep = new NSBox { BoxType = NSBoxType.NSBoxSeparator, TranslatesAutoresizingMaskIntoConstraints = false };

        AddSubview(title);
        AddSubview(tabs);
        AddSubview(sep);
        AddSubview(_content);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            title.LeadingAnchor.ConstraintEqualTo(SafeAreaLayoutGuide.LeadingAnchor, 26),
            title.TopAnchor.ConstraintEqualTo(SafeAreaLayoutGuide.TopAnchor, 22),

            tabs.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 24),
            tabs.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor, -24),
            tabs.TopAnchor.ConstraintEqualTo(title.BottomAnchor, 14),

            sep.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            sep.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            sep.TopAnchor.ConstraintEqualTo(tabs.BottomAnchor),
            sep.HeightAnchor.ConstraintEqualTo(1),

            _content.TopAnchor.ConstraintEqualTo(sep.BottomAnchor),
            _content.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _content.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            _content.BottomAnchor.ConstraintEqualTo(BottomAnchor),
        });

        Select(0);

        _ = _vm.LoadGroupsAsync();
        _ = _vm.LoadHostKeysAsync();
    }

    private void Select(int index)
    {
        foreach (var v in _content.Subviews.ToArray())
        {
            v.RemoveFromSuperview();
        }

        for (var i = 0; i < _tabButtons.Count; i++)
        {
            _tabButtons[i].SetActive(i == index);
        }

        var page = _pages[index].Value;
        page.TranslatesAutoresizingMaskIntoConstraints = false;
        _content.AddSubview(page);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            page.TopAnchor.ConstraintEqualTo(_content.TopAnchor),
            page.LeadingAnchor.ConstraintEqualTo(_content.LeadingAnchor),
            page.TrailingAnchor.ConstraintEqualTo(_content.TrailingAnchor),
            page.BottomAnchor.ConstraintEqualTo(_content.BottomAnchor),
        });
    }

    /// <summary>下划线式标签按钮（对齐 Windows 版设置页的分页样式）。</summary>
    private sealed class TabButton : NSView
    {
        private readonly NSTextField _label;
        private readonly NSView _underline;
        private readonly Action _onClick;
        private bool _active;

        public TabButton(string title, Action onClick)
        {
            _onClick = onClick;
            TranslatesAutoresizingMaskIntoConstraints = false;

            _label = new NSTextField
            {
                StringValue = title,
                Bordered = false,
                Editable = false,
                Selectable = false,
                DrawsBackground = false,
                Font = NSFont.SystemFontOfSize(13),
                TextColor = NSColor.SecondaryLabel,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            _underline = new NSView { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false };
            _underline.Layer!.CornerRadius = 1;

            AddSubview(_label);
            AddSubview(_underline);
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                _label.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 10),
                _label.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -10),
                _label.TopAnchor.ConstraintEqualTo(TopAnchor, 6),
                _underline.TopAnchor.ConstraintEqualTo(_label.BottomAnchor, 7),
                _underline.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 8),
                _underline.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -8),
                _underline.HeightAnchor.ConstraintEqualTo(2),
                _underline.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            });

            SetActive(false);
        }

        public void SetActive(bool active)
        {
            _active = active;
            Restyle();
        }

        public override void ViewDidChangeEffectiveAppearance()
        {
            base.ViewDidChangeEffectiveAppearance();
            Restyle();
        }

        private void Restyle()
        {
            var prev = NSAppearance.CurrentAppearance;
            NSAppearance.CurrentAppearance = EffectiveAppearance;
            _label.TextColor = _active ? NSColor.ControlAccent : NSColor.SecondaryLabel;
            _label.Font = NSFont.SystemFontOfSize(13, _active ? NSFontWeight.Semibold : NSFontWeight.Regular);
            _underline.Layer!.BackgroundColor = (_active ? NSColor.ControlAccent : NSColor.Clear).CGColor;
            NSAppearance.CurrentAppearance = prev;
        }

        public override void MouseDown(NSEvent theEvent) => _onClick();

        public override void ResetCursorRects() => AddCursorRect(Bounds, NSCursor.PointingHandCursor);
    }

    // ── 常规 ────────────────────────────────────────────────────

    private NSView BuildGeneral()
    {
        var theme = NSSegmentedControl.FromLabels(
            new[] { "跟随系统", "浅色", "深色" }, NSSegmentSwitchTracking.SelectOne, () => { });
        theme.SelectedSegment = _vm.SelectedTheme switch
        {
            AppTheme.Light => 1,
            AppTheme.Dark => 2,
            _ => 0,
        };
        theme.Activated += (_, _) => _vm.SelectedTheme = theme.SelectedSegment switch
        {
            1 => AppTheme.Light,
            2 => AppTheme.Dark,
            _ => AppTheme.System,
        };

        var landing = Popup(new[] { "首页", "我的连接", "收藏", "最近连接", "凭据" }, (int)_vm.DefaultLandingPage,
            i => _vm.DefaultLandingPage = (LandingPage)i);

        // ── 分组：保护默认分组 ──────────────────────────────────
        var protect = new NSButton { Title = _vm.DefaultGroupSwitchLabel, TranslatesAutoresizingMaskIntoConstraints = false };
        protect.SetButtonType(NSButtonType.Switch);
        protect.State = _vm.DefaultGroupProtected ? NSCellStateValue.On : NSCellStateValue.Off;
        protect.Enabled = _vm.ProtectionSwitchEnabled;
        protect.Activated += (_, _) => _vm.DefaultGroupProtected = protect.State == NSCellStateValue.On;

        var protectHint = Muted(_vm.DefaultGroupDescription);

        // LoadGroupsAsync 完成后同步开关标题 / 状态 / 副文案。
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsPageViewModel.DefaultGroupProtected)
                or nameof(SettingsPageViewModel.DefaultGroupSwitchLabel)
                or nameof(SettingsPageViewModel.DefaultGroupLabel)
                or nameof(SettingsPageViewModel.ProtectionSwitchEnabled)
                or nameof(SettingsPageViewModel.DefaultGroupDescription))
            {
                NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
                {
                    protect.Title = _vm.DefaultGroupSwitchLabel;
                    protect.State = _vm.DefaultGroupProtected ? NSCellStateValue.On : NSCellStateValue.Off;
                    protect.Enabled = _vm.ProtectionSwitchEnabled;
                    protectHint.StringValue = _vm.DefaultGroupDescription;
                });
            }
        };

        return Page(
            Row("主题", theme),
            Row("默认页面", landing),
            Check("登录时自动启动 RemoteFlow", _vm.LaunchOnStartup, v => _vm.LaunchOnStartup = v),
            Check("关闭窗口时最小化到菜单栏而非退出", _vm.MinimizeToTrayOnClose, v => _vm.MinimizeToTrayOnClose = v),
            Gap(8),
            SectionLabel("分组"),
            protect,
            protectHint);
    }

    // ── RDP ─────────────────────────────────────────────────────

    private NSView BuildRdp() => Page(
        SectionLabel("默认行为（新建 RDP 连接时套用）"),
        Check("默认「适应窗口」显示模式", _vm.RdpFitToWindow, v => _vm.RdpFitToWindow = v),
        Check("默认开启剪贴板重定向", _vm.RdpRedirectClipboard, v => _vm.RdpRedirectClipboard = v),
        Check("默认把远端音频播放到本机", _vm.RdpRedirectAudio, v => _vm.RdpRedirectAudio = v),
        Check("默认使用全部显示器", _vm.RdpUseMultimon, v => _vm.RdpUseMultimon = v));

    // ── SSH ─────────────────────────────────────────────────────

    private NSView BuildSsh()
    {
        var term = Popup(_vm.TerminalTypeOptions,
            Math.Max(0, _vm.TerminalTypeOptions.ToList().IndexOf(_vm.SshTerminalType)),
            i => _vm.SshTerminalType = _vm.TerminalTypeOptions[i]);

        var enc = Popup(_vm.SshEncodingOptions,
            Math.Max(0, _vm.SshEncodingOptions.ToList().IndexOf(_vm.SshEncoding)),
            i => _vm.SshEncoding = _vm.SshEncodingOptions[i]);

        var size = Popup(_vm.FontSizeOptions.Select(s => s.ToString()),
            Math.Max(0, _vm.FontSizeOptions.ToList().IndexOf(_vm.SshFontSize)),
            i => _vm.SshFontSize = _vm.FontSizeOptions[i]);

        var themeLabels = _vm.SshTerminalThemeOptions.Select(t => t.Label).ToList();
        var theme = Popup(themeLabels,
            Math.Max(0, _vm.SshTerminalThemeOptions.ToList().FindIndex(t => t.Value == _vm.SelectedSshTerminalTheme.Value)),
            i => _vm.SelectedSshTerminalTheme = _vm.SshTerminalThemeOptions[i]);

        var keepAlive = IntField(_vm.SshKeepAliveSeconds, v => _vm.SshKeepAliveSeconds = v);

        return Page(
            Row("终端类型", term),
            Row("字符编码", enc),
            Row("终端主题", theme),
            Row("字号", size),
            Row("保活间隔（秒）", keepAlive),
            Check("粘贴多行文本前确认", _vm.SshConfirmMultilinePaste, v => _vm.SshConfirmMultilinePaste = v),
            Check("粘贴大量文本时警告", _vm.SshWarnLargePaste, v => _vm.SshWarnLargePaste = v));
    }

    // ── VNC ─────────────────────────────────────────────────────

    private NSView BuildVnc() => Page(
        SectionLabel("默认行为（新建 VNC 连接时套用）"),
        Check("默认「适应窗口」缩放", _vm.VncFitToWindow, v => _vm.VncFitToWindow = v),
        Check("默认只读（不发送键鼠）", _vm.VncViewOnly, v => _vm.VncViewOnly = v),
        Check("默认共享连接（不踢掉其他客户端）", _vm.VncSharedConnection, v => _vm.VncSharedConnection = v),
        Check("默认同步远端剪贴板到本机", _vm.VncClipboardToLocal, v => _vm.VncClipboardToLocal = v));

    // ── 安全 ────────────────────────────────────────────────────

    private NSView BuildSecurity()
    {
        var table = new NSTableView
        {
            HeaderView = null,
            RowHeight = 40,
            BackgroundColor = NSColor.Clear,
            SelectionHighlightStyle = NSTableViewSelectionHighlightStyle.Regular,
            Style = NSTableViewStyle.Inset,
        };
        table.AddColumn(new NSTableColumn("h") { ResizingMask = NSTableColumnResizing.Autoresizing });

        var remove = new NSButton { Title = "移除所选", Enabled = false, BezelStyle = NSBezelStyle.Rounded };
        var src = new HostKeySource(table, _vm.TrustedHostKeys, item =>
        {
            _selectedHostKey = item;
            remove.Enabled = item is not null;
        });
        _ = src;
        remove.Activated += (_, _) =>
        {
            if (_selectedHostKey is { } k)
            {
                _ = _vm.RemoveHostKeyCommand.ExecuteAsync(k);
            }
        };

        var scroll = new NSScrollView
        {
            DocumentView = table,
            BorderType = NSBorderType.BezelBorder,
            HasVerticalScroller = true,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        scroll.HeightAnchor.ConstraintEqualTo(160).Active = true;

        var clearHistory = NSButton.CreateButton("清空连接历史…", () => _ = _vm.ClearHistoryCommand.ExecuteAsync(null));
        clearHistory.BezelStyle = NSBezelStyle.Rounded;

        return Page(
            SectionLabel("已信任的 SSH 主机密钥"),
            scroll,
            remove,
            Gap(8),
            SectionLabel("连接历史"),
            clearHistory);
    }

    private HostKeyItemViewModel? _selectedHostKey;

    // ── 数据与备份 ──────────────────────────────────────────────

    private NSView BuildData()
    {
        var openData = NSButton.CreateButton("在访达中打开", () => _vm.OpenDataDirectoryCommand.Execute(null));
        openData.BezelStyle = NSBezelStyle.Rounded;
        var openLogs = NSButton.CreateButton("在访达中打开", () => _vm.OpenLogDirectoryCommand.Execute(null));
        openLogs.BezelStyle = NSBezelStyle.Rounded;

        var exportCsv = NSButton.CreateButton("导出连接列表…", () => _ = _vm.ExportConnectionsCsvCommand.ExecuteAsync(null));
        exportCsv.BezelStyle = NSBezelStyle.Rounded;
        var importCsv = NSButton.CreateButton("导入连接列表…", () => _ = _vm.ImportConnectionsCsvCommand.ExecuteAsync(null));
        importCsv.BezelStyle = NSBezelStyle.Rounded;

        var csvRow = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Spacing = 8,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        csvRow.AddArrangedSubview(exportCsv);
        csvRow.AddArrangedSubview(importCsv);

        return Page(
            SectionLabel("数据目录"),
            PathRow(_vm.DataDirectory, openData),
            SectionLabel("日志目录"),
            PathRow(_vm.LogDirectory, openLogs),
            Gap(8),
            SectionLabel("连接列表（不含任何密码 / 私钥）"),
            csvRow,
            Gap(8),
            Muted($"版本 {_vm.AppVersion}"));
    }

    // ── 版式辅助 ────────────────────────────────────────────────

    private static NSView Page(params NSView[] rows)
    {
        var stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 12,
            EdgeInsets = new NSEdgeInsets(24, 26, 24, 26),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        foreach (var r in rows)
        {
            stack.AddArrangedSubview(r);
        }

        var scroll = new NSScrollView
        {
            DocumentView = stack,
            DrawsBackground = false,
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        var flipped = new FlippedView { TranslatesAutoresizingMaskIntoConstraints = false };
        flipped.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            stack.TopAnchor.ConstraintEqualTo(flipped.TopAnchor),
            stack.LeadingAnchor.ConstraintEqualTo(flipped.LeadingAnchor),
            stack.TrailingAnchor.ConstraintEqualTo(flipped.TrailingAnchor),
            stack.BottomAnchor.ConstraintEqualTo(flipped.BottomAnchor),
        });
        scroll.DocumentView = flipped;

        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        host.AddSubview(scroll);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            scroll.TopAnchor.ConstraintEqualTo(host.TopAnchor),
            scroll.LeadingAnchor.ConstraintEqualTo(host.LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(host.TrailingAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(host.BottomAnchor),
            flipped.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor),
        });
        return host;
    }

    private sealed class FlippedView : NSView
    {
        public override bool IsFlipped => true;
    }

    private static NSView Row(string label, NSView control)
    {
        var row = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Spacing = 12,
            Alignment = NSLayoutAttribute.CenterY,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        row.AddArrangedSubview(Caption(label));
        row.AddArrangedSubview(control);
        return row;
    }

    private static NSView PathRow(string path, NSButton button)
    {
        var field = new NSTextField
        {
            StringValue = path,
            Editable = false,
            Bordered = true,
            Bezeled = true,
            Selectable = true,
            Font = NSFont.SystemFontOfSize(12),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        field.WidthAnchor.ConstraintGreaterThanOrEqualTo(360).Active = true;

        var row = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Spacing = 8,
            Alignment = NSLayoutAttribute.CenterY,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        row.AddArrangedSubview(field);
        row.AddArrangedSubview(button);
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

    private static NSPopUpButton Popup(IEnumerable<string> items, int selected, Action<int> onSelect)
    {
        var p = new NSPopUpButton(new CGRect(0, 0, 220, 24), pullsDown: false)
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
        p.Activated += (_, _) => onSelect((int)p.IndexOfSelectedItem);
        return p;
    }

    private static NSTextField IntField(int value, Action<int> onChange)
    {
        var f = new NSTextField
        {
            StringValue = value.ToString(),
            Alignment = NSTextAlignment.Right,
            Bordered = true,
            Bezeled = true,
            Font = NSFont.SystemFontOfSize(13),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        f.WidthAnchor.ConstraintEqualTo(80).Active = true;
        f.Changed += (_, _) =>
        {
            if (int.TryParse(f.StringValue.Trim(), out var v) && v >= 0)
            {
                onChange(v);
            }
        };
        return f;
    }

    private static NSTextField Caption(string text) => new()
    {
        StringValue = text,
        Bordered = false,
        Editable = false,
        Selectable = false,
        DrawsBackground = false,
        Font = NSFont.SystemFontOfSize(13),
        TextColor = NSColor.SecondaryLabel,
        TranslatesAutoresizingMaskIntoConstraints = false,
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

    private static NSTextField Muted(string text) => new()
    {
        StringValue = text,
        Bordered = false,
        Editable = false,
        Selectable = true,
        DrawsBackground = false,
        Font = NSFont.SystemFontOfSize(12),
        TextColor = NSColor.SecondaryLabel,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    private static NSView Gap(nfloat h)
    {
        var v = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        v.HeightAnchor.ConstraintEqualTo(h).Active = true;
        return v;
    }

    // ── 主机密钥表 ─────────────────────────────────────────────

    private sealed class HostKeySource : NSTableViewDelegate
    {
        private readonly NSTableView _table;
        private readonly System.Collections.ObjectModel.ObservableCollection<HostKeyItemViewModel> _items;
        private readonly Action<HostKeyItemViewModel?> _onSelect;

        public HostKeySource(
            NSTableView table,
            System.Collections.ObjectModel.ObservableCollection<HostKeyItemViewModel> items,
            Action<HostKeyItemViewModel?> onSelect)
        {
            _table = table;
            _items = items;
            _onSelect = onSelect;
            _table.DataSource = new RowCount(this);
            _table.Delegate = this;
            _items.CollectionChanged += (_, _) =>
                NSApplication.SharedApplication.BeginInvokeOnMainThread(_table.ReloadData);
            _table.ReloadData();
        }

        public override void SelectionDidChange(Foundation.NSNotification notification)
        {
            var r = (int)_table.SelectedRow;
            _onSelect(r >= 0 && r < _items.Count ? _items[r] : null);
        }

        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
        {
            var item = _items[(int)row];
            const string id = "hk";
            if (tableView.MakeView(id, this) is not NSTableCellView cell)
            {
                var title = new NSTextField
                {
                    Identifier = "t", Bordered = false, Editable = false, Selectable = false,
                    DrawsBackground = false, Font = NSFont.SystemFontOfSize(13),
                    TranslatesAutoresizingMaskIntoConstraints = false,
                };
                var sub = new NSTextField
                {
                    Identifier = "s", Bordered = false, Editable = false, Selectable = false,
                    DrawsBackground = false, Font = NSFont.SystemFontOfSize(11),
                    TextColor = NSColor.SecondaryLabel,
                    TranslatesAutoresizingMaskIntoConstraints = false,
                    LineBreakMode = NSLineBreakMode.TruncatingMiddle,
                };
                var st = new NSStackView
                {
                    Orientation = NSUserInterfaceLayoutOrientation.Vertical,
                    Alignment = NSLayoutAttribute.Leading,
                    Spacing = 1,
                    TranslatesAutoresizingMaskIntoConstraints = false,
                };
                st.AddArrangedSubview(title);
                st.AddArrangedSubview(sub);
                cell = new NSTableCellView { Identifier = id };
                cell.AddSubview(st);
                cell.TextField = title;
                NSLayoutConstraint.ActivateConstraints(new[]
                {
                    st.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor, 6),
                    st.TrailingAnchor.ConstraintEqualTo(cell.TrailingAnchor, -6),
                    st.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                });
            }

            ((NSTextField)cell.Subviews[0].Subviews[0]).StringValue = item.Host;
            ((NSTextField)cell.Subviews[0].Subviews[1]).StringValue = $"{item.Algorithm}  {item.Fingerprint}";
            return cell;
        }

        private sealed class RowCount : NSTableViewDataSource
        {
            private readonly HostKeySource _o;
            public RowCount(HostKeySource o) => _o = o;
            public override nint GetRowCount(NSTableView tableView) => _o._items.Count;
        }
    }

}
