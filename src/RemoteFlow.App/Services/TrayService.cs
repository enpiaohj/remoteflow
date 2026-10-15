using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using RemoteFlow.Presentation.ViewModels;
using RemoteFlow.App.Converters;
using RemoteFlow.App.Views;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Presentation.Services;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfSeparator = System.Windows.Controls.Separator;

namespace RemoteFlow.App.Services;

/// <summary>
/// 系统托盘图标与右键结构化菜单。
/// <para>
/// 菜单用应用自己的 WPF 菜单样式（与应用内右键菜单同一套模板、图标与配色，跟随深浅色主题），
/// 而不是 WinForms 的系统默认菜单；在托盘图标右键抬起时于光标处弹出。
/// 菜单在每次打开时整树重建，保证「最近连接 / 活动会话」等动态数据不过期；
/// 会话状态一律实时读 <see cref="SessionManager"/> 快照，托盘不单独缓存连接状态。
/// 当「关闭窗口时最小化到通知区域」开启时，托盘是用户真正退出应用的入口；
/// 同时也让后台仍有会话在运行这件事可见，而不是悄无声息地驻留。
/// </para>
/// </summary>
public sealed class TrayService : IDisposable
{
    /// <summary>「最近连接」子菜单最多展示的条数。</summary>
    private const int RecentConnectionLimit = 5;
    private const int SwRestore = 9;

    private readonly MainWindow _window;
    private readonly MainViewModel _mainViewModel;
    private readonly SessionManager _sessions;
    private readonly ConnectionService _connections;
    private readonly IDialogService _dialogs;
    private readonly SettingsPageViewModel _settingsPage;
    private readonly ILogger<TrayService> _logger;

    private NotifyIcon? _notifyIcon;
    private WpfContextMenu? _menu;
    private readonly System.Windows.Threading.DispatcherTimer _clickTimer = new();

    /// <summary>单击去抖标记：单击不立即动作，等 DoubleClickTime 内是否跟来双击。</summary>
    private bool _singleClickPending;

    /// <summary>
    /// 双击后忽略 MouseClick 的截止时刻（<see cref="Environment.TickCount64"/>，毫秒）。
    /// NotifyIcon 的 MouseClick 在按钮抬起时触发，因此双击的第二下「抬起」会在
    /// <see cref="OnNotifyIconMouseDoubleClick"/> 之后又触发一次 MouseClick；
    /// 不吞掉它就会重新武装去抖定时器，把双击刚 RestoreWindow 的窗口再 Toggle 隐藏
    /// （表现为「双击显示了又隐藏」，窗口状态隔次翻转）。
    /// </summary>
    private long _suppressClickUntilTick;

    private bool _disposed;

    public TrayService(
        MainWindow window,
        MainViewModel mainViewModel,
        SessionManager sessions,
        ConnectionService connections,
        IDialogService dialogs,
        SettingsPageViewModel settingsPage,
        ILogger<TrayService> logger)
    {
        _window = window;
        _mainViewModel = mainViewModel;
        _sessions = sessions;
        _connections = connections;
        _dialogs = dialogs;
        _settingsPage = settingsPage;
        _logger = logger;
    }

    public void Initialize()
    {
        var notifyIcon = new NotifyIcon
        {
            Icon = LoadApplicationIcon(),
            Visible = true,
            Text = $"RemoteFlow {_mainViewModel.AppVersion}"
        };
        _notifyIcon = notifyIcon;

        // 右键抬起时弹出菜单；每次打开都整树重建：最近连接读库、活动会话读 SessionManager 实时快照。
        notifyIcon.MouseUp += OnNotifyIconMouseUp;
        UpdateTooltip();

        // 会话集合变化（创建 / 任意状态跳变 / 移除）都会改变提示文字；
        // 事件可在协议后台线程触发，UpdateTooltip 内部 marshal 回 UI 线程再更新。
        _sessions.SessionsChanged += (_, _) => UpdateTooltip();

        // 鼠标：单击显隐，双击打开并置前。单击先经双击时间窗去抖，避免双击的第一击把窗口隐藏。
        _clickTimer.Tick += OnSingleClickTimerTick;
        notifyIcon.MouseClick += OnNotifyIconMouseClick;
        notifyIcon.MouseDoubleClick += OnNotifyIconMouseDoubleClick;
    }

    // ── 菜单弹出与整树重建 ───────────────────────────────────────

    private void OnNotifyIconMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            ShowMenu();
        }
    }

    private void ShowMenu()
    {
        if (_disposed)
        {
            return;
        }

        if (_menu is { IsOpen: true } open)
        {
            open.IsOpen = false;
        }

        var menu = BuildMenu();
        _menu = menu;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;

        // 托盘菜单不属于任何已激活的窗口：把菜单自己的弹出窗口设为前台，
        // 这样点击菜单以外的任何位置、或按 Esc 都能正常关闭它。
        if (PresentationSource.FromVisual(menu) is HwndSource source)
        {
            SetForegroundWindow(source.Handle);
        }
    }

    /// <summary>
    /// 分组约定与应用内右键菜单一致：窗口 → 连接与会话 → 设置类 → 退出。
    /// </summary>
    private WpfContextMenu BuildMenu()
    {
        var menu = new WpfContextMenu();

        var activeSessions = _sessions.ActiveSessions
            .OrderBy(s => s.Profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // 1) 打开 / 隐藏（按主窗口状态动态文案）。
        var shown = IsMainWindowShown();
        menu.Items.Add(Item(shown ? "隐藏 RemoteFlow" : "打开 RemoteFlow", shown ? "Ui.Hide" : "Ui.Reveal", ToggleMainWindow));

        menu.Items.Add(new WpfSeparator());

        // 2) 连接与会话：新建连接 ▸ / 最近连接 ▸ / 活动会话（N）▸。
        var newConnection = Item("新建连接", "Ui.Add");
        newConnection.Items.Add(Item("RDP 连接", UiIconResources.ProtocolKey(ProtocolType.Rdp), () => OpenNewConnection(ProtocolType.Rdp)));
        newConnection.Items.Add(Item("SSH 连接", UiIconResources.ProtocolKey(ProtocolType.Ssh), () => OpenNewConnection(ProtocolType.Ssh)));
        newConnection.Items.Add(Item("VNC 连接", UiIconResources.ProtocolKey(ProtocolType.Vnc), () => OpenNewConnection(ProtocolType.Vnc)));
        menu.Items.Add(newConnection);
        menu.Items.Add(BuildRecentMenu());
        menu.Items.Add(BuildActiveSessionsMenu(activeSessions));

        menu.Items.Add(new WpfSeparator());

        // 3) 设置类：设置 / 开机启动（勾选反映当前设置）/ 检查更新（有更新实现时）。
        menu.Items.Add(Item("设置", "Ui.Settings", OpenSettings));

        var startup = Item("开机启动", null, ToggleLaunchOnStartup);
        startup.IsCheckable = true;
        startup.IsChecked = _settingsPage.LaunchOnStartup;
        menu.Items.Add(startup);

        if (_mainViewModel.Update is { } update)
        {
            var label = update.IsReadyToRestart ? "重启以完成更新" : update.HasUpdate ? "有新版本可用…" : "检查更新";
            menu.Items.Add(Item(label, "Ui.Refresh", () => OpenUpdates(update)));
        }

        menu.Items.Add(new WpfSeparator());

        // 4) 退出。
        menu.Items.Add(Item("退出 RemoteFlow", "Ui.Power", RequestExit));

        return menu;
    }

    private static WpfMenuItem Item(string header, string? iconKey, Action? onClick = null)
    {
        var item = new WpfMenuItem { Header = EscapeAccessKey(header) };
        if (iconKey is not null)
        {
            MenuItemAssist.SetIconKey(item, iconKey);
        }

        if (onClick is not null)
        {
            item.Click += (_, _) => onClick();
        }

        return item;
    }

    private WpfMenuItem BuildRecentMenu()
    {
        var recent = Item("最近连接", "Ui.Recent");

        var profiles = LoadRecentProfiles();
        if (profiles.Count == 0)
        {
            var empty = Item("暂无最近连接", null);
            empty.IsEnabled = false;
            recent.Items.Add(empty);
            return recent;
        }

        foreach (var profile in profiles)
        {
            recent.Items.Add(Item(profile.Name, UiIconResources.ProtocolKey(profile.Protocol), () => OpenRecentProfile(profile)));
        }

        recent.Items.Add(new WpfSeparator());
        recent.Items.Add(Item("查看全部最近连接", null, () => NavigateToPage(NavigationPage.Recent)));

        return recent;
    }

    private WpfMenuItem BuildActiveSessionsMenu(IReadOnlyList<IRemoteSession> activeSessions)
    {
        var menu = Item($"活动会话（{activeSessions.Count}）", "Ui.Sessions");

        if (activeSessions.Count == 0)
        {
            menu.IsEnabled = false;
            return menu;
        }

        foreach (var session in activeSessions)
        {
            var sessionId = session.SessionId;
            menu.Items.Add(Item(
                session.Profile.Name,
                UiIconResources.ProtocolKey(session.Profile.Protocol),
                () => ActivateSession(sessionId)));
        }

        menu.Items.Add(new WpfSeparator());
        var disconnectAll = Item("断开全部会话", "Ui.Disconnected", () => OnDisconnectAllSessionsClick(null, EventArgs.Empty));
        MenuItemAssist.SetIsDanger(disconnectAll, true);
        menu.Items.Add(disconnectAll);

        return menu;
    }

    // ── 数据源 ────────────────────────────────────────────────────

    /// <summary>
    /// 从数据库读取最近连接（LastConnectedAt 非空，倒序，最多 <see cref="RecentConnectionLimit"/> 条）。
    /// 菜单 Opening 在 UI 线程同步重建，这里把读库丢到线程池再等待（与 App 初始化种子的既有约定一致），
    /// 避免在 DispatcherSynchronizationContext 下对 async SQLite 做 sync-over-async 死锁。
    /// </summary>
    private List<ConnectionProfile> LoadRecentProfiles()
    {
        try
        {
            return Task.Run(() => _connections.GetAllAsync())
                .GetAwaiter().GetResult()
                .Where(p => p.LastConnectedAt is not null)
                .OrderByDescending(p => p.LastConnectedAt)
                .Take(RecentConnectionLimit)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取最近连接失败（托盘菜单）");
            return [];
        }
    }

    /// <summary>WPF 菜单项把下划线当作访问键前缀；名称里的字面下划线需写成两个。</summary>
    private static string EscapeAccessKey(string text) => text.Replace("_", "__");

    // ── 菜单动作 ──────────────────────────────────────────────────

    private void OpenNewConnection(ProtocolType protocol)
    {
        RestoreWindow();
        _ = RunNewConnectionAsync(protocol);
    }

    private async Task RunNewConnectionAsync(ProtocolType protocol)
    {
        try
        {
            await _mainViewModel.CreateConnectionAsync(protocol);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "从托盘新建 {Protocol} 连接失败", protocol);
        }
    }

    private void OpenRecentProfile(ConnectionProfile profile)
    {
        RestoreWindow();
        _ = RunOpenRecentAsync(profile);
    }

    private async Task RunOpenRecentAsync(ConnectionProfile profile)
    {
        try
        {
            // 复用统一开会话漏斗：无活动则建立，有活动则切到已有 Tab，不重复创建。
            await _mainViewModel.OpenSessionAsync(profile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "从托盘打开最近连接失败：{ConnectionName}", profile.Name);
        }
    }

    private void ActivateSession(Guid sessionId)
    {
        RestoreWindow();
        _mainViewModel.ActivateSession(sessionId);
    }

    private void OpenSettings()
    {
        RestoreWindow();
        _mainViewModel.NavigateTo(NavigationPage.Settings);
    }

    /// <summary>打开「关于」里的软件更新区；还没检查过就顺手检查一次（这是用户主动点的）。</summary>
    private void OpenUpdates(UpdateViewModel update)
    {
        RestoreWindow();
        _mainViewModel.NavigateTo(NavigationPage.About);
        if (update is { HasUpdate: false, IsReadyToRestart: false } && update.CheckCommand.CanExecute(null))
        {
            update.CheckCommand.Execute(null);
        }
    }

    private void NavigateToPage(NavigationPage page)
    {
        RestoreWindow();
        _mainViewModel.NavigateTo(page);
    }

    private void ToggleLaunchOnStartup()
    {
        try
        {
            _settingsPage.SetLaunchOnStartup(!_settingsPage.LaunchOnStartup);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "切换开机启动失败（托盘）");
        }
    }

    private async void OnDisconnectAllSessionsClick(object? sender, EventArgs e)
    {
        var sessions = _sessions.ActiveSessions.ToList();
        if (sessions.Count == 0)
        {
            return;
        }

        try
        {
            // 确认不先弹出主窗口：用户可能刻意把窗口隐藏着，仅因托盘操作打断会造成打扰；
            // 确认框自身带可见宿主（Owner 回退 MainWindow），取消时保持现状即可。
            var confirmed = await _dialogs.ConfirmAsync(
                "断开全部会话",
                $"确定要断开全部 {sessions.Count} 个活动会话吗？\n\n此操作不会删除连接配置或凭据。",
                "断开全部",
                isDanger: true);
            if (!confirmed)
            {
                return;
            }

            // 确认成功后把主窗口带到前台，让用户看到 Tab 逐个关闭与状态回落；
            // 断开动作本身仍走 SessionManager 的标准清理（非删 Tab / 非强杀）。
            RestoreWindow();
            foreach (var session in _sessions.ActiveSessions.ToList())
            {
                await _sessions.CloseSessionAsync(session.SessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "从托盘断开全部会话失败");
        }
    }

    private void RequestExit()
    {
        if (_window.Dispatcher.CheckAccess())
        {
            _window.RequestExit();
            return;
        }

        _window.Dispatcher.Invoke(_window.RequestExit);
    }

    // ── 窗口显隐 ──────────────────────────────────────────────────

    /// <summary>主窗口是否处于「真正显示」状态：可见且未被最小化。</summary>
    private bool IsMainWindowShown()
        => _window.Visibility == Visibility.Visible && _window.WindowState != WindowState.Minimized;

    private void ToggleMainWindow()
    {
        if (!_window.Dispatcher.CheckAccess())
        {
            _window.Dispatcher.BeginInvoke(ToggleMainWindow);
            return;
        }

        if (IsMainWindowShown())
        {
            _window.Hide();
        }
        else
        {
            RestoreWindow();
        }
    }

    /// <summary>打开并置前主窗口：还原 → 显示 → Activate → SetForegroundWindow，不只设 Visibility。</summary>
    /// <summary>显示并置前主窗口（再次启动应用时由已在运行的实例调用）。</summary>
    public void ShowMainWindow() => RestoreWindow();

    private void RestoreWindow()
    {
        if (!_window.Dispatcher.CheckAccess())
        {
            _window.Dispatcher.BeginInvoke(RestoreWindow);
            return;
        }

        _window.Show();

        if (_window.WindowState == WindowState.Minimized)
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
            if (handle != nint.Zero)
            {
                ShowWindow(handle, SwRestore);
            }
            else
            {
                _window.WindowState = WindowState.Normal;
            }
        }

        _window.Activate();

        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
            if (handle != nint.Zero)
            {
                SetForegroundWindow(handle);
            }
        }
        catch
        {
            // 置前失败不影响窗口已恢复显示。
        }
    }

    // ── 鼠标（单击显隐 / 双击打开置前 / 右键菜单）────────────────

    private void OnNotifyIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        // 双击第二下「抬起」补发的 MouseClick：落在双击处理之后，吞掉（见字段注释）。
        if (Environment.TickCount64 < _suppressClickUntilTick)
        {
            return;
        }

        // 双击的第一击也会先到 MouseClick；延迟到双击时间窗后执行，双击到达则取消。
        _singleClickPending = true;
        _clickTimer.Stop();
        _clickTimer.Interval = TimeSpan.FromMilliseconds(SystemInformation.DoubleClickTime);
        _clickTimer.Start();
    }

    private void OnNotifyIconMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _clickTimer.Stop();
        _singleClickPending = false;
        // 双击的第二下「抬起」还会补发一次 MouseClick，在双击时间窗内忽略它。
        _suppressClickUntilTick = Environment.TickCount64 + SystemInformation.DoubleClickTime;
        RestoreWindow();
    }

    private void OnSingleClickTimerTick(object? sender, EventArgs e)
    {
        _clickTimer.Stop();

        if (!_singleClickPending)
        {
            return;
        }

        _singleClickPending = false;
        ToggleMainWindow();
    }

    // ── Tooltip ───────────────────────────────────────────────────

    private void UpdateTooltip()
    {
        if (_notifyIcon is null)
        {
            return;
        }

        // SessionsChanged 可能来自协议后台线程，而 NotifyIcon 属 UI 线程（WPF 主 Dispatcher）资源，
        // 非 UI 线程切回再更新，避免跨线程访问控件。
        if (!_window.Dispatcher.CheckAccess())
        {
            _window.Dispatcher.BeginInvoke(UpdateTooltip);
            return;
        }

        var count = _sessions.ConnectedSessionCount;

        // NotifyIcon.Text 有 63 字符上限，这里的文案远低于该限制。
        _notifyIcon.Text = count == 0
            ? $"RemoteFlow {_mainViewModel.AppVersion}"
            : $"RemoteFlow {_mainViewModel.AppVersion} — {count} 个会话已连接";
    }

    // ── 图标与资源 ────────────────────────────────────────────────

    /// <summary>取应用自身的图标；取不到时退回系统默认图标，不让托盘初始化失败。</summary>
    private static Icon LoadApplicationIcon()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(executablePath))
            {
                var extracted = Icon.ExtractAssociatedIcon(executablePath);
                if (extracted is not null)
                {
                    return extracted;
                }
            }
        }
        catch
        {
            // 图标提取失败不是致命问题，继续使用系统默认图标。
        }

        return SystemIcons.Application;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _clickTimer.Stop();
        _clickTimer.Tick -= OnSingleClickTimerTick;

        if (_notifyIcon is not null)
        {
            // 必须先隐藏：否则进程退出后托盘会残留一个「僵尸图标」，
            // 直到用户把鼠标划过通知区域才消失。
            _notifyIcon.MouseUp -= OnNotifyIconMouseUp;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        if (_menu is { IsOpen: true } menu)
        {
            menu.IsOpen = false;
        }

        _menu = null;
    }
}
