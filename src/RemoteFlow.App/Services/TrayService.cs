using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using RemoteFlow.App.Views;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.App.Services;

/// <summary>
/// 系统托盘图标。
/// <para>
/// 当「关闭窗口时最小化到通知区域」开启时，托盘是用户真正退出应用的入口；
/// 同时也让后台仍有会话在运行这件事可见，而不是悄无声息地驻留。
/// 右键菜单在每次打开时按当前已连接会话重建，保证会话列表不过期。
/// </para>
/// </summary>
public sealed class TrayService(MainWindow window, SessionManager sessions) : IDisposable
{
    private NotifyIcon? _notifyIcon;
    private bool _disposed;

    /// <summary>托盘右键菜单。顶部与底部的固定项只建一次，中间「已连接会话」区每次打开重建。</summary>
    private readonly ContextMenuStrip _menu = new();

    /// <summary>「已连接会话」区与「退出 RemoteFlow」之间的分隔条；重建时把会话项插到它前面。</summary>
    private ToolStripSeparator? _sessionEndSeparator;

    /// <summary>当前「已连接会话」区内动态生成的菜单项，重建前逐个移除并释放。</summary>
    private readonly List<ToolStripItem> _sessionItems = [];

    /// <summary>用户在托盘菜单点选某个已连接会话时触发，参数为该会话的 SessionId。</summary>
    public event EventHandler<Guid>? SessionActivateRequested;

    public void Initialize()
    {
        var notifyIcon = new NotifyIcon
        {
            Icon = LoadApplicationIcon(),
            Visible = true,
            Text = "RemoteFlow"
        };
        _notifyIcon = notifyIcon;

        BuildMenu(notifyIcon);

        notifyIcon.ContextMenuStrip = _menu;

        // 每次打开菜单都重建「已连接会话」区，读取当前 Connected 会话集合，
        // 比在 SessionCreated/Closed 时维护列表更能避免过期项。
        _menu.Opening += (_, _) => RebuildConnectedSessionSection();

        // 会话数量变化时更新提示文字，让用户从托盘就能看到后台状态。
        sessions.SessionCreated += (_, _) => UpdateTooltip();
        sessions.SessionClosed += (_, _) => UpdateTooltip();
    }

    /// <summary>建立固定菜单骨架：打开项 / 分隔 / 会话区（动态）/ 分隔 / 退出。</summary>
    private void BuildMenu(NotifyIcon notifyIcon)
    {
        var openItem = new ToolStripMenuItem("打开 RemoteFlow");
        openItem.Click += (_, _) => RestoreWindow();

        var exitItem = new ToolStripMenuItem("退出 RemoteFlow");
        exitItem.Click += (_, _) => RequestExit();

        _sessionEndSeparator = new ToolStripSeparator();

        _menu.Items.Add(openItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_sessionEndSeparator);
        _menu.Items.Add(exitItem);

        notifyIcon.DoubleClick += (_, _) => RestoreWindow();
    }

    private void UpdateTooltip()
    {
        if (_notifyIcon is null)
        {
            return;
        }

        var count = sessions.ConnectedSessionCount;

        // NotifyIcon.Text 有 63 字符上限，这里的文案远低于该限制。
        _notifyIcon.Text = count == 0
            ? "RemoteFlow"
            : $"RemoteFlow — {count} 个会话已连接";
    }

    /// <summary>每次菜单打开时重建「已连接会话」区：先清掉上一轮动态项，再按当前 Connected 会话重排。</summary>
    private void RebuildConnectedSessionSection()
    {
        var anchor = _sessionEndSeparator;
        if (anchor is null)
        {
            return;
        }

        ClearSessionRegion();

        var connected = sessions.ActiveSessions
            .Where(s => s.State == ConnectionState.Connected)
            .OrderBy(s => s.Profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (connected.Count == 0)
        {
            InsertSessionItem(new ToolStripMenuItem("暂无已连接会话") { Enabled = false }, anchor);
            UpdateTooltip();
            return;
        }

        InsertSessionItem(CreateSectionHeader("已连接会话"), anchor);
        foreach (var session in connected)
        {
            var item = new ToolStripMenuItem(FormatSessionLabel(session));
            item.Click += (_, _) => RequestActivateSession(session.SessionId);
            InsertSessionItem(item, anchor);
        }

        UpdateTooltip();
    }

    private void ClearSessionRegion()
    {
        foreach (var item in _sessionItems)
        {
            _menu.Items.Remove(item);
            item.Dispose();
        }

        _sessionItems.Clear();
    }

    /// <summary>把动态会话项插到锚点分隔条之前，保持「…会话区 / 分隔 / 退出」的顺序。</summary>
    private void InsertSessionItem(ToolStripItem item, ToolStripSeparator anchor)
    {
        var index = _menu.Items.IndexOf(anchor);
        if (index < 0)
        {
            item.Dispose();
            return;
        }

        _menu.Items.Insert(index, item);
        _sessionItems.Add(item);
    }

    /// <summary>「已连接会话」分组标题：灰色、不可点击，仅作分组提示。</summary>
    private static ToolStripMenuItem CreateSectionHeader(string text) =>
        new(text) { Enabled = false };

    /// <summary>会话菜单文案：名称（协议）主机。端口为协议默认值时省略，非默认端口则显式带出。</summary>
    private static string FormatSessionLabel(IRemoteSession session)
    {
        var profile = session.Profile;
        var protocol = profile.Protocol switch
        {
            ProtocolType.Rdp => "RDP",
            ProtocolType.Ssh => "SSH",
            _ => "VNC"
        };
        var host = profile.Port == ConnectionProfile.GetDefaultPort(profile.Protocol)
            ? profile.Host
            : $"{profile.Host}:{profile.Port}";

        return $"{profile.Name}（{protocol}）{host}";
    }

    private void RestoreWindow()
    {
        window.Dispatcher.Invoke(() =>
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        });
    }

    /// <summary>点选某已连接会话：先把主窗口带到前台，再向外请求切换到该会话。</summary>
    private void RequestActivateSession(Guid sessionId)
    {
        RestoreWindow();
        SessionActivateRequested?.Invoke(this, sessionId);
    }

    private void RequestExit() => window.Dispatcher.Invoke(window.RequestExit);

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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_notifyIcon is not null)
        {
            // 必须先隐藏：否则进程退出后托盘会残留一个「僵尸图标」，
            // 直到用户把鼠标划过通知区域才消失。
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _menu.Dispose();
    }
}
