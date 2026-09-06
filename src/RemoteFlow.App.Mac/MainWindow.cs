using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Infrastructure;
using RemoteFlow.Protocol.Ssh;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 主窗口（可运行骨架）：顶部连接栏（SSH 直连）+ 中部会话区。
/// <para>
/// 这是 macOS 客户端的纵向切片：输入主机 / 账号 / 口令 → 连接 → 在
/// WebView + xterm.js 终端里交互。资产管理 / 凭据库 / 分组等完整 UI 属后续里程碑；
/// 会话经 SshSessionView 直接与会话建立，未走 SessionManager / 凭据库
/// （直连用 ResolvedCredential，由连接栏临时输入，不落盘）。
/// </para>
/// </summary>
public sealed class MainWindow : Window
{
    private readonly TextBox _hostBox = new() { Watermark = "主机或 IP", Width = 220 };
    private readonly TextBox _userBox = new() { Watermark = "账号", Width = 130 };
    private readonly TextBox _passBox = new() { Watermark = "口令", Width = 140, PasswordChar = '●' };
    private readonly TextBox _portBox = new() { Text = "22", Width = 60 };
    private readonly Button _connectButton = new() { Content = "连接" };
    private readonly Button _disconnectButton = new() { Content = "断开", IsVisible = false };
    private readonly TextBlock _statusText = new() { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };

    private readonly Border _sessionArea = new()
    {
        Background = Brushes.Black,
        CornerRadius = new CornerRadius(6),
        ClipToBounds = true,
    };

    private SshSessionView? _sessionView;
    private SshSession? _session;
    private readonly SshConnectionProvider _sshProvider;

    public MainWindow(AppPaths paths, SshConnectionProvider sshProvider)
    {
        _sshProvider = sshProvider;

        Title = "RemoteFlow";
        Width = 980;
        Height = 640;
        MinWidth = 720;
        MinHeight = 480;

        // 预填测试主机（便于快速连真机验证；正式凭据管理后续接入）。
        _hostBox.Text = Environment.GetEnvironmentVariable("RF_SSH_HOST");
        _userBox.Text = Environment.GetEnvironmentVariable("RF_SSH_USER");
        _passBox.Text = Environment.GetEnvironmentVariable("RF_SSH_PASS");

        var connectBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 10),
            Children = { _hostBox, _userBox, _passBox, _portBox, _connectButton, _disconnectButton, _statusText },
        };

        var hint = new TextBlock
        {
            Text = $"数据目录：{paths.DataDirectory}",
            Foreground = new SolidColorBrush(Color.Parse("#888888")),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 6),
        };

        _connectButton.Click += async (_, _) => await ConnectAsync();
        _disconnectButton.Click += async (_, _) => await DisconnectAsync();
        Closing += (_, _) => _ = DisconnectAsync();

        // 运行验证钩子：RF_AUTOCONNECT=1 时启动即自动连接（凭据来自 RF_SSH_* 环境变量）。
        if (Environment.GetEnvironmentVariable("RF_AUTOCONNECT") == "1")
        {
            Dispatcher.UIThread.Post(async () => await ConnectAsync(), DispatcherPriority.Background);
        }

        Content = new DockPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                LayoutHelper.DockTop(hint),
                LayoutHelper.DockTop(connectBar),
                _sessionArea,
            },
        };
    }

    private async Task ConnectAsync()
    {
        var host = _hostBox.Text?.Trim();
        var user = _userBox.Text?.Trim();
        var pass = _passBox.Text;
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            _statusText.Text = "请填写主机 / 账号 / 口令。";
            _statusText.Foreground = Brushes.Orange;
            return;
        }

        if (!int.TryParse(_portBox.Text?.Trim(), out var port) || port <= 0)
        {
            port = 22;
        }

        try
        {
            _connectButton.IsEnabled = false;
            _statusText.Text = $"正在连接 {user}@{host}:{port} …";
            _statusText.Foreground = Brushes.Gray;

            var profile = new ConnectionProfile
            {
                Name = host,
                Host = host,
                Port = port,
                Protocol = ProtocolType.Ssh,
            };
            profile.Ssh.ConnectTimeoutSeconds = 15;

            var credential = new ResolvedCredential
            {
                Type = CredentialType.SshPassword,
                Username = user,
                Password = pass,
            };

            var request = new SessionRequest
            {
                Profile = profile,
                Credential = credential,
                HostKeyPolicy = new DevTrustHostKeyPolicy(),
            };

            var session = (SshSession)_sshProvider.CreateSession(request);
            await session.ConnectAsync();

            if (session.State != ConnectionState.Connected)
            {
                Console.WriteLine($"[RF] CONNECT_FAIL {session.ErrorCode} {session.ErrorMessage}");
                _statusText.Text = $"连接失败：{session.ErrorMessage ?? session.ErrorCode.ToString()}";
                _statusText.Foreground = Brushes.OrangeRed;
                _connectButton.IsEnabled = true;
                return;
            }

            _session = session;
            Console.WriteLine($"[RF] CONNECTED {user}@{host}:{port}");
            _statusText.Text = $"已连接 {host}";
            _statusText.Foreground = new SolidColorBrush(Color.Parse("#2E8B57"));
            _connectButton.IsVisible = false;
            _disconnectButton.IsVisible = true;

            _sessionView = new SshSessionView(session);
            _sessionArea.Child = _sessionView;

            // 运行验证钩子：连接后读回终端已渲染文本。
            if (Environment.GetEnvironmentVariable("RF_VERIFY") == "1")
            {
                _ = VerifyTerminalAsync();
            }
        }
        catch (Exception ex)
        {
            _statusText.Text = $"连接异常：{ex.Message}";
            _statusText.Foreground = Brushes.OrangeRed;
            _connectButton.IsEnabled = true;
        }
    }

    private async Task VerifyTerminalAsync()
    {
        try
        {
            await Task.Delay(6000);
            var text = _sessionView is null ? "(no view)" : await _sessionView.ReadTerminalTextAsync();
            Console.WriteLine($"[RF] TERMINAL_TEXT_BEGIN\n{text}\n[RF] TERMINAL_TEXT_END");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RF] VERIFY_FAIL {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task DisconnectAsync()
    {
        if (_sessionView is not null)
        {
            await _sessionView.DisposeAsync();
            _sessionView = null;
        }

        if (_session is not null)
        {
            _session = null;
        }

        _connectButton.IsEnabled = true;
        _connectButton.IsVisible = true;
        _disconnectButton.IsVisible = false;
        _statusText.Text = "";
        _sessionArea.Child = null;
    }
}

/// <summary>布局辅助：把控件钉到 DockPanel 顶部并返回。</summary>
internal static class LayoutHelper
{
    public static T DockTop<T>(T control) where T : Control
    {
        DockPanel.SetDock(control, Dock.Top);
        return control;
    }
}

/// <summary>
/// 开发用 Host Key 策略：首次连接接受并记录、变化时放行。
/// <b>仅用于可运行骨架</b>——正式确认对话框（首次提示 / 变化强警告）属后续里程碑，
/// 见技术方案 §7.4。这里记录指纹以便日志回溯，但不阻断连接。
/// </summary>
internal sealed class DevTrustHostKeyPolicy : ISshHostKeyPolicy
{
    private string? _remembered;

    public SshHostKeyVerificationContext Lookup(SshHostKeyVerificationContext context) => new()
    {
        Host = context.Host,
        Port = context.Port,
        KeyAlgorithm = context.KeyAlgorithm,
        Fingerprint = context.Fingerprint,
        KnownFingerprint = _remembered,
    };

    public Task<bool> ConfirmAndRememberAsync(SshHostKeyVerificationContext context, CancellationToken ct)
    {
        _remembered = context.Fingerprint;
        return Task.FromResult(true);
    }
}
