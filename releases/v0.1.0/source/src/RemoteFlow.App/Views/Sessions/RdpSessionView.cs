using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Threading;
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

    private readonly RdpSession _session;
    private readonly SessionTabViewModel _viewModel;
    private readonly WindowsFormsHost _host;
    private readonly DispatcherTimer _resizeTimer;

    private CancellationTokenSource? _connectCts;
    private bool _connectStarted;
    private bool _disposed;

    public RdpSessionView(RdpSession session, SessionTabViewModel viewModel)
    {
        _session = session;
        _viewModel = viewModel;

        _host = new WindowsFormsHost
        {
            Child = session.HostControl,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

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

    private void OnActionRequested(object? sender, SessionAction action)
    {
        switch (action)
        {
            case SessionAction.ToggleScaling:
                _session.SetSmartSizing(_viewModel.ScaleToFit);
                break;

            case SessionAction.SendCtrlAltDelete:
                SendCtrlAltDelete();
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
    private void SendCtrlAltDelete()
    {
        if (_session.State != ConnectionState.Connected)
        {
            return;
        }

        // 先把焦点交给 RDP 控件，否则合成的按键会落到别处。
        _session.HostControl.Focus();

        const ushort VkControl = 0x11;
        const ushort VkMenu = 0x12;
        const ushort VkEnd = 0x23;

        var inputs = new[]
        {
            CreateKeyInput(VkControl, keyUp: false),
            CreateKeyInput(VkMenu, keyUp: false),
            CreateKeyInput(VkEnd, keyUp: false),
            CreateKeyInput(VkEnd, keyUp: true),
            CreateKeyInput(VkMenu, keyUp: true),
            CreateKeyInput(VkControl, keyUp: true)
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
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
}
