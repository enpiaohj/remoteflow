using AppKit;
using CoreGraphics;
using RemoteFlow.Core.Models;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 详情区顶部的会话 Tab 条：左侧可横向滚动的会话胶囊，右侧常驻动作区（全屏）。
/// 对齐 Windows 版 SessionHostView 的常驻工具条。无会话时由宿主隐藏。
/// </summary>
public sealed class SessionTabBar : NSView
{
    private const int BarHeight = 38;

    /// <summary>条高（点）。会话建立前要据此预留高度，否则 RDP 桌面会比视图高出这一截。</summary>
    public const int BarHeightPoints = BarHeight;

    private readonly NSStackView _row = new()
    {
        Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
        Spacing = 0,
        Distribution = NSStackViewDistribution.FillEqually,
        Alignment = NSLayoutAttribute.CenterY,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    private readonly List<Tab> _tabs = new();
    private Guid _active;

    public event EventHandler<Guid>? TabSelected;
    public event EventHandler<Guid>? TabClosed;

    /// <summary>右侧「窗口内全屏」按钮：折叠左侧两列，会话铺满窗口。</summary>
    public event EventHandler? WindowFullScreenRequested;

    /// <summary>右侧「完全全屏」按钮（⌃⌘F）：进 macOS 原生全屏。</summary>
    public event EventHandler? ScreenFullScreenRequested;

    public int Count => _tabs.Count;

    public SessionTabBar()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        RefreshChrome();

        var scroll = new NSScrollView
        {
            DocumentView = _row,
            DrawsBackground = false,
            HasHorizontalScroller = false,
            HasVerticalScroller = false,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        var winFull = IconButton("rectangle.expand.vertical", "窗口内全屏 —— 折叠左侧两列，会话铺满窗口");
        if (winFull.Image is null)
        {
            winFull.Image = NSImage.GetSystemSymbol("arrow.left.and.right", null);
        }
        winFull.Activated += (_, _) => WindowFullScreenRequested?.Invoke(this, EventArgs.Empty);

        var full = IconButton("arrow.up.left.and.arrow.down.right", "完全全屏（⌃⌘F）");
        full.Activated += (_, _) => ScreenFullScreenRequested?.Invoke(this, EventArgs.Empty);

        var actions = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = 2,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        actions.AddArrangedSubview(winFull);
        actions.AddArrangedSubview(full);

        var vsep = new NSBox { BoxType = NSBoxType.NSBoxSeparator, TranslatesAutoresizingMaskIntoConstraints = false };
        var sep = new NSBox { BoxType = NSBoxType.NSBoxSeparator, TranslatesAutoresizingMaskIntoConstraints = false };

        AddSubview(scroll);
        AddSubview(vsep);
        AddSubview(actions);
        AddSubview(sep);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            scroll.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(vsep.LeadingAnchor, -6),
            scroll.TopAnchor.ConstraintEqualTo(TopAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(sep.TopAnchor),
            _row.LeadingAnchor.ConstraintEqualTo(scroll.ContentView.LeadingAnchor, 6),
            _row.TopAnchor.ConstraintEqualTo(scroll.ContentView.TopAnchor),
            _row.BottomAnchor.ConstraintEqualTo(scroll.ContentView.BottomAnchor),

            vsep.WidthAnchor.ConstraintEqualTo(1),
            vsep.HeightAnchor.ConstraintEqualTo(16),
            vsep.CenterYAnchor.ConstraintEqualTo(CenterYAnchor, -1),
            vsep.TrailingAnchor.ConstraintEqualTo(actions.LeadingAnchor, -6),

            actions.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -8),
            actions.CenterYAnchor.ConstraintEqualTo(CenterYAnchor, -1),

            sep.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            sep.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            sep.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            HeightAnchor.ConstraintEqualTo(BarHeight),
        });
    }

    /// <summary>条上通用的 28×28 无边框图标按钮。</summary>
    internal static NSButton IconButton(string symbol, string tip)
    {
        var b = new NSButton
        {
            Image = NSImage.GetSystemSymbol(symbol, null),
            Bordered = false,
            ToolTip = tip,
            ContentTintColor = NSColor.SecondaryLabel,
            TranslatesAutoresizingMaskIntoConstraints = false,
            SymbolConfiguration = NSImageSymbolConfiguration.Create(13, NSFontWeight.Regular),
        };
        b.WidthAnchor.ConstraintEqualTo(28).Active = true;
        b.HeightAnchor.ConstraintEqualTo(26).Active = true;
        return b;
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        RefreshChrome();
        foreach (var t in _tabs)
        {
            t.RefreshChrome();
        }
    }

    private void RefreshChrome()
    {
        var prev = NSAppearance.CurrentAppearance;
        NSAppearance.CurrentAppearance = EffectiveAppearance;
        Layer!.BackgroundColor = NSColor.WindowBackground.CGColor;
        NSAppearance.CurrentAppearance = prev;
    }

    public void AddTab(Guid id, string name, ProtocolType protocol)
    {
        if (_tabs.Any(t => t.Id == id))
        {
            return;
        }

        var tab = new Tab(id, name, protocol, this);
        _tabs.Add(tab);
        _row.AddArrangedSubview(tab.View);
        Select(id);
        SyncSeparators();
    }

    public void RemoveTab(Guid id)
    {
        var tab = _tabs.FirstOrDefault(t => t.Id == id);
        if (tab is null)
        {
            return;
        }

        _tabs.Remove(tab);
        tab.View.RemoveFromSuperview();
        SyncSeparators();

        if (_active == id && _tabs.Count > 0)
        {
            Select(_tabs[^1].Id);
        }
    }

    public void RenameTab(Guid id, string name)
        => _tabs.FirstOrDefault(t => t.Id == id)?.SetTitle(name);

    /// <summary>会话列表（供全屏药丸的「切换会话」菜单用）。</summary>
    public IReadOnlyList<(Guid Id, string Title, ProtocolType Protocol)> Sessions
        => _tabs.Select(t => (t.Id, t.Title, t.Protocol)).ToList();

    public Guid ActiveId => _active;

    public void RequestSelect(Guid id) => Select(id);

    /// <summary>仅更新高亮，不触发 <see cref="TabSelected"/>（宿主自身切换时用）。</summary>
    public void HighlightOnly(Guid id)
    {
        _active = id;
        foreach (var t in _tabs)
        {
            t.SetActive(t.Id == id);
        }

        SyncSeparators();
    }

    public void ClearHighlight()
    {
        _active = Guid.Empty;
        foreach (var t in _tabs)
        {
            t.SetActive(false);
        }

        SyncSeparators();
    }

    /// <summary>
    /// Safari 式竖分隔线：只画在「两侧都不是选中 / 悬停」的相邻标签之间，
    /// 最后一个标签不画。这样选中的标签像一张浮起的卡片，两边自然断开。
    /// </summary>
    private void SyncSeparators()
    {
        for (var i = 0; i < _tabs.Count; i++)
        {
            var self = _tabs[i];
            var next = i + 1 < _tabs.Count ? _tabs[i + 1] : null;
            var show = next is not null
                       && !self.IsActive && !self.IsHovering
                       && !next.IsActive && !next.IsHovering;
            self.SetSeparator(show);
        }
    }

    private void Select(Guid id)
    {
        HighlightOnly(id);
        TabSelected?.Invoke(this, id);
    }

    private void Close(Guid id) => TabClosed?.Invoke(this, id);

    /// <summary>
    /// 单个会话标签（Safari 风格）：平底条上，选中项浮起成一张圆角卡片；
    /// 左槽平时是协议色圆点，悬停换成关闭叉；标题居中截断；尾部一条细竖线做分隔。
    /// </summary>
    private sealed class Tab
    {
        public Guid Id { get; }
        public string Title { get; private set; }
        public ProtocolType Protocol { get; }
        public NSView View { get; }

        public bool IsActive { get; private set; }
        public bool IsHovering => _card.Hovering;

        private readonly NSTextField _label;
        private readonly HoverCard _card;
        private readonly NSButton _close;
        private readonly NSView _dot;
        private readonly NSBox _sep;

        public Tab(Guid id, string name, ProtocolType protocol, SessionTabBar owner)
        {
            Id = id;
            Title = name;
            Protocol = protocol;

            _dot = new NSView { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false };
            _dot.Layer!.CornerRadius = 3.5f;

            _label = new NSTextField
            {
                StringValue = name,
                Bordered = false,
                Editable = false,
                Selectable = false,
                DrawsBackground = false,
                Alignment = NSTextAlignment.Center,
                Font = NSFont.SystemFontOfSize(12),
                TextColor = NSColor.SecondaryLabel,
                LineBreakMode = NSLineBreakMode.TruncatingTail,
                TranslatesAutoresizingMaskIntoConstraints = false,
                ToolTip = name,
            };
            _label.SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Horizontal);

            _close = new NSButton
            {
                Image = NSImage.GetSystemSymbol("xmark", null),
                Bordered = false,
                ToolTip = "关闭会话",
                ContentTintColor = NSColor.SecondaryLabel,
                TranslatesAutoresizingMaskIntoConstraints = false,
                SymbolConfiguration = NSImageSymbolConfiguration.Create(9, NSFontWeight.Semibold),
                Hidden = true,
            };
            _close.Activated += (_, _) => owner.Close(id);

            _card = new HoverCard(() => owner.Select(id));
            _card.HoverChanged = _ =>
            {
                Restyle();
                owner.SyncSeparators();
            };

            // 左槽 18pt：圆点与关闭叉同位，悬停时互换（Safari 的 favicon → 关闭）。
            _card.AddSubview(_dot);
            _card.AddSubview(_close);
            _card.AddSubview(_label);

            _sep = new NSBox { BoxType = NSBoxType.NSBoxSeparator, TranslatesAutoresizingMaskIntoConstraints = false };

            var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
            host.AddSubview(_card);
            host.AddSubview(_sep);

            NSLayoutConstraint.ActivateConstraints(new[]
            {
                _dot.LeadingAnchor.ConstraintEqualTo(_card.LeadingAnchor, 10),
                _dot.CenterYAnchor.ConstraintEqualTo(_card.CenterYAnchor),
                _dot.WidthAnchor.ConstraintEqualTo(7),
                _dot.HeightAnchor.ConstraintEqualTo(7),

                _close.CenterXAnchor.ConstraintEqualTo(_dot.CenterXAnchor),
                _close.CenterYAnchor.ConstraintEqualTo(_card.CenterYAnchor),
                _close.WidthAnchor.ConstraintEqualTo(16),
                _close.HeightAnchor.ConstraintEqualTo(16),

                _label.LeadingAnchor.ConstraintEqualTo(_dot.TrailingAnchor, 7),
                _label.TrailingAnchor.ConstraintEqualTo(_card.TrailingAnchor, -10),
                _label.CenterYAnchor.ConstraintEqualTo(_card.CenterYAnchor),

                // 标签本体：条内留 4pt 上下边距，做出「浮起卡片」的空隙。
                _card.LeadingAnchor.ConstraintEqualTo(host.LeadingAnchor),
                _card.TrailingAnchor.ConstraintEqualTo(host.TrailingAnchor),
                _card.TopAnchor.ConstraintEqualTo(host.TopAnchor, 4),
                _card.BottomAnchor.ConstraintEqualTo(host.BottomAnchor, -4),

                _sep.TrailingAnchor.ConstraintEqualTo(host.TrailingAnchor),
                _sep.CenterYAnchor.ConstraintEqualTo(host.CenterYAnchor),
                _sep.WidthAnchor.ConstraintEqualTo(1),
                _sep.HeightAnchor.ConstraintEqualTo(15),

                // 等宽由 FillEqually 负责，这里只兜住上下限：多标签时压缩、单标签不至于拉满整条。
                host.WidthAnchor.ConstraintGreaterThanOrEqualTo(110),
                host.WidthAnchor.ConstraintLessThanOrEqualTo(240),
                host.HeightAnchor.ConstraintEqualTo(BarHeight - 1),
            });

            View = host;
            Restyle();
        }

        public void SetTitle(string name)
        {
            Title = name;
            _label.StringValue = name;
            _label.ToolTip = name;
        }

        public void SetActive(bool active)
        {
            IsActive = active;
            Restyle();
        }

        public void SetSeparator(bool show) => _sep.Hidden = !show;

        public void RefreshChrome() => Restyle();

        private void Restyle()
        {
            var prev = NSAppearance.CurrentAppearance;
            NSAppearance.CurrentAppearance = _card.EffectiveAppearance;

            var layer = _card.Layer!;
            if (IsActive)
            {
                // 选中：浮起的一张卡片 —— 比条底亮一档 + 极淡描边 + 轻投影。
                layer.BackgroundColor = NSColor.ControlBackground.CGColor;
                layer.BorderWidth = 1;
                layer.BorderColor = NSColor.SecondaryLabel.ColorWithAlphaComponent(0.14f).CGColor;
                layer.ShadowOpacity = 0.10f;
                layer.ShadowRadius = 3;
                layer.ShadowOffset = new CGSize(0, -1);
                layer.ShadowColor = NSColor.Black.CGColor;
            }
            else
            {
                layer.BackgroundColor = (_card.Hovering
                    ? NSColor.SecondaryLabel.ColorWithAlphaComponent(0.09f)
                    : NSColor.Clear).CGColor;
                layer.BorderWidth = 0;
                layer.ShadowOpacity = 0;
            }

            _label.TextColor = IsActive ? NSColor.Label : NSColor.SecondaryLabel;
            _label.Font = NSFont.SystemFontOfSize(12, IsActive ? NSFontWeight.Medium : NSFontWeight.Regular);

            var tint = ProtocolStyle.Tint(Protocol);
            _dot.Layer!.BackgroundColor = (IsActive ? tint : tint.ColorWithAlphaComponent(0.6f)).CGColor;

            // 悬停时左槽换成关闭叉（Safari 的 favicon → 关闭）。
            _close.Hidden = !_card.Hovering;
            _dot.Hidden = _card.Hovering;

            NSAppearance.CurrentAppearance = prev;
        }

        /// <summary>可点击 + 悬停感知的圆角卡片。</summary>
        private sealed class HoverCard : NSView
        {
            private readonly Action _onClick;
            private NSTrackingArea? _tracking;

            public bool Hovering { get; private set; }
            public Action<bool>? HoverChanged;

            public HoverCard(Action onClick)
            {
                _onClick = onClick;
                WantsLayer = true;
                TranslatesAutoresizingMaskIntoConstraints = false;
                Layer!.CornerRadius = 8;
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
            {
                Hovering = true;
                HoverChanged?.Invoke(true);
            }

            public override void MouseExited(NSEvent theEvent)
            {
                Hovering = false;
                HoverChanged?.Invoke(false);
            }

            public override void MouseDown(NSEvent theEvent) => _onClick();

            public override void ViewDidChangeEffectiveAppearance()
            {
                base.ViewDidChangeEffectiveAppearance();
                HoverChanged?.Invoke(Hovering);
            }
        }
    }
}
