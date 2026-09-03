using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RemoteFlow.App.Services;
using RemoteFlow.App.ViewModels;
using RemoteFlow.App.Views;
using RemoteFlow.App.Views.Dialogs;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Infrastructure;
using RemoteFlow.Infrastructure.Data;
using RemoteFlow.Infrastructure.Logging;
using RemoteFlow.Infrastructure.Security;
using RemoteFlow.Infrastructure.Settings;
using RemoteFlow.Protocol.Rdp;
using RemoteFlow.Protocol.Ssh;
using RemoteFlow.Protocol.Vnc;
using AppServices = RemoteFlow.Application.Services;

namespace RemoteFlow.App;

/// <summary>
/// 应用入口与组合根（Composition Root）。
/// <para>
/// 所有依赖在此处一次性装配。分层依赖方向始终是
/// UI → Application → Core ← Infrastructure / Protocol.*，
/// UI 不直接引用任何协议实现细节。
/// </para>
/// </summary>
public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;
    private ILogger<App>? _logger;
    private TrayService? _tray;

    /// <summary>
    /// 供 XAML 创建的视图按需解析依赖。
    /// 视图由 WPF 实例化、无法构造注入，这是 WPF 下的惯用折中。
    /// </summary>
    public static IServiceProvider Services =>
        ((App)Current)._services ?? throw new InvalidOperationException("服务容器尚未初始化。");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 异常处理必须最先装好，否则启动过程中的失败会以无提示崩溃收场。
        RegisterGlobalExceptionHandlers();

        try
        {
            var paths = ResolvePaths();
            var loggerFactory = LoggingSetup.Create(paths.LogFileTemplate);

            _services = BuildServiceProvider(paths, loggerFactory);
            _logger = _services.GetRequiredService<ILogger<App>>();

            _logger.LogInformation("RemoteFlow 启动，数据目录 {DataDirectory}", paths.DataDirectory);

            InitializeDatabase();
            ApplyTheme();

            ShowMainWindow();
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "应用启动失败");

            MessageBox.Show(
                $"RemoteFlow 启动失败：\n\n{ex.Message}",
                "启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
        }
    }

    /// <summary>
    /// 解析数据目录。设置文件本身也在数据目录里，因此先用默认位置读一次设置，
    /// 若用户指定了自定义目录再据此重建路径。
    /// </summary>
    private static AppPaths ResolvePaths()
    {
        var defaultPaths = new AppPaths();

        using var bootstrapLoggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
        var bootstrapStore = new JsonSettingsStore(
            defaultPaths.SettingsPath,
            bootstrapLoggerFactory.CreateLogger<JsonSettingsStore>());

        var settings = bootstrapStore.Load();

        return string.IsNullOrWhiteSpace(settings.DataDirectory)
            ? defaultPaths
            : new AppPaths(settings.DataDirectory);
    }

    private static ServiceProvider BuildServiceProvider(AppPaths paths, ILoggerFactory loggerFactory)
    {
        var services = new ServiceCollection();

        services.AddSingleton(loggerFactory);
        services.AddLogging();
        services.AddSingleton(paths);

        // ── 设置 ──────────────────────────────────────────────────
        services.AddSingleton(sp => new JsonSettingsStore(
            paths.SettingsPath, sp.GetRequiredService<ILogger<JsonSettingsStore>>()));
        services.AddSingleton(sp => sp.GetRequiredService<JsonSettingsStore>().Load());

        // ── 持久化 ────────────────────────────────────────────────
        services.AddSingleton(sp => new RemoteFlowDatabase(
            paths.DatabasePath, sp.GetRequiredService<ILogger<RemoteFlowDatabase>>()));

        services.AddSingleton<IConnectionRepository, SqliteConnectionRepository>();
        services.AddSingleton<IGroupRepository, SqliteGroupRepository>();
        services.AddSingleton<ITagRepository, SqliteTagRepository>();
        services.AddSingleton<ICredentialRepository, SqliteCredentialRepository>();
        services.AddSingleton<IHistoryRepository, SqliteHistoryRepository>();
        services.AddSingleton<IHostKeyRepository, SqliteHostKeyRepository>();

        // ── 凭据保险库：全应用唯一接触明文 Secret 的组件 ─────────
        services.AddSingleton<ICredentialVault>(sp => new DpapiCredentialVault(
            paths.VaultPath, sp.GetRequiredService<ILogger<DpapiCredentialVault>>()));

        // ── 应用服务 ──────────────────────────────────────────────
        services.AddSingleton<AppServices.ConnectionService>();
        services.AddSingleton<AppServices.CredentialService>();
        services.AddSingleton<AppServices.ConnectionSearchService>();
        services.AddSingleton<AppServices.ImportExportService>();
        services.AddSingleton<AppServices.SessionManager>();

        // ── 协议 Provider：新增协议只需在此追加一行 ───────────────
        services.AddSingleton<IConnectionProvider, RdpConnectionProvider>();
        services.AddSingleton<IConnectionProvider, SshConnectionProvider>();
        services.AddSingleton<IConnectionProvider, VncConnectionProvider>();

        // ── UI 服务 ───────────────────────────────────────────────
        services.AddSingleton<ThemeService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<ISshHostKeyPolicy, InteractiveSshHostKeyPolicy>();
        services.AddSingleton<ISessionViewFactory, SessionViewFactory>();

        // ── ViewModel ─────────────────────────────────────────────
        services.AddSingleton<HomePageViewModel>();
        services.AddSingleton<ConnectionsPageViewModel>();
        services.AddSingleton<CredentialsPageViewModel>();
        services.AddSingleton<ImportExportPageViewModel>();
        services.AddSingleton<SettingsPageViewModel>();
        services.AddSingleton<MainViewModel>();

        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    private void InitializeDatabase()
    {
        var database = Services.GetRequiredService<RemoteFlowDatabase>();
        database.Initialize();

        SeedDefaultsIfEmpty();
    }

    /// <summary>
    /// 首次运行时创建几个常用分组与标签。
    /// <para>
    /// 目的不是塞演示数据，而是让「新建连接」对话框的分组/标签下拉不至于空白，
    /// 降低首次使用的门槛。不创建任何示例连接或凭据。
    /// </para>
    /// </summary>
    private void SeedDefaultsIfEmpty()
    {
        var groups = Services.GetRequiredService<IGroupRepository>();
        var tags = Services.GetRequiredService<ITagRepository>();

        if (groups.GetAllAsync().GetAwaiter().GetResult().Count > 0)
        {
            return;
        }

        var defaultGroups = new[] { "Windows", "Linux", "macOS", "网络设备" };
        for (var i = 0; i < defaultGroups.Length; i++)
        {
            groups.AddAsync(new ConnectionGroup { Name = defaultGroups[i], SortOrder = i })
                .GetAwaiter().GetResult();
        }

        var defaultTags = new (string Name, string Color)[]
        {
            ("生产", "#C42B1C"),
            ("测试", "#9D5D00"),
            ("开发", "#0F7B0F")
        };

        foreach (var (name, color) in defaultTags)
        {
            tags.AddAsync(new Tag { Name = name, Color = color }).GetAwaiter().GetResult();
        }

        _logger?.LogInformation("已创建默认分组与标签");
    }

    private void ApplyTheme()
    {
        var theme = Services.GetRequiredService<ThemeService>();
        var settings = Services.GetRequiredService<AppSettings>();

        theme.Apply(settings.Theme);
        theme.StartListeningToSystemTheme();
    }

    private void ShowMainWindow()
    {
        var window = Services.GetRequiredService<MainWindow>();
        MainWindow = window;

        // 托盘图标让「关闭即最小化」有一个明确的退出入口。
        _tray = new TrayService(window, Services.GetRequiredService<AppServices.SessionManager>());
        _tray.Initialize();

        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // 退出前关闭全部会话，确保 ActiveX 控件、SSH 连接、VNC 线程被完整释放。
            _services?.GetService<AppServices.SessionManager>()?.CloseAllAsync().GetAwaiter().GetResult();

            _tray?.Dispose();
            _services?.GetService<MainViewModel>()?.Dispose();

            _logger?.LogInformation("RemoteFlow 已退出");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "退出清理过程中出现异常");
        }
        finally
        {
            _services?.Dispose();
            LoggingSetup.Shutdown();
        }

        base.OnExit(e);
    }

    // ── 全局异常处理 ──────────────────────────────────────────────

    private void RegisterGlobalExceptionHandlers()
    {
        // UI 线程异常：记录并提示，尽量让应用继续可用而不是直接崩掉。
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 未观察的 Task 异常：只记录，不影响运行。
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError(args.Exception, "未观察的后台任务异常");
            args.SetObserved();
        };

        // 非 UI 线程的致命异常：只能记录，进程随后会终止。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                _logger?.LogCritical(exception, "未处理的致命异常，进程即将终止");
            }

            LoggingSetup.Shutdown();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "界面线程发生未处理异常");

        // 标记为已处理，避免单个界面错误导致整个应用退出、连带断开所有会话。
        e.Handled = true;

        MessageDialog.ShowMessage(
            MainWindow,
            "发生错误",
            $"操作未能完成：{e.Exception.Message}\n\n详细信息已写入日志，应用可以继续使用。",
            DialogKind.Error);
    }
}
