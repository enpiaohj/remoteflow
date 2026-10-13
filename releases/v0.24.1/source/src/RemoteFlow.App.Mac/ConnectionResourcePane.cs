using AppKit;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 主窗口第二列：连接资源导航，一比一对齐 Windows 版 <c>ConnectionResourceTree.xaml</c>——
/// 独立的一栏，只负责「智能视图」（所有设备 / 收藏 / 最近连接）+「分组」摊平导航树的筛选，
/// 不显示任何连接行本身（那是第三列 <see cref="ConnectionListPane"/> 的职责）。
/// </summary>
public sealed class ConnectionResourcePane : NSViewController
{
    private readonly ConnectionsPageViewModel _vm;

    private readonly NSTableView _smartTable = new();
    private readonly NSTableView _groupTable = new();
    private readonly NSScrollView _smartScroll;
    private readonly NSScrollView _groupScroll;
    private NSButton _addGroup = null!;

    private SmartViewSource? _smartSource;
    private GroupNavSource? _groupSource;

    public ConnectionResourcePane(ConnectionsPageViewModel vm)
    {
        _vm = vm;

        // ── 智能视图（固定 3 行，不需要滚动）───────────────────────
        _smartTable.HeaderView = null;
        _smartTable.SelectionHighlightStyle = NSTableViewSelectionHighlightStyle.SourceList;
        _smartTable.RowSizeStyle = NSTableViewRowSizeStyle.Custom;
        _smartTable.RowHeight = SmartViewSource.RowHeight;
        _smartTable.BackgroundColor = NSColor.Clear;
        // 整张表在获得键盘焦点时会画一圈包住全部行的方框（AppKit 对 NSTableView 本身的
        // 焦点环，不是逐行的选中框）——3 行都框进去，视觉上像「所有设备/收藏/最近连接」
        // 被同一个框圈住，关掉即可，选中态已经有 SourceList 高亮足够表达。
        _smartTable.FocusRingType = NSFocusRingType.None;
        // 单列表格：列宽交给 AppKit 跟着表格宽度走，绝不自己写死一个「猜」的宽度。
        // 踩过两次同一个坑——写死的列宽只要大于表格真实宽度（这里表格 = 栏宽 − 左右各 16
        // 的内边距，比「栏宽」窄 32），右对齐的数量标签就会被摆到可视区外、整个看不见；
        // 而 Autoresizing 只会把列往宽了拉、不会主动往窄了收，救不回来。
        // LastColumnOnly + 一个肯定偏小的初始宽度 = 列永远精确撑满表格。
        _smartTable.ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.LastColumnOnly;
        _smartTable.AddColumn(new NSTableColumn("c") { Width = 80, ResizingMask = NSTableColumnResizing.Autoresizing });
        _smartSource = new SmartViewSource(_smartTable, _vm);
        _smartSource.SelectionChanged += (_, _) =>
        {
            if (_smartSource!.Selected is { } opt) _vm.SelectedSmartView = opt;
        };
        // 装进 NSScrollView（不显示滚动条，只有 3 行、高度写死）——不是为了滚动，而是
        // 因为 NSTableView 只有作为 documentView 时才会把自己的宽度贴合容器、并把单列
        // 一起收到同样的宽度。之前把它当普通子视图直接摆进 NSStackView，表格宽度是
        // 「栏宽 − 32」而列宽还停在建表时写死的值，右对齐的数量标签就被摆到可视区外、
        // 整个看不见；同一个界面里装在滚动视图里的分组表格数量一直正常显示，就是这个
        // 区别（真实事故的判定依据）。
        // 行间距清零，高度就等于「行数 × 行高」，不多不少。
        // 用默认行间距（纵向 2pt）时 3 行实际需要 100pt 左右，而这里写死 96，
        // 内容在滚动视图里就能微微上下滑动——选中不同行会被自动滚动带着位移
        // （真实反馈：选「所有设备」整块下移一点、选「最近连接」又上移一点）。
        _smartTable.IntercellSpacing = new CoreGraphics.CGSize(0, 0);
        _smartScroll = Scroll(_smartTable);
        _smartScroll.HasVerticalScroller = false;
        _smartScroll.VerticalScrollElasticity = NSScrollElasticity.None;
        _smartScroll.TranslatesAutoresizingMaskIntoConstraints = false;
        _smartScroll.HeightAnchor.ConstraintEqualTo(SmartViewSource.RowHeight * 3).Active = true;

        // ── 分组导航（可滚动，行数不定）─────────────────────────────
        _groupTable.HeaderView = null;
        _groupTable.SelectionHighlightStyle = NSTableViewSelectionHighlightStyle.SourceList;
        _groupTable.RowSizeStyle = NSTableViewRowSizeStyle.Custom;
        _groupTable.RowHeight = 28;
        _groupTable.BackgroundColor = NSColor.Clear;
        _groupTable.FocusRingType = NSFocusRingType.None; // 同上：关掉整表的焦点框
        _groupTable.ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.LastColumnOnly; // 同上：列宽跟随表格
        _groupTable.AddColumn(new NSTableColumn("c") { Width = 80, ResizingMask = NSTableColumnResizing.Autoresizing });
        _groupSource = new GroupNavSource(_groupTable, _vm.FlatTreeNodes);
        _groupSource.SelectionChanged += (_, _) =>
        {
            if (_groupSource!.Selected is { } node) _vm.SelectedGroupNode = node;
        };

        // 每次 LoadAsync 都会重建 FlatTreeNodes，两张表随之 ReloadData——重载会清掉
        // 选中态，而此时 VM 里的 SelectedSmartView 往往没变（还是「所有设备」），
        // 不会触发下面那个 PropertyChanged，高亮就补不回来。这里在重载后补一次。
        _vm.FlatTreeNodes.CollectionChanged += (_, _) =>
            NSApplication.SharedApplication.BeginInvokeOnMainThread(SyncSelectionHighlight);
        _groupTable.Menu = new NSMenu { Delegate = new GroupRowMenu(this) };
        _groupScroll = Scroll(_groupTable);
        _groupScroll.TranslatesAutoresizingMaskIntoConstraints = false;

        _addGroup = new NSButton
        {
            Image = NSImage.GetSystemSymbol("folder.badge.plus", null),
            BezelStyle = NSBezelStyle.TexturedRounded,
            ToolTip = "新建分组",
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _addGroup.Activated += (_, _) => _ = RunGroupCreateAsync();

        // 外部（VM.SelectedSmartView / SelectedGroupNode 变化）驱动这里的高亮——
        // 与第三列（ConnectionListPane）各自订阅同一个 VM，互不引用。
        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(_vm.SelectedSmartView):
                case nameof(_vm.SelectedGroupNode):
                    SyncSelectionHighlight();
                    break;
            }
        };

        // 标题行：图标 + 「连接资源」，字号比其余分区标题大一档，独占一行；
        // 「新建分组」按钮换到下一行左对齐，避免和标题挤在一起、按钮又太小不好点。
        var titleIcon = new NSImageView
        {
            Image = NSImage.GetSystemSymbol("point.3.filled.connected.trianglepath.dotted", null),
            ContentTintColor = NSColor.SecondaryLabel,
            SymbolConfiguration = NSImageSymbolConfiguration.Create(16, NSFontWeight.Medium),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var titleLabel = new NSTextField
        {
            StringValue = "连接资源",
            Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
            Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold),
            TextColor = NSColor.Label,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var titleRow = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = 7,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        titleRow.AddArrangedSubview(titleIcon);
        titleRow.AddArrangedSubview(titleLabel);

        var addGroupRow = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        addGroupRow.AddArrangedSubview(_addGroup);
        addGroupRow.AddArrangedSubview(new NSView());

        // 固定内容（标题 / 新建分组 / 智能视图 / 分组标签）用一个 NSStackView 摆好，
        // 再连同分组列表一起手动钉进一个普通 NSView——不让 NSStackView 自己当整个
        // ViewController.View。之前让「root」这个纵向 NSStackView 直接当 View 用时，
        // 分栏系统会把它的 frame 撑到整栏高度，而 NSStackView 在自身高度超出内容实际
        // 需要的高度时，摆位跟着 Distribution 走、并不保证贴顶——曾经整段内容被推到栏
        // 底部、顶上留一大片空白（真实事故）。这里改成显式约束钉顶部 + 分组列表拉伸到
        // 底部，跟 ConnectionListPane / DetailView 等其余几栏的写法保持一致，行为可预期。
        var header = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 8,
            EdgeInsets = new NSEdgeInsets(12, 16, 8, 16),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        header.AddArrangedSubview(titleRow);
        titleRow.WidthAnchor.ConstraintEqualTo(header.WidthAnchor, 1, -32).Active = true;
        header.AddArrangedSubview(addGroupRow);
        addGroupRow.WidthAnchor.ConstraintEqualTo(header.WidthAnchor, 1, -32).Active = true;
        header.AddArrangedSubview(_smartScroll);
        _smartScroll.WidthAnchor.ConstraintEqualTo(header.WidthAnchor, 1, -32).Active = true;
        var divider = Divider();
        header.AddArrangedSubview(divider);
        header.AddArrangedSubview(SectionLabel("分组"));
        // 智能视图与分隔线之间单独加宽（栈的统一间距是 8，这里给到 26）——
        // 贴着「最近连接」那行画分隔线太紧，视觉上像是黏在一起。
        header.SetCustomSpacing(26, _smartScroll);
        header.SetCustomSpacing(12, divider);

        var root = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        root.AddSubview(header);
        root.AddSubview(_groupScroll);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            header.TopAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TopAnchor),
            header.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            header.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),

            _groupScroll.TopAnchor.ConstraintEqualTo(header.BottomAnchor),
            _groupScroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 16),
            _groupScroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor, -16),
            _groupScroll.BottomAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.BottomAnchor, -10),
        });

        View = root;
    }

    /// <summary>把两张导航表的高亮同步到 VM 的当前选择：智能视图与分组互斥，
    /// 选中谁就高亮谁、另一张清空。</summary>
    private void SyncSelectionHighlight()
    {
        if (_vm.SelectedGroupNode is { } node)
        {
            _groupSource?.SelectRowFor(node);
            _smartSource?.SelectRowFor(null);
            return;
        }

        _smartSource?.SelectRowFor(_vm.SelectedSmartView);
        _groupSource?.SelectRowFor(null);
    }

    // ── 右键菜单：分组导航 ──────────────────────────────────────────

    private sealed class GroupRowMenu : NSMenuDelegate
    {
        private readonly ConnectionResourcePane _pane;
        public GroupRowMenu(ConnectionResourcePane pane) => _pane = pane;

        public override void MenuWillOpen(NSMenu menu)
        {
            menu.RemoveAllItems();
            var row = _pane._groupTable.ClickedRow;
            if (_pane._groupSource?.RowObject(row) is not { } group) return;

            menu.AddItem(Item("新建子分组…", () => _pane.RunGroupItem(_pane._vm.CreateChildGroupCommand, group)));
            if (!group.IsUngrouped)
            {
                menu.AddItem(Item("重命名…", () => _pane.RunGroupItem(_pane._vm.RenameGroupCommand, group)));
                menu.AddItem(NSMenuItem.SeparatorItem);
                menu.AddItem(Item("删除…", () => _pane.RunGroupItem(_pane._vm.DeleteGroupCommand, group)));
            }
        }
    }

    private static NSMenuItem Item(string title, Action action)
    {
        var item = new NSMenuItem(title);
        item.Activated += (_, _) => action();
        return item;
    }

    private async void RunGroupItem(CommunityToolkit.Mvvm.Input.IAsyncRelayCommand command, ConnectionGroupNodeViewModel group)
    {
        // async void：任何异常逃逸都会变成进程级崩溃，刷新也得包进来。
        try
        {
            await command.ExecuteAsync(group);
            await _vm.LoadAsync();
        }
        catch
        {
            // 命令内部已负责用户提示。
        }
    }

    private async Task RunGroupCreateAsync()
    {
        try
        {
            await _vm.CreateGroupCommand.ExecuteAsync(null);
            await _vm.LoadAsync();
        }
        catch
        {
            // 命令内部已负责用户提示。
        }
    }

    // ── helpers ─────────────────────────────────────────────────

    private static NSScrollView Scroll(NSView doc) => new()
    {
        DocumentView = doc,
        DrawsBackground = false,
        HasVerticalScroller = true,
        AutomaticallyAdjustsContentInsets = true,
    };

    private static NSView Divider() => new NSBox
    {
        BoxType = NSBoxType.NSBoxSeparator,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    private static NSTextField SectionLabel(string text) => new()
    {
        StringValue = text,
        Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
        Font = NSFont.SystemFontOfSize(11, NSFontWeight.Semibold),
        TextColor = NSColor.SecondaryLabel,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };
}
