using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Infrastructure;
using RemoteFlow.Presentation.ViewModels;
using RemoteFlow.Protocol.Ssh;
using RemoteFlow.Protocol.Vnc;
using AppServices = RemoteFlow.Application.Services;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 主窗口 —— 两栏工作台：左「连接资产」列表，右会话区。
/// 资产复用共享 ConnectionsPageViewModel（Filter=All）；新建经共享服务 + Keychain 落库；
/// 打开经 SessionManager 开会话。
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly IBrush SurfaceBrush = new SolidColorBrush(Color.Parse("#0D000000"));
    private static readonly IBrush DividerBrush = new SolidColorBrush(Color.Parse("#1F000000"));

    private readonly AppServices.ConnectionService _connections;
    private readonly AppServices.CredentialService _credentials;
    private readonly AppServices.SessionManager _sessions;
    private readonly ConnectionsPageViewModel _connectionsVm;

    private readonly Border _sessionArea = new() { Background = Brushes.Black, CornerRadius = new CornerRadius(8), ClipToBounds = true };
    private readonly ListBox _assetList = new();
    private readonly TextBlock _countText = new() { Foreground = Brushes.Gray, FontSize = 12 };
    private readonly Button _disconnectButton = new() { Content = "断开", IsVisible = false };
    private readonly TextBlock _sessionTitle = new() { VerticalAlignment = VerticalAlignment.Center };

    private SshSessionView? _sshView;
    private VncSessionView? _vncView;

    public MainWindow(
        AppPaths paths,
        AppServices.ConnectionService connections,
        AppServices.CredentialService credentials,
        AppServices.SessionManager sessions,
        ConnectionsPageViewModel connectionsVm)
    {
        _connections = connections;
        _credentials = credentials;
        _sessions = sessions;
        _connectionsVm = connectionsVm;

        Title = "RemoteFlow";
        Width = 1200;
        Height = 760;
        MinWidth = 960;
        MinHeight = 560;

        BuildUi(paths);

        Closing += (_, _) => _ = DisconnectAsync();

        Opened += async (_, _) =>
        {
            await ReloadAssetsAsync();
            await SeedDemoIfRequestedAsync();
            await AutoOpenIfRequestedAsync();
        };
    }

    private void BuildUi(AppPaths paths)
    {
        // ── 顶栏 ────────────────────────────────────────────────
        var appTitle = new TextBlock { Text = "RemoteFlow", FontSize = 17, FontWeight = FontWeight.SemiBold };
        _disconnectButton.Click += async (_, _) => await DisconnectAsync();

        var sessionBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { _sessionTitle, _disconnectButton },
        };

        var header = new Grid { ColumnDefinitions = new("*, Auto") };
        header.Children.Add(appTitle);
        header.Children.Add(sessionBar);
        Grid.SetColumn(sessionBar, 1);

        // ── 左栏工具栏 ──────────────────────────────────────────
        _connectionsVm.Filter = ConnectionFilter.All;
        _assetList.ItemsSource = _connectionsVm.GroupedRows;
        _assetList.ItemTemplate = new FuncDataTemplate<object>((item, _) => item switch
        {
            ConnectionItemViewModel c => ConnectionRow(c),
            ConnectionGroupNodeViewModel g => GroupHeader(g),
            _ => null,
        });
        _assetList.DoubleTapped += async (_, _) => await OpenSelectedAsync();
        _assetList.SelectionChanged += (_, _) => { };

        var newButton = new Button { Content = "＋ 新建连接" };
        newButton.Click += async (_, _) => await ShowNewConnectionDialogAsync();
        var refreshButton = new Button { Content = "刷新" };
        refreshButton.Click += async (_, _) => await ReloadAssetsAsync();

        var label = new TextBlock
        {
            Text = "连接资产",
            FontSize = 11,
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var toolbarRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { refreshButton, newButton },
        };
        var toolbar = new Grid { ColumnDefinitions = new("*, Auto") };
        toolbar.Children.Add(label);
        toolbar.Children.Add(toolbarRight);
        Grid.SetColumn(toolbarRight, 1);

        var leftPanel = new Border
        {
            Background = SurfaceBrush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 14, 0),
            Child = new DockPanel
            {
                Children =
                {
                    DockTop(toolbar),
                    DockBottom(new Border { Padding = new Thickness(0, 8, 0, 0), Child = _countText }),
                    new ScrollViewer { Content = _assetList },
                },
            },
        };

        // ── 右栏空态 ────────────────────────────────────────────
        ShowSessionNotice("暂无会话", false);
        _sessionArea.Child = new Border
        {
            Background = SurfaceBrush,
            Child = new StackPanel
            {
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "暂无会话", FontSize = 20, FontWeight = FontWeight.Medium, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "从左侧选择一个连接资产，或点「＋ 新建连接」。", Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };

        // ── 主体 ────────────────────────────────────────────────
        var body = new Grid { ColumnDefinitions = new("352, 1, *") };
        body.Children.Add(leftPanel);
        var divider = new Border { Background = DividerBrush };
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(_sessionArea, 2);
        _sessionArea.Margin = new Thickness(0, 0, 0, 0);
        body.Children.Add(divider);
        body.Children.Add(_sessionArea);

        var dataHint = new TextBlock
        {
            Text = paths.DataDirectory,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#66000000")),
            Margin = new Thickness(2, 10, 2, 0),
        };

        var root = new DockPanel { Margin = new Thickness(20, 14, 20, 16) };
        root.Children.Add(DockTop(header));
        root.Children.Add(DockTop(dataHint));
        root.Children.Add(body);
        Content = root;
    }

    private static Control DockTop(Control c) { DockPanel.SetDock(c, Dock.Top); return c; }
    private static Control DockBottom(Control c) { DockPanel.SetDock(c, Dock.Bottom); return c; }

    private static Control ConnectionRow(ConnectionItemViewModel c)
    {
        var icon = new TextBlock { Text = c.ProtocolIcon, FontSize = 16, Width = 26, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = c.Name, FontSize = 13, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
        var sub = new TextBlock { Text = $"{c.HostDisplay}  ·  {c.ProtocolName}", FontSize = 11, Foreground = Brushes.Gray };
        var col = new StackPanel { Spacing = 1, Margin = new Thickness(8, 0, 0, 0), Children = { name, sub } };
        var grid = new Grid { ColumnDefinitions = new("Auto, *") };
        grid.Children.Add(icon);
        grid.Children.Add(col);
        Grid.SetColumn(col, 1);
        return new Border { Padding = new Thickness(8, 7), Child = grid };
    }

    private static Control GroupHeader(ConnectionGroupNodeViewModel g)
    {
        var text = new TextBlock
        {
            Text = g.IsUngrouped ? "（未分组）" : g.Name,
            FontSize = 11,
            FontStyle = FontStyle.Italic,
            Foreground = Brushes.Gray,
        };
        return new Border { Padding = new Thickness(10, 10, 10, 3), Child = text };
    }

    private async Task ReloadAssetsAsync()
    {
        try
        {
            await _connectionsVm.LoadAsync();
            var n = _connectionsVm.GroupedRows.Count(o => o is ConnectionItemViewModel);
            _countText.Text = n == 0 ? "暂无连接" : $"{n} 条连接";
        }
        catch (Exception ex)
        {
            _countText.Text = "加载失败";
            Console.Error.WriteLine($"[assets] {ex}");
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
            var credential = new Credential
            {
                Name = $"{result.Name} 凭据",
                Type = result.Protocol == ProtocolType.Ssh ? CredentialType.SshPassword : CredentialType.VncPassword,
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
        }
        catch (Exception ex)
        {
            _countText.Text = "创建失败";
            Console.Error.WriteLine($"[new] {ex}");
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
            var session = await _sessions.CreateSessionAsync(profile);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await session.ConnectAsync(cts.Token);

            if (session.State != ConnectionState.Connected)
            {
                ShowSessionNotice(session.ErrorMessage ?? session.ErrorCode.ToString(), true);
                return;
            }

            switch (session)
            {
                case SshSession ssh:
                    _sshView = new SshSessionView(ssh);
                    break;
                case VncSession vnc:
                    _vncView = new VncSessionView(vnc);
                    break;
                default:
                    throw new NotSupportedException($"协议 {profile.Protocol} 尚无 macOS 会话视图。");
            }

            _sessionArea.Child = _sshView ?? (Control?)_vncView;
            _sessionTitle.Text = profile.Name;
            _sessionTitle.Foreground = new SolidColorBrush(Color.Parse("#7CB342"));
            _disconnectButton.IsVisible = true;
            Title = $"{profile.Name} · RemoteFlow";
            await ReloadAssetsAsync();
        }
        catch (Exception ex)
        {
            ShowSessionNotice(ex.Message, true);
        }
    }

    private void ShowSessionNotice(string text, bool isError)
    {
        var notice = new TextBlock
        {
            Text = text,
            Foreground = isError ? Brushes.OrangeRed : Brushes.Gray,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
        };
        _sessionArea.Child = new Border
        {
            Background = isError ? new SolidColorBrush(Color.Parse("#24C0392B")) : SurfaceBrush,
            Child = notice,
        };
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

        _disconnectButton.IsVisible = false;
        _sessionTitle.Text = "";
        Title = "RemoteFlow";
        ShowSessionNotice("会话已断开。", false);
    }

    /// <summary>演示钩子：RF_AUTOOPEN=1 时启动即打开第一条连接资产（无需手动双击）。</summary>
    private async Task AutoOpenIfRequestedAsync()
    {
        if (Environment.GetEnvironmentVariable("RF_AUTOOPEN") != "1")
        {
            return;
        }

        var first = (await _connections.GetAllAsync()).FirstOrDefault();
        if (first is not null)
        {
            await OpenProfileAsync(first);
        }
    }

    private async Task SeedDemoIfRequestedAsync()
    {
        if (Environment.GetEnvironmentVariable("RF_DEMO_SEED") != "1")
        {
            return;
        }

        var profiles = await _connections.GetAllAsync();
        if (profiles.Count > 0)
        {
            return;
        }

        var host = Environment.GetEnvironmentVariable("RF_DEMO_HOST") ?? "192.0.2.20";
        var user = Environment.GetEnvironmentVariable("RF_DEMO_USER") ?? "rootadmin";
        var pass = Environment.GetEnvironmentVariable("RF_SSH_PASS");
        if (string.IsNullOrEmpty(pass))
        {
            return;
        }

        var credential = await _credentials.CreateAsync(
            new Credential { Name = "示例 SSH 主机凭据", Type = CredentialType.SshPassword, Username = user },
            pass, null);
        await _connections.CreateAsync(new ConnectionProfile
        {
            Name = "示例 SSH 主机",
            Host = host,
            Port = 22,
            Protocol = ProtocolType.Ssh,
            CredentialId = credential.Id,
        });
        await ReloadAssetsAsync();
    }
}

/// <summary>新建连接对话框的结果。</summary>
public sealed record ConnectionDialogResult(
    string Name, string Host, int Port, ProtocolType Protocol, string Username, string Password);
