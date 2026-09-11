using System.Threading.Channels;
using AppKit;
using CoreGraphics;
using Foundation;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Presentation.Terminal;
using RemoteFlow.Protocol.Ssh;
using WebKit;

namespace RemoteFlow.App.Mac;

/// <summary>
/// SSH 会话视图：<see cref="WKWebView"/> 承载 xterm.js 终端（复用 Presentation 的
/// <c>terminal.html</c> 前端）。
/// <para>
/// 数据流：远端字节（<see cref="SshSession.DataReceived"/>，协议线程）→ 主线程按帧合批 →
/// <c>window.rfTerminal.write(base64)</c>；页面 <c>post({type:'input'|'resize'|…})</c>
/// 经 <c>window.webkit.messageHandlers.remoteflow</c> 回到 <see cref="Bridge"/> →
/// 后台串行泵 <see cref="SshSession.SendInput"/>（不占 UI 线程，避免按键掉字 / 卡顿）。
/// </para>
/// </summary>
public sealed class SshTerminalView : NSView
{
    private readonly SshSession _session;
    private readonly WKWebView _web;
    private readonly NSTextField _status;
    private readonly NSButton _reconnect;
    private readonly Bridge _bridge; // 强引用：防止 ObjC 侧回调时托管桥被 GC。

    /// <summary>会话断开后用户点「重新连接」。宿主据此对同一 Profile 重开会话。</summary>
    public event EventHandler? ReconnectRequested;

    public RemoteFlow.Core.Models.ConnectionProfile Profile => _session.Profile;

    // 入向：协议线程写入、主线程按帧取走的合批缓冲。
    private readonly object _rxLock = new();
    private readonly List<byte> _rx = new();
    private NSTimer? _rxTimer;

    // 出向：主线程投递、后台单线程串行发送，保证顺序且不阻塞 UI。
    private readonly Channel<byte[]> _outbound =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    private bool _webReady;
    private readonly bool _verify = Environment.GetEnvironmentVariable("RF_VERIFY") == "1";

    public SshTerminalView(SshSession session)
    {
        _session = session;
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        Layer!.BackgroundColor = NSColor.Black.CGColor;

        var config = new WKWebViewConfiguration();
        _bridge = new Bridge(this);
        config.UserContentController.AddScriptMessageHandler(_bridge, "remoteflow");

        _web = new WKWebView(new CGRect(0, 0, 640, 400), config)
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _web.SetValueForKey(NSNumber.FromBoolean(false), new NSString("drawsBackground"));

        _status = new NSTextField
        {
            StringValue = "正在连接…",
            Bordered = false,
            Editable = false,
            Selectable = false,
            DrawsBackground = false,
            Alignment = NSTextAlignment.Center,
            TextColor = NSColor.SecondaryLabel,
            Font = NSFont.SystemFontOfSize(14),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _reconnect = NSButton.CreateButton("重新连接", () => ReconnectRequested?.Invoke(this, EventArgs.Empty));
        _reconnect.BezelStyle = NSBezelStyle.Rounded;
        _reconnect.Hidden = true;
        _reconnect.TranslatesAutoresizingMaskIntoConstraints = false;

        AddSubview(_web);
        AddSubview(_status);
        AddSubview(_reconnect);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            _web.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _web.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            _web.TopAnchor.ConstraintEqualTo(TopAnchor),
            _web.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            _status.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            _status.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            _reconnect.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            _reconnect.TopAnchor.ConstraintEqualTo(_status.BottomAnchor, 12),
        });

        _session.DataReceived += OnDataReceived;
        _session.StateChanged += OnStateChanged;

        _ = PumpOutboundAsync();

        // ~60fps 取帧：把突发的逐字符回显合并成一次 write，降低 JS 桥往返。
        _rxTimer = NSTimer.CreateRepeatingTimer(TimeSpan.FromMilliseconds(16), _ => FlushInbound());
        NSRunLoop.Main.AddTimer(_rxTimer, NSRunLoopMode.Common);

        var dir = TerminalAssetStore.EnsureAvailable();
        var html = Path.Combine(dir, "terminal.html");
        _web.LoadFileUrl(NSUrl.FromFilename(html), NSUrl.FromFilename(dir));
    }

    public override bool AcceptsFirstResponder() => true;

    public override bool BecomeFirstResponder() => Window?.MakeFirstResponder(_web) ?? false;

    public override void ViewDidMoveToWindow()
    {
        base.ViewDidMoveToWindow();
        // 让 WKWebView 成为窗口第一响应者，键盘事件直达终端，避免首击需先点一下。
        FocusWeb();
    }

    private void FocusWeb()
    {
        Window?.MakeFirstResponder(_web);
        if (_webReady)
        {
            _web.EvaluateJavaScript("window.rfTerminal && window.rfTerminal.focus()", (_, _) => { });
        }
    }

    /// <summary>由宿主在视图移出详情区时调用，解开会话回调与后台泵。</summary>
    public void Detach()
    {
        _session.DataReceived -= OnDataReceived;
        _session.StateChanged -= OnStateChanged;
        _outbound.Writer.TryComplete();
        _rxTimer?.Invalidate();
        _rxTimer = null;
    }

    // ── 远端 → 终端（合批）────────────────────────────────────────
    private void OnDataReceived(object? sender, byte[] data)
    {
        lock (_rxLock)
        {
            // 上限保护：极端刷屏时丢最旧的，优先保证响应而非完整回放。
            if (_rx.Count + data.Length > 1_048_576)
            {
                _rx.Clear();
            }

            _rx.AddRange(data);
        }
    }

    private void FlushInbound()
    {
        if (!_webReady)
        {
            return;
        }

        byte[] chunk;
        lock (_rxLock)
        {
            if (_rx.Count == 0)
            {
                return;
            }

            chunk = _rx.ToArray();
            _rx.Clear();
        }

        if (_verify)
        {
            var preview = System.Text.Encoding.UTF8.GetString(chunk);
            if (preview.Length > 120)
            {
                preview = preview[..120] + "…";
            }

            Console.WriteLine(
                $"[RF][rx] {chunk.Length}B \"{preview.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\x1b", "\\e")}\"");
        }

        var payload = Convert.ToBase64String(chunk);
        Eval($"window.rfTerminal && window.rfTerminal.write('{payload}')");
    }

    private void OnStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            if (e.NewState is ConnectionState.Failed or ConnectionState.Disconnected or ConnectionState.Closed)
            {
                var what = e.NewState switch
                {
                    ConnectionState.Failed => "连接失败",
                    ConnectionState.Disconnected => "会话已断开",
                    _ => "会话已结束",
                };
                var reason = _session.ErrorMessage
                    ?? (_session.ErrorCode == ConnectionErrorCode.None
                        ? null
                        : RemoteFlow.Presentation.ConnectionErrorText.Title(_session.ErrorCode));
                _status.StringValue = reason is null ? what : $"{what}：{reason}";
                _status.Hidden = false;
                _reconnect.Hidden = false;
            }
        });
    }

    private void Eval(string js)
    {
        if (!_webReady)
        {
            return;
        }

        _web.EvaluateJavaScript(js, (_, _) => { });
    }

    // ── 出向：后台串行发送 ─────────────────────────────────────────
    private async Task PumpOutboundAsync()
    {
        var reader = _outbound.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                // 攒齐当前所有待发一次性 Write+Flush：逐字符 Write/Flush 既是无谓 churn，
                // 也可能撞上 SSH.NET ShellStream 在 Write 与 Read 并发下的缓冲竞态。
                using var ms = new MemoryStream();
                while (reader.TryRead(out var chunk))
                {
                    ms.Write(chunk, 0, chunk.Length);
                }

                if (ms.Length == 0)
                {
                    continue;
                }

                try
                {
                    _session.SendInput(ms.ToArray());
                }
                catch
                {
                    // 单批发送失败不影响后续输入。
                }
            }
        }
        catch
        {
            // 通道关闭 / 视图销毁，正常结束。
        }
    }

    // ── 页面 → 宿主 ────────────────────────────────────────────
    private void HandleMessage(string body)
    {
        var type = Extract(body, "\"type\":\"", "\"");
        switch (type)
        {
            case "ready":
                _webReady = true;
                _status.Hidden = true;
                FocusWeb();
                FlushInbound();
                break;

            case "input":
            {
                var b64 = Extract(body, "\"data\":\"", "\"");
                if (b64.Length > 0)
                {
                    try
                    {
                        var bytes = Convert.FromBase64String(b64);
                        if (_verify)
                        {
                            var seq = Extract(body, "\"seq\":", ",");
                            Console.WriteLine(
                                $"[RF][in] seq={seq} {bytes.Length}B \"{System.Text.Encoding.UTF8.GetString(bytes).Replace("\n", "\\n").Replace("\r", "\\r")}\"");
                        }

                        _outbound.Writer.TryWrite(bytes);
                    }
                    catch (FormatException)
                    {
                        // 非法负载丢弃。
                    }
                }
                break;
            }

            case "paste-intercepted":
            {
                var text = Unescape(Extract(body, "\"text\":\"", "\"}"));
                if (text.Length > 0)
                {
                    _outbound.Writer.TryWrite(System.Text.Encoding.UTF8.GetBytes(text));
                }
                break;
            }

            case "resize":
            {
                var cols = ExtractInt(body, "\"cols\":");
                var rows = ExtractInt(body, "\"rows\":");
                var w = ExtractInt(body, "\"width\":");
                var h = ExtractInt(body, "\"height\":");
                if (cols > 0 && rows > 0)
                {
                    _session.Resize(cols, rows, w, h);
                }
                break;
            }
        }
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

    private static int ExtractInt(string source, string after)
    {
        var start = source.IndexOf(after, StringComparison.Ordinal);
        if (start < 0)
        {
            return 0;
        }

        start += after.Length;
        var end = start;
        while (end < source.Length && (char.IsDigit(source[end]) || source[end] == '-'))
        {
            end++;
        }

        return int.TryParse(source[start..end], out var v) ? v : 0;
    }

    private static string Unescape(string s) =>
        s.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t").Replace("\\\"", "\"").Replace("\\\\", "\\");

    private sealed class Bridge : NSObject, IWKScriptMessageHandler
    {
        private readonly SshTerminalView _owner;
        public Bridge(SshTerminalView owner) => _owner = owner;

        public void DidReceiveScriptMessage(WKUserContentController userContentController, WKScriptMessage message)
        {
            if (message.Body is NSString s)
            {
                _owner.HandleMessage(s.ToString());
            }
        }
    }
}
