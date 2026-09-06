using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Infrastructure;
using RemoteFlow.Presentation.ViewModels;
using RemoteFlow.Protocol.Ssh;
using RemoteFlow.Protocol.Vnc;
using AppServices = RemoteFlow.Application.Services;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 主窗口（工作台形态）：左侧「连接资产」列表 + 右侧会话区。
/// <para>
/// 资产列表复用共享 <see cref="ConnectionsPageViewModel"/>（Filter=All，GroupedRows 扁平行）；
/// 「新建连接」经共享 ConnectionService / CredentialService + Keychain 落库；
/// 双击或选中点「打开」经 SessionManager 开会话（内部解析 Keychain 凭据）。
/// 顶部仍保留协议直连条（开发验证用，对应 SSH/VNC 会话视图）。
/// </para>
/// </summary>
public sealed class MainWindow : Window
{
    private readonly SshConnectionProvider _sshProvider;
    private readonly VncConnectionProvider _vncProvider;
    private readonly AppServices.ConnectionService _connections;
    private readonly AppServices.CredentialService _credentials;
    private readonly ICredentialVault _vault;
    private readonly AppServices.SessionManager _sessions;
    private readonly ConnectionsPageViewModel _connectionsVm;

    private readonly Border _sessionArea = new()
    {
        Background = Brushes.Black,
        CornerRadius = new CornerRadius(6),
        ClipToBounds = true,
    };

    private readonly ListBox _assetList = new();
    private readonly TextBlock _listStatus = new() { Foreground = Brushes.Gray, FontSize = 12 };
    private readonly ComboBox _protoBox = new() { Width = 88 };
    private readonly TextBox _hostBox = new() { Watermark = "主机", Width = 160 };
    private readonly TextBox _userBox = new() { Watermark = "账号", Width = 100 };
    private readonly TextBox _passBox = new() { Watermark = "口令", Width = 120, PasswordChar = '●' };
    private readonly TextBox _portBox = new() { Text = "22", Width = 56 };
    private readonly Button _connectButton = new() { Content = "直连" };
    private readonly Button _disconnectButton = new() { Content = "断开", IsVisible = false };
    private readonly TextBlock _statusText = new() { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };

    private SshSessionView? _sshView;
    private VncSessionView? _vncView;
    private SshSession? _sshSession;
    private VncSession? _vncSession;

    public MainWindow(
        AppPaths paths,
        SshConnectionProvider sshProvider,
        VncConnectionProvider vncProvider,
        AppServices.ConnectionService connections,
        AppServices.CredentialService credentials,
        ICredentialVault vault,
        AppServices.SessionManager sessions,
        ConnectionsPageViewModel connectionsVm)
    {
        _sshProvider = sshProvider;
        _vncProvider = vncProvider;
        _connections = connections;
        _credentials = credentials;
        _vault = vault;
        _sessions = sessions;
        _connectionsVm = connectionsVm;

        Title = "RemoteFlow";
        Width = 1180;
        Height = 720;
        MinWidth = 860;
        MinHeight = 520;

        // ── 左：连接资产 ──────────────────────────────────────────
        _connectionsVm.Filter = ConnectionFilter.All;

        _assetList.ItemsSource = _connectionsVm.GroupedRows;
        _assetList.ItemTemplate = new FuncDataTemplate<object>((item, _) => item switch
        {
            ConnectionItemViewModel c => MakeConnectionRow(c),
            ConnectionGroupNodeViewModel g => MakeGroupRow(g),
            _ => null,
        });
        _assetList.DoubleTapped += async (_, _) => await OpenSelectedAsync();

        var openButton = new Button { Content = "打开选中", HorizontalAlignment = HorizontalAlignment.Left };
        openButton.Click += async (_, _) => await OpenSelectedAsync();

        var newButton = new Button { Content = "＋ 新建连接", HorizontalAlignment = HorizontalAlignment.Left };
        newButton.Click += async (_, _) => await ShowNewConnectionDialogAsync();

        var refreshButton = new Button { Content = "刷新", HorizontalAlignment = HorizontalAlignment.Left };
        refreshButton.Click += async (_, _) => await ReloadAssetsAsync();

        var left = new DockPanel
        {
            Width = 330,
            Margin = new Thickness(0, 0, 12, 0),
            Children =
            {
                DockTop(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Margin = new Thickness(0, 0, 0, 8),
                    Children = { newButton, openButton, refreshButton },
                }),
                DockTop(_listStatus),
                _assetList,
            },
        };

        // ── 右：会话区 ────────────────────────────────────────────
        _sessionArea.Child = new TextBlock
        {
            Text = "选择左侧连接资产，双击或点「打开选中」建立会话。",
            Foreground = Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // ── 顶：直连条（开发验证用）──────────────────────────────
        _protoBox.ItemsSource = new[] { "SSH", "VNC" };
        _protoBox.SelectedIndex = 0;
        _protoBox.SelectionChanged += (_, _) => _portBox.Text = _protoBox.SelectedIndex == 0 ? "22" : "5900";
        _connectButton.Click += async (_, _) => await DirectConnectAsync();
        _disconnectButton.Click += async (_, _) => await DisconnectAsync();

        var quick = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "直连：", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray },
                _protoBox, _hostBox, _userBox, _passBox, _portBox, _connectButton, _disconnectButton,
            },
        };

        var hint = new TextBlock
        {
            Text = $"数据目录：{paths.DataDirectory}",
            Foreground = new SolidColorBrush(Color.Parse("#888888")),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 6),
        };

        Content = new DockPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                DockTop(hint),
                DockTop(new StackPanel { Margin = new Thickness(0, 0, 0, 12), Children = { quick } }),
                new Grid { ColumnDefinitions = new("330, *"), Children = { left, _sessionArea } },
            },
        };

        Closing += (_, _) => _ = DisconnectAsync();

        // 预填直连（运行验证钩子）
        _hostBox.Text = Environment.GetEnvironmentVariable("RF_HOST") ?? Environment.GetEnvironmentVariable("RF_SSH_HOST");
        _userBox.Text = Environment.GetEnvironmentVariable("RF_USER") ?? Environment.GetEnvironmentVariable("RF_SSH_USER");
        _passBox.Text = Environment.GetEnvironmentVariable("RF_PASS") ?? Environment.GetEnvironmentVariable("RF_SSH_PASS");

        Opened += async (_, _) => await ReloadAssetsAsync();

        if (Environment.GetEnvironmentVariable("RF_AUTOCONNECT") == "1")
        {
            Dispatcher.UIThread.Post(async () =>
            {
                if (Environment.GetEnvironmentVariable("RF_PROTO") == "vnc")
                {
                    _protoBox.SelectedIndex = 1;
                }

                await DirectConnectAsync();
            }, DispatcherPriority.Background);
        }
    }

    private static Control DockTop(Control c)
    {
        DockPanel.SetDock(c, Dock.Top);
        return c;
    }

    // ── 资产列表行模板 ───────────────────────────────────────────

    private static Control MakeConnectionRow(ConnectionItemViewModel c)
    {
        var host = new TextBlock { Text = c.HostDisplay, Foreground = Brushes.Gray, FontSize = 11 };
        var name = new TextBlock { Text = c.Name, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
        var stack = new StackPanel { Spacing = 1 };
        stack.Children.Add(name);
        stack.Children.Add(host);
        return new Border { Padding = new Thickness(4, 3), Child = stack };
    }

    private static Control MakeGroupRow(ConnectionGroupNodeViewModel g)
    {
        var text = new TextBlock
        {
            Text = g.IsUngrouped ? "（未分组）" : g.Name,
            Foreground = Brushes.DimGray,
            FontStyle = FontStyle.Italic,
            FontSize = 12,
        };
        return new Border { Padding = new Thickness(4, 3), Child = text };
    }

    // ── 资产加载 / 新建 / 打开 ───────────────────────────────────

    private async Task ReloadAssetsAsync()
    {
        try
        {
            _listStatus.Text = "加载连接…";
            await _connectionsVm.LoadAsync();
            var n = _connectionsVm.GroupedRows.Count(o => o is ConnectionItemViewModel);
            _listStatus.Text = $"{n} 条连接";
        }
        catch (Exception ex)
        {
            _listStatus.Text = $"加载失败：{ex.Message}";
        }
    }

    private async Task ShowNewConnectionDialogAsync()
    {
        var dlg = new ConnectionDialog();
        var result = await dlg.ShowDialog<ConnectionDialogResult?>(this);
        if (result is null)
        {
            return;
        }

        try
        {
            // 先把口令存进 Keychain，再建凭据元数据，最后建连接。
            var credential = new Credential
            {
                Name = $"{result.Name} 凭据",
                Type = result.Protocol == ProtocolType.Ssh
                    ? CredentialType.SshPassword
                    : CredentialType.VncPassword,
                Username = result.Username,
            };

            var created = await _credentials.CreateAsync(credential, result.Password, null);

            var profile = new ConnectionProfile
            {
                Name = result.Name,
                Host = result.Host,
                Port = result.Port,
                Protocol = result.Protocol,
                CredentialId = created.Id,
            };

            await _connections.CreateAsync(profile);
            await ReloadAssetsAsync();
            _listStatus.Text = $"已创建 {result.Name}";
        }
        catch (Exception ex)
        {
            _listStatus.Text = $"创建失败：{ex.Message}";
        }
    }

    private async Task OpenSelectedAsync()
    {
        if (_assetList.SelectedItem is not ConnectionItemViewModel item)
        {
            return;
        }

        await OpenProfileAsync(item.Profile);
    }

    private async Task OpenProfileAsync(ConnectionProfile profile)
    {
        try
        {
            SetBusy(true);
            _statusText.Text = $"正在连接 {profile.Name} …";

            var session = await _sessions.CreateSessionAsync(profile);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await session.ConnectAsync(cts.Token);

            if (session.State != ConnectionState.Connected)
            {
                SetBusy(false);
                _statusText.Text = $"连接失败：{session.ErrorMessage ?? session.ErrorCode.ToString()}";
                _statusText.Foreground = Brushes.OrangeRed;
                return;
            }

            switch (session)
            {
                case SshSession ssh:
                    _sshSession = ssh;
                    _sshView = new SshSessionView(ssh);
                    _sessionArea.Child = _sshView;
                    break;
                case VncSession vnc:
                    _vncSession = vnc;
                    _vncView = new VncSessionView(vnc);
                    _sessionArea.Child = _vncView;
                    break;
                default:
                    throw new NotSupportedException($"协议 {profile.Protocol} 尚无 macOS 会话视图。");
            }

            Title = $"{profile.Name} · RemoteFlow";
            _statusText.Text = $"已连接 {profile.Name}";
            _statusText.Foreground = new SolidColorBrush(Color.Parse("#2E8B57"));
            SetBusy(false);
            await ReloadAssetsAsync();
        }
        catch (Exception ex)
        {
            SetBusy(false);
            _statusText.Text = $"连接异常：{ex.Message}";
            _statusText.Foreground = Brushes.OrangeRed;
        }
    }

    private async Task DirectConnectAsync()
    {
        var host = _hostBox.Text?.Trim();
        var user = _userBox.Text?.Trim();
        var pass = _passBox.Text;
        var isSsh = _protoBox.SelectedIndex != 1;

        if (string.IsNullOrEmpty(host) || (isSsh && string.IsNullOrEmpty(pass)))
        {
            _statusText.Text = isSsh ? "SSH 需填主机与口令。" : "VNC 需填主机。";
            _statusText.Foreground = Brushes.Orange;
            return;
        }

        if (!int.TryParse(_portBox.Text?.Trim(), out var port) || port <= 0)
        {
            port = isSsh ? 22 : 5900;
        }

        var profile = new ConnectionProfile
        {
            Name = host,
            Host = host,
            Port = port,
            Protocol = isSsh ? ProtocolType.Ssh : ProtocolType.Vnc,
        };

        // 直连不入库：临时构造 ResolvedCredential 直接开会话。
        if (isSsh)
        {
            profile.Ssh.ConnectTimeoutSeconds = 15;
            var session = (SshSession)_sshProvider.CreateSession(new SessionRequest
            {
                Profile = profile,
                Credential = new ResolvedCredential
                {
                    Type = CredentialType.SshPassword,
                    Username = user ?? string.Empty,
                    Password = pass,
                },
                HostKeyPolicy = new DevTrustHostKeyPolicy(),
            });
            await session.ConnectAsync();

            if (session.State != ConnectionState.Connected)
            {
                _statusText.Text = $"连接失败：{session.ErrorMessage ?? session.ErrorCode.ToString()}";
                _statusText.Foreground = Brushes.OrangeRed;
                return;
            }

            _sshSession = session;
            _sshView = new SshSessionView(session);
            _sessionArea.Child = _sshView;
            Title = $"{host} · RemoteFlow";
            _statusText.Text = $"已连接 {host} (SSH)";
            _statusText.Foreground = new SolidColorBrush(Color.Parse("#2E8B57"));
            _connectButton.IsVisible = false;
            _disconnectButton.IsVisible = true;
        }
        else
        {
            profile.Vnc.ConnectTimeoutSeconds = 15;
            profile.Vnc.SharedConnection = true;
            var session = (VncSession)_vncProvider.CreateSession(new SessionRequest
            {
                Profile = profile,
                Credential = new ResolvedCredential
                {
                    Type = CredentialType.VncPassword,
                    Username = user ?? string.Empty,
                    Password = pass,
                },
            });
            await session.ConnectAsync();

            if (session.State != ConnectionState.Connected)
            {
                _statusText.Text = $"连接失败：{session.ErrorMessage ?? session.ErrorCode.ToString()}";
                _statusText.Foreground = Brushes.OrangeRed;
                return;
            }

            _vncSession = session;
            _vncView = new VncSessionView(session);
            _sessionArea.Child = _vncView;
            Title = $"{host} · RemoteFlow";
            _statusText.Text = $"已连接 {host} (VNC)";
            _statusText.Foreground = new SolidColorBrush(Color.Parse("#2E8B57"));
            _connectButton.IsVisible = false;
            _disconnectButton.IsVisible = true;
        }
    }

    private void SetBusy(bool busy) => _connectButton.IsEnabled = !busy;

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
    }
}

/// <summary>新建连接对话框的结果。</summary>
public sealed record ConnectionDialogResult(
    string Name, string Host, int Port, ProtocolType Protocol, string Username, string Password);
