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
using Forms = System.Windows.Forms;

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
    private static readonly TimeSpan FocusSettleDelay = TimeSpan.FromMilliseconds(50);

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

            case SessionAction.ReturnFocusToSession:
                ReturnFocusToRemoteSurface();
                break;
        }
    }

    /// <summary>
    /// Flyout / 下拉等浮层关闭后把键盘焦点还给 RDP 画面：
    /// 置前主窗 → 聚焦控件 → SetFocus 兜底。RDP 接收不到键时，多半是焦点仍停在浮层上。
    /// </summary>
    private void ReturnFocusToRemoteSurface()
    {
        if (_disposed)
        {
            return;
        }

        BringHostWindowToForeground();

        var control = _session.HostControl;
        if (control is null || control.IsDisposed || !control.IsHandleCreated)
        {
            return;
        }

        control.Focus();
        if (GetFocus() != control.Handle)
        {
            SetFocus(control.Handle);
        }
    }

    /// <summary>
    /// 向远程会话发送 Ctrl+Alt+Del。
    /// <para>
    /// RDP 控件没有直接的注入 API，其约定是把本机的 <b>Ctrl+Alt+End</b> 翻译成
    /// 远端的 Ctrl+Alt+Del（本机的真实 Ctrl+Alt+Del 会被 Windows 安全桌面拦截，
    /// 永远送不到远端）。因此这里用 SendKeys.SendWait 在 UI 线程合成一次
    /// Ctrl+Alt+End（SendKeys 表达式 <c>^%{END}</c>），由 mstsc 控件转发给远端。
    /// </para>
    /// </summary>
    private Task SendCtrlAltDeleteAsync() =>
        SendKeyComboAsync("Ctrl+Alt+Del", "^%{END}");

    /// <summary>
    /// 在远端启动任务管理器：把 Ctrl+Shift+Esc 当作普通按键流，用
    /// <see cref="PostMessage"/> 直接投递给 RDP 宿主控件，而不是 SendKeys。
    /// <para>
    /// <b>为什么不用 SendKeys：</b>Ctrl+Shift+Esc 会被本机 shell / 已注册热键抢先拦截
    /// （表现为打开<b>本机</b>任务管理器、远端无反应）。SendKeys 底层走 SendInput，
    /// 属于真实输入管线，投递前仍会经过热键判定，绕不过本机拦截；而 PostMessage 直接把
    /// WM_KEYDOWN / WM_KEYUP 塞进 RDP 控件所在窗口的消息队列，不经过输入 / 热键管线，
    /// 让 mstsc 控件把它当作普通按键流转发给远端，由远端打开任务管理器。
    /// </para>
    /// <para>
    /// <b>与「远端 Ctrl+Alt+Del」路径的区别：</b>CAD 走 mstsc 的内置翻译
    /// （本机 Ctrl+Alt+End → 远端 Ctrl+Alt+Del），是控件明确支持的“安全通道”；
    /// 本方法没有这样的魔法组合，只能把组合键当作普通按键流注入。若 RDP 控件在
    /// 本地 / 远端配置下仍不转发该组合到远端任务管理器，实机验证会暴露出来：
    /// 此时需按阶段日志核对焦点句柄与每次 PostMessage 结果，并考虑把消息改投给
    /// 控件内部真正持有键盘焦点的子窗口。
    /// </para>
    /// </summary>
    private async Task LaunchTaskManagerAsync()
    {
        var control = await PrepareRdpControlForKeyInjectionAsync("Ctrl+Shift+Esc");
        if (control is null)
        {
            return;
        }

        PostCtrlShiftEsc(control.Handle);
    }

    /// <summary>
    /// 向远程会话注入按键 / 组合键前的统一准备：
    /// 置前主窗口 → 聚焦 RDP 控件 → 延时稳定 → SetFocus 钉住焦点。
    /// <para>
    /// 全屏时工具条是 Popup（独立顶层窗口），点击其按钮后 Popup 持有前台与键盘焦点；
    /// 若不先把承载 RDP ActiveX 的主窗口带回前台并让 RDP 控件取得焦点，后续注入会落到
    /// Popup / 其它元素，远端收不到。返回就绪的宿主控件；任一前置条件不满足时返回 null
    /// （已记录日志）。只能在 UI 线程调用（命令 / 事件触发）。
    /// </para>
    /// </summary>
    private async Task<Forms.Control?> PrepareRdpControlForKeyInjectionAsync(string actionName)
    {
        if (_disposed)
        {
            return null;
        }

        _logger.LogInformation(
            "RDP 会话 {SessionId} 收到「{ActionName}」注入请求，当前状态 {State}",
            _session.SessionId, actionName, _session.State);

        if (_session.State != ConnectionState.Connected)
        {
            _logger.LogWarning(
                "RDP 会话 {SessionId} 忽略「{ActionName}」：当前状态 {State}，非已连接",
                _session.SessionId, actionName, _session.State);
            return null;
        }

        var control = _session.HostControl;
        if (control is null)
        {
            _logger.LogWarning(
                "RDP 会话 {SessionId} 无法「{ActionName}」：RDP 宿主控件为 null",
                _session.SessionId, actionName);
            return null;
        }

        if (control.IsDisposed || !control.IsHandleCreated)
        {
            _logger.LogWarning(
                "RDP 会话 {SessionId} 无法「{ActionName}」：RDP 宿主控件未就绪（IsDisposed={IsDisposed}, IsHandleCreated={IsHandleCreated}）",
                _session.SessionId, actionName, control.IsDisposed, control.IsHandleCreated);
            return null;
        }

        // 1) 先把承载 RDP ActiveX 的主窗口带到前台，再聚焦控件，避免 Popup 抢走前台。
        BringHostWindowToForeground();

        // 2) 让 RDP 控件请求键盘焦点。Focus() 返回 false 不代表最终失败，
        //    后续注入前还会用 SetFocus 强制把焦点钉到控件句柄上兜底。
        var focusResult = control.Focus();
        _logger.LogInformation(
            "RDP 会话 {SessionId} 「{ActionName}」宿主窗口已置前，HostControl.Focus()={FocusResult}",
            _session.SessionId, actionName, focusResult);

        // 3) 焦点稳定后再注入。WPF/WinForms 焦点交接有异步时序，立即注入
        //    可能抢在焦点真正落到 RDP 控件之前。
        await Task.Delay(FocusSettleDelay);

        // 延迟期间会话可能被关闭 / 断开 / 控件被销毁，注入前复检，避免把按键发到已释放的控件上。
        if (_disposed || _session.State != ConnectionState.Connected
            || control.IsDisposed || !control.IsHandleCreated)
        {
            _logger.LogWarning(
                "RDP 会话 {SessionId} 取消「{ActionName}」：延时后视图已释放 / 状态已变化 / 控件不可用（State={State}, IsDisposed={IsDisposed}, IsHandleCreated={IsHandleCreated}）",
                _session.SessionId, actionName, _session.State, control.IsDisposed, control.IsHandleCreated);
            return null;
        }

        // 4) 注入前把键盘焦点钉在 RDP 控件句柄上（覆盖延时期间焦点管理器把焦点移回
        //    工具条 / 其它元素的场景），并记录实际焦点句柄，便于无反应时定位。
        if (GetFocus() != control.Handle)
        {
            _logger.LogInformation(
                "RDP 会话 {SessionId} 「{ActionName}」当前焦点不在控件句柄上，执行 SetFocus（控件句柄 {ControlHandle}）",
                _session.SessionId, actionName, control.Handle);
            SetFocus(control.Handle);
        }

        _logger.LogInformation(
            "RDP 会话 {SessionId} 「{ActionName}」延时后焦点句柄 {FocusHandle}，控件句柄 {ControlHandle}（两者一致即已聚焦）",
            _session.SessionId, actionName, GetFocus(), control.Handle);

        return control;
    }

    /// <summary>
    /// 用 <see cref="System.Windows.Forms.SendKeys.SendWait"/> 在 UI 线程合成组合键，
    /// 投递给当前持有键盘焦点的窗口（RDP 控件已在
    /// <see cref="PrepareRdpControlForKeyInjectionAsync"/> 中取得焦点）。
    /// <para>
    /// 当前仅「发送 Ctrl+Alt+Del」使用。若目标组合会被本机热键拦截（如 Ctrl+Shift+Esc，
    /// 打开本机任务管理器），SendKeys 走 SendInput 仍会被拦，请改用
    /// <see cref="LaunchTaskManagerAsync"/> 的 PostMessage 路径。
    /// </para>
    /// </summary>
    private async Task SendKeyComboAsync(string actionName, string sendKeysExpression)
    {
        var control = await PrepareRdpControlForKeyInjectionAsync(actionName);
        if (control is null)
        {
            return;
        }

        _logger.LogInformation(
            "RDP 会话 {SessionId} 开始 SendWait({SendKeysExpression}) 注入「{ActionName}」",
            _session.SessionId, sendKeysExpression, actionName);
        try
        {
            Forms.SendKeys.SendWait(sendKeysExpression);
            _logger.LogInformation(
                "RDP 会话 {SessionId} SendWait({SendKeysExpression}) 注入「{ActionName}」完成",
                _session.SessionId, sendKeysExpression, actionName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "RDP 会话 {SessionId} SendWait({SendKeysExpression}) 注入「{ActionName}」抛出异常",
                _session.SessionId, sendKeysExpression, actionName);
        }
    }

    /// <summary>
    /// 把 Ctrl+Shift+Esc 作为普通按键流，用 <see cref="PostMessage"/> 依次投递到 RDP
    /// 宿主控件句柄，供 mstsc 转发给远端。
    /// <para>
    /// 投递顺序：Ctrl Down → Shift Down → Esc Down → Esc Up → Shift Up → Ctrl Up。
    /// 修饰键 Ctrl / Shift 与目标 Esc 都成对发送 WM_KEYDOWN / WM_KEYUP。Esc 不是扩展键、
    /// 这里也只合成左 Ctrl / 左 Shift（无扩展键标记），故 lParam 不置扩展键位。mstsc
    /// 若按消息到达顺序跟踪修饰键状态，就能把该序列还原为 Ctrl+Shift+Esc 并转发远端。
    /// </para>
    /// </summary>
    private void PostCtrlShiftEsc(nint hwnd)
    {
        var scanControl = MapVirtualKeyW(VkControl, MapVkVkToVsc);
        var scanShift = MapVirtualKeyW(VkShift, MapVkVkToVsc);
        var scanEscape = MapVirtualKeyW(VkEscape, MapVkVkToVsc);

        _logger.LogInformation(
            "RDP 会话 {SessionId} PostMessage 开始投递 Ctrl+Shift+Esc → 控件句柄 0x{Hwnd:X}（scan: Ctrl=0x{CtrlScan:X2}, Shift=0x{ShiftScan:X2}, Esc=0x{EscScan:X2}）",
            _session.SessionId, hwnd, scanControl, scanShift, scanEscape);

        var results = new[]
        {
            PostRdpKey(hwnd, "Ctrl", VkControl, scanControl, isUp: false),
            PostRdpKey(hwnd, "Shift", VkShift, scanShift, isUp: false),
            PostRdpKey(hwnd, "Esc", VkEscape, scanEscape, isUp: false),
            PostRdpKey(hwnd, "Esc", VkEscape, scanEscape, isUp: true),
            PostRdpKey(hwnd, "Shift", VkShift, scanShift, isUp: true),
            PostRdpKey(hwnd, "Ctrl", VkControl, scanControl, isUp: true)
        };

        _logger.LogInformation(
            "RDP 会话 {SessionId} PostMessage 投递 Ctrl+Shift+Esc 完成，逐键结果 [Ctrl↓={R0}, Shift↓={R1}, Esc↓={R2}, Esc↑={R3}, Shift↑={R4}, Ctrl↑={R5}]",
            _session.SessionId, results[0], results[1], results[2], results[3], results[4], results[5]);
    }

    /// <summary>向目标句柄 PostMessage 一个按键消息（WM_KEYDOWN / WM_KEYUP），返回投递结果并记录日志。</summary>
    private bool PostRdpKey(nint hwnd, string keyName, uint vk, uint scanCode, bool isUp)
    {
        var message = isUp ? WmKeyUp : WmKeyDown;

        // WM_KEYDOWN / WM_KEYUP 的 lParam 位布局：
        //   bit 0–15   重复计数（这里恒为 1）
        //   bit 16–23  scan code（MapVirtualKey(MAPVK_VK_TO_VSC) 所得）
        //   bit 24     扩展键标记（Ctrl/Shift/Esc 均非扩展键，置 0）
        //   bit 30     先前按键状态（keyup 时恒为 1，表示此前已按下）
        //   bit 31     transition（1 = 释放）
        var lParam = 1u | (scanCode << 16);
        if (isUp)
        {
            lParam |= 0xC0000000u;
        }

        var ok = PostMessage(hwnd, message, (nint)vk, (nint)lParam);
        _logger.LogInformation(
            "RDP 会话 {SessionId} PostMessage {Key} {Direction} → Hwnd=0x{Hwnd:X}, Msg=0x{Message:X4}, WParam=0x{Vk:X2}, LParam=0x{LParam:X8}, 结果={Result}",
            _session.SessionId, keyName, isUp ? "Up" : "Down", hwnd, message, vk, lParam, ok);
        return ok;
    }

    /// <summary>
    /// 把承载本视图的主窗口带到前台（还原最小化 → WPF Activate → SetForegroundWindow 兜底）。
    /// <para>
    /// 全屏药丸是 Popup 独立顶层窗口，点击其按钮后 Popup 持有前台；而 SendKeys.SendWait
    /// 把按键投递给前台窗口中持有键盘焦点的控件，因此必须先让主窗口抢回前台。置前失败
    /// 不致命：下方 Focus / SetFocus 仍会尽力把焦点交给 RDP 控件，故失败仅记日志、不抛。
    /// </para>
    /// </summary>
    private void BringHostWindowToForeground()
    {
        try
        {
            var window = System.Windows.Window.GetWindow(this);
            if (window is null)
            {
                _logger.LogInformation(
                    "RDP 会话 {SessionId} 无法置前宿主窗口：未能定位所属 Window",
                    _session.SessionId);
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
            var foregroundResult = hwnd != nint.Zero && SetForegroundWindow(hwnd);
            _logger.LogInformation(
                "RDP 会话 {SessionId} 宿主窗口置前完成（Hwnd={Hwnd}, SetForegroundWindow={Result}）",
                _session.SessionId, hwnd, foregroundResult);
        }
        catch (Exception ex)
        {
            // 置前失败不阻断后续 Focus + 注入流程；记录后由下方 Focus / SetFocus 兜底。
            _logger.LogWarning(ex, "RDP 会话 {SessionId} 宿主窗口置前失败", _session.SessionId);
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

    // ── 焦点互操作 ────────────────────────────────────────────────

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial nint SetFocus(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetFocus();

    // ── 按键直达互操作（PostMessage 合成按键，绕过本机热键拦截）──────

    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint VkControl = 0x11;
    private const uint VkShift = 0x10;
    private const uint VkEscape = 0x1B;
    private const uint MapVkVkToVsc = 0;

    // user32 只导出 PostMessageW/PostMessageA（无裸 PostMessage），这里显式指定 W 入口。
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyW(uint uCode, uint uMapType);
}
