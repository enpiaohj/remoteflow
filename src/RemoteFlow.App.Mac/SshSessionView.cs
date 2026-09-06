using System.Text;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Presentation.Terminal;
using RemoteFlow.Protocol.Ssh;

namespace RemoteFlow.App.Mac;

/// <summary>
/// SSH 会话视图：Avalonia WebView（macOS 下 WKWebView）承载 xterm.js 终端。
/// <para>
/// 数据流：远端字节（SshSession.DataReceived，协议线程）→ 封送 UI 线程 →
/// <c>window.rfTerminal.write(base64)</c>；用户键入由页面 <c>post()</c> 经
/// <c>window.webkit.messageHandlers</c> 回宿主 → <see cref="NativeWebView.WebMessageReceived"/>
/// → <see cref="SshSession.SendInput"/>。页面与宿主的具体通道契约见
/// <c>RemoteFlow.Presentation/Terminal/Assets/terminal.html</c> 的 <c>post()</c>。
/// </para>
/// <para>
/// <b>必须遵守 Phase 0 门禁绕法：</b>WKWebView 在宿主尚无确定尺寸时创建会导致
/// <c>NSView initWithFrame</c> 收到非法几何而 SIGILL。因此本控件把 WebView 挂到
/// 一个已入树的容器，等 <see cref="Loaded"/>（布局完成、Bounds 确定）后再挂载并导航。
/// </para>
/// </summary>
public sealed class SshSessionView : UserControl
{
    private readonly SshSession _session;
    private readonly NativeWebView _web = new();
    private readonly Decorator _host = new();
    private readonly TextBlock _connecting = new()
    {
        Text = "正在连接…",
        Foreground = Brushes.Gray,
        FontSize = 15,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };

    private bool _webReady;
    private readonly object _inputGate = new();

    /// <summary>Web 就绪前到达的远端字节（登录 banner 常早于页面导航完成）。有界，防无限增长。</summary>
    private readonly List<byte[]> _pending = new();
    private int _pendingBytes;

    public SshSessionView(SshSession session)
    {
        _session = session;

        Background = Brushes.Black;
        Content = new Grid
        {
            Children =
            {
                _connecting,
                _host,
            },
        };

        // 远端数据（协议线程）→ UI 线程 → 写入终端。
        _session.DataReceived += OnDataReceived;
        _session.StateChanged += OnStateChanged;

        _web.NavigationCompleted += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            _webReady = true;

            // 重放登录前收到的 banner，避免连接早于页面就绪时丢内容。
            var pending = new List<byte[]>(_pending);
            _pending.Clear();
            _pendingBytes = 0;
            foreach (var chunk in pending)
            {
                await WriteToTerminalAsync(chunk);
            }

            await FitAsync();
        });

        // JS → 宿主：用户键入（JSON {type:'input', data:<base64>}）→ 发往远端。
        _web.WebMessageReceived += (_, e) =>
        {
            Dispatcher.UIThread.Post(() => HandlePageMessage(e.Body));
        };

        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // 延迟到布局完成、宿主有确定尺寸后再创建 WKWebView（SIGILL 绕法）。
        Dispatcher.UIThread.Post(async () =>
        {
            var terminalDir = TerminalAssetStore.EnsureAvailable();
            var html = Path.Combine(terminalDir, "terminal.html");

            _host.Child = _web;
            _web.Navigate(new Uri(html));
        }, DispatcherPriority.Background);
    }

    private void OnDataReceived(object? sender, byte[] data)
    {
        // 协议线程回调：封送到 UI 线程再写终端。
        Dispatcher.UIThread.Post(() =>
        {
            // 运行验证诊断（RF_VERIFY 时）：只报字节数，避免把可能的二进制/乱码打进终端日志。
            if (Environment.GetEnvironmentVariable("RF_VERIFY") == "1")
            {
                Console.WriteLine($"[RF][rx] {data.Length} bytes");
            }

            if (!_webReady)
            {
                // 页面未就绪：入有界缓冲，就绪后重放。
                if (_pendingBytes + data.Length < 32 * 1024)
                {
                    _pending.Add(data);
                    _pendingBytes += data.Length;
                }

                return;
            }

            _ = WriteToTerminalAsync(data);
        });
    }

    private void OnStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (e.NewState is ConnectionState.Failed or ConnectionState.Disconnected)
            {
                _connecting.Text = $"会话{e.NewState switch
                {
                    ConnectionState.Failed => "失败",
                    ConnectionState.Disconnected => "已断开",
                    _ => "结束"
                }}：{_session.ErrorMessage ?? _session.ErrorCode.ToString()}";
                _connecting.IsVisible = true;
            }
        });
    }

    private async Task WriteToTerminalAsync(byte[] data)
    {
        var payload = Convert.ToBase64String(data);
        await Invoke($"window.rfTerminal && window.rfTerminal.write('{payload}')");
    }

    /// <summary>读回 xterm 当前可见文本（验证钩子用，避免截屏权限限制）。</summary>
    public async Task<string> ReadTerminalTextAsync()
    {
        var text = await Invoke(
            "(function(){var r=document.querySelector('.xterm-rows');" +
            "return r ? r.innerText.slice(0, 500) : '(no-xterm-rows)';})()");
        return text;
    }

    private void HandlePageMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || !_webReady)
        {
            return;
        }

        try
        {
            // 页面 post() 序列化为 JSON：{type:'input', data:<base64 utf8>}
            var (type, dataBase64) = TryParseMessage(body);
            if (type != "input" || dataBase64 is null)
            {
                return;
            }

            var bytes = Convert.FromBase64String(dataBase64);
            lock (_inputGate)
            {
                _session.SendInput(bytes);
            }
        }
        catch (Exception)
        {
            // 消息格式异常只丢弃，不影响会话。
        }
    }

    private static (string Type, string? DataBase64) TryParseMessage(string body)
    {
        // 不引第三方 JSON：此协议自产自销，字段极简，手写解析足够。
        var type = Extract(body, "\"type\":\"", "\"");
        var data = Extract(body, "\"data\":\"", "\"");
        return (type, data);
    }

    private static string Extract(string source, string after, string until)
    {
        var start = source.IndexOf(after, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += after.Length;
        var end = source.IndexOf(until, start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    /// <summary>执行 JS，返回其字符串结果。页面未就绪时安全返回。</summary>
    private async Task<string> Invoke(string script)
    {
        if (!_webReady)
        {
            return string.Empty;
        }

        try
        {
            return await _web.InvokeScript(script) ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>让 xterm 适应容器，并把实际行列数回传 SshSession 供远端调整。</summary>
    private async Task FitAsync()
    {
        // 页面 fit() 后把终端行列数存到 window，宿主读回。
        await Invoke(
            "window.rfTerminal.fit();" +
            "(function(){var t=document.querySelector('.xterm-helper-textarea')||document.querySelector('.xterm');" +
            "return window.rfTerminal ? 'ok' : 'missing';})()");

        var dims = await Invoke(
            "(function(){var el=document.querySelector('.xterm');" +
            "return JSON.stringify({rows: el? window.rfTerminal? 'ready':'x': 0});})()");

        // 简化：WebView 尺寸决定终端宽高，行列数由页面布局给出。
        // 首次连接用默认 80x24，后续 fit 由 resize 驱动（本骨架阶段不逐字符同步）。
        _session.Resize(80, 24, (int)_host.Bounds.Width, (int)_host.Bounds.Height);
        _ = dims;
    }

    /// <summary>会话视图释放：退订并关闭会话。</summary>
    public async ValueTask DisposeAsync()
    {
        _session.DataReceived -= OnDataReceived;
        _session.StateChanged -= OnStateChanged;
        await _session.DisconnectAsync();
        _session.MarkClosed();
        await _session.DisposeAsync();
    }
}
