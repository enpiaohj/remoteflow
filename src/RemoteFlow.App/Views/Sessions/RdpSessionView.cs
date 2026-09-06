using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using RemoteFlow.App.ViewModels;
using RemoteFlow.Core.Models;
using RemoteFlow.Protocol.Rdp;

namespace RemoteFlow.App.Views.Sessions;

/// <summary>
/// RDP 会话视图。用 <see cref="WindowsFormsHost"/> 承载 RDP ActiveX 控件。
/// <para>
/// 时序要求：ActiveX 必须先进入可视树并创建句柄，才能配置属性与连接，
/// 因此连接动作在 <see cref="FrameworkElement.Loaded"/> 之后才发起。
/// </para>
/// </summary>
public sealed partial class RdpSessionView : ContentControl, IDisposable
{
    /// <summary>窗口尺寸变化后延迟多久再通知远端调整分辨率，避免拖拽过程中频繁重协商。</summary>
    private static readonly TimeSpan ResizeDebounce = TimeSpan.FromMilliseconds(400);

    /// <summary>聚焦 RDP 控件后、注入按键前的等待时间，让焦点真正落到控件再发键。</summary>
    private static readonly TimeSpan FocusSettleDelay = TimeSpan.FromMilliseconds(40);

    // 合成快捷键用到的虚拟键码。RDP 没有注入 API，只能 SendInput 发键；
    // Ctrl+Alt+Del 的远端约定见 SendCtrlAltDeleteAsync。
    private const ushort VkControl = 0x11;
    private const ushort VkShift = 0x10;
    private const ushort VkMenu = 0x12;
    private const ushort VkEscape = 0x1B;
    private const ushort VkEnd = 0x23;

    private readonly RdpSession _session;
    private readonly SessionTabViewModel _viewModel;
    private readonly ILogger<RdpSessionView> _logger;
    private readonly WindowsFormsHost _host;
    private readonly DispatcherTimer _resizeTimer;

    private CancellationTokenSource? _connectCts;
    private bool _connectStarted;
    private bool _disposed;

    public RdpSessionView(RdpSession session, SessionTabViewModel viewModel, ILogger<RdpSessionView> logger)
    {
        _session = session;
        _viewModel = viewModel;
        _logger = logger;

        _host = new WindowsFormsHost
        {
            Child = session.HostControl,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // 视图自身也必须拉伸，否则全屏后 RDP 画面缩在左上角。
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        Content = _host;

        _resizeTimer = new DispatcherTimer { Interval = ResizeDebounce };
        _resizeTimer.Tick += OnResizeTimerTick;

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;

        _viewModel.ActionRequested += OnActionRequested;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Tab 切换会反复触发 Loaded，只在首次真正发起连接。
        if (_connectStarted)
        {
            return;
        }

        _connectStarted = true;
        _connectCts = new CancellationTokenSource();

        await _session.ConnectAsync(_connectCts.Token);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_session.Profile.Rdp.DisplayMode != RdpDisplayMode.FitToWindow)
        {
            return;
        }

        // 重启计时器：只有尺寸稳定下来之后才通知远端，避免拖拽期间反复重协商分辨率。
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void OnResizeTimerTick(object? sender, EventArgs e)
    {
        _resizeTimer.Stop();

        if (_disposed || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        // ActiveX 使用物理像素，WPF 使用设备无关单位，跨 DPI 时必须换算，
        // 否则在 125% / 150% 缩放下远程桌面分辨率会偏小。
        var scale = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Round(ActualWidth * scale.DpiScaleX);
        var height = (int)Math.Round(ActualHeight * scale.DpiScaleY);

        _session.UpdateDisplaySize(width, height);
    }

    private async void OnActionRequested(object? sender, SessionAction action)
    {
        switch (action)
        {
            case SessionAction.ToggleScaling:
                _session.SetSmartSizing(_viewModel.ScaleToFit);
                break;

            case SessionAction.SendCtrlAltDelete:
                try
                {
                    await SendCtrlAltDeleteAsync();
                }
                catch (Exception ex)
                {
                    // async void 事件处理器：吞掉并记录异常，避免未观察异常击穿 UI。
                    _logger.LogWarning(ex, "RDP 会话 {SessionId} 「发送 Ctrl+Alt+Del」失败", _session.SessionId);
                }
                break;

            case SessionAction.LaunchTaskManager:
                try
                {
                    await LaunchTaskManagerAsync();
                }
                catch (Exception ex)
                {
                    // async void 事件处理器：吞掉并记录异常，避免未观察异常击穿 UI。
                    _logger.LogWarning(ex, "RDP 会话 {SessionId} 「启动任务管理器」失败", _session.SessionId);
                }
                break;
        }
    }

    /// <summary>
    /// 向远程会话发送 Ctrl+Alt+Del。
    /// <para>
    /// RDP 控件没有直接的 API，其约定是把本机的 <b>Ctrl+Alt+End</b> 翻译成
    /// 远端的 Ctrl+Alt+Del（本机的真实 Ctrl+Alt+Del 会被 Windows 安全桌面拦截，
    /// 永远送不到远端）。因此这里合成一次 Ctrl+Alt+End 按键。
    /// </para>
    /// </summary>
    private Task SendCtrlAltDeleteAsync() =>
        SendKeyComboAsync("Ctrl+Alt+Del", VkControl, VkMenu, VkEnd);

    /// <summary>
    /// 在远端启动任务管理器：合成一次 Ctrl+Shift+Esc。
    /// <para>
    /// Ctrl+Shift+Esc 是 Windows 直接打开任务管理器的系统快捷键，不经安全桌面，
    /// 可像普通按键一样经 SendInput 注入到远端会话。
    /// </para>
    /// </summary>
    private Task LaunchTaskManagerAsync() =>
        SendKeyComboAsync("Ctrl+Shift+Esc", VkControl, VkShift, VkEscape);

    /// <summary>
    /// 向远程会话注入一组组合键。
    /// <para>
    /// SendInput 会把按键投递给<b>前台窗口</b>中持有键盘焦点的控件。全屏时工具条是
    /// Popup（独立顶层窗口），点击其按钮后 Popup 持有前台与键盘焦点；若不先把承载
    /// RDP ActiveX 的主窗口带回前台并让 RDP 控件取得焦点，组合键会落到 Popup /
    /// 其它元素，远端收不到。因此本方法按「置前 → 聚焦 → 延时稳定 → 注入」的顺序执行。
    /// 只能在 UI 线程调用（命令/事件触发）。
    /// </para>
    /// <para>
    /// 注入顺序：按 <paramref name="virtualKeys"/> 依次按下，再按相反顺序依次抬起
    /// （例如 Ctrl+Shift+Esc：down Ctrl → down Shift → down Esc → up Esc → up Shift → up Ctrl）。
    /// </para>
    /// </summary>
    private async Task SendKeyComboAsync(string actionName, params ushort[] virtualKeys)
    {
        if (_disposed)
        {
            return;
        }

        if (_session.State != ConnectionState.Connected)
        {
            _logger.LogInformation(
                "RDP 会话 {SessionId} 忽略「{ActionName}」：当前状态 {State}，非已连接",
                _session.SessionId, actionName, _session.State);
            return;
        }

        var control = _session.HostControl;
        if (control is null || control.IsDisposed || !control.IsHandleCreated)
        {
            _logger.LogWarning(
                "RDP 会话 {SessionId} 无法「{ActionName}」：RDP 控件未就绪（IsDisposed={IsDisposed}, IsHandleCreated={IsHandleCreated}）",
                _session.SessionId, actionName, control?.IsDisposed, control?.IsHandleCreated);
            return;
        }

        // 1) 先把承载 RDP ActiveX 的主窗口带到前台，再聚焦控件，避免 Popup 抢走前台。
        BringHostWindowToForeground();

        // 2) 让 RDP 控件请求键盘焦点。Focus() 返回 false 不代表最终失败，
        //    注入前还会用 SetFocus 强制把焦点钉到控件句柄上兜底。
        control.Focus();

        // 3) 焦点稳定后再注入。WPF/WinForms 焦点交接有异步时序，立即 SendInput
        //    可能抢在焦点真正落到 RDP 控件之前。
        await Task.Delay(FocusSettleDelay);

        // 延迟期间会话可能被关闭 / 断开，注入前复检，避免把按键发到已释放的控件上。
        if (_disposed || _session.State != ConnectionState.Connected)
        {
            _logger.LogDebug(
                "RDP 会话 {SessionId} 取消「{ActionName}」：视图已释放或连接状态已变化（{State}）",
                _session.SessionId, actionName, _session.State);
            return;
        }

        // 4) 注入前最后把键盘焦点钉在 RDP 控件句柄上（覆盖延迟期间 WPF 焦点管理器
        //    把焦点移回工具条 / 其它元素的场景）。
        if (GetFocus() != control.Handle)
        {
            SetFocus(control.Handle);
        }

        // 5) 依次按下各键、再按相反顺序抬起，合成组合键。
        var inputs = new Input[virtualKeys.Length * 2];
        for (var i = 0; i < virtualKeys.Length; i++)
        {
            inputs[i] = CreateKeyInput(virtualKeys[i], keyUp: false);
            inputs[virtualKeys.Length + i] =
                CreateKeyInput(virtualKeys[virtualKeys.Length - 1 - i], keyUp: true);
        }

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());

        _logger.LogDebug("RDP 会话 {SessionId} 已发送 {ActionName}", _session.SessionId, actionName);
    }

    /// <summary>
    /// 把承载本视图的主窗口带到前台（还原最小化 → WPF Activate → SetForegroundWindow 兜底）。
    /// <para>
    /// 全屏药丸是 Popup 独立顶层窗口，点击其按钮后 Popup 持有前台；而 SendInput 把按键
    /// 投递给前台窗口的焦点控件，因此必须先让主窗口抢回前台。置前失败不致命：下方
    /// Focus / SetFocus 仍会尽力把焦点交给 RDP 控件，故失败仅记日志、不抛。
    /// </para>
    /// </summary>
    private void BringHostWindowToForeground()
    {
        try
        {
            var window = System.Windows.Window.GetWindow(this);
            if (window is null)
            {
                return;
            }

            // 最小化时先还原，否则激活不会落到可见窗口。
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            // WPF 激活遵守前台切换规则；随后用 SetForegroundWindow 兜底，覆盖
            // Popup 占用前台导致 Activate 被系统吞掉的情况。
            window.Activate();

            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != nint.Zero)
            {
                SetForegroundWindow(hwnd);
            }
        }
        catch (Exception ex)
        {
            // 置前失败不阻断后续 Focus + 注入流程；记录后由下方 Focus / SetFocus 兜底。
            _logger.LogDebug(ex, "RDP 会话 {SessionId} 宿主窗口置前失败", _session.SessionId);
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
        SizeChanged -= OnSizeChanged;
        _viewModel.ActionRequested -= OnActionRequested;

        _resizeTimer.Stop();
        _resizeTimer.Tick -= OnResizeTimerTick;

        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = null;

        // 先摘掉 Child 再释放宿主，避免 WindowsFormsHost 连同会话持有的
        // ActiveX 控件一起销毁——控件的生命周期由 RdpSession 负责。
        _host.Child = null;
        _host.Dispose();
    }

    // ── SendInput 互操作 ──────────────────────────────────────────

    private static Input CreateKeyInput(ushort virtualKey, bool keyUp) => new()
    {
        Type = InputTypeKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                ScanCode = 0,
                Flags = keyUp ? KeyEventFlagKeyUp : 0u,
                Time = 0,
                ExtraInfo = nint.Zero
            }
        }
    };

    private const uint InputTypeKeyboard = 1;
    private const uint KeyEventFlagKeyUp = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint numberOfInputs, [In] Input[] inputs, int sizeOfInput);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial nint SetFocus(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetFocus();
}
