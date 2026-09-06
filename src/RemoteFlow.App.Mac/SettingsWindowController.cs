using AppKit;
using CoreGraphics;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 偏好设置窗口（⌘,）—— macOS 惯例的独立窗口 + 工具栏分页。
/// 常规页绑共享 <see cref="SettingsPageViewModel"/>；RDP / SSH / VNC / 安全 / 数据页 8.C 补。
/// </summary>
public sealed class SettingsWindowController : NSWindowController
{
    private readonly SettingsPageViewModel _vm;
    private readonly NSTabView _tabs = new() { TabViewType = NSTabViewType.NSNoTabsNoBorder };

    public SettingsWindowController(SettingsPageViewModel vm)
        : base(NewWindow())
    {
        _vm = vm;
        Window.Title = "设置";

        _tabs.Add(new NSTabViewItem { Label = "常规", View = BuildGeneral() });
        _tabs.Add(new NSTabViewItem { Label = "RDP", View = Placeholder("RDP 设置") });
        _tabs.Add(new NSTabViewItem { Label = "SSH", View = Placeholder("SSH 设置") });
        _tabs.Add(new NSTabViewItem { Label = "VNC", View = Placeholder("VNC 设置") });
        _tabs.Add(new NSTabViewItem { Label = "安全", View = Placeholder("安全设置") });
        _tabs.Add(new NSTabViewItem { Label = "数据与备份", View = Placeholder("数据与备份") });

        _tabs.TranslatesAutoresizingMaskIntoConstraints = false;
        Window.ContentView = _tabs;

        var toolbar = new NSToolbar("rf.settings")
        {
            Delegate = new TabToolbar(this),
            DisplayMode = NSToolbarDisplayMode.IconAndLabel,
            AllowsUserCustomization = false,
        };
        Window.Toolbar = toolbar;
        Window.ToolbarStyle = NSWindowToolbarStyle.Preference;

        _ = _vm.LoadGroupsAsync();
    }

    private static NSWindow NewWindow() => new(
        new CGRect(0, 0, 560, 420),
        NSWindowStyle.Titled | NSWindowStyle.Closable,
        NSBackingStore.Buffered,
        deferCreation: false);

    private void Select(int index) => _tabs.SelectAt(index);

    // ── 常规页 ──────────────────────────────────────────────────

    private NSView BuildGeneral()
    {
        var grid = new NSGridView
        {
            RowSpacing = 16,
            ColumnSpacing = 16,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        // 主题
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
        grid.AddRow(new NSView[] { Caption("主题"), theme });

        // 开机自启
        var startup = new NSButton { Title = "登录时自动启动 RemoteFlow" };
        startup.SetButtonType(NSButtonType.Switch);
        startup.State = _vm.LaunchOnStartup ? NSCellStateValue.On : NSCellStateValue.Off;
        startup.Activated += (_, _) => _vm.LaunchOnStartup = startup.State == NSCellStateValue.On;
        grid.AddRow(new NSView[] { Caption("启动"), startup });

        // 关闭行为
        var minimize = new NSButton { Title = "关闭窗口时最小化到菜单栏而非退出" };
        minimize.SetButtonType(NSButtonType.Switch);
        minimize.State = _vm.MinimizeToTrayOnClose ? NSCellStateValue.On : NSCellStateValue.Off;
        minimize.Activated += (_, _) => _vm.MinimizeToTrayOnClose = minimize.State == NSCellStateValue.On;
        grid.AddRow(new NSView[] { Caption("关闭"), minimize });

        // 默认落地页
        var landing = new NSPopUpButton(new CGRect(0, 0, 160, 24), pullsDown: false);
        landing.AddItems(new[] { "首页", "我的连接", "收藏", "最近连接", "凭据" });
        landing.SelectItem((int)_vm.DefaultLandingPage);
        landing.Activated += (_, _) => _vm.DefaultLandingPage = (LandingPage)(int)landing.IndexOfSelectedItem;
        grid.AddRow(new NSView[] { Caption("默认页面"), landing });


        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        host.AddSubview(grid);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            grid.TopAnchor.ConstraintEqualTo(host.TopAnchor, 28),
            grid.CenterXAnchor.ConstraintEqualTo(host.CenterXAnchor),
        });
        return host;
    }

    private static NSView Placeholder(string text)
    {
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        var label = new NSTextField
        {
            StringValue = $"{text}（Phase 8.C 补齐）",
            Bordered = false,
            Editable = false,
            DrawsBackground = false,
            TextColor = NSColor.SecondaryLabel,
            Font = NSFont.SystemFontOfSize(13),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        host.AddSubview(label);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            label.CenterXAnchor.ConstraintEqualTo(host.CenterXAnchor),
            label.CenterYAnchor.ConstraintEqualTo(host.CenterYAnchor),
        });
        return host;
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

    // ── 工具栏分页 ──────────────────────────────────────────────

    private sealed class TabToolbar : NSToolbarDelegate
    {
        private static readonly (string Id, string Title, string Symbol)[] Tabs =
        {
            ("t.general", "常规", "gearshape"),
            ("t.rdp", "RDP", "display"),
            ("t.ssh", "SSH", "terminal"),
            ("t.vnc", "VNC", "rectangle.on.rectangle"),
            ("t.security", "安全", "lock.shield"),
            ("t.data", "数据与备份", "externaldrive"),
        };

        private readonly SettingsWindowController _o;
        public TabToolbar(SettingsWindowController o) => _o = o;

        public override string[] DefaultItemIdentifiers(NSToolbar t) => Tabs.Select(x => x.Id).ToArray();
        public override string[] AllowedItemIdentifiers(NSToolbar t) => DefaultItemIdentifiers(t);
        public override string[] SelectableItemIdentifiers(NSToolbar t) => DefaultItemIdentifiers(t);

        public override NSToolbarItem WillInsertItem(NSToolbar toolbar, string id, bool willInsert)
        {
            var idx = Array.FindIndex(Tabs, x => x.Id == id);
            var (_, title, symbol) = Tabs[idx];
            var item = new NSToolbarItem(id)
            {
                Label = title,
                Image = NSImage.GetSystemSymbol(symbol, null),
            };
            item.Activated += (_, _) => _o.Select(idx);
            return item;
        }
    }
}
