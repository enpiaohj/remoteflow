using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using RemoteFlow.App.Views;
using RemoteFlow.Application.Services;

namespace RemoteFlow.App.Services;

/// <summary>
/// 系统托盘图标。
/// <para>
/// 当「关闭窗口时最小化到通知区域」开启时，托盘是用户真正退出应用的入口；
/// 同时也让后台仍有会话在运行这件事可见，而不是悄无声息地驻留。
/// </para>
/// </summary>
public sealed class TrayService(MainWindow window, SessionManager sessions) : IDisposable
{
    private NotifyIcon? _notifyIcon;
    private bool _disposed;

    public void Initialize()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = LoadApplicationIcon(),
            Visible = true,
            Text = "RemoteFlow"
        };

        var menu = new ContextMenuStrip();

        var showItem = new ToolStripMenuItem("显示主窗口");
        showItem.Click += (_, _) => RestoreWindow();

        var exitItem = new ToolStripMenuItem("退出 RemoteFlow");
        exitItem.Click += (_, _) => RequestExit();

        menu.Items.Add(showItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => RestoreWindow();

        // 会话数量变化时更新提示文字，让用户从托盘就能看到后台状态。
        sessions.SessionCreated += (_, _) => UpdateTooltip();
        sessions.SessionClosed += (_, _) => UpdateTooltip();
    }

    private void UpdateTooltip()
    {
        if (_notifyIcon is null)
        {
            return;
        }

        var count = sessions.ActiveSessionCount;

        // NotifyIcon.Text 有 63 字符上限，这里的文案远低于该限制。
        _notifyIcon.Text = count == 0
            ? "RemoteFlow"
            : $"RemoteFlow — {count} 个会话进行中";
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
    }
}
