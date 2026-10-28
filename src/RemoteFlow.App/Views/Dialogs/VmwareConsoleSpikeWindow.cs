using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MarcusW.VncClient;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing;
using MarcusW.VncClient.Protocol.SecurityTypes;
using MarcusW.VncClient.Security;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Vmware;
using RemoteFlow.Infrastructure.Vmware;
using RemoteFlow.Protocol.Vnc;
using ConnectParameters = MarcusW.VncClient.ConnectParameters;
using RfbConnection = MarcusW.VncClient.RfbConnection;
using VncLibClient = MarcusW.VncClient.VncClient;

namespace RemoteFlow.App.Views.Dialogs;

/// <summary>
/// ESXi 控制台验证窗口（--vmware-console-spike 启动）：SOAP 登录 → 选 VM → 取 webmks 票据 →
/// 经已核对指纹的 WSS 交给现有 VNC 库，由 WPF 直接显示。不保存地址、凭据或票据，也不改变虚拟机状态。
/// </summary>
public sealed class VmwareConsoleSpikeWindow : Window
{
    private readonly TextBox _endpoint = new() { Text = "https://", MinWidth = 200 };
    private readonly TextBox _userName = new() { MinWidth = 110 };
    private readonly PasswordBox _password = new() { MinWidth = 110 };
    private readonly TextBox _pin = new() { MinWidth = 380, FontFamily = new FontFamily("Consolas") };
    private readonly ComboBox _vms = new() { DisplayMemberPath = "Name", MinWidth = 200 };
    private readonly TextBlock _status = new() { Text = "先获取并核对证书指纹，再登录。", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _diag = new() { IsReadOnly = true, Height = 90, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12 };
    private readonly Queue<string> _diagLines = new();
    private readonly Image _view = new() { Stretch = Stretch.Uniform };
    private readonly CancellationTokenSource _lifetime = new();
    private VsphereConsoleApi? _api;
    private Uri? _connectedEndpoint;
    private string _trustedPin = string.Empty;
    private bool _busy;
    private bool _closed;
    private RfbConnection? _rfb;
    private VncRenderTarget? _target;
    private DispatcherTimer? _frameTimer;
    private WriteableBitmap? _bitmap;

    public VmwareConsoleSpikeWindow()
    {
        // 可选预填：避免把测试环境地址写进源码或每次手输。
        if (Environment.GetEnvironmentVariable("REMOTEFLOW_VMWARE_SPIKE_ENDPOINT") is { Length: > 0 } endpoint) _endpoint.Text = endpoint;
        Title = "VMware 控制台验证 · RemoteFlow";
        Width = 1100;
        Height = 780;
        MinWidth = 720;
        MinHeight = 500;
        SetResourceReference(TextElement.ForegroundProperty, "Text.Primary");
        SetResourceReference(BackgroundProperty, "Bg.Base");

        var root = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(new TextBlock { Text = "ESXi 8 控制台验证", FontSize = 20, Margin = new Thickness(0, 0, 0, 12) });
        var form = new WrapPanel();
        AddField(form, "HTTPS 主机地址", _endpoint);
        AddField(form, "用户名", _userName);
        AddField(form, "密码（不保存）", _password);
        AddField(form, "证书 SHA-256 指纹（核对后才登录）", _pin);
        header.Children.Add(form);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 10) };
        AddButton(actions, "获取证书指纹", FetchPinAsync);
        AddButton(actions, "登录并列出 VM", LoginAsync);
        actions.Children.Add(_vms);
        AddButton(actions, "连接控制台", ConnectAsync);
        AddButton(actions, "发送唤醒输入", () => { SendWakeInput(); return Task.CompletedTask; });
        AddButton(actions, "Ctrl+Alt+Del", () => { SendCad(); return Task.CompletedTask; });
        header.Children.Add(actions);
        header.Children.Add(_status);
        _diag.Margin = new Thickness(0, 8, 8, 8);
        header.Children.Add(_diag);
        root.Children.Add(header);
        root.Children.Add(new Border { Background = Brushes.Black, Child = _view });
        Content = root;
        Closed += OnClosed;
    }

    private static void AddField(Panel panel, string label, Control control)
    {
        var field = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        field.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) });
        field.Children.Add(control);
        panel.Children.Add(field);
    }

    private void AddButton(Panel panel, string label, Func<Task> action)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(10, 5, 10, 5) };
        button.Click += async (_, _) =>
        {
            if (_busy || _closed) return;
            _busy = true;
            try { await action(); }
            catch (OperationCanceledException) { if (!_closed) _status.Text = "已取消。"; }
            catch (Exception ex)
            {
                // 不记录原始异常，防止异常消息携带票据或认证数据。
                if (!_closed) _status.Text = ex switch
                {
                    VsphereApiException api => api.Message,
                    HttpRequestException => "无法建立安全 API 连接：证书指纹不匹配或网络不通。",
                    _ => "操作失败：" + ex.GetType().Name
                };
            }
            finally { _busy = false; }
        };
        panel.Children.Add(button);
    }

    private void AddDiag(string line)
    {
        _diagLines.Enqueue(line.Length > 300 ? line[..300] + "…" : line);
        while (_diagLines.Count > 14) _diagLines.Dequeue();
        _diag.Text = string.Join(Environment.NewLine, _diagLines);
        _diag.ScrollToEnd();
    }

    private bool TryEndpoint(out Uri endpoint)
    {
        // 用户可能直接粘贴 Host Client 登录页地址，这里只取主机与端口。
        if (Uri.TryCreate(_endpoint.Text.Trim(), UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps)
        {
            endpoint = new UriBuilder(Uri.UriSchemeHttps, parsed.Host, parsed.IsDefaultPort ? 443 : parsed.Port).Uri;
            return true;
        }
        endpoint = null!;
        return false;
    }

    private async Task FetchPinAsync()
    {
        if (!TryEndpoint(out var endpoint)) { _status.Text = "请输入 HTTPS 主机地址。"; return; }
        _status.Text = "正在读取证书指纹…";
        _pin.Text = await VmwareTls.FetchPinAsync(endpoint.Host, endpoint.Port, _lifetime.Token);
        _status.Text = "已读取证书指纹。请先与 ESXi 证书信息核对，确认无误再登录；之后只接受该指纹。";
    }

    private async Task LoginAsync()
    {
        if (!TryEndpoint(out var endpoint)) { _status.Text = "请输入 HTTPS 主机地址。"; return; }
        _trustedPin = VmwareTls.NormalizePin(_pin.Text);
        if (_trustedPin.Length == 0) { _status.Text = "请先点击“获取证书指纹”，核对后再登录。"; return; }
        await StopNativeAsync();
        if (_api is not null) { await _api.DisposeAsync(); _api = null; }
        _vms.ItemsSource = null;
        _connectedEndpoint = null;
        _status.Text = "正在登录并读取虚拟机…";
        var api = new VsphereConsoleApi(endpoint, VmwareTls.CreatePinnedHandler(_trustedPin));
        try
        {
            var password = _password.Password;
            try { await api.LoginAsync(_userName.Text.Trim(), password, _lifetime.Token); }
            finally { _password.Clear(); }
            var vms = await api.ListVirtualMachinesAsync(_lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            _api = api;
            _connectedEndpoint = endpoint;
            _vms.ItemsSource = vms;
            _vms.SelectedIndex = vms.Count > 0 ? 0 : -1;
            _status.Text = $"登录成功，读取到 {vms.Count} 台 VM。选择运行中的 VM 连接控制台。";
        }
        catch { await api.DisposeAsync(); throw; }
    }

    private async Task ConnectAsync()
    {
        if (_api is null || _vms.SelectedItem is not VsphereVm vm || _connectedEndpoint is null)
        {
            _status.Text = "请先登录并选择虚拟机。";
            return;
        }
        if (vm.PowerState != "poweredOn") { _status.Text = "该 VM 未运行。验证入口不执行开机。"; return; }
        await StopNativeAsync();
        var ticket = await _api.AcquireWebMksTicketAsync(vm.Reference, _lifetime.Token);
        if (!string.Equals(ticket.Host, _connectedEndpoint.Host, StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = $"票据目标主机“{ticket.Host}”与登录主机不同，已阻止连接。";
            return;
        }
        var target = new VncRenderTarget();
        target.FrameSizeChanged += (_, size) => Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            _bitmap = new WriteableBitmap(size.Width, size.Height, 96, 96, PixelFormats.Bgra32, null);
            _view.Source = _bitmap;
        });
        var parameters = new ConnectParameters
        {
            TransportParameters = new WebSocketTransportParameters
            {
                Url = new UriBuilder("wss", ticket.Host, ticket.Port) { Path = "/ticket/" + Uri.EscapeDataString(ticket.Ticket) }.Uri,
                PinSha256 = _trustedPin
            },
            AuthenticationHandler = new RejectAuthentication(),
            InitialRenderTarget = target,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AllowSharedConnection = true,
            MaxReconnectAttempts = 0
        };
        _status.Text = "正在连接控制台…";
        var client = new VncLibClient(NullLoggerFactory.Instance, new WebSocketVncImplementation());
        try { _rfb = await client.ConnectAsync(parameters, _lifetime.Token); }
        catch { target.Dispose(); throw; }
        _target = target;
        _frameTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _frameTimer.Tick += (_, _) => PaintFrame();
        _frameTimer.Start();
        var size = _rfb.RemoteFramebufferSize;
        AddDiag($"已连接：{_rfb.ProtocolVersion}，安全类型 {_rfb.UsedSecurityType?.Name}，桌面 {size.Width}x{size.Height}");
        _status.Text = "已连接。若画面全黑，点“发送唤醒输入”（虚拟显示器可能在休眠）。";
    }

    private void PaintFrame()
    {
        if (_bitmap is null || _target is null) return;
        _bitmap.Lock();
        try
        {
            var capacity = (long)_bitmap.BackBufferStride * _bitmap.PixelHeight;
            if (_target.TryCopyLatestFrame(_bitmap.BackBuffer, capacity, _bitmap.BackBufferStride, _bitmap.PixelWidth, _bitmap.PixelHeight))
                _bitmap.AddDirtyRect(new Int32Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight));
        }
        finally { _bitmap.Unlock(); }
    }

    private void SendWakeInput()
    {
        if (_rfb is null) { _status.Text = "尚未连接控制台。"; return; }
        // 先动一下鼠标，再点一下 Shift：不会触发任何客户机操作，只用于唤醒休眠的虚拟显示器。
        _rfb.EnqueueMessage(new PointerEventMessage(new Position(10, 10), MouseButtons.None));
        _rfb.EnqueueMessage(new PointerEventMessage(new Position(30, 30), MouseButtons.None));
        _rfb.EnqueueMessage(new KeyEventMessage(true, KeySymbol.Shift_L));
        _rfb.EnqueueMessage(new KeyEventMessage(false, KeySymbol.Shift_L));
        AddDiag("已发送唤醒输入。");
    }

    private void SendCad()
    {
        if (_rfb is null) { _status.Text = "尚未连接控制台。"; return; }
        KeySymbol[] keys = [KeySymbol.Control_L, KeySymbol.Alt_L, KeySymbol.Delete];
        foreach (var key in keys) _rfb.EnqueueMessage(new KeyEventMessage(true, key));
        foreach (var key in keys.Reverse()) _rfb.EnqueueMessage(new KeyEventMessage(false, key));
        AddDiag("已发送 Ctrl+Alt+Del。");
    }

    private async Task StopNativeAsync()
    {
        _frameTimer?.Stop();
        _frameTimer = null;
        var rfb = _rfb;
        _rfb = null;
        if (rfb is not null)
        {
            try { await rfb.CloseAsync(); } catch { }
            try { rfb.Dispose(); } catch { }
        }
        _target?.Dispose();
        _target = null;
        _bitmap = null;
        _view.Source = null;
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _lifetime.Cancel();
        await StopNativeAsync();
        if (_api is not null)
        {
            try { await _api.DisposeAsync(); }
            catch { /* 窗口已关闭，不输出可能含敏感数据的清理异常。 */ }
        }
    }

    /// <summary>票据流不应再要求口令；若要求，立即失败而不是猜测。</summary>
    private sealed class RejectAuthentication : IAuthenticationHandler
    {
        public Task<TInput> ProvideAuthenticationInputAsync<TInput>(
            RfbConnection connection, ISecurityType securityType, IAuthenticationInputRequest<TInput> request)
            where TInput : class, IAuthenticationInput
            => throw new InvalidOperationException("服务器要求认证输入，票据流不应出现这种情况。");
    }
}
