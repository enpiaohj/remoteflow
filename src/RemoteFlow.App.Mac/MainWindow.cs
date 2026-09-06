using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Infrastructure;
using RemoteFlow.Protocol.Ssh;
using RemoteFlow.Protocol.Vnc;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 主窗口（可运行骨架）：顶部连接栏（协议 + 主机 / 账号 / 口令 / 端口）+ 中部会话区。
/// <para>
/// 纵向切片：输入目标 → 连接 → 在 WebView + xterm.js（SSH）或位图渲染（VNC）里交互。
/// 资产 / 凭据库 / 分组等完整工作台 UI 与统一会话管理属后续里程碑；
/// 此处经各 Provider 直连（ResolvedCredential 由连接栏临时输入，不落盘）。
/// </para>
/// </summary>
public sealed class MainWindow : Window
{
    private readonly ComboBox _protoBox = new() { Width = 90 };
    private readonly TextBox _hostBox = new() { Watermark = "主机或 IP", Width = 200 };
    private readonly TextBox _userBox = new() { Watermark = "账号", Width = 110 };
    private readonly TextBox _passBox = new() { Watermark = "口令 / VNC 密码", Width = 130, PasswordChar = '●' };
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

    private readonly SshConnectionProvider _sshProvider;
    private readonly VncConnectionProvider _vncProvider;

    private SshSessionView? _sshView;
    private VncSessionView? _vncView;
    private SshSession? _sshSession;
    private VncSession? _vncSession;

    public MainWindow(
        AppPaths paths,
        SshConnectionProvider sshProvider,
        VncConnectionProvider vncProvider)
    {
        _sshProvider = sshProvider;
        _vncProvider = vncProvider;

        Title = "RemoteFlow";
        Width = 1020;
        Height = 660;
        MinWidth = 760;
        MinHeight = 480;

        // 协议选择（SSH / VNC）。RDP 随 Phase 2 (MacRdpSession) 追加。
        _protoBox.ItemsSource = new[] { "SSH", "VNC" };
        _protoBox.SelectedIndex = 0;
        _protoBox.SelectionChanged += (_, _) =>
            _portBox.Text = _protoBox.SelectedIndex == 0 ? "22" : "5900";

        // 预填测试主机（便于快速连真机验证；正式凭据管理后续接入）。
        // RF_PROTO=vnc 时切到 VNC；host/user/pass 分别可被 RF_HOST/USER/PASS 覆盖。
        if (Environment.GetEnvironmentVariable("RF_PROTO") == "vnc")
        {
            _protoBox.SelectedIndex = 1;
        }

        _hostBox.Text = Environment.GetEnvironmentVariable("RF_HOST") ?? Environment.GetEnvironmentVariable("RF_SSH_HOST");
        _userBox.Text = Environment.GetEnvironmentVariable("RF_USER") ?? Environment.GetEnvironmentVariable("RF_SSH_USER");
        _passBox.Text = Environment.GetEnvironmentVariable("RF_PASS") ?? Environment.GetEnvironmentVariable("RF_SSH_PASS");

        var connectBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 10),
            Children = { _protoBox, _hostBox, _userBox, _passBox, _portBox, _connectButton, _disconnectButton, _statusText },
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

        Content = new DockPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                DockTop(hint),
                DockTop(connectBar),
                _sessionArea,
            },
        };

        // 运行验证钩子：RF_AUTOCONNECT=1 时启动即自动连接（凭据来自 RF_* 环境变量）。
        if (Environment.GetEnvironmentVariable("RF_AUTOCONNECT") == "1")
        {
            Dispatcher.UIThread.Post(async () => await ConnectAsync(), DispatcherPriority.Background);
        }
    }

    private static Control DockTop(Control control)
    {
        DockPanel.SetDock(control, Dock.Top);
        return control;
    }

    private bool IsSsh => _protoBox.SelectedIndex != 1;

    private async Task ConnectAsync()
    {
        var host = _hostBox.Text?.Trim();
        var user = _userBox.Text?.Trim();
        var pass = _passBox.Text;
        if (string.IsNullOrEmpty(host) || (IsSsh && string.IsNullOrEmpty(pass)))
        {
            _statusText.Text = IsSsh ? "SSH 需填写主机与口令。" : "VNC 需填写主机。";
            _statusText.Foreground = Brushes.Orange;
            return;
        }

        if (!int.TryParse(_portBox.Text?.Trim(), out var port) || port <= 0)
        {
            port = IsSsh ? 22 : 5900;
        }

        try
        {
            _connectButton.IsEnabled = false;
            _statusText.Text = $"正在连接 {host}:{port} …";
            _statusText.Foreground = Brushes.Gray;

            var protocol = IsSsh ? ProtocolType.Ssh : ProtocolType.Vnc;
            var profile = new ConnectionProfile
            {
                Name = host,
                Host = host,
                Port = port,
                Protocol = protocol,
            };

            if (IsSsh)
            {
                profile.Ssh.ConnectTimeoutSeconds = 15;
                var credential = new ResolvedCredential
                {
                    Type = CredentialType.SshPassword,
                    Username = user ?? string.Empty,
                    Password = pass,
                };

                var session = (SshSession)_sshProvider.CreateSession(new SessionRequest
                {
                    Profile = profile,
                    Credential = credential,
                    HostKeyPolicy = new DevTrustHostKeyPolicy(),
                });
                await session.ConnectAsync();

                if (session.State != ConnectionState.Connected)
                {
                    FailConnect(session.ErrorMessage ?? session.ErrorCode.ToString());
                    return;
                }

                _sshSession = session;
                _sshView = new SshSessionView(session);
                _sessionArea.Child = _sshView;
                Connected($"已连接 {host} (SSH)");

                if (Environment.GetEnvironmentVariable("RF_VERIFY") == "1")
                {
                    _ = VerifySshTerminalAsync();
                }
            }
            else
            {
                profile.Vnc.ConnectTimeoutSeconds = 15;
                profile.Vnc.SharedConnection = true;

                var credential = new ResolvedCredential
                {
                    Type = CredentialType.VncPassword,
                    Username = user ?? string.Empty,
                    Password = pass,
                };

                var session = (VncSession)_vncProvider.CreateSession(new SessionRequest
                {
                    Profile = profile,
                    Credential = credential,
                });
                await session.ConnectAsync();

                if (session.State != ConnectionState.Connected)
                {
                    FailConnect(session.ErrorMessage ?? session.ErrorCode.ToString());
                    return;
                }

                _vncSession = session;
                _vncView = new VncSessionView(session);
                _sessionArea.Child = _vncView;
                Connected($"已连接 {host} (VNC)");

                if (Environment.GetEnvironmentVariable("RF_VERIFY") == "1")
                {
                    _ = VerifyVncConnectedAsync();
                }
            }
        }
        catch (Exception ex)
        {
            FailConnect(ex.Message);
        }
    }

    private void Connected(string text)
    {
        Console.WriteLine($"[RF] CONNECTED {text}");
        _statusText.Text = text;
        _statusText.Foreground = new SolidColorBrush(Color.Parse("#2E8B57"));
        _connectButton.IsVisible = false;
        _disconnectButton.IsVisible = true;
    }

    private void FailConnect(string reason)
    {
        Console.WriteLine($"[RF] CONNECT_FAIL {reason}");
        _statusText.Text = $"连接失败：{reason}";
        _statusText.Foreground = Brushes.OrangeRed;
        _connectButton.IsEnabled = true;
    }

    private async Task DisconnectAsync()
    {
        if (_sshView is not null)
        {
            await _sshView.DisposeAsync();
            _sshView = null;
        }

        if (_vncView is not null)
        {
            _vncView.DisposeView();
            _vncView = null;
        }

        _sshSession = null;
        _vncSession = null;
        _connectButton.IsEnabled = true;
        _connectButton.IsVisible = true;
        _disconnectButton.IsVisible = false;
        _statusText.Text = "";
        _sessionArea.Child = null;
    }

    private async Task VerifySshTerminalAsync()
    {
        try
        {
            await Task.Delay(6000);
            var text = _sshView is null ? "(no view)" : await _sshView.ReadTerminalTextAsync();
            Console.WriteLine($"[RF] TERMINAL_TEXT_BEGIN\n{text}\n[RF] TERMINAL_TEXT_END");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RF] VERIFY_FAIL {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task VerifyVncConnectedAsync()
    {
        try
        {
            await Task.Delay(5000);
            var v = _vncSession;
            if (v is null)
            {
                Console.WriteLine("[RF] VNC_NO_SESSION");
                return;
            }

            var size = v.RemoteSize;
            Console.WriteLine($"[RF] VNC_CONNECTED remote={size.Width}x{size.Height} state={v.State}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RF] VNC_VERIFY_FAIL {ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>
/// 开发用 Host Key 策略：首次连接接受并记录、变化时放行。
/// <b>仅用于可运行骨架</b>——正式确认对话框（首次提示 / 变化强警告）属后续里程碑，
/// 见技术方案 §7.4。这里记录指纹但不阻断连接。
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
