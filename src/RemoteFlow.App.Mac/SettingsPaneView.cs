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
        WantsLayer = true;
        RefreshGround();

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
            title.LeadingAnchor.ConstraintEqualTo(SafeAreaLayoutGuide.LeadingAnchor, 30),
            title.TopAnchor.ConstraintEqualTo(SafeAreaLayoutGuide.TopAnchor, 22),

            tabs.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 22),
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

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        RefreshGround();
    }

    private void RefreshGround()
        => Palette.With(this, () => Layer!.BackgroundColor = Palette.PageGround(this).CGColor);

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

    /// <summary>
    /// 分组卡片（对齐 Windows 版设置页）：图标 + 组标题 + 一行说明，下面放内容。
    /// 设置项不该看着像一堆工程开关，得有分组和解释。
    /// </summary>
    /// <summary>无描述文字的卡片重载：内容行自带副标题时用它，省掉那行空注释。</summary>
    private static NSView Card(string title, string symbol, params NSView[] content)
        => Card(title, symbol, null, content);

    private static NSView Card(string title, string symbol, string? desc, params NSView[] content)
    {
        var box = new SoftBox();

        var icon = new NSImageView
        {
            Image = NSImage.GetSystemSymbol(symbol, null),
            ContentTintColor = NSColor.ControlAccent,
            TranslatesAutoresizingMaskIntoConstraints = false,
            SymbolConfiguration = NSImageSymbolConfiguration.Create(13, NSFontWeight.Medium),
        };
        var head = new NSTextField
        {
            StringValue = title,
            Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
            Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold),
            TextColor = NSColor.Label,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var headRow = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = 7,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        headRow.AddArrangedSubview(icon);
        headRow.AddArrangedSubview(head);

        var col = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 10,
            EdgeInsets = new NSEdgeInsets(15, 18, 16, 18),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        col.AddArrangedSubview(headRow);

        if (!string.IsNullOrEmpty(desc))
        {
            var note = new NSTextField
            {
                StringValue = desc,
                Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
                Font = NSFont.SystemFontOfSize(11),
                TextColor = NSColor.SecondaryLabel,
                LineBreakMode = NSLineBreakMode.ByWordWrapping,
                PreferredMaxLayoutWidth = 540,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            col.AddArrangedSubview(note);
            col.SetCustomSpacing(12, note);
        }
        else
        {
            col.SetCustomSpacing(12, headRow);
        }

        foreach (var c in content)
        {
            col.AddArrangedSubview(c);
        }

        box.AddSubview(col);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            col.LeadingAnchor.ConstraintEqualTo(box.LeadingAnchor),
            col.TrailingAnchor.ConstraintEqualTo(box.TrailingAnchor),
            col.TopAnchor.ConstraintEqualTo(box.TopAnchor),
            col.BottomAnchor.ConstraintEqualTo(box.BottomAnchor),
        });
        return box;
    }

    // ── 「一行一操作」式卡片（数据 / 安全页）─────────────────────

    private const int SectionCardInset = 16;

    /// <summary>
    /// 标题卡 + 若干**通栏**内容行。与 <see cref="Card"/> 的区别：内容行拉满卡片内宽，
    /// 便于把操作按钮压到最右（对齐 Windows 设置页的「LinkRow」）。
    /// </summary>
    private static NSView SectionCard(string title, string symbol, string? desc, params NSView[] rows)
    {
        var box = new SoftBox();

        var headRow = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = 7,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        headRow.AddArrangedSubview(new NSImageView
        {
            Image = NSImage.GetSystemSymbol(symbol, null),
            ContentTintColor = NSColor.ControlAccent,
            SymbolConfiguration = NSImageSymbolConfiguration.Create(13, NSFontWeight.Medium),
            TranslatesAutoresizingMaskIntoConstraints = false,
        });
        headRow.AddArrangedSubview(new NSTextField
        {
            StringValue = title,
            Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
            Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold),
            TextColor = NSColor.Label,
            TranslatesAutoresizingMaskIntoConstraints = false,
        });

        var col = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 8,
            EdgeInsets = new NSEdgeInsets(13, SectionCardInset, 13, SectionCardInset),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        col.AddArrangedSubview(headRow);

        if (!string.IsNullOrEmpty(desc))
        {
            var note = new NSTextField
            {
                StringValue = desc,
                Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
                Font = NSFont.SystemFontOfSize(11),
                TextColor = NSColor.SecondaryLabel,
                LineBreakMode = NSLineBreakMode.ByWordWrapping,
                MaximumNumberOfLines = 0,
                PreferredMaxLayoutWidth = 520,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            col.AddArrangedSubview(note);
            col.SetCustomSpacing(10, note);
        }
        else
        {
            col.SetCustomSpacing(10, headRow);
        }

        foreach (var r in rows)
        {
            col.AddArrangedSubview(r);
        }

        box.AddSubview(col);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            col.LeadingAnchor.ConstraintEqualTo(box.LeadingAnchor),
            col.TrailingAnchor.ConstraintEqualTo(box.TrailingAnchor),
            col.TopAnchor.ConstraintEqualTo(box.TopAnchor),
            col.BottomAnchor.ConstraintEqualTo(box.BottomAnchor),
        });

        // 内容行（含 note）通栏；标题行保持自然宽度。
        foreach (var r in rows)
        {
            r.WidthAnchor.ConstraintEqualTo(col.WidthAnchor, 1, -SectionCardInset * 2).Active = true;
        }

        return box;
    }

    /// <summary>卡内的一条操作行：图标底片 · 标题 + 副文案 · 右侧操作控件。</summary>
    private static NSView ActionRow(string symbol, NSColor tint, string title, string subtitle,
        NSView trailing, bool monoSubtitle = false)
    {
        var tile = IconTile(symbol, tint);

        var head = new NSTextField
        {
            StringValue = title,
            Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
            Font = NSFont.SystemFontOfSize(12, NSFontWeight.Semibold),
            TextColor = NSColor.Label,
            LineBreakMode = NSLineBreakMode.TruncatingTail,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var sub = new NSTextField
        {
            StringValue = subtitle,
            Bordered = false, Editable = false, Selectable = monoSubtitle, DrawsBackground = false,
            Font = monoSubtitle ? NSFont.MonospacedSystemFont(10, NSFontWeight.Regular) : NSFont.SystemFontOfSize(11),
            TextColor = NSColor.SecondaryLabel,
            LineBreakMode = monoSubtitle ? NSLineBreakMode.TruncatingMiddle : NSLineBreakMode.ByWordWrapping,
            MaximumNumberOfLines = monoSubtitle ? 1 : 0,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        // 副文案的抗压缩优先级压到很低：宽度不够时它换行 / 截断，而不是把右侧按钮挤出行外。
        sub.SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        head.SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);

        var textCol = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 2,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        textCol.AddArrangedSubview(head);
        textCol.AddArrangedSubview(sub);

        trailing.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        trailing.SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);

        // 显式约束而非 NSStackView 的优先级博弈：图钉左、按钮右、文字列吃中间，
        // 换行宽度由「按钮左沿」决定，行高由文字列撑开（至少 44）。
        var row = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        row.AddSubview(tile);
        row.AddSubview(textCol);
        row.AddSubview(trailing);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            tile.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor),
            tile.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor),

            textCol.LeadingAnchor.ConstraintEqualTo(tile.TrailingAnchor, 11),
            textCol.TrailingAnchor.ConstraintEqualTo(trailing.LeadingAnchor, -12),
            textCol.TopAnchor.ConstraintEqualTo(row.TopAnchor, 9),
            textCol.BottomAnchor.ConstraintEqualTo(row.BottomAnchor, -9),

            trailing.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor),
            trailing.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor),

            row.HeightAnchor.ConstraintGreaterThanOrEqualTo(44),
        });
        return row;
    }

    private static NSView IconTile(string symbol, NSColor tint)
    {
        var tile = new NSView { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false };
        tile.Layer!.CornerRadius = 6;
        tile.Layer.BackgroundColor = tint.ColorWithAlphaComponent(0.13f).CGColor;
        tile.WidthAnchor.ConstraintEqualTo(26).Active = true;
        tile.HeightAnchor.ConstraintEqualTo(26).Active = true;

        var glyph = new NSImageView
        {
            Image = NSImage.GetSystemSymbol(symbol, null),
            ContentTintColor = tint,
            SymbolConfiguration = NSImageSymbolConfiguration.Create(12, NSFontWeight.Medium),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        tile.AddSubview(glyph);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            glyph.CenterXAnchor.ConstraintEqualTo(tile.CenterXAnchor),
            glyph.CenterYAnchor.ConstraintEqualTo(tile.CenterYAnchor),
        });
        return tile;
    }

    private static NSBox HairlineDivider()
    {
        var b = new NSBox { BoxType = NSBoxType.NSBoxSeparator, TranslatesAutoresizingMaskIntoConstraints = false };
        b.HeightAnchor.ConstraintEqualTo(1).Active = true;
        return b;
    }

    /// <summary>说明面板：比卡片更轻的一块底纹，放「仅存本地 / 加密保护」这类静态说明。</summary>
    private static NSView InfoPanel(params (string Symbol, NSColor Tint, string Title, string Body)[] items)
    {
        var panel = new InfoBox();

        var col = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 12,
            EdgeInsets = new NSEdgeInsets(15, SectionCardInset, 15, SectionCardInset),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        foreach (var (symbol, tint, title, body) in items)
        {
            var headRow = new NSStackView
            {
                Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
                Alignment = NSLayoutAttribute.CenterY,
                Spacing = 7,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            headRow.AddArrangedSubview(new NSImageView
            {
                Image = NSImage.GetSystemSymbol(symbol, null),
                ContentTintColor = tint,
                SymbolConfiguration = NSImageSymbolConfiguration.Create(13, NSFontWeight.Medium),
                TranslatesAutoresizingMaskIntoConstraints = false,
            });
            headRow.AddArrangedSubview(new NSTextField
            {
                StringValue = title,
                Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
                Font = NSFont.SystemFontOfSize(12, NSFontWeight.Semibold),
                TextColor = NSColor.Label,
                TranslatesAutoresizingMaskIntoConstraints = false,
            });

            var bodyLabel = new NSTextField
            {
                StringValue = body,
                Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
                Font = NSFont.SystemFontOfSize(11),
                TextColor = NSColor.SecondaryLabel,
                LineBreakMode = NSLineBreakMode.ByWordWrapping,
                MaximumNumberOfLines = 0,
                PreferredMaxLayoutWidth = 520,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };

            var group = new NSStackView
            {
                Orientation = NSUserInterfaceLayoutOrientation.Vertical,
                Alignment = NSLayoutAttribute.Leading,
                Spacing = 4,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            group.AddArrangedSubview(headRow);
            group.AddArrangedSubview(bodyLabel);
            col.AddArrangedSubview(group);
        }

        panel.AddSubview(col);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            col.LeadingAnchor.ConstraintEqualTo(panel.LeadingAnchor),
            col.TrailingAnchor.ConstraintEqualTo(panel.TrailingAnchor),
            col.TopAnchor.ConstraintEqualTo(panel.TopAnchor),
            col.BottomAnchor.ConstraintEqualTo(panel.BottomAnchor),
        });
        return panel;
    }

    /// <summary>比 SoftBox 更内敛的说明底纹：淡填充、无描边、无投影。</summary>
    private sealed class InfoBox : NSView
    {
        public InfoBox()
        {
            WantsLayer = true;
            TranslatesAutoresizingMaskIntoConstraints = false;
            Layer!.CornerRadius = 9;
            Refresh();
        }

        public override void ViewDidChangeEffectiveAppearance()
        {
            base.ViewDidChangeEffectiveAppearance();
            Refresh();
        }

        private void Refresh()
        {
            var prev = NSAppearance.CurrentAppearance;
            NSAppearance.CurrentAppearance = EffectiveAppearance;
            Layer!.BackgroundColor = Palette.InsetFill(this).CGColor;
            NSAppearance.CurrentAppearance = prev;
        }
    }

    /// <summary>
    /// 抬起的内容表面：系统内容底色 + 细描边 + 极轻投影，跟随明暗切换。
    /// 分层靠抬升而非染色 —— 灰底上再叠灰卡会发闷。
    /// </summary>
    private sealed class SoftBox : NSView
    {
        public SoftBox()
        {
            WantsLayer = true;
            TranslatesAutoresizingMaskIntoConstraints = false;
            Layer!.CornerRadius = 9;
            Layer.BorderWidth = 1;
            Layer.ShadowOpacity = 0.06f;
            Layer.ShadowRadius = 3;
            Layer.ShadowOffset = new CoreGraphics.CGSize(0, -1);
            Refresh();
        }

        public override void ViewDidChangeEffectiveAppearance()
        {
            base.ViewDidChangeEffectiveAppearance();
            Refresh();
        }

        private void Refresh()
        {
            var prev = NSAppearance.CurrentAppearance;
            NSAppearance.CurrentAppearance = EffectiveAppearance;
            Layer!.BackgroundColor = Palette.CardSurface(this).CGColor;
            Layer.BorderColor = Palette.Hairline(this).CGColor;
            Layer.ShadowColor = NSColor.Black.CGColor;
            NSAppearance.CurrentAppearance = prev;
        }
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

        var concurrency = IntField(_vm.MaxConcurrentSessions, v => _vm.MaxConcurrentSessions = v);

        var language = Popup(new[] { "简体中文" }, 0, _ => { });
        language.Enabled = false; // 目前仅简体中文；保留控件以对齐 Windows 版，i18n 落地后接 _vm.LanguageOptions。

        // ── 日期与时间 ──────────────────────────────────────────
        var dateFmt = Popup(
            _vm.DateFormatOptions.Select(o => o.Label),
            Math.Max(0, _vm.DateFormatOptions.ToList().FindIndex(o => o.Value == _vm.SelectedDateFormat.Value)),
            i => _vm.SelectedDateFormat = _vm.DateFormatOptions[i]);

        var timeFmt = Popup(
            _vm.TimeFormatOptions.Select(o => o.Label),
            Math.Max(0, _vm.TimeFormatOptions.ToList().FindIndex(o => o.Value == _vm.SelectedTimeFormat.Value)),
            i => _vm.SelectedTimeFormat = _vm.TimeFormatOptions[i]);

        // 首页时间行：下拉项即当前日期/时间格式下的真实样例。改日期或 12/24 小时后
        // VM 会重建 HomeDateLineOptions，这里跟着重填。
        var homeLine = new NSPopUpButton(new CGRect(0, 0, 260, 24), pullsDown: false)
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        void RebuildHomeLine()
        {
            homeLine.RemoveAllItems();
            foreach (var o in _vm.HomeDateLineOptions)
            {
                homeLine.AddItem(o.Sample);
            }

            var idx = _vm.HomeDateLineOptions.ToList().FindIndex(o => o.Value == _vm.SelectedHomeDateLine.Value);
            if (idx >= 0)
            {
                homeLine.SelectItem(idx);
            }
        }

        RebuildHomeLine();
        homeLine.Activated += (_, _) =>
        {
            var i = (int)homeLine.IndexOfSelectedItem;
            if (i >= 0 && i < _vm.HomeDateLineOptions.Count)
            {
                _vm.SelectedHomeDateLine = _vm.HomeDateLineOptions[i];
            }
        };

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsPageViewModel.HomeDateLineOptions)
                or nameof(SettingsPageViewModel.SelectedHomeDateLine))
            {
                NSApplication.SharedApplication.BeginInvokeOnMainThread(RebuildHomeLine);
            }
        };

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
            Card("外观与行为", "paintbrush",
                "主题会应用到所有页面；默认页面决定每次启动后先落在哪儿；"
                + "并发会话上限用于在异常情况下防止无限重复建立连接。",
                Row("主题", theme),
                Row("默认页面", landing),
                Row("并发会话上限", concurrency),
                Check("登录时自动启动 RemoteFlow", _vm.LaunchOnStartup, v => _vm.LaunchOnStartup = v),
                Check("关闭窗口时最小化到菜单栏而非退出", _vm.MinimizeToTrayOnClose, v => _vm.MinimizeToTrayOnClose = v)),
            Card("日期与时间", "calendar",
                "日期格式用于首页完整日期、创建时间等；时间格式用于列表与历史；"
                + "「首页时间行」决定首页标题下方那行的详略程度，下拉项即当前格式下的真实样例。",
                Row("日期格式", dateFmt),
                Row("时间格式", timeFmt),
                Row("首页时间行", homeLine)),
            Card("语言", "globe",
                "目前仅提供简体中文，后续版本开放更多语言。",
                Row("界面语言", language)),
            Card("分组", "folder",
                "默认分组是新建连接的落点。开启保护可以防止它被误删或误改，日常整理时更安心。",
                protect,
                protectHint));
    }

    // ── RDP ─────────────────────────────────────────────────────

    private NSView BuildRdp() => Page(
        Card("默认行为", "display",
            "新建 RDP 连接时套用这些默认值。已有连接不受影响，可在各自的编辑页单独调整。",
            Check("默认「适应窗口」显示模式", _vm.RdpFitToWindow, v => _vm.RdpFitToWindow = v),
            Check("默认开启剪贴板重定向", _vm.RdpRedirectClipboard, v => _vm.RdpRedirectClipboard = v),
            Check("默认把远端音频播放到本机", _vm.RdpRedirectAudio, v => _vm.RdpRedirectAudio = v),
            Check("默认使用全部显示器", _vm.RdpUseMultimon, v => _vm.RdpUseMultimon = v)));

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

        var fontFamily = TextField(_vm.SshFontFamily, v => _vm.SshFontFamily = v);

        return Page(
            Card("终端", "apple.terminal",
                "终端类型与编码要和服务端匹配，否则会出现乱码或按键错位。"
                + "字体填 Web 字体族名（逗号分隔），首个可用的生效。",
                Row("终端类型", term),
                Row("字符编码", enc),
                Row("终端主题", theme),
                Row("终端字体", fontFamily),
                Row("字号", size)),
            Card("连接与粘贴", "arrow.left.arrow.right",
                "保活间隔用于在空闲时维持连接；粘贴确认能避免把多行内容误当命令一次性执行。",
                Row("保活间隔（秒）", keepAlive),
                Check("粘贴多行文本前确认", _vm.SshConfirmMultilinePaste, v => _vm.SshConfirmMultilinePaste = v),
                Check("粘贴大量文本时警告", _vm.SshWarnLargePaste, v => _vm.SshWarnLargePaste = v)));
    }

    // ── VNC ─────────────────────────────────────────────────────

    private NSView BuildVnc() => Page(
        Card("默认行为", "rectangle.on.rectangle",
            "新建 VNC 连接时套用这些默认值。「共享连接」关闭时会踢掉已连在该桌面上的其他客户端。",
            Check("默认「适应窗口」缩放", _vm.VncFitToWindow, v => _vm.VncFitToWindow = v),
            Check("默认只读（不发送键鼠）", _vm.VncViewOnly, v => _vm.VncViewOnly = v),
            Check("默认共享连接（不踢掉其他客户端）", _vm.VncSharedConnection, v => _vm.VncSharedConnection = v),
            Check("默认同步远端剪贴板到本机", _vm.VncClipboardToLocal, v => _vm.VncClipboardToLocal = v)));

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

        // BezelBorder + 默认白底在这套低反差配色里是一块刺眼的白板，
        // 换成与详情页卡片一致的「极淡中性底 + 极淡描边」圆角容器。
        var scroll = new NSScrollView
        {
            DocumentView = table,
            BorderType = NSBorderType.NoBorder,
            DrawsBackground = false,
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        var listBox = new SoftBox();
        listBox.AddSubview(scroll);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            scroll.LeadingAnchor.ConstraintEqualTo(listBox.LeadingAnchor, 1),
            scroll.TrailingAnchor.ConstraintEqualTo(listBox.TrailingAnchor, -1),
            scroll.TopAnchor.ConstraintEqualTo(listBox.TopAnchor, 1),
            scroll.BottomAnchor.ConstraintEqualTo(listBox.BottomAnchor, -1),
            listBox.HeightAnchor.ConstraintEqualTo(200),
        });

        // 空态：没有已信任条目时盖一层说明，别只剩一块空容器。
        var empty = new NSTextField
        {
            StringValue = "还没有已信任的主机。首次连接某台服务器时，它的密钥指纹会记在这里。",
            Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
            Alignment = NSTextAlignment.Center,
            Font = NSFont.SystemFontOfSize(11),
            TextColor = NSColor.TertiaryLabel,
            LineBreakMode = NSLineBreakMode.ByWordWrapping,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        listBox.AddSubview(empty);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            empty.CenterXAnchor.ConstraintEqualTo(listBox.CenterXAnchor),
            empty.CenterYAnchor.ConstraintEqualTo(listBox.CenterYAnchor),
            empty.WidthAnchor.ConstraintLessThanOrEqualTo(listBox.WidthAnchor, 1, -48),
        });
        void SyncEmpty() => empty.Hidden = _vm.TrustedHostKeys.Count > 0;
        SyncEmpty();
        _vm.TrustedHostKeys.CollectionChanged += (_, _) =>
            NSApplication.SharedApplication.BeginInvokeOnMainThread(SyncEmpty);

        return Page(
            SectionCard("已信任的主机", "lock.shield",
                "首次连接时记录主机密钥 / 证书指纹。指纹变化会强警告、绝不静默接受；"
                + "只有在你确认服务器确实重装或换了证书时，才移除对应条目。",
                listBox,
                ButtonRow(remove)));
    }

    private HostKeyItemViewModel? _selectedHostKey;

    // ── 数据与备份 ──────────────────────────────────────────────

    private NSView BuildData()
    {
        NSButton Action(string title, Action run)
        {
            var b = NSButton.CreateButton(title, () => run());
            b.BezelStyle = NSBezelStyle.Rounded;
            b.SetContentHuggingPriorityForOrientation(251, NSLayoutConstraintOrientation.Horizontal);
            return b;
        }

        var accent = NSColor.ControlAccent;
        var green = NSColor.SystemGreen;
        var muted = NSColor.SecondaryLabel;

        // ── 连接列表 ────────────────────────────────────────────
        var connData = SectionCard("连接列表", "list.bullet.rectangle", null,
            ActionRow("square.and.arrow.down", accent, "导入连接列表",
                "从 CSV 批量添加服务器。同名连接会跳过。",
                Action("导入…", () => _ = _vm.ImportConnectionsCsvCommand.ExecuteAsync(null))),
            HairlineDivider(),
            ActionRow("square.and.arrow.up", accent, "导出连接列表",
                "导出为 CSV，便于备份或迁移。不含任何密码 / 私钥。",
                Action("导出…", () => _ = _vm.ExportConnectionsCsvCommand.ExecuteAsync(null))));

        // ── 凭据备份 ────────────────────────────────────────────
        var credBackup = SectionCard("凭据备份", "key.fill", null,
            ActionRow("square.and.arrow.down", green, "导入凭据",
                "从 .rfbackup 恢复保存的登录信息，需要导出时设的口令。",
                Action("导入…", () => _ = _vm.ImportCredentialsCommand.ExecuteAsync(null))),
            HairlineDivider(),
            ActionRow("square.and.arrow.up", green, "导出凭据",
                "导出为 .rfbackup —— 口令派生（PBKDF2-SHA256）+ AES-256-GCM 加密，含密码 / 私钥，请离线保管。",
                Action("导出…", () => _ = _vm.ExportCredentialsCommand.ExecuteAsync(null))));

        // ── 应用数据 ────────────────────────────────────────────
        var appData = SectionCard("应用数据", "externaldrive", null,
            ActionRow("folder", muted, "数据目录", _vm.DataDirectory,
                Action("在访达中打开", () => _vm.OpenDataDirectoryCommand.Execute(null)), monoSubtitle: true),
            HairlineDivider(),
            ActionRow("doc.text", muted, "日志目录", _vm.LogDirectory,
                Action("在访达中打开", () => _vm.OpenLogDirectoryCommand.Execute(null)), monoSubtitle: true),
            HairlineDivider(),
            ActionRow("arrow.down.doc", muted, "完整备份",
                "把连接数据库与设置复制到你选的目录（钥匙串里的凭据不随此备份，走上面的「导出凭据」）。",
                Action("完整备份…", () => _ = _vm.BackupDataCommand.ExecuteAsync(null))),
            HairlineDivider(),
            ActionRow("trash", NSColor.SystemRed, "清理连接历史",
                "删除历史记录、释放空间。不影响连接配置与钥匙串里的凭据，删除后不可恢复。",
                Action("清理历史…", () => _ = _vm.ClearHistoryCommand.ExecuteAsync(null))));

        // ── 数据安全（说明面板）────────────────────────────────
        var security = InfoPanel(
            ("checkmark.shield", green, "仅存本地",
                "所有数据只保存在这台 Mac 上，不上传、不同步任何云端。"),
            ("lock", muted, "加密保护",
                "密码与私钥由 macOS 钥匙串（Keychain）保管，和连接数据库分开存放 —— "
                + "单独拷走 remoteflow.db 得不到任何密码。.rfbackup 的加密与平台无关，换到 Windows 也能导入。"));

        var version = Muted($"RemoteFlow {_vm.AppVersion} · macOS");

        return Page(connData, credBackup, appData, security, ImportMessagesPanel(), Gap(2), version);
    }

    /// <summary>导入 CSV / 凭据后逐行提示（跳过的重复项等）。无消息时整块隐藏。</summary>
    private NSView ImportMessagesPanel()
    {
        var list = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 4,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        var card = SectionCard("导入提示", "exclamationmark.bubble", null, list);

        void Sync()
        {
            foreach (var v in list.ArrangedSubviews.ToArray())
            {
                list.RemoveArrangedSubview(v);
                v.RemoveFromSuperview();
            }

            foreach (var msg in _vm.ImportMessages)
            {
                var lbl = Muted("· " + msg);
                lbl.LineBreakMode = NSLineBreakMode.ByWordWrapping;
                lbl.MaximumNumberOfLines = 0;
                list.AddArrangedSubview(lbl);
                lbl.WidthAnchor.ConstraintEqualTo(list.WidthAnchor).Active = true;
            }

            card.Hidden = !_vm.HasMessages;
        }

        Sync();
        _vm.ImportMessages.CollectionChanged += (_, _) =>
            NSApplication.SharedApplication.BeginInvokeOnMainThread(Sync);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsPageViewModel.HasMessages))
            {
                NSApplication.SharedApplication.BeginInvokeOnMainThread(Sync);
            }
        };
        return card;
    }

    // ── 版式辅助 ────────────────────────────────────────────────

    /// <summary>分页内容的左右留白。与凭据页保持一致。</summary>
    private const int PageHInset = 30;

    private static NSView Page(params NSView[] rows)
    {
        var stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 12,
            EdgeInsets = new NSEdgeInsets(22, PageHInset, 26, PageHInset),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        foreach (var r in rows)
        {
            // 先加入再约束，否则两者无共同祖先。顶层行（分组卡片）通栏铺满，
            // 与 Windows 版一致；靠 Leading 对齐会让卡片缩成窄条。
            // 注意要减去栈的左右内边距 —— 直接 == stack.Width 会盖过 EdgeInsets，
            // 卡片左边留白、右边却顶到窗口边缘。
            stack.AddArrangedSubview(r);
            r.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -(PageHInset * 2)).Active = true;
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

    /// <summary>一行左对齐的按钮组（导出 / 导入 / 备份等成组操作）。</summary>
    private static NSView ButtonRow(params NSButton[] buttons)
    {
        var row = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Spacing = 8,
            Alignment = NSLayoutAttribute.CenterY,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        foreach (var b in buttons)
        {
            row.AddArrangedSubview(b);
        }

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

    private static NSTextField TextField(string value, Action<string> onChange)
    {
        var f = new NSTextField
        {
            StringValue = value ?? string.Empty,
            Bordered = true,
            Bezeled = true,
            Font = NSFont.SystemFontOfSize(13),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        f.WidthAnchor.ConstraintEqualTo(260).Active = true;
        f.Changed += (_, _) => onChange(f.StringValue);
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
