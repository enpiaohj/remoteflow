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

        LoggingSetup.Shutdown();
    }

    public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => true;

    // ── 原生菜单栏 ───────────────────────────────────────────────

    private static void BuildMainMenu()
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

        var editItem = new NSMenuItem();
        menubar.AddItem(editItem);
        var editMenu = new NSMenu("编辑");
        editItem.Submenu = editMenu;
        editMenu.AddItem(new NSMenuItem("剪切", "x") { Action = new ObjCRuntime.Selector("cut:") });
        editMenu.AddItem(new NSMenuItem("拷贝", "c") { Action = new ObjCRuntime.Selector("copy:") });
        editMenu.AddItem(new NSMenuItem("粘贴", "v") { Action = new ObjCRuntime.Selector("paste:") });
        editMenu.AddItem(new NSMenuItem("全选", "a") { Action = new ObjCRuntime.Selector("selectAll:") });

        var windowItem = new NSMenuItem();
        menubar.AddItem(windowItem);
        var windowMenu = new NSMenu("窗口");
        windowItem.Submenu = windowMenu;
        windowMenu.AddItem(new NSMenuItem("最小化", "m") { Action = new ObjCRuntime.Selector("performMiniaturize:") });
        windowMenu.AddItem(new NSMenuItem("缩放") { Action = new ObjCRuntime.Selector("performZoom:") });
        NSApplication.SharedApplication.WindowsMenu = windowMenu;

        NSApplication.SharedApplication.MainMenu = menubar;
    }

    // ── DI ───────────────────────────────────────────────────────

    private static void InitializeDatabase(IServiceProvider services)
    {
        services.GetRequiredService<RemoteFlowDatabase>().Initialize();

        Task.Run(async () =>
        {
            await services.GetRequiredService<DefaultGroupResolver>().ResolveDefaultAsync();

            var tags = services.GetRequiredService<ITagRepository>();
            if ((await tags.GetAllAsync()).Count == 0)
            {
                foreach (var (name, color) in new[]
                         { ("生产", "#C42B1C"), ("测试", "#9D5D00"), ("开发", "#0F7B0F") })
                {
                    await tags.AddAsync(new Tag { Name = name, Color = color });
                }
            }
        }).GetAwaiter().GetResult();
    }

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
        services.AddSingleton<ISshHostKeyPolicy>(_ => new DevTrustHostKeyPolicy());
        services.AddSingleton<AppServices.SessionManager>();

        services.AddSingleton<IConnectionProvider, RemoteFlow.Protocol.Ssh.SshConnectionProvider>();
        services.AddSingleton<IConnectionProvider, RemoteFlow.Protocol.Vnc.VncConnectionProvider>();

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
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider();
    }
}
