using System.IO;
using System.Net.Http;
using System.Windows.Documents;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RemoteFlow.Core.Vmware;
using RemoteFlow.Infrastructure.Vmware;

namespace RemoteFlow.App.Views.Dialogs;

/// <summary>独立验证入口，不保存地址、凭据或票据；生产来源集成前用于验证目标 SDK 与 Runtime。</summary>
public sealed class VmwareConsoleSpikeWindow : Window
{
    private readonly TextBox _endpoint = new() { Text = "https://", MinWidth = 180 };
    private readonly TextBox _userName = new() { MinWidth = 100 };
    private readonly PasswordBox _password = new() { MinWidth = 100 };
    private readonly TextBox _sdkFolder = new() { MinWidth = 180 };
    private readonly TextBox _pin = new() { MinWidth = 360, FontFamily = new FontFamily("Consolas") };
    private string _trustedPin = string.Empty;
    private static readonly string[] SdkFiles = ["wmks.min.js", "jquery.min.js", "jquery-ui.min.js", "css/wmks-all.css"];
    private readonly ComboBox _vms = new() { DisplayMemberPath = "Name", MinWidth = 180 };
    private readonly TextBlock _status = new() { Text = "先登录 ESXi，再选择 VM。SDK 目录需包含 wmks.min.js 及其依赖资源。", TextWrapping = TextWrapping.Wrap };
    private readonly WebView2 _web = new();
    private readonly CancellationTokenSource _lifetime = new();
    private VsphereConsoleApi? _api;
    private Uri? _connectedEndpoint;
    private bool _busy;
    private bool _pageReady;
    private bool _closed;
    private string? _assetDirectory;

    public VmwareConsoleSpikeWindow()
    {
        Title = "VMware 控制台验证 · RemoteFlow";
        Width = 1100;
        Height = 760;
        MinWidth = 720;
        MinHeight = 500;
        SetResourceReference(TextElement.ForegroundProperty, "Text.Primary");
        SetResourceReference(BackgroundProperty, "Bg.Base");
        var root = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(new TextBlock { Text = "ESXi 8 内嵌控制台验证", FontSize = 20, Margin = new Thickness(0, 0, 0, 12) });
        var form = new WrapPanel();
        AddField(form, "HTTPS 主机地址", _endpoint);
        AddField(form, "用户名", _userName);
        AddField(form, "密码（不保存）", _password);
        AddField(form, "本机 SDK 目录", _sdkFolder);
        AddField(form, "证书 SHA-256 指纹（核对后才登录）", _pin);
        header.Children.Add(form);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 10) };
        AddButton(actions, "获取证书指纹", FetchPinAsync);
        AddButton(actions, "登录并列出 VM", LoginAsync);
        actions.Children.Add(_vms);
        AddButton(actions, "打开 / 重连控制台", OpenConsoleAsync);
        AddButton(actions, "Ctrl+Alt+Del", SendSecureAttentionAsync);
        header.Children.Add(actions);
        header.Children.Add(_status);
        header.Children.Add(new TextBlock
        {
            Text = "验证入口：不更改虚拟机电源和快照。SDK 使用须符合随附许可。当前仅使用系统 TLS 信任，不绕过自签名证书。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12)
        });
        root.Children.Add(header);
        root.Children.Add(new Border { Background = Brushes.Black, Child = _web });
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
                if (!_closed) _status.Text = ex switch
                {
                    VsphereApiException api => api.Message,
                    HttpRequestException => "无法建立安全 API 连接。请检查网络及系统证书信任；当前不绕过 TLS 验证。",
                    _ => "验证操作失败。请检查地址、SDK 资源与 WebView2 Runtime。"
                };
                // 不记录原始异常，防止浏览器或 HTTP 异常携带票据/认证数据。
            }
            finally { _busy = false; }
        };
        panel.Children.Add(button);
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
        var pin = await VmwareTls.FetchPinAsync(endpoint.Host, endpoint.Port, _lifetime.Token);
        _pin.Text = pin;
        _status.Text = "已读取证书指纹。请先与 ESXi 证书信息核对，确认无误再登录；登录将只接受该指纹。";
    }

    private async Task LoginAsync()
    {
        if (!TryEndpoint(out var endpoint))
        {
            _status.Text = "请输入 HTTPS 主机地址，不带 /ui/ 或登录页面片段。";
            return;
        }
        _trustedPin = VmwareTls.NormalizePin(_pin.Text);
        if (_trustedPin.Length == 0)
        {
            _status.Text = "请先点击“获取证书指纹”，核对后再登录。";
            return;
        }
        if (_api is not null)
        {
            await _api.DisposeAsync();
            _api = null;
        }
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
            _status.Text = $"登录成功，读取到 {vms.Count} 台 VM。选择运行中的 VM 打开控制台。";
        }
        catch { await api.DisposeAsync(); throw; }
    }

    private async Task OpenConsoleAsync()
    {
        if (_api is null || _vms.SelectedItem is not VsphereVm vm)
        {
            _status.Text = "请先登录并选择虚拟机。";
            return;
        }
        if (vm.PowerState != "poweredOn")
        {
            _status.Text = "该 VM 未运行。验证入口不执行开机，请在 ESXi 中选择一台运行中的测试 VM。";
            return;
        }
        if (string.IsNullOrWhiteSpace(_sdkFolder.Text)) { _status.Text = "请填写本机 SDK 目录。"; return; }
        var folder = Path.GetFullPath(_sdkFolder.Text.Trim());
        var missing = SdkFiles.Where(f => !File.Exists(Path.Combine(folder, f))).ToArray();
        if (missing.Length > 0)
        {
            _status.Text = "SDK 目录缺少：" + string.Join("、", missing) + "。请使用合法取得的 SDK 及其依赖。";
            return;
        }
        if (_assetDirectory is null || !string.Equals(_assetDirectory, folder, StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = "SDK 路径确定后需重新打开此验证窗口，避免混用前端资源。";
            if (_assetDirectory is not null) return;
            await InitializeRendererAsync(folder);
        }
        if (!_pageReady)
        {
            _status.Text = "控制台页面正在加载；出现“页面已就绪”后再次点击打开。";
            return;
        }
        var ticket = await _api.AcquireWebMksTicketAsync(vm.Reference, _lifetime.Token);
        // 首版验证限定与用户登录端点同主机。跨主机票据须先有独立信任与授权记录。
        if (!string.Equals(ticket.Host, _connectedEndpoint!.Host, StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = "票据目标与登录主机不同，已阻止连接；需先实现独立端点验证。";
            return;
        }
        var target = new UriBuilder("wss", ticket.Host, ticket.Port) { Path = "/ticket/" + Uri.EscapeDataString(ticket.Ticket) }.Uri.AbsoluteUri;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "connect", url = target }));
        _status.Text = "正在连接控制台；以实际画面与输入验证结果为准。";
    }

    private async Task InitializeRendererAsync(string folder)
    {
        var environment = await CoreWebView2Environment.CreateAsync(null,
            Path.Combine(Path.GetTempPath(), "RemoteFlow", "VmwareSpike", Guid.NewGuid().ToString("N")));
        _lifetime.Token.ThrowIfCancellationRequested();
        await _web.EnsureCoreWebView2Async(environment);
        _lifetime.Token.ThrowIfCancellationRequested();
        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.SetVirtualHostNameToFolderMapping("vmware-sdk.example", folder, CoreWebView2HostResourceAccessKind.DenyCors);
        core.NavigationStarting += (_, e) => { if (e.Uri != "about:blank") e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        // WSS 走浏览器自己的 TLS：仅当目标主机与登录主机一致且证书指纹与已核对指纹一致才放行。
        core.ServerCertificateErrorDetected += (_, e) =>
        {
            var uri = new Uri(e.RequestUri);
            using var cert = e.ServerCertificate.ToX509Certificate2();
            e.Action = uri.Scheme == "wss" && _connectedEndpoint is not null
                && string.Equals(uri.Host, _connectedEndpoint.Host, StringComparison.OrdinalIgnoreCase)
                && VmwareTls.PinMatches(cert, _trustedPin)
                ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow
                : CoreWebView2ServerCertificateErrorAction.Cancel;
        };
        core.WebMessageReceived += (_, e) =>
        {
            if (_closed || e.Source != "about:blank") return;
            var message = e.TryGetWebMessageAsString();
            if (message == "ready") { _pageReady = true; _status.Text = "控制台页面已就绪，请再次点击打开控制台。"; }
            else if (message == "connected") _status.Text = "渲染器报告已连接，请检查画面和键鼠。";
            else if (message == "disconnected") _status.Text = "控制台已断开，可重新获取票据连接。";
            else if (message == "error") _status.Text = "渲染器连接失败，请检查 SDK 依赖、WSS 网络和证书。";
        };
        _assetDirectory = folder;
        core.NavigateToString(RendererHtml);
    }

    private async Task SendSecureAttentionAsync()
    {
        if (_pageReady) await _web.ExecuteScriptAsync("window.remoteFlowSendCad?.()");
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _lifetime.Cancel();
        _web.Dispose();
        if (_api is not null)
        {
            try { await _api.DisposeAsync(); }
            catch { /* 窗口已关闭，不将含敏感数据的清理异常输出到日志。 */ }
        }
    }

    private const string RendererHtml = """
        <!doctype html><html><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src https://vmware-sdk.example 'unsafe-inline'; style-src https://vmware-sdk.example 'unsafe-inline'; img-src https://vmware-sdk.example data:; connect-src wss:;">
        <link rel="stylesheet" href="https://vmware-sdk.example/css/wmks-all.css">
        <style>html,body,#console{margin:0;width:100%;height:100%;overflow:hidden;background:#12131a;color:white}</style>
        <script src="https://vmware-sdk.example/jquery.min.js"></script>
        <script src="https://vmware-sdk.example/jquery-ui.min.js"></script>
        <script src="https://vmware-sdk.example/wmks.min.js"></script></head><body><div id="console"></div><script>
        let renderer=null;
        try {
          if(!window.WMKS)throw new Error();
          renderer=WMKS.createWMKS('console',{});
          renderer.register(WMKS.CONST.Events.CONNECTION_STATE_CHANGE,function(sender,data){
            const state=data.state;
            if(state===WMKS.CONST.ConnectionState.CONNECTED)chrome.webview.postMessage('connected');
            else if(state===WMKS.CONST.ConnectionState.DISCONNECTED)chrome.webview.postMessage('disconnected');
          });
          window.addEventListener('resize',()=>renderer.updateScreen());
          window.remoteFlowSendCad=()=>renderer.sendCAD();
          chrome.webview.addEventListener('message',e=>{
            if(e.data.type==='connect'){
              try{renderer.disconnect();renderer.connect(e.data.url)}catch{chrome.webview.postMessage('error')}
            }
          });
          chrome.webview.postMessage('ready');
        }catch{chrome.webview.postMessage('error')}
        </script></body></html>
        """;
}
