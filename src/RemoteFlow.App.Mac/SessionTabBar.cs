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

    private readonly NSStackView _row = new()
    {
        Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
        Spacing = 2,
        Alignment = NSLayoutAttribute.CenterY,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    private readonly List<Tab> _tabs = new();
    private Guid _active;

    public event EventHandler<Guid>? TabSelected;
    public event EventHandler<Guid>? TabClosed;

    /// <summary>右侧「全屏」按钮（⌃⌘F）。</summary>
    public event EventHandler? FullScreenRequested;

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

        var full = IconButton("arrow.up.left.and.arrow.down.right", "进入全屏（⌃⌘F）");
        full.Activated += (_, _) => FullScreenRequested?.Invoke(this, EventArgs.Empty);

        var actions = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = 2,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
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
            _row.LeadingAnchor.ConstraintEqualTo(scroll.ContentView.LeadingAnchor, 8),
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
    }

    public void ClearHighlight()
    {
        _active = Guid.Empty;
        foreach (var t in _tabs)
        {
            t.SetActive(false);
        }
    }

    private void Select(Guid id)
    {
        HighlightOnly(id);
        TabSelected?.Invoke(this, id);
    }

    private void Close(Guid id) => TabClosed?.Invoke(this, id);

    /// <summary>单个会话胶囊：协议色圆点 + 名称 + 关闭（悬停 / 选中才显示）。</summary>
    private sealed class Tab
    {
        public Guid Id { get; }
        public string Title { get; private set; }
        public ProtocolType Protocol { get; }
        public NSView View { get; }

        private readonly NSTextField _label;
        private readonly HoverPill _pill;
        private readonly NSButton _close;
        private readonly NSView _dot;
        private bool _active;

        public Tab(Guid id, string name, ProtocolType protocol, SessionTabBar owner)
        {
            Id = id;
            Title = name;
            Protocol = protocol;

            var tint = ProtocolStyle.Tint(protocol);
            _dot = new NSView { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false };
            _dot.Layer!.CornerRadius = 3;
            _dot.Layer.BackgroundColor = tint.CGColor;

            _label = new NSTextField
            {
                StringValue = name,
                Bordered = false,
                Editable = false,
                Selectable = false,
                DrawsBackground = false,
                Font = NSFont.SystemFontOfSize(12),
                TextColor = NSColor.SecondaryLabel,
                LineBreakMode = NSLineBreakMode.TruncatingTail,
                TranslatesAutoresizingMaskIntoConstraints = false,
                ToolTip = name,
            };

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

            _pill = new HoverPill(() => owner.Select(id));
            _pill.HoverChanged = _ => Restyle();

            _pill.AddSubview(_dot);
            _pill.AddSubview(_label);
            _pill.AddSubview(_close);
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                _dot.LeadingAnchor.ConstraintEqualTo(_pill.LeadingAnchor, 10),
                _dot.CenterYAnchor.ConstraintEqualTo(_pill.CenterYAnchor),
                _dot.WidthAnchor.ConstraintEqualTo(6),
                _dot.HeightAnchor.ConstraintEqualTo(6),

                _label.LeadingAnchor.ConstraintEqualTo(_dot.TrailingAnchor, 8),
                _label.CenterYAnchor.ConstraintEqualTo(_pill.CenterYAnchor),
                _label.WidthAnchor.ConstraintLessThanOrEqualTo(150),

                _close.LeadingAnchor.ConstraintEqualTo(_label.TrailingAnchor, 6),
                _close.TrailingAnchor.ConstraintEqualTo(_pill.TrailingAnchor, -7),
                _close.CenterYAnchor.ConstraintEqualTo(_pill.CenterYAnchor),
                _close.WidthAnchor.ConstraintEqualTo(15),
                _close.HeightAnchor.ConstraintEqualTo(15),

                _pill.HeightAnchor.ConstraintEqualTo(28),
            });

            View = _pill;
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
            _active = active;
            Restyle();
        }

        public void RefreshChrome() => Restyle();

        private void Restyle()
        {
            var prev = NSAppearance.CurrentAppearance;
            NSAppearance.CurrentAppearance = _pill.EffectiveAppearance;

            // 选中：控件强调色淡填充 + 同色描边；悬停：极淡中性底；其余：透明。
            NSColor fill = _active
                ? NSColor.ControlAccent.ColorWithAlphaComponent(0.16f)
                : _pill.Hovering
                    ? NSColor.SecondaryLabel.ColorWithAlphaComponent(0.10f)
                    : NSColor.Clear;
            _pill.Layer!.BackgroundColor = fill.CGColor;
            _pill.Layer.BorderWidth = _active ? 1 : 0;
            _pill.Layer.BorderColor = NSColor.ControlAccent.ColorWithAlphaComponent(0.35f).CGColor;

            _label.TextColor = _active ? NSColor.Label : NSColor.SecondaryLabel;
            _label.Font = NSFont.SystemFontOfSize(12, _active ? NSFontWeight.Medium : NSFontWeight.Regular);
            _dot.Layer!.BackgroundColor = _active
                ? ProtocolStyle.Tint(Protocol).CGColor
                : ProtocolStyle.Tint(Protocol).ColorWithAlphaComponent(0.55f).CGColor;

            // 关闭按钮只在选中或悬停时出现，避免一排叉号。
            _close.Hidden = !(_active || _pill.Hovering);

            NSAppearance.CurrentAppearance = prev;
        }

        /// <summary>可点击 + 悬停感知的圆角容器。</summary>
        private sealed class HoverPill : NSView
        {
            private readonly Action _onClick;
            private NSTrackingArea? _tracking;

            public bool Hovering { get; private set; }
            public Action<bool>? HoverChanged;

            public HoverPill(Action onClick)
            {
                _onClick = onClick;
                WantsLayer = true;
                TranslatesAutoresizingMaskIntoConstraints = false;
                Layer!.CornerRadius = 7;
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
