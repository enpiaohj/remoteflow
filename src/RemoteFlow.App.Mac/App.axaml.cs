using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Infrastructure;
using RemoteFlow.Infrastructure.Data;
using RemoteFlow.Infrastructure.Logging;
using RemoteFlow.Infrastructure.Security;
using RemoteFlow.Infrastructure.Settings;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Presentation.Terminal;
using RemoteFlow.Presentation.ViewModels;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Application.Services;
using RemoteFlow.Protocol.Ssh;
using RemoteFlow.Protocol.Vnc;
using AppServices = RemoteFlow.Application.Services;

namespace RemoteFlow.App.Mac;

/// <summary>
/// macOS 客户端组合根。装配与 WPF 版 <c>App.App.xaml.cs</c> 对应的依赖，
/// 仅凭据保险库换用 Keychain、会话 Provider 暂时为 SSH + VNC（RDP 待 Phase 2）。
/// </summary>
[SupportedOSPlatform("macos")]
public partial class App : Avalonia.Application
{
    private ServiceProvider? _services;
    private ILogger<App>? _logger;
    private MainWindow? _mainWindow;

    // Composition Root 完成信号，供壳自检与后续 UI 复用。
    public static IServiceProvider Services =>
        (Current as App)?._services
        ?? throw new InvalidOperationException("服务容器尚未初始化。");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Startup += (_, _) => Start();
            desktop.ShutdownRequested += (_, e) =>
            {
                // 关闭会话：尽力而为、设上限，不让卡住的会话拖住退出。
                try
                {
                    var sessions = _services?.GetService<SessionManager>();
                    sessions?.CloseAllAsync().GetAwaiter().GetResult();
                }
                catch { }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Start()
    {
        try
        {
            var paths = new AppPaths();
            var loggerFactory = LoggingSetup.Create(paths.LogFileTemplate);

            _services = BuildServiceProvider(paths, loggerFactory);
            _logger = _services.GetRequiredService<ILogger<App>>();
            _logger.LogInformation("RemoteFlow (macOS) 启动，数据目录 {DataDirectory}", paths.DataDirectory);

            var sshProvider = (RemoteFlow.Protocol.Ssh.SshConnectionProvider)_services
                .GetServices<IConnectionProvider>()
                .First(p => p.Protocol == RemoteFlow.Core.Models.ProtocolType.Ssh);
            var vncProvider = (RemoteFlow.Protocol.Vnc.VncConnectionProvider)_services
                .GetServices<IConnectionProvider>()
                .First(p => p.Protocol == RemoteFlow.Core.Models.ProtocolType.Vnc);

            _mainWindow = new MainWindow(paths, sshProvider, vncProvider);
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = _mainWindow;
            }

            _mainWindow.Title = "RemoteFlow · 工作台装配 OK";
            _mainWindow.Show();
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "应用启动失败");
            throw;
        }
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
        services.AddSingleton<AppServices.SessionManager>();

        // 会话 Provider：SSH / VNC 现即可用，RDP 随 Phase 2 (MacRdpSession) 追加。
        services.AddSingleton<IConnectionProvider, SshConnectionProvider>();
        services.AddSingleton<IConnectionProvider, VncConnectionProvider>();

        services.AddSingleton<DefaultGroupResolver>();

        // ── UI 抽象与平台服务（macOS / Avalonia）────────────────
        services.AddSingleton<RemoteFlow.Presentation.Host.IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<RemoteFlow.Presentation.Host.IUiTimerFactory, AvaloniaUiTimerFactory>();
        services.AddSingleton<RemoteFlow.Presentation.Host.IThemeService>(_ =>
            new AvaloniaThemeService(Current as Avalonia.Application
                ?? throw new InvalidOperationException("Application 尚未初始化。")));
        services.AddSingleton<RemoteFlow.Presentation.Host.ILaunchOnStartupService,
            NoOpLaunchOnStartupService>();
        services.AddSingleton<RemoteFlow.Presentation.Services.IDialogService, AvaloniaDialogService>();

        // ── 共享 ViewModel（工作台 UI 的装配根；页面逐步接入）────
        services.AddSingleton<RemoteFlow.Presentation.ViewModels.HomePageViewModel>();
        services.AddSingleton<RemoteFlow.Presentation.ViewModels.ConnectionsPageViewModel>();
        services.AddSingleton<RemoteFlow.Presentation.ViewModels.CredentialsPageViewModel>();
        services.AddSingleton<RemoteFlow.Presentation.ViewModels.SettingsPageViewModel>();
        services.AddSingleton<RemoteFlow.Presentation.ViewModels.MainViewModel>();

        return services.BuildServiceProvider();
    }
}
