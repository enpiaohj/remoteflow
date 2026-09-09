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

    // ── 分组列表（macOS 系统设置式 inset grouped list，用于「安全」「数据与备份」）──
    //
    //   小节标签（容器外）
    //   ┌───────────────────────────────┐   ← 纯圆角描边容器，无标题栏 / 无投影
    //   │  标题            副文案   [按钮] │
    //   │ ───────────────────────────── │   ← 通栏发丝分隔
    //   │  标题                     [按钮] │
    //   └───────────────────────────────┘
    //   footnote（容器外下方）

    private const int RowPadX = 14;

    /// <summary>页面竖向骨架：Spacing 0 的竖栈，子项通栏。用 <see cref="Gap"/> 显式控制间距。</summary>
    private static NSView PageColumn(params NSView[] items)
    {
        var stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 0,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        foreach (var it in items)
        {
            stack.AddArrangedSubview(it);
            it.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        }

        return stack;
    }

    /// <summary>小节标签：11 / 600，坐在分组容器上方。</summary>
    private static NSView SectionLabel(string text, string? trailing = null)
    {
        var label = new NSTextField
        {
            StringValue = text,
            Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
            Font = NSFont.SystemFontOfSize(11, NSFontWeight.Semibold),
            TextColor = NSColor.SecondaryLabel,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        if (trailing is null)
        {
            label.SetContentHuggingPriorityForOrientation(250, NSLayoutConstraintOrientation.Horizontal);
            return Indent(label, 2);
        }

        var count = new NSTextField
        {
            StringValue = trailing,
            Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
            Font = NSFont.SystemFontOfSize(11),
            TextColor = NSColor.TertiaryLabel,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var row = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.FirstBaseline,
            Spacing = 5,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        row.AddArrangedSubview(label);
        row.AddArrangedSubview(count);
        return Indent(row, 2);
    }

    private static NSView Indent(NSView v, nfloat left)
    {
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        host.AddSubview(v);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            v.LeadingAnchor.ConstraintEqualTo(host.LeadingAnchor, left),
            v.TopAnchor.ConstraintEqualTo(host.TopAnchor),
            v.BottomAnchor.ConstraintEqualTo(host.BottomAnchor),
            v.TrailingAnchor.ConstraintLessThanOrEqualTo(host.TrailingAnchor),
        });
        return host;
    }

    /// <summary>分组容器：圆角 10 + 发丝描边 + 内容底色，无投影。行之间通栏发丝分隔。</summary>
    private static NSView GroupList(params NSView[] rows)
    {
        var box = new PlainBox();
        var col = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 0,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0)
            {
                col.AddArrangedSubview(HairlineDivider());
            }

            col.AddArrangedSubview(rows[i]);
        }

        box.AddSubview(col);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            col.LeadingAnchor.ConstraintEqualTo(box.LeadingAnchor),
            col.TrailingAnchor.ConstraintEqualTo(box.TrailingAnchor),
            col.TopAnchor.ConstraintEqualTo(box.TopAnchor),
            col.BottomAnchor.ConstraintEqualTo(box.BottomAnchor),
        });
        foreach (var v in col.ArrangedSubviews)
        {
            v.WidthAnchor.ConstraintEqualTo(col.WidthAnchor).Active = true;
        }

        return box;
    }

    /// <summary>分组内的一行：标题（+可选副文案）在左，操作控件在右。行高 ≥ 42。</summary>
    /// <param name="monoSub">副文案用等宽字体（路径 / 指纹）。</param>
    /// <param name="wrapSub">副文案换行而非中截（指纹要看全时）。</param>
    private static NSView ListRow(string label, string? sub, NSView? trailing,
        bool danger = false, bool monoSub = false, bool wrapSub = false)
    {
        var head = new NSTextField
        {
            StringValue = label,
            Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
            Font = NSFont.SystemFontOfSize(13),
            TextColor = danger ? NSColor.SystemRed : NSColor.Label,
            LineBreakMode = NSLineBreakMode.TruncatingTail,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        head.SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);

        NSView body;
        if (sub is null)
        {
            body = head;
        }
        else
        {
            var subLabel = new NSTextField
            {
                StringValue = sub,
                Bordered = false, Editable = false, Selectable = monoSub, DrawsBackground = false,
                Font = monoSub ? NSFont.MonospacedSystemFont(10, NSFontWeight.Regular) : NSFont.SystemFontOfSize(11),
                TextColor = NSColor.SecondaryLabel,
                LineBreakMode = (monoSub && !wrapSub) ? NSLineBreakMode.TruncatingMiddle : NSLineBreakMode.ByWordWrapping,
                MaximumNumberOfLines = (monoSub && !wrapSub) ? 1 : 0,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            subLabel.SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
            if (monoSub)
            {
                subLabel.ToolTip = sub; // 中截 / 换行后仍能悬停看全（也可选中复制）
            }

            var stack = new NSStackView
            {
                Orientation = NSUserInterfaceLayoutOrientation.Vertical,
                Alignment = NSLayoutAttribute.Leading,
                Spacing = 2,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            stack.AddArrangedSubview(head);
            stack.AddArrangedSubview(subLabel);
            // 竖栈默认不把子项拉到全宽 —— 显式绑定，标题才会按行宽截断、副文案才会换行。
            head.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
            subLabel.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
            body = stack;
        }

        var row = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        row.AddSubview(body);

        // body 居中 + 上下至少 9pt 留白；行高取「内容 + 18」与 42 的较大者。
        // 用 >= 而非 ==，避免内容不足 42 时和最小高冲突。
        var cons = new List<NSLayoutConstraint>
        {
            body.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor, RowPadX),
            body.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor),
            body.TopAnchor.ConstraintGreaterThanOrEqualTo(row.TopAnchor, 9),
            row.HeightAnchor.ConstraintGreaterThanOrEqualTo(body.HeightAnchor, 1, 18),
            row.HeightAnchor.ConstraintGreaterThanOrEqualTo(42),
        };

        if (trailing is null)
        {
            cons.Add(body.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor, -RowPadX));
        }
        else
        {
            trailing.TranslatesAutoresizingMaskIntoConstraints = false;
            trailing.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
            trailing.SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);
            row.AddSubview(trailing);
            cons.Add(trailing.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor, -RowPadX));
            cons.Add(trailing.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor));
            cons.Add(body.TrailingAnchor.ConstraintEqualTo(trailing.LeadingAnchor, -12));
        }

        NSLayoutConstraint.ActivateConstraints(cons.ToArray());
        return row;
    }

    /// <summary>分组内的空态行：居中的一句浅色说明。</summary>
    private static NSView EmptyRow(string text)
    {
        var label = new NSTextField
        {
            StringValue = text,
            Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
            Alignment = NSTextAlignment.Center,
            Font = NSFont.SystemFontOfSize(11),
            TextColor = NSColor.TertiaryLabel,
            LineBreakMode = NSLineBreakMode.ByWordWrapping,
            MaximumNumberOfLines = 0,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var row = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        row.AddSubview(label);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            label.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor, 18),
            label.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor, -18),
            label.TopAnchor.ConstraintEqualTo(row.TopAnchor, 18),
            label.BottomAnchor.ConstraintEqualTo(row.BottomAnchor, -18),
        });
        return row;
    }

    /// <summary>分组容器下方的说明文字。</summary>
    private static NSView Footnote(string text)
    {
        var label = new NSTextField
        {
            StringValue = text,
            Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
            Font = NSFont.SystemFontOfSize(11),
            TextColor = NSColor.SecondaryLabel,
            LineBreakMode = NSLineBreakMode.ByWordWrapping,
            MaximumNumberOfLines = 0,
            PreferredMaxLayoutWidth = 460,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        return Indent(label, 2);
    }

    private static NSView HairlineDivider()
    {
        var line = new PlainBox { IsHairline = true };
        line.HeightAnchor.ConstraintEqualTo(1).Active = true;
        return line;
    }

    /// <summary>标准圆角 bordered 按钮（分组行右侧的操作）。</summary>
    private static NSButton RowButton(string title, Action run, bool danger = false)
    {
        var b = NSButton.CreateButton(title, () => run());
        b.BezelStyle = NSBezelStyle.Rounded;
        b.ControlSize = NSControlSize.Small;
        b.Font = NSFont.SystemFontOfSize(12);
        if (danger)
        {
            b.ContentTintColor = NSColor.SystemRed;
        }

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
            EdgeInsets = new NSEdgeInsets(15, RowPadX, 15, RowPadX),
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
            group.WidthAnchor.ConstraintEqualTo(col.WidthAnchor, 1, -RowPadX * 2).Active = true;
            bodyLabel.WidthAnchor.ConstraintEqualTo(group.WidthAnchor).Active = true;
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

    /// <summary>
    /// 分组列表容器 / 发丝分隔线。<see cref="IsHairline"/> = true 时退化成一条纯色发丝线
    /// （无圆角 / 无描边）；否则是圆角 10 + 发丝描边 + 内容底色的分组容器，**无投影**
    /// （层次靠描边，不靠抬升 —— 对齐 macOS 系统设置的分组）。
    /// </summary>
    private sealed class PlainBox : NSView
    {
        private bool _hairline;

        public PlainBox()
        {
            WantsLayer = true;
            TranslatesAutoresizingMaskIntoConstraints = false;
            Refresh();
        }

        public bool IsHairline
        {
            get => _hairline;
            set { _hairline = value; Refresh(); }
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
            if (_hairline)
            {
                Layer!.CornerRadius = 0;
                Layer.BorderWidth = 0;
                Layer.BackgroundColor = Palette.Hairline(this).CGColor;
            }
            else
            {
                Layer!.CornerRadius = 10;
                Layer.BorderWidth = 1;
                Layer.BorderColor = Palette.Hairline(this).CGColor;
                Layer.BackgroundColor = Palette.CardSurface(this).CGColor;
            }

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
                + "字体填字体族名（逗号分隔），首个本机可用的生效。",
                Row("终端类型", term),
                Row("字符编码", enc),
                Row("终端主题", theme),
                Row("终端字体", fontFamily),
                Row("字号", size),
                Gap(2),
                Muted("实时预览（近似）"),
                TerminalPreview()),
            Card("连接与粘贴", "arrow.left.arrow.right",
                "保活间隔用于在空闲时维持连接；粘贴确认能避免把多行内容误当命令一次性执行。",
                Row("保活间隔（秒）", keepAlive),
                Check("粘贴多行文本前确认", _vm.SshConfirmMultilinePaste, v => _vm.SshConfirmMultilinePaste = v),
                Check("粘贴大量文本时警告", _vm.SshWarnLargePaste, v => _vm.SshWarnLargePaste = v)));
    }

    /// <summary>
    /// 「终端外观」近似预览：背景 / 前景随所选主题（VM 的 <c>TerminalPreview*</c>）刷新，
    /// 字体与字号跟随「终端字体 / 字号」，下方 16 格 ANSI 色带。不承载真实终端。
    /// </summary>
    private NSView TerminalPreview()
    {
        var box = new NSView { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false };
        box.Layer!.CornerRadius = 8;
        box.Layer.BorderWidth = 1;

        NSTextField SampleLine(string text) => new()
        {
            StringValue = text,
            Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
            LineBreakMode = NSLineBreakMode.Clipping,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        var line1 = SampleLine("user@remote ~ $ ls -la");
        var line2 = SampleLine("drwxr-xr-x  5 root  staff  160  RemoteFlow");
        line2.AlphaValue = 0.82f;

        var swatchRow = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Spacing = 2,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var swatches = new NSView[16];
        for (var i = 0; i < swatches.Length; i++)
        {
            var s = new NSView { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false };
            s.Layer!.CornerRadius = 2;
            s.Layer.BorderWidth = 1;
            s.Layer.BorderColor = NSColor.White.ColorWithAlphaComponent(0.22f).CGColor;
            s.WidthAnchor.ConstraintEqualTo(13).Active = true;
            s.HeightAnchor.ConstraintEqualTo(13).Active = true;
            swatches[i] = s;
            swatchRow.AddArrangedSubview(s);
        }

        var col = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 4,
            EdgeInsets = new NSEdgeInsets(12, 14, 12, 14),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        col.AddArrangedSubview(line1);
        col.AddArrangedSubview(line2);
        col.SetCustomSpacing(10, line2);
        col.AddArrangedSubview(swatchRow);

        box.AddSubview(col);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            col.LeadingAnchor.ConstraintEqualTo(box.LeadingAnchor),
            col.TrailingAnchor.ConstraintEqualTo(box.TrailingAnchor),
            col.TopAnchor.ConstraintEqualTo(box.TopAnchor),
            col.BottomAnchor.ConstraintEqualTo(box.BottomAnchor),
        });

        void Apply()
        {
            var bg = ColorFromHex(_vm.TerminalPreviewBackground);
            var fg = ColorFromHex(_vm.TerminalPreviewForeground);
            var font = ResolveTerminalFont(_vm.SshFontFamily, _vm.SshFontSize);

            box.Layer!.BackgroundColor = bg.CGColor;
            box.Layer.BorderColor = fg.ColorWithAlphaComponent(0.18f).CGColor;

            foreach (var l in new[] { line1, line2 })
            {
                l.TextColor = fg;
                l.Font = font;
            }

            var colors = _vm.TerminalPreviewColors;
            for (var i = 0; i < swatches.Length; i++)
            {
                swatches[i].Layer!.BackgroundColor =
                    (i < colors.Count ? ColorFromHex(colors[i].Hex) : NSColor.Clear).CGColor;
            }
        }

        Apply();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsPageViewModel.TerminalPreviewBackground)
                or nameof(SettingsPageViewModel.TerminalPreviewForeground)
                or nameof(SettingsPageViewModel.TerminalPreviewColors)
                or nameof(SettingsPageViewModel.SshFontFamily)
                or nameof(SettingsPageViewModel.SshFontSize))
            {
                NSApplication.SharedApplication.BeginInvokeOnMainThread(Apply);
            }
        };
        return box;
    }

    private static NSFont ResolveTerminalFont(string? family, int size)
    {
        nfloat pt = size > 0 ? size : 13;
        foreach (var raw in (family ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw.Trim().Trim('\'', '"').Trim();
            if (name.Length > 0 && NSFont.FromFontName(name, pt) is { } f)
            {
                return f;
            }
        }

        return NSFont.MonospacedSystemFont(pt, NSFontWeight.Regular);
    }

    private static NSColor ColorFromHex(string? hex)
    {
        var h = (hex ?? string.Empty).Trim().TrimStart('#');
        if ((h.Length == 6 || h.Length == 8)
            && uint.TryParse(h, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            var (r, g, b, a) = h.Length == 8
                ? ((v >> 24) & 0xFF, (v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF)
                : ((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF, 0xFFu);
            return NSColor.FromSrgb(r / 255f, g / 255f, b / 255f, a / 255f);
        }

        return NSColor.Black;
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
        // 分组内容随「已信任主机」列表变化重建（条目少，直接重建整组，不用表格）。
        var groupHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        NSView? current = null;

        void Rebuild()
        {
            current?.RemoveFromSuperview();

            var rows = _vm.TrustedHostKeys.Count == 0
                ? new[] { EmptyRow("还没有已信任的主机。首次连接某台服务器时，它的指纹会记在这里。") }
                : _vm.TrustedHostKeys.Select(HostKeyRow).ToArray();

            current = GroupList(rows);
            groupHost.AddSubview(current);
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                current.LeadingAnchor.ConstraintEqualTo(groupHost.LeadingAnchor),
                current.TrailingAnchor.ConstraintEqualTo(groupHost.TrailingAnchor),
                current.TopAnchor.ConstraintEqualTo(groupHost.TopAnchor),
                current.BottomAnchor.ConstraintEqualTo(groupHost.BottomAnchor),
            });
        }

        Rebuild();
        _vm.TrustedHostKeys.CollectionChanged += (_, _) =>
            NSApplication.SharedApplication.BeginInvokeOnMainThread(Rebuild);

        return Page(PageColumn(
            SectionLabel("已信任的主机"),
            Gap(7),
            groupHost,
            Gap(9),
            Footnote("首次连接时记录主机密钥 / 证书指纹。指纹发生变化会中止连接并强警告，"
                + "绝不静默接受 —— 只有在你确认服务器确实重装或换了证书后，才删除对应条目。")));
    }

    private NSView HostKeyRow(HostKeyItemViewModel item)
    {
        var algo = new NSTextField
        {
            StringValue = item.Algorithm,
            Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
            Font = NSFont.MonospacedSystemFont(10, NSFontWeight.Regular),
            TextColor = NSColor.TertiaryLabel,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var remove = RowButton("删除", () => _ = _vm.RemoveHostKeyCommand.ExecuteAsync(item));

        var trail = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = 8,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        trail.AddArrangedSubview(algo);
        trail.AddArrangedSubview(remove);

        // 指纹单行中截（悬停 / 选中看全），整行两行高，和设计稿一致。
        return ListRow(item.Host, item.Fingerprint, trail, monoSub: true);
    }

    // ── 数据与备份 ──────────────────────────────────────────────

    private NSView BuildData()
    {
        var connList = GroupList(
            ListRow("导出连接列表", "存为 CSV，便于备份或迁移。不含任何密码 / 私钥。",
                RowButton("导出…", () => _ = _vm.ExportConnectionsCsvCommand.ExecuteAsync(null))),
            ListRow("导入连接列表", "从 CSV 批量添加服务器，同名连接自动跳过。",
                RowButton("导入…", () => _ = _vm.ImportConnectionsCsvCommand.ExecuteAsync(null))));

        var credList = GroupList(
            ListRow("导出加密备份", "存为 .rfbackup，含密码 / 私钥，用你设的口令加密。",
                RowButton("导出…", () => _ = _vm.ExportCredentialsCommand.ExecuteAsync(null))),
            ListRow("从加密备份恢复", "导入 .rfbackup 里保存的登录信息，需要当初的口令。",
                RowButton("恢复…", () => _ = _vm.ImportCredentialsCommand.ExecuteAsync(null))));

        var appList = GroupList(
            ListRow("数据文件夹", _vm.DataDirectory,
                RowButton("在访达中打开", () => _vm.OpenDataDirectoryCommand.Execute(null)), monoSub: true),
            ListRow("日志文件夹", _vm.LogDirectory,
                RowButton("在访达中打开", () => _vm.OpenLogDirectoryCommand.Execute(null)), monoSub: true),
            ListRow("完整备份", "把连接数据库与设置复制到你选的目录（钥匙串里的凭据不随此备份）。",
                RowButton("完整备份…", () => _ = _vm.BackupDataCommand.ExecuteAsync(null))),
            ListRow("清理连接历史", "删除历史记录、释放空间。不影响连接配置与凭据，删除后不可恢复。",
                RowButton("清理…", () => _ = _vm.ClearHistoryCommand.ExecuteAsync(null)), danger: true));

        var trust = InfoPanel(
            ("checkmark.shield", NSColor.SecondaryLabel, "仅存本地",
                "所有数据只保存在这台 Mac 上，不上传、不同步任何云端。"),
            ("lock", NSColor.SecondaryLabel, "凭据在钥匙串",
                "密码与私钥由 macOS 钥匙串（Keychain）保管，和连接数据库分开存放 —— "
                + "单独拷走数据库文件拿不到任何密码。"));

        return Page(PageColumn(
            SectionLabel("连接列表"),
            Gap(7), connList, Gap(22),

            SectionLabel("凭据备份"),
            Gap(7), credList, Gap(8),
            Footnote(".rfbackup 用口令派生（PBKDF2-HMAC-SHA256，600 000 次）+ AES-256-GCM 加密，"
                + "与平台无关 —— 在 Windows 版 RemoteFlow 里也能导入。"),
            Gap(22),

            SectionLabel("应用数据"),
            Gap(7), appList, Gap(20),

            trust,
            ImportMessagesPanel(),
            Gap(16),
            Footnote($"RemoteFlow {_vm.AppVersion} · macOS")));
    }

    /// <summary>导入 CSV / 凭据后逐行提示（跳过的重复项等）。无消息时整块隐藏。</summary>
    private NSView ImportMessagesPanel()
    {
        var list = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 3,
            EdgeInsets = new NSEdgeInsets(11, RowPadX, 11, RowPadX),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var box = new PlainBox();
        box.AddSubview(list);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            list.LeadingAnchor.ConstraintEqualTo(box.LeadingAnchor),
            list.TrailingAnchor.ConstraintEqualTo(box.TrailingAnchor),
            list.TopAnchor.ConstraintEqualTo(box.TopAnchor),
            list.BottomAnchor.ConstraintEqualTo(box.BottomAnchor),
        });

        var host = PageColumn(SectionLabel("导入提示"), Gap(7), box);

        void Sync()
        {
            foreach (var v in list.ArrangedSubviews.ToArray())
            {
                list.RemoveArrangedSubview(v);
                v.RemoveFromSuperview();
            }

            foreach (var msg in _vm.ImportMessages)
            {
                var lbl = new NSTextField
                {
                    StringValue = "· " + msg,
                    Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
                    Font = NSFont.SystemFontOfSize(11),
                    TextColor = NSColor.SecondaryLabel,
                    LineBreakMode = NSLineBreakMode.ByWordWrapping,
                    MaximumNumberOfLines = 0,
                    TranslatesAutoresizingMaskIntoConstraints = false,
                };
                list.AddArrangedSubview(lbl);
                lbl.WidthAnchor.ConstraintEqualTo(list.WidthAnchor, 1, -RowPadX * 2).Active = true;
            }

            host.Hidden = !_vm.HasMessages;
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
        return host;
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

}
