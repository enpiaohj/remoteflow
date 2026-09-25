using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RemoteFlow.App.Services;
using RemoteFlow.Presentation.ViewModels;
using RemoteFlow.App.Views;
using RemoteFlow.App.Views.Dialogs;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Infrastructure;
using RemoteFlow.Infrastructure.Data;
using RemoteFlow.Infrastructure.Logging;
using RemoteFlow.Infrastructure.Security;
using RemoteFlow.Infrastructure.Sync;
using RemoteFlow.Infrastructure.Settings;
using RemoteFlow.Protocol.Rdp;
using RemoteFlow.Protocol.Ssh;
using RemoteFlow.Protocol.Vnc;
using AppServices = RemoteFlow.Application.Services;
using RemoteFlow.Presentation.Services;

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
    /// 会话退出标记（session-state.json）。启动时读上次状态并武装 cleanExit:false，
    /// OnExit 正常收尾后再写 cleanExit:true；进程崩溃 / 被杀时 OnExit 不执行，供下次启动识别异常退出。
    /// </summary>
    private SessionStateStore? _sessionStateStore;

    /// <summary>
    /// 单实例互斥体。两个 RemoteFlow 实例同时打开同一份 SQLite 与保险库会互相争锁，
    /// 轻则启动报错，重则 WAL 状态错乱。这里保证同一用户会话只运行一个实例，
    /// 已在运行时把已有窗口带到前台。
    /// </summary>
    private static readonly Mutex SingleInstanceMutex = new(initiallyOwned: false, @"Local\RemoteFlow.SingleInstance");
    private bool _ownsMutex;

    /// <summary>进入退出流程的标记：此后界面异常处理器不再尝试弹窗。</summary>
    private bool _isShuttingDown;

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

        if (!TryAcquireSingleInstance())
        {
            ActivateExistingInstance();
            Shutdown(0);
            return;
        }

        try
        {
            var paths = ResolvePaths();
            var loggerFactory = LoggingSetup.Create(paths.LogFileTemplate);

            _services = BuildServiceProvider(paths, loggerFactory);
            _logger = _services.GetRequiredService<ILogger<App>>();

            _logger.LogInformation("RemoteFlow 启动，数据目录 {DataDirectory}", paths.DataDirectory);

            // 崩溃恢复标记：读上次退出状态 → 若异常退出写 recovery 日志 → 武装本次 cleanExit:false。
            InitializeSessionState(paths, loggerFactory);

            ApplyLanguage();
            InitializeDatabase();
            ApplyTheme();

            // 让全项目统一日期时间格式跟随用户设置（保存设置后会再次刷新）。
            DateTimeDisplay.Configure(_services.GetRequiredService<AppSettings>());

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

    // ── 单实例 ────────────────────────────────────────────────────

    private bool TryAcquireSingleInstance()
    {
        try
        {
            // 已有实例持有互斥体时立即返回 false，不阻塞等待。
            _ownsMutex = SingleInstanceMutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例崩溃时未释放互斥体——本实例接管即可。
            _ownsMutex = true;
        }

        return _ownsMutex;
    }

    /// <summary>把已在运行的实例窗口带到前台。</summary>
    private static void ActivateExistingInstance()
    {
        try
        {
            var current = System.Diagnostics.Process.GetCurrentProcess();
            var other = System.Diagnostics.Process
                .GetProcessesByName(current.ProcessName)
                .FirstOrDefault(p => p.Id != current.Id && p.MainWindowHandle != nint.Zero);

            if (other?.MainWindowHandle is { } handle && handle != nint.Zero)
            {
                NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
                NativeMethods.SetForegroundWindow(handle);
            }
        }
        catch
        {
            // 激活失败无关紧要：至少本实例不会重复启动。
        }
    }

    private static class NativeMethods
    {
        public const int SW_RESTORE = 9;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(nint hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ShowWindow(nint hWnd, int nCmdShow);
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
        services.AddSingleton<AppServices.GroupService>();
        services.AddSingleton<AppServices.CredentialService>();
        services.AddSingleton<AppServices.ConnectionSearchService>();
        services.AddSingleton<PresenceProbeService>();
        services.AddSingleton<AppServices.ImportExportService>();
        services.AddSingleton<AppServices.CredentialBackupService>();
        services.AddSingleton<LocalBackupService>();
        services.AddSingleton<LocalDataWiper>();
        services.AddCloudSync();
        services.AddSingleton<AppServices.SessionManager>();

        // ── 协议 Provider：新增协议只需在此追加一行 ───────────────
        services.AddSingleton<IConnectionProvider, RdpConnectionProvider>();
        services.AddSingleton<IConnectionProvider, SshConnectionProvider>();
        services.AddSingleton<IConnectionProvider, VncConnectionProvider>();

        // ── UI 服务 ───────────────────────────────────────────────
        services.AddSingleton<ThemeService>();
        services.AddSingleton<RemoteFlow.Presentation.Host.IThemeService>(
            sp => sp.GetRequiredService<ThemeService>());
        services.AddSingleton<RemoteFlow.Presentation.Host.IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<RemoteFlow.Presentation.Host.IUiTimerFactory, WpfUiTimerFactory>();
        services.AddSingleton<RemoteFlow.Presentation.Host.ILaunchOnStartupService, WpfLaunchOnStartupService>();
        services.AddSingleton<DefaultGroupResolver>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<ISshHostKeyPolicy, InteractiveSshHostKeyPolicy>();
        services.AddSingleton<ISessionViewFactory, SessionViewFactory>();

        // ── ViewModel ─────────────────────────────────────────────
        services.AddSingleton<HomePageViewModel>();
        services.AddSingleton<ConnectionsPageViewModel>();
        services.AddSingleton<CredentialsPageViewModel>();
        services.AddSingleton<SettingsPageViewModel>();
        services.AddSingleton<CloudSyncViewModel>();
        services.AddSingleton<MainViewModel>();

        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    private void InitializeDatabase()
    {
        var database = Services.GetRequiredService<RemoteFlowDatabase>();
        database.Initialize();

        // 种子数据走仓储的 async API。此刻在 UI 线程上，直接 .GetResult() 属于
        // sync-over-async（SQLite 的 async 目前是同步实现，暂不死锁，但不依赖这个
        // 巧合）：丢到线程池线程上跑，那里没有 DispatcherSynchronizationContext。
        Task.Run(SeedDefaultsIfEmptyAsync).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 首次运行时补齐必要的种子数据。
    /// <para>
    /// 分组：保证系统「未分组」存在；库中没有任何用户分组时创建默认分组「我的设备」
    /// （不再造 Windows / Linux 等业务含义分组，见 UI 分组优化提示词 §9 / §13）。
    /// 标签：库中没有标签时补几个常用标签，避免「新建连接」对话框的标签选择空白。
    /// 不创建任何示例连接或凭据。
    /// </para>
    /// </summary>
    private async Task SeedDefaultsIfEmptyAsync()
    {
        // 首启种子经 DefaultGroupResolver：由它决定 createIfEmpty（真·首启 true，
        // 之后 false），并在首启成功后落 DefaultGroupSeedDone 标记，避免删光分组后每次启动又复活。
        await Services.GetRequiredService<DefaultGroupResolver>().ResolveDefaultAsync();

        var tags = Services.GetRequiredService<ITagRepository>();
        if ((await tags.GetAllAsync()).Count == 0)
        {
            var defaultTags = new (string Name, string Color)[]
            {
                ("生产", "#C42B1C"),
                ("测试", "#9D5D00"),
                ("开发", "#0F7B0F")
            };

            foreach (var (name, color) in defaultTags)
            {
                await tags.AddAsync(new Tag { Name = name, Color = color });
            }
        }

        _logger?.LogInformation("种子数据检查完成");
    }

    /// <summary>
    /// 按设置中的语言配置界面文化。
    /// <para>
    /// V0.1 只内置简体中文；<see cref="AppSettings.Language"/> 已预留，
    /// 后续新增 <c>Strings.&lt;culture&gt;.resx</c> 后此处即可切换 UI 语言，无需改调用点。
    /// </para>
    /// </summary>
    private void ApplyLanguage()
    {
        var settings = Services.GetRequiredService<AppSettings>();

        var language = string.IsNullOrWhiteSpace(settings.Language) ? "zh-CN" : settings.Language;

        try
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo(language);
            System.Globalization.CultureInfo.CurrentUICulture = culture;
            System.Globalization.CultureInfo.CurrentCulture = culture;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
        }
        catch (CultureNotFoundException ex)
        {
            _logger?.LogWarning(ex, "无法识别的界面语言 {Language}，回退到简体中文", language);
        }

        // 让 WPF 文本元素继承当前文化（影响日期、数字等格式化）。
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(
                System.Windows.Markup.XmlLanguage.GetLanguage(
                    System.Globalization.CultureInfo.CurrentUICulture.IetfLanguageTag)));
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
        // 菜单动作（新建连接 / 最近连接 / 切换会话 / 设置）统一收敛到 MainViewModel，托盘不重复实现。
        _tray = new TrayService(
            window,
            Services.GetRequiredService<MainViewModel>(),
            Services.GetRequiredService<AppServices.SessionManager>(),
            Services.GetRequiredService<AppServices.ConnectionService>(),
            Services.GetRequiredService<IDialogService>(),
            Services.GetRequiredService<SettingsPageViewModel>(),
            Services.GetRequiredService<ILogger<TrayService>>());
        _tray.Initialize();

        window.Show();

        // 云同步：恢复会话并按周期后台同步（未启用云同步时内部直接空转）。
        Services.GetRequiredService<CloudSyncAutoRunner>().Start(TimeSpan.FromMinutes(3));
    }

    /// <summary>
    /// 崩溃恢复标记初始化（启动时调用）。
    /// <para>
    /// 先读上次退出状态：若上次异常退出（cleanExit=false），写一条 recovery 日志；
    /// 随后无条件把本次标记为「运行中未干净」（cleanExit:false）——若本次运行崩溃或被强杀，
    /// OnExit 不会把标记翻成 true，下次启动即可据此识别。
    /// 会话本就不跨重启持久化：这里只清标记 + 记录，不恢复任何会话；DB / TerminalAssets 的
    /// 遗留临时数据继续走各自既有自愈路径。
    /// </para>
    /// </summary>
    private void InitializeSessionState(AppPaths paths, ILoggerFactory loggerFactory)
    {
        _sessionStateStore = new SessionStateStore(
            paths.DataDirectory,
            loggerFactory.CreateLogger<SessionStateStore>());

        var previous = _sessionStateStore.Load();
        if (previous is { CleanExit: false })
        {
            // 本次不恢复任何会话（产品不跨重启持久化会话），仅标记 + recovery 日志。
            _logger?.LogInformation(
                "上次异常退出（标记于 {LastAt:o}），本次不恢复任何会话：不自动重连、不做跨重启恢复。",
                previous.LastAt);
        }

        _sessionStateStore.Save(cleanExit: false, timestamp: DateTimeOffset.Now);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 进入退出流程后，界面异常处理不再尝试弹窗（此时资源系统已开始拆除）。
        _isShuttingDown = true;

        // 退出顺序（spec §6.1）：Graceful Close All → 释放共享资源 → 容器兜底释放 →
        // ClearAllPools → 写干净退出标记 → Logging Shutdown → Mutex 释放。全程异常安全（finally 兜底）。
        try
        {
            // 1) Graceful Close All：逐会话走统一关闭模板（各有界等待）。整体放到后台线程并设 5s 上限，
            //    极端情况下（例如 RDP 控件正卡在自身的模态证书对话框上）Disconnect 会长时间占住 UI 线程，
            //    此时超时后交由进程退出兜底（force 已由会话关闭模板内含），不让整个应用卡死在关闭流程里。
            var sessions = _services?.GetService<AppServices.SessionManager>();
            if (sessions is not null)
            {
                var closed = Task.Run(() => sessions.CloseAllAsync()).Wait(TimeSpan.FromSeconds(5));
                if (!closed)
                {
                    _logger?.LogWarning("关闭会话超时，进入强制退出");
                }
            }

            // 2) 释放共享资源：托盘 → 主视图模型 → 共享 WebView2 环境。
            //    WebView2：会话清理时各 SSH 终端视图已各自 Dispose 并 Release，
            //    这里禁止后续获取并丢弃环境引用；浏览器进程由 WebView2 运行时自行退出，不按进程名强杀。
            _services?.GetService<CloudSyncAutoRunner>()?.Dispose();
            _tray?.Dispose();
            _services?.GetService<MainViewModel>()?.Dispose();
            SharedWebView2Environment.Instance.Shutdown();

            _logger?.LogInformation("RemoteFlow 已退出");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "退出清理过程中出现异常");
        }
        finally
        {
            // 3) 容器兜底释放：SessionManager 只实现 IAsyncDisposable，容器同步 Dispose 会抛异常，
            //    因此走异步释放路径（二次 CloseAll，SessionManager 幂等）；同样设上限，避免卡住的会话拖住退出。
            try
            {
                if (_services is not null)
                {
                    Task.Run(async () => await _services.DisposeAsync()).Wait(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "释放服务容器时出现异常");
            }

            // 4) 关闭连接池里所有 SQLite 连接，触发 WAL 校验点，
            //    让 -wal / -shm 文件不残留到下次启动（配合数据库层的启动重试）。
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            // 5) 干净退出标记：走到这里说明 OnExit 收尾（含上述兜底）已完成，翻成 cleanExit:true。
            //    若进程在本次运行中崩溃 / 被强杀，OnExit 不执行，启动时武装的 false 会保留 →
            //    下次启动写 recovery 日志。Save 契约「尽力而为、永不抛出」，无需再包 try/catch；
            //    即便写失败也只 LogWarning，不会阻断下方 Logging / Mutex 收尾。
            _sessionStateStore?.Save(cleanExit: true, timestamp: DateTimeOffset.Now);

            // 6) 关闭 Serilog，刷新并释放日志（须在 Save 之后，Save 失败还能记日志）。
            LoggingSetup.Shutdown();

            // 7) 释放单实例互斥体。
            if (_ownsMutex)
            {
                SingleInstanceMutex.ReleaseMutex();
            }
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

        // 退出流程中资源系统正在拆除，此时再弹窗会二次抛异常；仅记录即可。
        if (_isShuttingDown)
        {
            return;
        }

        MessageDialog.ShowMessage(
            MainWindow,
            "发生错误",
            $"操作未能完成：{e.Exception.Message}\n\n详细信息已写入日志，应用可以继续使用。",
            DialogKind.Error);
    }
}
