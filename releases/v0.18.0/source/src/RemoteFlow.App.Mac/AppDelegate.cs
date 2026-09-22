using AppKit;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RemoteFlow.App.Mac.Host;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Infrastructure;
using RemoteFlow.Infrastructure.Data;
using RemoteFlow.Infrastructure.Logging;
using RemoteFlow.Infrastructure.Security;
using RemoteFlow.Infrastructure.Sync;
using RemoteFlow.Infrastructure.Settings;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Presentation.ViewModels;
using AppServices = RemoteFlow.Application.Services;

namespace RemoteFlow.App.Mac;

/// <summary>
/// macOS 客户端入口 + 组合根。DI 图与 WPF 版 <c>App.xaml.cs</c> 对应，
/// 平台服务换 AppKit 实现，凭据保险库用 Keychain。
/// </summary>
[Register("AppDelegate")]
public sealed class AppDelegate : NSApplicationDelegate
{
    private ServiceProvider? _services;
    private ILogger<AppDelegate>? _logger;
    private MainWindowController? _mainWindow;

    /// <summary>「显示 / 隐藏连接列表」菜单项 —— 首页 / 凭据页无列表列时由主窗口禁用。</summary>
    internal NSMenuItem? ToggleListMenuItem { get; private set; }

    /// <summary>「显示 / 隐藏边栏」菜单项 —— 随侧栏折叠状态切换标题（macOS HIG）。</summary>
    private NSMenuItem? _toggleSidebarMenuItem;

    /// <summary>「进入 / 退出全屏」菜单项 —— 随窗口全屏状态切换标题（macOS HIG）。</summary>
    private NSMenuItem? _screenFullScreenMenuItem;

    public override void DidFinishLaunching(NSNotification notification)
    {
        BuildMainMenu();

        try
        {
            var paths = new AppPaths();
            var loggerFactory = LoggingSetup.Create(paths.LogFileTemplate);

            _services = BuildServiceProvider(paths, loggerFactory);
            _logger = _services.GetRequiredService<ILogger<AppDelegate>>();
            _logger.LogInformation("RemoteFlow (macOS) 启动，数据目录 {DataDirectory}", paths.DataDirectory);

            InitializeDatabase(_services);

            _mainWindow = new MainWindowController(_services);
            _mainWindow.Window.MakeKeyAndOrderFront(null);
            NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);

            SeedTagsInBackground(_services);
            _services.GetRequiredService<CloudSyncAutoRunner>().Start(TimeSpan.FromMinutes(3));
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "应用启动失败");
            new NSAlert
            {
                MessageText = "RemoteFlow 启动失败",
                InformativeText = ex.Message,
                AlertStyle = NSAlertStyle.Critical,
            }.RunModal();
            NSApplication.SharedApplication.Terminate(this);
        }
    }

    public override void WillTerminate(NSNotification notification)
    {
        try
        {
            _services?.GetService<AppServices.SessionManager>()?.CloseAllAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // 退出清理尽力而为。
        }

        _services?.GetService<CloudSyncAutoRunner>()?.Dispose();
        LoggingSetup.Shutdown();
    }

    public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => true;

    // ── 原生菜单栏 ───────────────────────────────────────────────

    private void BuildMainMenu()
    {
        var menubar = new NSMenu();

        var appItem = new NSMenuItem();
        menubar.AddItem(appItem);
        var appMenu = new NSMenu();
        appItem.Submenu = appMenu;
        appMenu.AddItem(new NSMenuItem("关于 RemoteFlow", (_, _) =>
            new NSAlert { MessageText = "RemoteFlow", InformativeText = "统一远程连接工作台 · macOS" }.RunModal()));
        appMenu.AddItem(NSMenuItem.SeparatorItem);
        appMenu.AddItem(new NSMenuItem("设置…", ",", (_, _) =>
            (NSApplication.SharedApplication.Delegate as AppDelegate)?._mainWindow?.OpenSettings()));
        appMenu.AddItem(NSMenuItem.SeparatorItem);
        appMenu.AddItem(new NSMenuItem("退出 RemoteFlow", "q",
            (_, _) => NSApplication.SharedApplication.Terminate(null)));

        var fileItem = new NSMenuItem();
        menubar.AddItem(fileItem);
        var fileMenu = new NSMenu("文件");
        fileItem.Submenu = fileMenu;
        fileMenu.AddItem(new NSMenuItem("新建连接…", "n", (_, _) =>
            (NSApplication.SharedApplication.Delegate as AppDelegate)?._mainWindow?.BeginNewConnection()));
        fileMenu.AddItem(NSMenuItem.SeparatorItem);
        fileMenu.AddItem(new NSMenuItem("断开会话", "w", (_, _) =>
            (NSApplication.SharedApplication.Delegate as AppDelegate)?._mainWindow?.DisconnectCurrentSession())
        {
            KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ShiftKeyMask,
        });

        var editItem = new NSMenuItem();
        menubar.AddItem(editItem);
        var editMenu = new NSMenu("编辑");
        editItem.Submenu = editMenu;
        editMenu.AddItem(new NSMenuItem("剪切", "x") { Action = new ObjCRuntime.Selector("cut:") });
        editMenu.AddItem(new NSMenuItem("拷贝", "c") { Action = new ObjCRuntime.Selector("copy:") });
        editMenu.AddItem(new NSMenuItem("粘贴", "v") { Action = new ObjCRuntime.Selector("paste:") });
        editMenu.AddItem(new NSMenuItem("全选", "a") { Action = new ObjCRuntime.Selector("selectAll:") });

        var viewItem = new NSMenuItem();
        menubar.AddItem(viewItem);
        var viewMenu = new NSMenu("显示");
        viewItem.Submenu = viewMenu;
        viewMenu.Delegate = new ViewMenuDelegate(this); // 菜单打开前刷新「显示」类标题
        _toggleSidebarMenuItem = new NSMenuItem("显示 / 隐藏边栏", "s", (_, _) =>
            NSApplication.SharedApplication.SendAction(
                new ObjCRuntime.Selector("toggleSidebar:"), null, NSApplication.SharedApplication))
        {
            KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask,
        };
        viewMenu.AddItem(_toggleSidebarMenuItem);
        viewMenu.AutoEnablesItems = false;
        ToggleListMenuItem = new NSMenuItem("显示 / 隐藏连接列表", "l", (_, _) =>
            (NSApplication.SharedApplication.Delegate as AppDelegate)?._mainWindow?.ToggleListPane())
        {
            KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask,
        };
        viewMenu.AddItem(ToggleListMenuItem);
        viewMenu.AddItem(NSMenuItem.SeparatorItem);
        _screenFullScreenMenuItem = new NSMenuItem("进入 / 退出全屏", "f", (_, _) =>
            (NSApplication.SharedApplication.Delegate as AppDelegate)?._mainWindow?.ToggleScreenFullScreen())
        {
            KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ControlKeyMask,
        };
        viewMenu.AddItem(_screenFullScreenMenuItem);

        var windowItem = new NSMenuItem();
        menubar.AddItem(windowItem);
        var windowMenu = new NSMenu("窗口");
        windowItem.Submenu = windowMenu;
        windowMenu.AddItem(new NSMenuItem("最小化", "m") { Action = new ObjCRuntime.Selector("performMiniaturize:") });
        windowMenu.AddItem(new NSMenuItem("缩放") { Action = new ObjCRuntime.Selector("performZoom:") });
        NSApplication.SharedApplication.WindowsMenu = windowMenu;

        NSApplication.SharedApplication.MainMenu = menubar;
    }

    /// <summary>「显示」菜单每次打开前，同步边栏 / 连接列表 / 全屏三项的标题与启用态（macOS HIG：
    /// 这些菜单项应随状态切换，而不是写死的「A / B」。状态源在主窗口，这里只做读取与设置。</summary>
    private void RefreshViewMenuTitles()
    {
        if (_mainWindow is not { } w)
        {
            return;
        }

        if (_toggleSidebarMenuItem is { } sidebar)
        {
            sidebar.Title = w.IsSidebarCollapsed ? "显示边栏" : "隐藏边栏";
        }

        if (_screenFullScreenMenuItem is { } full)
        {
            full.Title = w.IsNativeFullScreen ? "退出全屏" : "进入全屏";
        }

        if (ToggleListMenuItem is { } list)
        {
            // 首页 / 凭据页无列表列：禁用；否则按列表列是否折叠切换标题。
            list.Enabled = w.IsListApplicable;
            list.Title = w.IsListVisible ? "隐藏连接列表" : "显示连接列表";
        }
    }

    /// <summary>「显示」菜单的 delegate —— 打开前触发标题刷新。</summary>
    private sealed class ViewMenuDelegate(AppDelegate owner) : NSMenuDelegate
    {
        public override void MenuWillOpen(NSMenu menu) => owner.RefreshViewMenuTitles();
    }

    // ── DI ───────────────────────────────────────────────────────

    private static void InitializeDatabase(IServiceProvider services)
    {
        // 迁移 + 默认分组必须在首个页面渲染前就绪（连接页要用）。两步都很快。
        // 经 Task.Run 隔开：ResolveDefaultAsync 内部 await 不带 ConfigureAwait(false)，
        // 直接在主线程 .GetResult() 会撞上 AppKit 同步上下文死锁。
        services.GetRequiredService<RemoteFlowDatabase>().Initialize();
        Task.Run(() => services.GetRequiredService<DefaultGroupResolver>().ResolveDefaultAsync())
            .GetAwaiter().GetResult();
    }

    /// <summary>预置标签（生产 / 测试 / 开发）没有任何启动路径依赖，挪到窗口显示后再补。</summary>
    private static void SeedTagsInBackground(IServiceProvider services)
        => Task.Run(async () =>
        {
            try
            {
                var tags = services.GetRequiredService<ITagRepository>();
                if ((await tags.GetAllAsync()).Count == 0)
                {
                    foreach (var (name, color) in new[]
                             { ("生产", "#C42B1C"), ("测试", "#9D5D00"), ("开发", "#0F7B0F") })
                    {
                        await tags.AddAsync(new Tag { Name = name, Color = color });
                    }
                }
            }
            catch
            {
                // 预置标签补不上不影响使用，下次启动再试。
            }
        });

    private static ServiceProvider BuildServiceProvider(AppPaths paths, ILoggerFactory loggerFactory)
    {
        var services = new ServiceCollection();

        services.AddSingleton(loggerFactory);
        services.AddLogging();
        services.AddSingleton(paths);

        services.AddSingleton(sp => new JsonSettingsStore(
            paths.SettingsPath, sp.GetRequiredService<ILogger<JsonSettingsStore>>()));
        services.AddSingleton(sp => sp.GetRequiredService<JsonSettingsStore>().Load());

        services.AddSingleton(sp => new RemoteFlowDatabase(
            paths.DatabasePath, sp.GetRequiredService<ILogger<RemoteFlowDatabase>>()));

        services.AddSingleton<IConnectionRepository, SqliteConnectionRepository>();
        services.AddSingleton<IGroupRepository, SqliteGroupRepository>();
        services.AddSingleton<ITagRepository, SqliteTagRepository>();
        services.AddSingleton<ICredentialRepository, SqliteCredentialRepository>();
        services.AddSingleton<IHistoryRepository, SqliteHistoryRepository>();
        services.AddSingleton<IHostKeyRepository, SqliteHostKeyRepository>();

        services.AddSingleton<ICredentialVault>(sp => new KeychainCredentialVault(
            sp.GetRequiredService<ILogger<KeychainCredentialVault>>()));

        services.AddSingleton<AppServices.ConnectionService>();
        services.AddSingleton<AppServices.GroupService>();
        services.AddSingleton<AppServices.CredentialService>();
        services.AddSingleton<AppServices.ConnectionSearchService>();
        services.AddSingleton<AppServices.ImportExportService>();
        services.AddSingleton<AppServices.CredentialBackupService>();
        services.AddSingleton<LocalBackupService>();
        services.AddSingleton<LocalDataWiper>();
        services.AddCloudSync();
        services.AddSingleton<ISshHostKeyPolicy, RemoteFlow.Presentation.Services.InteractiveSshHostKeyPolicy>();
        services.AddSingleton<AppServices.SessionManager>();

        services.AddSingleton<IConnectionProvider, RemoteFlow.Protocol.Ssh.SshConnectionProvider>();
        services.AddSingleton<IConnectionProvider, RemoteFlow.Protocol.Vnc.VncConnectionProvider>();
        services.AddSingleton<IConnectionProvider, RemoteFlow.Protocol.Rdp.Mac.RdpConnectionProvider>();

        services.AddSingleton<DefaultGroupResolver>();

        // 平台服务（AppKit）
        services.AddSingleton<IUiDispatcher, AppKitUiDispatcher>();
        services.AddSingleton<IUiTimerFactory, AppKitUiTimerFactory>();
        services.AddSingleton<IThemeService, AppKitThemeService>();
        services.AddSingleton<ILaunchOnStartupService, NoOpLaunchOnStartupService>();
        services.AddSingleton<IDialogService, AppKitDialogService>();

        // 共享 ViewModel
        services.AddSingleton<HomePageViewModel>();
        services.AddSingleton<ConnectionsPageViewModel>();
        services.AddSingleton<CredentialsPageViewModel>();
        services.AddSingleton<SettingsPageViewModel>();
        services.AddSingleton<CloudSyncViewModel>();
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider();
    }
}
