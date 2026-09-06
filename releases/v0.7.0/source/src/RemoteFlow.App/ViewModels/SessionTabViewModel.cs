using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.App.ViewModels;

/// <summary>
/// 单个远程会话 Tab。
/// <para>
/// 负责把协议层的连接状态翻译成用户可读的界面状态。
/// 断线时在会话内部显示轻量状态层并提供「重新连接」，
/// 而不是弹出模态错误框打断用户（产品设计文档 §7.9）。
/// </para>
/// </summary>
public sealed partial class SessionTabViewModel : WorkspaceTabViewModel, IDisposable
{
    private readonly Func<Guid, Task> _closeCallback;
    private readonly Func<ConnectionProfile, Task> _reconnectCallback;

    public SessionTabViewModel(
        IRemoteSession session,
        Func<Guid, Task> closeCallback,
        Func<ConnectionProfile, Task> reconnectCallback)
    {
        Session = session;
        _closeCallback = closeCallback;
        _reconnectCallback = reconnectCallback;

        Title = session.Profile.Name;
        Icon = session.Protocol switch
        {
            ProtocolType.Rdp => "\uE7F4",
            ProtocolType.Ssh => "\uE756",
            _ => "\uE7F8"
        };

        session.StateChanged += OnSessionStateChanged;
        UpdateStateDisplay(session.State, session.ErrorCode, session.ErrorMessage);

        // VNC 会话打开时按该连接记住的档位初始化（RDP 不读此项）。
        if (Protocol == ProtocolType.Vnc)
        {
            VncScale = Session.Profile.Vnc.ScaleMode;
        }
    }

    public IRemoteSession Session { get; }

    public ProtocolType Protocol => Session.Protocol;

    public ConnectionProfile Profile => Session.Profile;

    /// <summary>面向用户的状态短语，如「已连接」「正在连接…」。</summary>
    [ObservableProperty]
    private string _stateText = "准备中";

    /// <summary>
    /// 状态图标。与状态文字同时呈现，
    /// 确保不单靠颜色传达信息（可访问性要求）。
    /// </summary>
    [ObservableProperty]
    private string _stateIcon = "\uE895";

    /// <summary>状态色资源键。</summary>
    [ObservableProperty]
    private string _stateBrushKey = "Status.Idle";

    /// <summary>是否处于连接中（用于显示进度指示）。</summary>
    [ObservableProperty]
    private bool _isConnecting;

    /// <summary>是否已成功连接。</summary>
    [ObservableProperty]
    private bool _isConnected;

    /// <summary>是否需要显示断线/失败的状态层。</summary>
    [ObservableProperty]
    private bool _isInterrupted;

    /// <summary>是否已按「启动后全屏」发起过一次进入全屏请求。同一会话实例只触发一次。</summary>
    private bool _startFullScreenRequested;

    /// <summary>失败原因（面向用户的中文说明，不含任何 Secret）。</summary>
    [ObservableProperty]
    private string _interruptionMessage = string.Empty;

    // ── 会话工具条状态 ────────────────────────────────────────────

    /// <summary>缩放以适应窗口。RDP 与 VNC 共用该开关。</summary>
    [ObservableProperty]
    private bool _scaleToFit = true;

    /// <summary>VNC 缩放模式（VNC 专用三态；RDP 仍用 ScaleToFit bool）。</summary>
    [ObservableProperty]
    private VncScaleMode _vncScale = VncScaleMode.FitToWindow;

    /// <summary>VNC 缩放按钮 ToolTip，随档位变化。</summary>
    public string VncScalingLabel => VncScale switch
    {
        VncScaleMode.Fill => "拉伸铺满",
        VncScaleMode.Original => "1:1 原始",
        _ => "等比适应"
    };

    /// <summary>请求宿主视图执行某个操作（全屏、发送 Ctrl+Alt+Del 等）。</summary>
    public event EventHandler<SessionAction>? ActionRequested;

    [RelayCommand]
    private void ToggleFullScreen() => ActionRequested?.Invoke(this, SessionAction.ToggleFullScreen);

    [RelayCommand]
    private void SendCtrlAltDelete() => ActionRequested?.Invoke(this, SessionAction.SendCtrlAltDelete);

    [RelayCommand]
    private void CopySelection() => ActionRequested?.Invoke(this, SessionAction.Copy);

    [RelayCommand]
    private void PasteClipboard() => ActionRequested?.Invoke(this, SessionAction.Paste);

    [RelayCommand]
    private void ClearTerminal() => ActionRequested?.Invoke(this, SessionAction.ClearTerminal);

    [RelayCommand]
    private void SearchTerminal() => ActionRequested?.Invoke(this, SessionAction.SearchTerminal);

    [RelayCommand]
    private void ToggleScaling()
    {
        ScaleToFit = !ScaleToFit;
        ActionRequested?.Invoke(this, SessionAction.ToggleScaling);
    }

    /// <summary>VNC 缩放三档循环：等比适应 → 拉伸铺满 → 1:1 → 等比适应。</summary>
    [RelayCommand]
    private void CycleVncScaling()
    {
        VncScale = VncScale switch
        {
            VncScaleMode.FitToWindow => VncScaleMode.Fill,
            VncScaleMode.Fill => VncScaleMode.Original,
            _ => VncScaleMode.FitToWindow
        };
        OnPropertyChanged(nameof(VncScalingLabel));
        ActionRequested?.Invoke(this, SessionAction.ToggleScaling);
    }

    [RelayCommand]
    private async Task CloseAsync() => await _closeCallback(Session.SessionId);

    /// <summary>
    /// 重新连接。当前会话已不可用，因此关闭它并以相同配置新建一个会话，
    /// 保证协议控件与后台线程被完整释放，不残留半个会话。
    /// </summary>
    [RelayCommand]
    private async Task ReconnectAsync()
    {
        var profile = Session.Profile;
        await _closeCallback(Session.SessionId);
        await _reconnectCallback(profile);
    }

    private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        // 状态事件可能来自协议库的后台线程，切回 UI 线程再更新绑定属性。
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            UpdateStateDisplay(e.NewState, e.ErrorCode, e.ErrorMessage);
        }
        else
        {
            dispatcher.BeginInvoke(() => UpdateStateDisplay(e.NewState, e.ErrorCode, e.ErrorMessage));
        }
    }

    private void UpdateStateDisplay(ConnectionState state, ConnectionErrorCode errorCode, string? errorMessage)
    {
        // 自动重连（Reconnecting）与初次连接一样需要进度指示，因此并入 IsConnecting；
        // 但不算已连接，也不进入断线状态层（由协议库自动恢复，无需手动「重新连接」按钮）。
        IsConnecting = state is ConnectionState.Connecting or ConnectionState.Reconnecting;
        IsConnected = state == ConnectionState.Connected;
        IsInterrupted = state is ConnectionState.Failed or ConnectionState.Disconnected;

        switch (state)
        {
            case ConnectionState.Idle:
                StateText = "准备中";
                StateIcon = "\uE895";
                StateBrushKey = "Status.Idle";
                break;

            case ConnectionState.Connecting:
                StateText = "正在连接…";
                StateIcon = "\uE895";
                StateBrushKey = "Status.Info";
                break;

            case ConnectionState.Connected:
                StateText = "已连接";
                StateIcon = "\uE930";
                StateBrushKey = "Status.Success";
                InterruptionMessage = string.Empty;
                RequestStartFullScreenIfConfigured();
                break;

            case ConnectionState.Reconnecting:
                // 自动重连中：表现对齐 Connecting（IsConnecting/图标/Info 色），
                // 不再让状态栏停留在「已连接」造成「已连却断」的观感矛盾。
                StateText = "重新连接中…";
                StateIcon = "\uE895";
                StateBrushKey = "Status.Info";
                InterruptionMessage = string.Empty;
                break;

            case ConnectionState.Disconnecting:
                StateText = "正在断开…";
                StateIcon = "\uE895";
                StateBrushKey = "Status.Idle";
                break;

            case ConnectionState.Disconnected:
                StateText = "已断开";
                StateIcon = "\uE7BA";
                StateBrushKey = "Status.Warning";
                InterruptionMessage = "会话已断开。";
                break;

            case ConnectionState.Failed:
                StateText = "连接失败";
                StateIcon = "\uEA39";
                StateBrushKey = "Status.Danger";
                InterruptionMessage = errorMessage ?? ConnectionException.Describe(errorCode);
                break;

            case ConnectionState.Closed:
                // 会话已终结（SessionManager 移除后 MarkClosed），Tab 即将被移除：短暂窗口内给出明确文案。
                StateText = "已关闭";
                StateIcon = "\uE7BA";
                StateBrushKey = "Status.Idle";
                break;
        }
    }

    /// <summary>
    /// 连接配置勾选「启动后进入全屏」时，首次进入 Connected 后向宿主请求应用级全屏。
    /// 只在当前会话可见（选中）时生效；自动重连回到 Connected 不重复请求。
    /// </summary>
    private void RequestStartFullScreenIfConfigured()
    {
        if (_startFullScreenRequested || !IsActive)
        {
            return;
        }

        if (Protocol != ProtocolType.Rdp || !Profile.Rdp.StartFullScreen)
        {
            return;
        }

        _startFullScreenRequested = true;
        ActionRequested?.Invoke(this, SessionAction.EnterFullScreen);
    }

    public void Dispose() => Session.StateChanged -= OnSessionStateChanged;
}

/// <summary>会话工具条动作。由 ViewModel 发出，具体行为交给对应的协议视图执行。</summary>
public enum SessionAction
{
    ToggleFullScreen,
    EnterFullScreen,
    ToggleScaling,
    SendCtrlAltDelete,
    Copy,
    Paste,
    ClearTerminal,
    SearchTerminal
}
