using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using RemoteFlow.Presentation.ViewModels;
using RemoteFlow.App.Views;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.App.Services;

/// <summary>
/// 系统托盘图标与右键结构化菜单。
/// <para>
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
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Threading.DispatcherTimer _clickTimer = new();

    /// <summary>单击去抖标记：单击不立即动作，等 DoubleClickTime 内是否跟来双击。</summary>
    private bool _singleClickPending;

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
            Text = "RemoteFlow"
        };
        _notifyIcon = notifyIcon;

        // 首次建立菜单骨架（随后每次打开再整树重建）。
        RebuildMenu();
        notifyIcon.ContextMenuStrip = _menu;

        // 每次打开菜单都整树重建：最近连接读库、活动会话读 SessionManager 实时快照。
        _menu.Opening += (_, _) => RebuildMenu();

        // 会话集合变化（创建 / 任意状态跳变 / 移除）都会改变提示文字；
        // 事件可在协议后台线程触发，UpdateTooltip 内部 marshal 回 UI 线程再更新。
        _sessions.SessionsChanged += (_, _) => UpdateTooltip();

        // 鼠标：单击显隐，双击打开并置前。单击先经双击时间窗去抖，避免双击的第一击把窗口隐藏。
        _clickTimer.Tick += OnSingleClickTimerTick;
        notifyIcon.MouseClick += OnNotifyIconMouseClick;
        notifyIcon.MouseDoubleClick += OnNotifyIconMouseDoubleClick;
    }

    // ── 菜单整树重建 ─────────────────────────────────────────────

    private void RebuildMenu()
    {
        ClearMenu();

        var activeSessions = _sessions.ActiveSessions
            .OrderBy(s => s.Profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // 1) 打开 / 隐藏（按主窗口状态动态文案）。
        var toggle = new ToolStripMenuItem(IsMainWindowShown() ? "隐藏 RemoteFlow" : "打开 RemoteFlow");
        toggle.Click += (_, _) => ToggleMainWindow();
        _menu.Items.Add(toggle);

        AddSeparator();

        // 2) 新建连接 ▸ RDP / SSH / VNC。
        var newConnection = new ToolStripMenuItem("新建连接");
        newConnection.DropDownItems.Add(CreateNewProtocolItem(ProtocolType.Rdp, "RDP 连接"));
        newConnection.DropDownItems.Add(CreateNewProtocolItem(ProtocolType.Ssh, "SSH 连接"));
        newConnection.DropDownItems.Add(CreateNewProtocolItem(ProtocolType.Vnc, "VNC 连接"));
        _menu.Items.Add(newConnection);

        AddSeparator();

        // 3) 最近连接 ▸（读库 ≤5 + 查看全部）。
        _menu.Items.Add(BuildRecentMenu());

        AddSeparator();

        // 4) 活动会话（N）▸（实时读 SessionManager；N==0 禁用不展开）。
        _menu.Items.Add(BuildActiveSessionsMenu(activeSessions));

        AddSeparator();

        // 5) 设置：打开 RemoteFlow → 设置页。
        var settingsItem = new ToolStripMenuItem("设置");
        settingsItem.Click += (_, _) => OpenSettings();
        _menu.Items.Add(settingsItem);

        // 6) 开机启动：Check 反映当前设置，点击双向同步到注册表与设置页。
        var startupItem = new ToolStripMenuItem("开机启动") { Checked = _settingsPage.LaunchOnStartup };
        startupItem.Click += (_, _) => ToggleLaunchOnStartup();
        _menu.Items.Add(startupItem);

        AddSeparator();

        // 7) 退出。
        var exitItem = new ToolStripMenuItem("退出 RemoteFlow");
        exitItem.Click += (_, _) => RequestExit();
        _menu.Items.Add(exitItem);

        UpdateTooltip();
    }

    private void ClearMenu()
    {
        while (_menu.Items.Count > 0)
        {
            var item = _menu.Items[0];
            _menu.Items.RemoveAt(0);
            item.Dispose();
        }
    }

    private void AddSeparator() => _menu.Items.Add(new ToolStripSeparator());

    private ToolStripMenuItem CreateNewProtocolItem(ProtocolType protocol, string text)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => OpenNewConnection(protocol);
        return item;
    }

    private ToolStripMenuItem BuildRecentMenu()
    {
        var recent = new ToolStripMenuItem("最近连接");

        var profiles = LoadRecentProfiles();
        if (profiles.Count == 0)
        {
            recent.DropDownItems.Add(new ToolStripMenuItem("暂无最近连接") { Enabled = false });
            return recent;
        }

        foreach (var profile in profiles)
        {
            var item = new ToolStripMenuItem(FormatRecentLabel(profile));
            item.Click += (_, _) => OpenRecentProfile(profile);
            recent.DropDownItems.Add(item);
        }

        recent.DropDownItems.Add(new ToolStripSeparator());
        var viewAll = new ToolStripMenuItem("查看全部最近连接");
        viewAll.Click += (_, _) => NavigateToPage(NavigationPage.Recent);
        recent.DropDownItems.Add(viewAll);

        return recent;
    }

    private ToolStripMenuItem BuildActiveSessionsMenu(IReadOnlyList<IRemoteSession> activeSessions)
    {
        var menu = new ToolStripMenuItem($"活动会话（{activeSessions.Count}）");

        if (activeSessions.Count == 0)
        {
            menu.Enabled = false;
            return menu;
        }

        foreach (var session in activeSessions)
        {
            var label = $"● {EscapeMnemonic(session.Profile.Name)}（{FormatProtocolName(session.Profile.Protocol)}）";
            var item = new ToolStripMenuItem(label);
            var sessionId = session.SessionId;
            item.Click += (_, _) => ActivateSession(sessionId);
            menu.DropDownItems.Add(item);
        }

        menu.DropDownItems.Add(new ToolStripSeparator());
        var disconnectAll = new ToolStripMenuItem("断开全部会话");
        disconnectAll.Click += OnDisconnectAllSessionsClick;
        menu.DropDownItems.Add(disconnectAll);

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

    private static string FormatRecentLabel(ConnectionProfile profile)
        => $"{EscapeMnemonic(profile.Name)}（{FormatProtocolName(profile.Protocol)}）";

    /// <summary>WinForms 菜单项把 <c>&amp;</c> 当作助记符前缀；名称里的字面 <c>&amp;</c> 需转义为 <c>&amp;&amp;</c>。</summary>
    private static string EscapeMnemonic(string text) => text.Replace("&", "&&");

    private static string FormatProtocolName(ProtocolType protocol) => protocol switch
    {
        ProtocolType.Rdp => "RDP",
        ProtocolType.Ssh => "SSH",
        _ => "VNC"
    };

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
            ? "RemoteFlow"
            : $"RemoteFlow — {count} 个会话已连接";
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
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        ClearMenu();
        _menu.Dispose();
    }
}
