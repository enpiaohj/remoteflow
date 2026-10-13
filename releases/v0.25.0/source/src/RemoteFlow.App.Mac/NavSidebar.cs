using AppKit;

namespace RemoteFlow.App.Mac;

/// <summary>主窗口第一列：导航图标栏，一比一对齐 Windows 版最左侧的纯图标窄栏——
/// 只有图标 + Tooltip，不带文字标签（文字侧栏是本项目在 Windows 四栏方案确定前的
/// 过渡形态，已按用户反馈改回纯图标）。连接 / 管理两组，路由到中间列表。
///
/// 用一排 <see cref="NSButton"/> 而不是 NSTableView 实现：早先用单列 NSTableView
/// 时，NSTableColumn 的默认宽度（~100pt）比这栏实际可用宽度（~56pt）还宽，
/// Autoresizing 只会把列拉宽去填满多余空间、不会主动把默认宽度往下收窄，图标因此
/// 按「列中心」摆到了栏外，只露出被裁切的一条边、且怎么调都对不齐（真实事故）。
/// 按钮直接以本视图为参照居中，不经过任何列宽同步环节，没有这类问题。</summary>
public sealed class NavSidebar : NSViewController
{
    /// <summary>收藏 / 最近连接不再是一级导航项（对齐 Windows「连接工作台」），
    /// 降级为 <see cref="ConnectionResourcePane"/> 里的智能视图切换。</summary>
    public enum Item { Home, Connections, Credentials, Settings }

    private static readonly (Item Item, string Title, string Symbol)[] Rows =
    {
        (Item.Home, "首页", "house"),
        (Item.Connections, "我的连接", "rectangle.stack"),
        (Item.Credentials, "凭据", "key"),
        (Item.Settings, "设置", "gearshape"),
    };

    private readonly Dictionary<Item, NSButton> _buttons = new();
    // 初值 null 而非 Item.Home：如果写死 Home，启动落地页恰好是「首页」时，第一次
    // Select(Home) 会被下面「相同项不重复 fire」的判断误判成「没变化」而吞掉，
    // 页面内容就永远初始化不到（真实事故，见 Select 方法的注释）。
    private Item? _selected;

    public event EventHandler<Item>? Selected;

    public NavSidebar()
    {
        var fx = new NSVisualEffectView
        {
            Material = NSVisualEffectMaterial.Sidebar,
            BlendingMode = NSVisualEffectBlendingMode.BehindWindow,
            State = NSVisualEffectState.FollowsWindowActiveState,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        // 顶部产品标识：只留强调色圆角图标块，不再放产品名 / 版本号文字——
        // 栏宽收窄到纯图标尺寸后放不下。
        var brandTile = new NSView { TranslatesAutoresizingMaskIntoConstraints = false, WantsLayer = true };
        brandTile.Layer!.CornerRadius = 6;
        Palette.With(brandTile, () => brandTile.Layer.BackgroundColor = NSColor.ControlAccent.CGColor);
        var brandGlyph = new NSImageView
        {
            Image = NSImage.GetSystemSymbol("display", null),
            ContentTintColor = NSColor.White,
            TranslatesAutoresizingMaskIntoConstraints = false,
            SymbolConfiguration = NSImageSymbolConfiguration.Create(13, NSFontWeight.Medium),
        };
        brandTile.AddSubview(brandGlyph);
        brandTile.ToolTip = "RemoteFlow " + AppVersion;
        fx.AddSubview(brandTile);

        var stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.CenterX,
            Spacing = 4,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        foreach (var (item, title, symbol) in Rows)
        {
            // 用户主动点击 → 即使点的是当前页也要 fire：会话铺满舞台时图标栏是唯一可见的
            // 导航，点「我的连接」就是用户「回到工作台」的操作（对齐 Windows）。沿用
            // 「变化才 fire」会让这一下毫无反应（真实事故）。程序化 Select 仍保持去重。
            var button = NSButton.CreateButton(string.Empty, () => Select(item, fromUser: true));
            button.Image = NSImage.GetSystemSymbol(symbol, null);
            button.ImagePosition = NSCellImagePosition.ImageOnly;
            button.SymbolConfiguration = NSImageSymbolConfiguration.Create(17, NSFontWeight.Regular);
            button.Bordered = false;
            button.WantsLayer = true;
            button.Layer!.CornerRadius = 8;
            button.ToolTip = title;
            // NSButton.CreateButton 默认 TranslatesAutoresizingMaskIntoConstraints=true，
            // 跟下面的显式宽高约束会打架，必须手动关掉。
            button.TranslatesAutoresizingMaskIntoConstraints = false;
            button.WidthAnchor.ConstraintEqualTo(40).Active = true;
            button.HeightAnchor.ConstraintEqualTo(36).Active = true;

            stack.AddArrangedSubview(button);
            _buttons[item] = button;
        }

        fx.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            // 顶部留出标题栏的高度（窗口是 FullSizeContentView，内容会延伸到红黄绿按钮那一行）。
            brandTile.CenterXAnchor.ConstraintEqualTo(fx.CenterXAnchor),
            brandTile.TopAnchor.ConstraintEqualTo(fx.SafeAreaLayoutGuide.TopAnchor, 12),
            brandTile.WidthAnchor.ConstraintEqualTo(28),
            brandTile.HeightAnchor.ConstraintEqualTo(28),
            brandGlyph.CenterXAnchor.ConstraintEqualTo(brandTile.CenterXAnchor),
            brandGlyph.CenterYAnchor.ConstraintEqualTo(brandTile.CenterYAnchor),

            stack.CenterXAnchor.ConstraintEqualTo(fx.CenterXAnchor),
            stack.TopAnchor.ConstraintEqualTo(brandTile.BottomAnchor, 16),
        });

        ApplySelectionAppearance();
        View = fx;
    }

    /// <summary>产品版本号，取自 bundle 的 <c>CFBundleShortVersionString</c>
    /// （= csproj 的 <c>ApplicationDisplayVersion</c>，构建时写进 Info.plist）。</summary>
    private static string AppVersion =>
        "v" + (NSBundle.MainBundle.InfoDictionary?["CFBundleShortVersionString"]?.ToString() ?? "?");

    /// <summary>选中导航项——按钮点击和外部调用（MainWindowController 启动 / NavigateTo）
    /// 走同一个方法，跟旧版 NSTableView.SelectRow 的语义对齐：目标项和当前已选中的相同时
    /// 不重复 fire <see cref="Selected"/>；真正变化时才 fire，驱动页面切换。
    /// 早先重写成 NSButton 时这里漏掉了「变化才 fire」，导致 <c>StartAsync</c> 里
    /// <c>_nav.Select(item)</c> 只是把按钮点亮却从没真正切换过页面内容——图标显示「首页」，
    /// 内容却还停在分栏系统的默认态（连接工作台，且未加载）（真实事故）。</summary>
    /// <param name="fromUser">true = 用户点了图标栏。此时即使目标就是当前页也 fire，
    /// 让宿主有机会把铺满舞台的会话收起、回到这一页；宿主自己判断要不要重建页面。</param>
    public void Select(Item item, bool fromUser = false)
    {
        if (_selected == item && !fromUser)
        {
            return;
        }

        _selected = item;
        ApplySelectionAppearance();
        Selected?.Invoke(this, item);
    }

    private void ApplySelectionAppearance()
    {
        foreach (var (item, button) in _buttons)
        {
            var isSelected = item == _selected;
            Palette.With(button, () => button.Layer!.BackgroundColor =
                isSelected ? NSColor.ControlAccent.CGColor : NSColor.Clear.CGColor);
            button.ContentTintColor = isSelected ? NSColor.White : NSColor.SecondaryLabel;
        }
    }
}
