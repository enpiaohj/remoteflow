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
/// 数据流：远端字节（<see cref="SshSession.DataReceived"/>，协议线程）→ 主线程 →
/// <c>window.rfTerminal.write(base64)</c>；页面 <c>post({type:'input'|'resize'|…})</c>
/// 经 <c>window.webkit.messageHandlers.remoteflow</c> 回到 <see cref="Bridge"/> →
/// <see cref="SshSession.SendInput"/> / <see cref="SshSession.Resize"/>。
/// </para>
/// </summary>
public sealed class SshTerminalView : NSView
{
    private readonly SshSession _session;
    private readonly WKWebView _web;
    private readonly NSTextField _status;

    private bool _webReady;
    private readonly List<byte[]> _pending = new();
    private int _pendingBytes;
    private readonly bool _verify = Environment.GetEnvironmentVariable("RF_VERIFY") == "1";

    public SshTerminalView(SshSession session)
    {
        _session = session;
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        Layer!.BackgroundColor = NSColor.Black.CGColor;

        var config = new WKWebViewConfiguration();
        config.UserContentController.AddScriptMessageHandler(new Bridge(this), "remoteflow");

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

        AddSubview(_web);
        AddSubview(_status);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            _web.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _web.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            _web.TopAnchor.ConstraintEqualTo(TopAnchor),
            _web.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            _status.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            _status.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
        });

        _session.DataReceived += OnDataReceived;
        _session.StateChanged += OnStateChanged;

        var dir = TerminalAssetStore.EnsureAvailable();
        var html = Path.Combine(dir, "terminal.html");
        _web.LoadFileUrl(NSUrl.FromFilename(html), NSUrl.FromFilename(dir));
    }

    /// <summary>由宿主在视图移出详情区时调用，解开会话回调。</summary>
    public void Detach()
    {
        _session.DataReceived -= OnDataReceived;
        _session.StateChanged -= OnStateChanged;
    }

    // ── 远端 → 终端 ────────────────────────────────────────────
    private void OnDataReceived(object? sender, byte[] data)
    {
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            if (_verify)
            {
                Console.WriteLine($"[RF][rx] {data.Length} bytes");
            }

            if (!_webReady)
            {
                if (_pendingBytes + data.Length < 64 * 1024)
                {
                    _pending.Add(data);
                    _pendingBytes += data.Length;
                }

                return;
            }

            WriteToTerminal(data);
        });
    }

    private void OnStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            if (e.NewState is ConnectionState.Failed or ConnectionState.Disconnected or ConnectionState.Closed)
            {
                var what = e.NewState switch
                {
                    ConnectionState.Failed => "会话失败",
                    ConnectionState.Disconnected => "会话已断开",
                    _ => "会话已结束",
                };
                var reason = _session.ErrorMessage
                    ?? (_session.ErrorCode == ConnectionErrorCode.None ? null : _session.ErrorCode.ToString());
                _status.StringValue = reason is null ? what : $"{what}：{reason}";
                _status.Hidden = false;
            }
        });
    }

    private void WriteToTerminal(byte[] data)
    {
        var payload = Convert.ToBase64String(data);
        Eval($"window.rfTerminal && window.rfTerminal.write('{payload}')");
    }

    private void Eval(string js)
    {
        if (!_webReady)
        {
            return;
        }

        _web.EvaluateJavaScript(js, (_, _) => { });
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
                var replay = _pending.ToArray();
                _pending.Clear();
                _pendingBytes = 0;
                foreach (var chunk in replay)
                {
                    WriteToTerminal(chunk);
                }
                Eval("window.rfTerminal.focus()");
                break;

            case "input":
            {
                var b64 = Extract(body, "\"data\":\"", "\"");
                if (b64.Length > 0)
                {
                    try
                    {
                        _session.SendInput(Convert.FromBase64String(b64));
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
                    _session.SendInput(text);
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
