using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RemoteFlow.App.Services;
using RemoteFlow.Presentation.Terminal;
using RemoteFlow.Presentation.ViewModels;
using RemoteFlow.Core.Models;
using RemoteFlow.Protocol.Ssh;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.App.Views.Sessions;

/// <summary>
/// SSH 会话视图。用 WebView2 承载 xterm.js 作为终端渲染层。
/// <para>
/// 之所以不自绘终端：ANSI 转义序列、东亚宽字符排版、滚动缓冲、选区复制
/// 这些细节的正确实现成本极高，而 xterm.js 是被 VS Code 等产品长期验证过的方案。
/// 协议层（SSH.NET）与渲染层（xterm.js）之间只交换<b>原始字节</b>，
/// 因此多字节字符被 TCP 分包切断也不会产生乱码。
/// </para>
/// </summary>
public sealed class SshSessionView : ContentControl, IDisposable
{
    /// <summary>虚拟主机名。WebView2 通过它以 https 方式加载本地终端资源，避免 file:// 的安全限制。</summary>
    private const string VirtualHost = "remoteflow.terminal";

    /// <summary>输出合批间隔。远端刷屏时逐包调用 JS 开销过大，按帧合并后再写入。</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>粘贴大文本的字符数阈值，超过即触发「大文本粘贴警告」。</summary>
    private const int LargePasteThreshold = 4096;

    private readonly SshSession _session;
    private readonly SessionTabViewModel _viewModel;
    private readonly AppSettings _settings;
    private readonly ThemeService _theme;
    private readonly IDialogService _dialogs;
    private readonly ILogger _logger;

    private readonly WebView2 _webView;
    private readonly DispatcherTimer _flushTimer;

    /// <summary>待写入终端的输出缓冲。由协议线程写入，UI 线程按帧取走。</summary>
    private readonly List<byte[]> _pendingOutput = [];
    private readonly Lock _outputLock = new();

    private bool _terminalReady;
    private bool _connectStarted;
    private bool _disposed;

    /// <summary>是否已从 <see cref="SharedWebView2Environment"/> 取得共享环境引用（Dispose 时归还）。</summary>
    private bool _holdsSharedEnvironment;

    public SshSessionView(
        SshSession session,
        SessionTabViewModel viewModel,
        AppSettings settings,
        ThemeService theme,
        IDialogService dialogs,
        ILogger logger)
    {
        _session = session;
        _viewModel = viewModel;
        _settings = settings;
        _theme = theme;
        _dialogs = dialogs;
        _logger = logger;

        _webView = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(30, 30, 30) };
        Content = _webView;

        // 视图与终端一并铺满容器，全屏时终端跟随扩展。
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        _flushTimer = new DispatcherTimer { Interval = FlushInterval };
        _flushTimer.Tick += OnFlushTick;

        _session.DataReceived += OnSessionDataReceived;
        _viewModel.ActionRequested += OnActionRequested;
        _theme.EffectiveThemeChanged += OnThemeChanged;
        SettingsPageViewModel.SettingsSaved += OnSettingsSaved;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_connectStarted)
        {
            return;
        }

        _connectStarted = true;

        try
        {
            await InitializeWebViewAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "初始化终端失败：{ConnectionName}", _session.Profile.Name);

            _viewModel.InterruptionMessage =
                "终端组件初始化失败。请确认本机已安装 Microsoft Edge WebView2 运行时。";
            _viewModel.IsInterrupted = true;
        }
    }

    private async Task InitializeWebViewAsync()
    {
        // WebView2 需要可写的用户数据目录；程序目录可能只读，因此放到本地应用数据。
        // 用户数据目录与共享环境的创建统一由 SharedWebView2Environment 负责。
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemoteFlow", "WebView2");

        // 进程内所有 SSH 终端共享同一个 WebView2 环境：运行时按环境复用浏览器子进程，
        // 多个会话不再各自拉起一套 msedgewebview2 进程（见 SharedWebView2Environment）。
        var environment = await SharedWebView2Environment.Instance.AcquireAsync(userDataFolder);
        _holdsSharedEnvironment = true;

        // 等待环境期间会话已被关闭：不再初始化 WebView2，立即归还共享环境引用。
        if (_disposed)
        {
            ReleaseSharedEnvironment();
            return;
        }

        await _webView.EnsureCoreWebView2Async(environment);

        var core = _webView.CoreWebView2;

        // 终端是本地 UI，不需要浏览器的这些能力，一律关闭以缩小攻击面。
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;

        var assetFolder = TerminalAssetStore.EnsureAvailable();
        core.SetVirtualHostNameToFolderMapping(
            VirtualHost, assetFolder, CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessageReceived;

        _webView.Source = new Uri($"https://{VirtualHost}/terminal.html");
    }

    // ── 与终端前端的消息往来 ──────────────────────────────────────

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            // 前端用 postMessage(JSON.stringify(...)) 发送，因此这里取字符串再解析，
            // 而不是用 WebMessageAsJson（那会得到被二次编码的字符串字面量）。
            using var document = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "ready":
                    await OnTerminalReadyAsync(root);
                    break;

                case "input":
                    var payload = root.GetProperty("data").GetString();
                    if (!string.IsNullOrEmpty(payload))
                    {
                        _session.SendInput(Convert.FromBase64String(payload));
                    }
                    break;

                case "resize":
                    _session.Resize(
                        root.GetProperty("cols").GetInt32(),
                        root.GetProperty("rows").GetInt32(),
                        root.GetProperty("width").GetInt32(),
                        root.GetProperty("height").GetInt32());
                    break;

                case "paste-intercepted":
                    // WebView 内 Ctrl+V 被页面拦下后转交到这里，与工具条粘贴走同一套安全确认。
                    var pasteText = root.GetProperty("text").GetString();
                    if (!string.IsNullOrEmpty(pasteText))
                    {
                        await ConfirmAndSendPasteAsync(pasteText);
                    }
                    break;

                case "copy-selection":
                    // 右键菜单「复制选中」：文本已在页面取好，宿主负责写剪贴板。
                    var copyText = root.GetProperty("text").GetString();
                    if (!string.IsNullOrEmpty(copyText))
                    {
                        Clipboard.SetText(copyText);
                    }
                    break;

                case "request-paste":
                    // 右键菜单「粘贴到终端」：由宿主读剪贴板并走同一套粘贴安全确认。
                    await PasteFromClipboardAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "处理终端消息失败");
        }
    }

    /// <summary>
    /// 终端就绪后才发起 SSH 连接：此时行列数已确定，
    /// 远端从一开始就能按正确尺寸绘制，避免 vim/top 之类的全屏程序错位。
    /// </summary>
    private async Task OnTerminalReadyAsync(JsonElement message)
    {
        _terminalReady = true;

        await ApplyTerminalOptionsAsync();
        _flushTimer.Start();

        await _session.ConnectAsync();

        if (_session.State == ConnectionState.Connected)
        {
            _session.Resize(
                message.GetProperty("cols").GetInt32(),
                message.GetProperty("rows").GetInt32(),
                (int)ActualWidth,
                (int)ActualHeight);

            await InvokeTerminalAsync("focus");
        }
        else if (_session.ErrorMessage is { } error)
        {
            // 失败原因同时写进终端，用户在会话里就能看到发生了什么。
            await InvokeTerminalAsync("writeLine", $"[31m{error}[0m");
        }
    }

    private async Task ApplyTerminalOptionsAsync()
    {
        await ApplyTerminalThemeAsync();
        await InvokeTerminalAsync("setFont", _settings.SshFontFamily, _settings.SshFontSize);
        await InvokeTerminalAsync("setBracketedPaste", _settings.SshBracketedPaste);
    }

    /// <summary>
    /// 按「设置 → SSH → 终端主题」应用配色。
    /// 跟随应用主题时传 follow + 当前深浅；选择固定预设时直接传预设名。
    /// </summary>
    private async Task ApplyTerminalThemeAsync()
    {
        if (_settings.SshTerminalTheme == SshTerminalTheme.FollowApp)
        {
            await InvokeTerminalAsync("setTheme", "follow", _theme.IsDark);
        }
        else
        {
            await InvokeTerminalAsync("setTheme", TerminalThemeName(_settings.SshTerminalTheme));
        }
    }

    private static string TerminalThemeName(SshTerminalTheme theme) => theme switch
    {
        SshTerminalTheme.DarkGray => "darkGray",
        SshTerminalTheme.Black => "black",
        SshTerminalTheme.Navy => "navy",
        SshTerminalTheme.SolarizedDark => "solarizedDark",
        SshTerminalTheme.Light => "light",
        _ => "darkGray",
    };

    // ── 输出合批 ──────────────────────────────────────────────────

    private void OnSessionDataReceived(object? sender, byte[] data)
    {
        // 该事件来自 SSH 读取线程，只做入队，真正写入终端由 UI 线程按帧完成。
        lock (_outputLock)
        {
            _pendingOutput.Add(data);
        }
    }

    private async void OnFlushTick(object? sender, EventArgs e)
    {
        if (_disposed || !_terminalReady)
        {
            return;
        }

        byte[] batch;
        lock (_outputLock)
        {
            if (_pendingOutput.Count == 0)
            {
                return;
            }

            var total = _pendingOutput.Sum(chunk => chunk.Length);
            batch = new byte[total];

            var offset = 0;
            foreach (var chunk in _pendingOutput)
            {
                Buffer.BlockCopy(chunk, 0, batch, offset, chunk.Length);
                offset += chunk.Length;
            }

            _pendingOutput.Clear();
        }

        await InvokeTerminalAsync("write", Convert.ToBase64String(batch));
    }

    /// <summary>调用终端前端暴露的方法。参数经 JSON 序列化，避免脚本注入。</summary>
    private async Task InvokeTerminalAsync(string method, params object[] arguments)
    {
        if (_disposed || _webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var serialized = string.Join(',', arguments.Select(a => JsonSerializer.Serialize(a)));
            await _webView.CoreWebView2.ExecuteScriptAsync($"window.rfTerminal.{method}({serialized})");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "调用终端方法 {Method} 失败", method);
        }
    }

    // ── 工具条动作 ────────────────────────────────────────────────

    private async void OnActionRequested(object? sender, SessionAction action)
    {
        switch (action)
        {
            case SessionAction.Copy:
                await CopySelectionAsync();
                break;

            case SessionAction.Paste:
                await PasteFromClipboardAsync();
                break;

            case SessionAction.ClearTerminal:
                if (_terminalReady)
                {
                    await InvokeTerminalAsync("clear");
                }
                break;

            case SessionAction.SearchTerminal:
                if (_terminalReady)
                {
                    await InvokeTerminalAsync("openSearch");
                }
                break;

            case SessionAction.ReturnFocusToSession:
                FocusTerminal();
                break;
        }
    }

    /// <summary>Flyout / 浮层关闭后把键盘焦点还给终端 WebView。</summary>
    private void FocusTerminal()
    {
        if (_disposed || !_terminalReady)
        {
            return;
        }

        var window = Window.GetWindow(this);
        if (window is not null && window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        _webView.Focus();
    }

    private async Task CopySelectionAsync()
    {
        if (_webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var result = await _webView.CoreWebView2.ExecuteScriptAsync("window.rfTerminal.getSelection()");
            var text = JsonSerializer.Deserialize<string>(result);

            if (!string.IsNullOrEmpty(text))
            {
                Clipboard.SetText(text);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "复制终端选中内容失败");
        }
    }

    private async Task PasteFromClipboardAsync()
    {
        string text;
        try
        {
            if (!Clipboard.ContainsText())
            {
                return;
            }

            text = Clipboard.GetText();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取剪贴板失败");
            return;
        }

        if (!string.IsNullOrEmpty(text))
        {
            await ConfirmAndSendPasteAsync(text);
        }
    }

    /// <summary>
    /// 粘贴安全漏斗：多行 / 大文本按设置先确认，普通小段文本直接放行。
    /// 无论来自工具条按钮还是 WebView 内 Ctrl+V（页面已拦截转发），都收敛到这里再发往远端。
    /// 确认后交给 xterm 的 paste 管线发送（换行归一与 bracketed-paste 包裹由 xterm 处理）；
    /// 仅终端未就绪时退回直发。
    /// </summary>
    private async Task ConfirmAndSendPasteAsync(string rawText)
    {
        if (_session.State != ConnectionState.Connected)
        {
            return;
        }

        var normalized = rawText.Replace("\r\n", "\n").Replace('\r', '\n');
        // 行数只用于判定与提示：先去掉尾换行再数，避免「单行命令 + 尾换行」被误判成
        // 两行、提示 N+1。发送时仍保留原文（尾换行会转成 \r，恰好是提交回车）。
        var lineCount = normalized.TrimEnd('\n').Split('\n').Length;
        var multiline = lineCount > 1;
        var large = normalized.Length > LargePasteThreshold;

        var needConfirm =
            (multiline && _settings.SshConfirmMultilinePaste)
            || (large && _settings.SshWarnLargePaste);

        if (needConfirm)
        {
            var message = BuildPasteConfirmMessage(multiline, large, lineCount, normalized.Length);
            var confirmed = await _dialogs.ConfirmAsync("粘贴确认", message, "仍要粘贴", isDanger: true);

            if (!confirmed)
            {
                // 用户取消则不发送任何内容。不要把本地提示写进终端——
                // 那样会让终端画面与远端 readline 状态脱节，反而制造混乱。
                return;
            }
        }

        if (_terminalReady)
        {
            // 终端已就绪：交给 xterm 的 paste 管线发送——它会做 \r\n→\r 归一，并且
            // 远端启用 bracketed-paste 时自动加 ESC[200~ / ESC[201~ 包裹，随后经
            // onData → 宿主 input 消息送往远端（与键入同一条通道，绝不打日志）。
            await InvokeTerminalAsync("paste", normalized);
        }
        else
        {
            // 极端情况下终端尚未就绪（例如正在关闭）：退回直发，仅换行转回车。
            _session.SendInput(normalized.Replace('\n', '\r'));
        }
    }

    private static string BuildPasteConfirmMessage(bool multiline, bool large, int lineCount, int charCount)
    {
        var builder = new StringBuilder("剪贴板内容");
        if (multiline)
        {
            builder.Append($"包含 {lineCount} 行");
        }

        if (multiline && large)
        {
            builder.Append('、');
        }

        if (large)
        {
            builder.Append($"约 {charCount} 个字符");
        }

        builder.AppendLine("。粘贴后内容会被逐条发送并可能立即执行。");
        builder.AppendLine();
        builder.Append("确定要粘贴吗？");
        return builder.ToString();
    }

    private async void OnThemeChanged(object? sender, EventArgs e)
    {
        // 应用深浅切换只联动「跟随应用」；固定预设不因应用主题改变而变化。
        if (_terminalReady && _settings.SshTerminalTheme == SshTerminalTheme.FollowApp)
        {
            await InvokeTerminalAsync("setTheme", "follow", _theme.IsDark);
        }
    }

    /// <summary>设置保存后把终端相关选项重放到进行中的会话（如括号粘贴开关即时生效）。</summary>
    private async void OnSettingsSaved(object? sender, EventArgs e)
    {
        if (_terminalReady)
        {
            await ApplyTerminalOptionsAsync();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Loaded -= OnLoaded;
        _session.DataReceived -= OnSessionDataReceived;
        _viewModel.ActionRequested -= OnActionRequested;
        _theme.EffectiveThemeChanged -= OnThemeChanged;
        SettingsPageViewModel.SettingsSaved -= OnSettingsSaved;

        _flushTimer.Stop();
        _flushTimer.Tick -= OnFlushTick;

        if (_webView.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        }

        // 先释放本会话持有的 WebView2/Controller，再归还共享环境引用——
        // 每个会话只释放自己拥有的那一份，共享环境本身由引用计数守护到进程退出。
        _webView.Dispose();
        ReleaseSharedEnvironment();
    }

    /// <summary>归还共享 WebView2 环境引用。幂等；仅在确实取得过引用时递减计数。</summary>
    private void ReleaseSharedEnvironment()
    {
        if (!_holdsSharedEnvironment)
        {
            return;
        }

        _holdsSharedEnvironment = false;
        SharedWebView2Environment.Instance.Release();
    }
}
