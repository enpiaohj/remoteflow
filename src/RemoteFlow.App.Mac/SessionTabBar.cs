using AppKit;
using CoreGraphics;
using RemoteFlow.Core.Models;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 公共标题栏工具栏中的会话 Tab：左侧可横向滚动的会话胶囊，右侧常驻动作区（全屏）。
/// 对齐 Windows 版 SessionHostView 的标题栏标签。无会话时由宿主移除对应 NSToolbarItem。
/// </summary>
public sealed class SessionTabBar : NSView
{
    private const int BarHeight = 38;

    /// <summary>条高（点）。会话建立前要据此预留高度，否则 RDP 桌面会比视图高出这一截。</summary>
    public const int BarHeightPoints = BarHeight;

    // 对齐 Windows：标签按内容宽度依次排开（不是等分铺满），标签之间只留 2pt 间隙、没有分隔线。
    private readonly NSStackView _row = new()
    {
        Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
        Spacing = 2,
        Distribution = NSStackViewDistribution.GravityAreas,
        Alignment = NSLayoutAttribute.CenterY,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    private readonly List<Tab> _tabs = new();
    private Guid _active;

    /// <summary>单个标签的下限宽度（跟 Tab 内部约束的 <see cref="Tab.MinWidth"/> 对齐）——用来判断横向铺不铺得下。</summary>
    private static readonly nfloat MinTabWidth = Tab.MinWidth;
    private readonly NSScrollView _scroll;
    private readonly NSButton _collapsedButton;
    private bool _collapsed;

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

        var scroll = _scroll = new NSScrollView
        {
            DocumentView = _row,
            DrawsBackground = false,
            HasHorizontalScroller = false,
            HasVerticalScroller = false,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        // 空间不够铺不下所有标签时（对齐 Windows 版），整条折叠成一个按钮：
        // 有当前会话就显示它的名字，否则显示「活动连接 (N)」；点开跟正常标签一样能切换。
        _collapsedButton = new NSButton
        {
            Bordered = false,
            ImagePosition = NSCellImagePosition.ImageRight,
            Image = NSImage.GetSystemSymbol("chevron.down", null),
            Font = NSFont.SystemFontOfSize(12, NSFontWeight.Medium),
            ContentTintColor = NSColor.Label,
            TranslatesAutoresizingMaskIntoConstraints = false,
            Hidden = true,
        };
        _collapsedButton.SymbolConfiguration = NSImageSymbolConfiguration.Create(10, NSFontWeight.Medium);
        _collapsedButton.Activated += (_, _) => ShowSessionMenu(_collapsedButton);

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
        AddSubview(_collapsedButton);
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

            _collapsedButton.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 12),
            _collapsedButton.CenterYAnchor.ConstraintEqualTo(CenterYAnchor, -1),

            vsep.WidthAnchor.ConstraintEqualTo(1),
            vsep.HeightAnchor.ConstraintEqualTo(16),
            vsep.CenterYAnchor.ConstraintEqualTo(CenterYAnchor, -1),
            vsep.TrailingAnchor.ConstraintEqualTo(actions.LeadingAnchor, -6),

            actions.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -8),
            actions.CenterYAnchor.ConstraintEqualTo(CenterYAnchor, -1),

            sep.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            sep.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            sep.BottomAnchor.ConstraintEqualTo(BottomAnchor),
        });

        _heightConstraint = HeightAnchor.ConstraintEqualTo(BarHeight);
        _heightConstraint.Active = true;

        // 自定义 NSToolbarItem 从 macOS 12 起应由 View 自身约束声明尺寸，不能再用已废弃的
        // NSToolbarItem.MinSize/MaxSize。520pt 是首选宽度；窗口变窄时可压到 220，变宽时最多 720。
        WidthAnchor.ConstraintGreaterThanOrEqualTo(220).Active = true;
        WidthAnchor.ConstraintLessThanOrEqualTo(720).Active = true;
        var preferredWidth = WidthAnchor.ConstraintEqualTo(520);
        preferredWidth.Priority = (float)NSLayoutPriority.DefaultLow;
        preferredWidth.Active = true;
    }

    private readonly NSLayoutConstraint _heightConstraint;

    /// <summary>
    /// 收起 / 展开整条。宿主移除工具栏项时同步压低高度，重新插入前再恢复，
    /// 避免同一 View 在 NSToolbarItem 间复用时保留不可见但有尺寸的布局状态。
    /// </summary>
    public void SetBarHidden(bool hidden)
    {
        Hidden = hidden;
        _heightConstraint.Constant = hidden ? 0 : BarHeight;
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

    /// <summary>
    /// 条本身保持透明。它作为 NSToolbarItem 落在统一标题栏里，铺一层不透明的
    /// WindowBackground 会在窗口材质为云母 / 亚克力时挖出一块死板的矩形，
    /// 把整窗玻璃切断（见 <c>MainWindowController.ApplyGlassAppearance</c>）。
    /// 唯一上色的是选中标签那张卡片，其余交给标题栏材质透出来。
    /// </summary>
    private void RefreshChrome()
    {
        Layer!.BackgroundColor = NSColor.Clear.CGColor;
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
        UpdateCollapse();
    }

    /// <summary>同步某个会话标签的连接状态（标题右侧那颗状态点）。</summary>
    public void SetTabConnected(Guid id, bool connected)
        => _tabs.FirstOrDefault(t => t.Id == id)?.SetConnected(connected);

    public void RemoveTab(Guid id)
    {
        var tab = _tabs.FirstOrDefault(t => t.Id == id);
        if (tab is null)
        {
            return;
        }

        _tabs.Remove(tab);
        tab.View.RemoveFromSuperview();
        UpdateCollapse();

        if (_active == id && _tabs.Count > 0)
        {
            Select(_tabs[^1].Id);
        }
    }

    /// <summary>点开「切换会话」：有当前激活会话就显示它的名字，否则显示「活动连接 (N)」
    /// （对齐 Windows 版——空间不够横排时整条折叠成这一个按钮）。</summary>
    private void ShowSessionMenu(NSView anchor)
    {
        if (_tabs.Count == 0)
        {
            return;
        }

        var menu = new NSMenu();
        foreach (var t in _tabs)
        {
            menu.AddItem(new NSMenuItem(t.Title, (_, _) => Select(t.Id)) { Image = ProtocolStyle.Symbol(t.Protocol) });
        }

        menu.PopUpMenu(null, new CGPoint(0, anchor.Bounds.Height + 4), anchor);
    }

    /// <summary>横向铺不下所有标签时（宽度 &lt; 标签数 × 最小宽度）整条折叠成一个按钮。
    /// 每次尺寸变化（窗口缩放、折叠 / 展开左侧两列）都要重新判断。</summary>
    public override void Layout()
    {
        base.Layout();
        UpdateCollapse();
    }

    private void UpdateCollapse()
    {
        var available = Bounds.Width - 90; // 粗略扣掉 vsep + actions 两个按钮的固定占宽
        var needed = _tabs.Count * MinTabWidth;
        var collapsed = _tabs.Count > 0 && available > 0 && needed > available;
        _collapsed = collapsed;
        _scroll.Hidden = collapsed;
        _collapsedButton.Hidden = !collapsed;
        if (collapsed)
        {
            var activeTab = _tabs.FirstOrDefault(t => t.Id == _active);
            _collapsedButton.Title = activeTab?.Title ?? $"活动连接 ({_tabs.Count})";
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

        if (_collapsed)
        {
            UpdateCollapse();
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

    /// <summary>
    /// 单个会话标签，一比一对齐 Windows 版标题栏标签：
    /// 左起协议图标 → 标题（左对齐、尾部截断）→ 连接状态绿点 → 常驻关闭叉；
    /// 选中项是一张贴着内容区的浅色圆角卡片，未选中全透明（悬停只做一层极淡高亮）。
    /// 标签之间不画竖分隔线 —— Windows 版也没有，靠卡片本身区分当前会话。
    /// </summary>
    private sealed class Tab
    {
        /// <summary>标签宽度下限 / 上限。Windows 版标签按标题长短自适应：
        /// 短主机名明显窄于被截断的长 FQDN，这里保持同样的行为。</summary>
        public static readonly nfloat MinWidth = 150f;
        private static readonly nfloat MaxWidth = 250f;

        public Guid Id { get; }
        public string Title { get; private set; }
        public ProtocolType Protocol { get; }
        public NSView View { get; }

        public bool IsActive { get; private set; }
        public bool IsHovering => _card.Hovering;

        private readonly NSTextField _label;
        private readonly HoverCard _card;
        private readonly NSButton _close;
        private readonly NSImageView _icon;
        private readonly NSView _dot;
        private bool _connected = true;

        public Tab(Guid id, string name, ProtocolType protocol, SessionTabBar owner)
        {
            Id = id;
            Title = name;
            Protocol = protocol;

            _icon = new NSImageView
            {
                Image = ProtocolStyle.Symbol(protocol),
                ContentTintColor = ProtocolStyle.Tint(protocol),
                SymbolConfiguration = NSImageSymbolConfiguration.Create(13, NSFontWeight.Regular),
                TranslatesAutoresizingMaskIntoConstraints = false,
            };

            _label = new NSTextField
            {
                StringValue = name,
                Bordered = false,
                Editable = false,
                Selectable = false,
                DrawsBackground = false,
                Alignment = NSTextAlignment.Left,
                Font = NSFont.SystemFontOfSize(12),
                TextColor = NSColor.SecondaryLabel,
                LineBreakMode = NSLineBreakMode.TruncatingTail,
                TranslatesAutoresizingMaskIntoConstraints = false,
                ToolTip = name,
            };
            _label.SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Horizontal);

            // 连接状态绿点（Windows 版标题与关闭叉之间那颗）。
            _dot = new NSView { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false };
            _dot.Layer!.CornerRadius = 3f;

            _close = new NSButton
            {
                Image = NSImage.GetSystemSymbol("xmark", null),
                Bordered = false,
                ToolTip = "关闭会话",
                ContentTintColor = NSColor.TertiaryLabel,
                TranslatesAutoresizingMaskIntoConstraints = false,
                SymbolConfiguration = NSImageSymbolConfiguration.Create(9, NSFontWeight.Semibold),
            };
            _close.Activated += (_, _) => owner.Close(id);

            _card = new HoverCard(() => owner.Select(id));
            _card.HoverChanged = _ => Restyle();

            _card.AddSubview(_icon);
            _card.AddSubview(_label);
            _card.AddSubview(_dot);
            _card.AddSubview(_close);

            var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
            host.AddSubview(_card);

            NSLayoutConstraint.ActivateConstraints(new[]
            {
                _icon.LeadingAnchor.ConstraintEqualTo(_card.LeadingAnchor, 10),
                _icon.CenterYAnchor.ConstraintEqualTo(_card.CenterYAnchor),
                _icon.WidthAnchor.ConstraintEqualTo(16),
                _icon.HeightAnchor.ConstraintEqualTo(16),

                _label.LeadingAnchor.ConstraintEqualTo(_icon.TrailingAnchor, 8),
                _label.CenterYAnchor.ConstraintEqualTo(_card.CenterYAnchor),
                _label.TrailingAnchor.ConstraintLessThanOrEqualTo(_dot.LeadingAnchor, -6),

                _dot.TrailingAnchor.ConstraintEqualTo(_close.LeadingAnchor, -8),
                _dot.CenterYAnchor.ConstraintEqualTo(_card.CenterYAnchor),
                _dot.WidthAnchor.ConstraintEqualTo(6),
                _dot.HeightAnchor.ConstraintEqualTo(6),

                _close.TrailingAnchor.ConstraintEqualTo(_card.TrailingAnchor, -8),
                _close.CenterYAnchor.ConstraintEqualTo(_card.CenterYAnchor),
                _close.WidthAnchor.ConstraintEqualTo(16),
                _close.HeightAnchor.ConstraintEqualTo(16),

                // 选中卡片上下各留 4pt，做出 Windows 里标签「嵌在标题栏中」的那层留白。
                _card.LeadingAnchor.ConstraintEqualTo(host.LeadingAnchor),
                _card.TrailingAnchor.ConstraintEqualTo(host.TrailingAnchor),
                _card.TopAnchor.ConstraintEqualTo(host.TopAnchor, 4),
                _card.BottomAnchor.ConstraintEqualTo(host.BottomAnchor, -4),

                host.WidthAnchor.ConstraintGreaterThanOrEqualTo(MinWidth),
                host.WidthAnchor.ConstraintLessThanOrEqualTo(MaxWidth),
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

        /// <summary>会话是否已连接：决定标题右侧那颗状态点是绿色（已连接）还是橙色（连接中 / 异常）。</summary>
        public void SetConnected(bool connected)
        {
            if (_connected == connected)
            {
                return;
            }

            _connected = connected;
            Restyle();
        }

        public void RefreshChrome() => Restyle();

        private void Restyle()
        {
            var prev = NSAppearance.CurrentAppearance;
            NSAppearance.CurrentAppearance = _card.EffectiveAppearance;

            var layer = _card.Layer!;
            if (IsActive)
            {
                // 选中：与内容区同色的一张圆角卡片（Windows 版就是这个观感），不加描边和投影。
                layer.BackgroundColor = NSColor.ControlBackground.CGColor;
            }
            else
            {
                layer.BackgroundColor = (_card.Hovering
                    ? NSColor.SecondaryLabel.ColorWithAlphaComponent(0.10f)
                    : NSColor.Clear).CGColor;
            }

            _label.TextColor = IsActive ? NSColor.Label : NSColor.SecondaryLabel;
            _label.Font = NSFont.SystemFontOfSize(12, IsActive ? NSFontWeight.Medium : NSFontWeight.Regular);

            var tint = ProtocolStyle.Tint(Protocol);
            _icon.ContentTintColor = IsActive ? tint : tint.ColorWithAlphaComponent(0.75f);

            var status = _connected ? NSColor.SystemGreen : NSColor.SystemOrange;
            _dot.Layer!.BackgroundColor = status.CGColor;
            _dot.ToolTip = _connected ? "已连接" : "连接中";

            // 关闭叉常驻（对齐 Windows），只在悬停时加深，避免未选中标签太抢眼。
            _close.ContentTintColor = _card.Hovering || IsActive
                ? NSColor.SecondaryLabel
                : NSColor.TertiaryLabel;

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
