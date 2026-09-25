using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Presentation.Host;

namespace RemoteFlow.Presentation.ViewModels;

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

    /// <summary>会话时长走秒刷新；仅在已连接（含自动重连期间）运行，空闲不打扰。</summary>
    private readonly IUiTimer _durationTimer;

    private readonly IUiDispatcher _ui;

    /// <summary>首次进入 Connected 的时刻（UTC）。自动重连不重置，UI 刷新不重置。</summary>
    private DateTime? _connectedAtUtc;

    public SessionTabViewModel(
        IRemoteSession session,
        Func<Guid, Task> closeCallback,
        Func<ConnectionProfile, Task> reconnectCallback,
        IUiDispatcher uiDispatcher,
        IUiTimerFactory timerFactory)
    {
        Session = session;
        _closeCallback = closeCallback;
        _reconnectCallback = reconnectCallback;
        _ui = uiDispatcher;

        Title = session.Profile.Name;
        Icon = session.Protocol switch
        {
            ProtocolType.Rdp => "\uE7F4",
            ProtocolType.Ssh => "\uE756",
            _ => "\uE7F8"
        };

        // 连接质量详情（Flyout 数据源）。同一实例贯穿整个会话生命周期，
        // 由宿主视图在点击状态入口时读取；探测只在打开 / 重新检测时执行。
        Quality = new SessionQualityState(this, session.Profile.Host, session.Profile.Port);
        Quality.PropertyChanged += OnQualityPropertyChanged;

        _durationTimer = timerFactory.Create(TimeSpan.FromSeconds(1));
        _durationTimer.Tick += OnDurationTick;

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

    /// <summary>连接质量详情状态（Flyout 绑定源）。</summary>
    public SessionQualityState Quality { get; }

    /// <summary>Host:Port 摘要，供 Flyout 头部展示。</summary>
    public string HostPortLine => $"{Profile.Host}:{Profile.Port}";

    // ── Flyout 头部（统一连接状态，叠加「网络波动」派生）───────────

    /// <summary>Flyout 头部状态文案：通常等于 <see cref="StateText"/>；
    /// 已连接但质量为较差时显示「网络波动」。常驻条不用这三者。</summary>
    [ObservableProperty]
    private string _flyoutStateText = "准备中";

    [ObservableProperty]
    private string _flyoutStateIcon = "";

    [ObservableProperty]
    private string _flyoutStateBrushKey = "Status.Idle";

    /// <summary>会话真实自动重连次数（进入 Reconnecting 才 +1，首次连接不计）。</summary>
    [ObservableProperty]
    private int _reconnectCount;

    /// <summary>会话时长 HH:mm:ss，从首次 ConnectedAt 起算，重连不重置。</summary>
    [ObservableProperty]
    private string _sessionDurationText = "--";

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

    /// <summary>逐档进：常规 → 窗口最大化 → 完全全屏 → 常规。</summary>
    [RelayCommand]
    private void AdvanceFullScreen() => ActionRequested?.Invoke(this, SessionAction.AdvanceFullScreen);

    /// <summary>一路退到底：无论哪一档都直接回常规。</summary>
    [RelayCommand]
    private void ExitFullScreen() => ActionRequested?.Invoke(this, SessionAction.ExitFullScreen);

    /// <summary>完全全屏开关：窗口最大化 ↔ 完全全屏。</summary>
    [RelayCommand]
    private void ToggleScreenFull() => ActionRequested?.Invoke(this, SessionAction.ToggleScreenFull);

    [RelayCommand]
    private void SendCtrlAltDelete() => ActionRequested?.Invoke(this, SessionAction.SendCtrlAltDelete);

    /// <summary>请求 RDP 视图向远端发送 Ctrl+Shift+Esc（启动任务管理器）。</summary>
    [RelayCommand]
    private void SendTaskManager() => ActionRequested?.Invoke(this, SessionAction.LaunchTaskManager);

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

    /// <summary>请求宿主把键盘焦点还给会话画面（RDP ActiveX / 终端 / VNC）。由 Flyout 关闭等场景调用。</summary>
    public void RequestSessionFocus() => ActionRequested?.Invoke(this, SessionAction.ReturnFocusToSession);

    private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        // 状态事件可能来自协议库的后台线程，切回 UI 线程再更新绑定属性。
        if (_ui.RequeueIfNeeded(() => OnSessionStateChanged(sender, e)))
        {
            return;
        }

        UpdateStateDisplay(e.NewState, e.ErrorCode, e.ErrorMessage);
        TrackSessionStats(e.NewState);
    }

    /// <summary>
    /// 跟踪会话时长与重连次数（Flyout 数据）。首次进入 Connected 记起点；
    /// 每次真实自动重连（Reconnecting）计数 +1；断开 / 失败 / 终结时停表并复位展示。
    /// </summary>
    private void TrackSessionStats(ConnectionState state)
    {
        switch (state)
        {
            case ConnectionState.Connected when _connectedAtUtc is null:
                _connectedAtUtc = DateTime.UtcNow;
                _durationTimer.Start();
                RefreshDurationText();
                break;

            case ConnectionState.Reconnecting:
                ReconnectCount++;
                break;

            case ConnectionState.Failed or ConnectionState.Disconnected or ConnectionState.Closed:
                _connectedAtUtc = null;
                _durationTimer.Stop();
                SessionDurationText = "--";
                break;
        }

        RefreshFlyoutStateDisplay();
    }

    private void OnDurationTick(object? sender, EventArgs e) => RefreshDurationText();

    private void RefreshDurationText()
    {
        if (_connectedAtUtc is not { } connectedAt)
        {
            SessionDurationText = "--";
            return;
        }

        var elapsed = DateTime.UtcNow - connectedAt;
        SessionDurationText = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    /// <summary>Quality 状态（网络波动判定）变化时同步 Flyout 头部。</summary>
    private void OnQualityPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionQualityState.IsVolatile))
        {
            RefreshFlyoutStateDisplay();
        }
    }

    /// <summary>
    /// 计算 Flyout 头部状态：常驻条始终显示 <see cref="StateText"/>；
    /// Flyout 在「已连接但网络波动」时改显「网络波动」（橙色），其余情况与常驻条一致。
    /// </summary>
    private void RefreshFlyoutStateDisplay()
    {
        if (IsConnected && Quality.IsVolatile)
        {
            FlyoutStateText = "网络波动";
            FlyoutStateIcon = ((char)0xE7BA).ToString();
            FlyoutStateBrushKey = "Status.Warning";
        }
        else
        {
            FlyoutStateText = StateText;
            FlyoutStateIcon = StateIcon;
            FlyoutStateBrushKey = StateBrushKey;
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

        RefreshFlyoutStateDisplay();
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
        ActionRequested?.Invoke(this, SessionAction.EnterScreenFull);
    }

    private bool _disposed;

    /// <summary>幂等：退出清理路径会对本对象 dispose 两次（见 <see cref="SessionQualityState.Dispose"/>）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Session.StateChanged -= OnSessionStateChanged;
        Quality.PropertyChanged -= OnQualityPropertyChanged;
        Quality.Dispose();
        _durationTimer.Tick -= OnDurationTick;
        _durationTimer.Dispose();
    }
}

/// <summary>
/// 会话工具条动作。由 ViewModel 发出，具体行为交给对应的协议视图执行。
/// <para>
/// 新增成员时必须同步复查 <c>RdpSessionView</c> / <c>VncSessionView</c> /
/// <c>SshSessionView</c> 的 <c>switch (action)</c>——这三处目前都没有 default 分支，
/// 未匹配的动作被静默忽略，不会抛异常。
/// </para>
/// </summary>
public enum SessionAction
{
    /// <summary>逐档进：常规 → 窗口最大化 → 完全全屏 → 常规。</summary>
    AdvanceFullScreen,

    /// <summary>一路退到底：无论哪一档都直接回常规。</summary>
    ExitFullScreen,

    /// <summary>完全全屏开关：窗口最大化 ↔ 完全全屏。</summary>
    ToggleScreenFull,

    /// <summary>直达完全全屏，只进不退（连接配置「启动后进入全屏」）。</summary>
    EnterScreenFull,

    ToggleScaling,
    SendCtrlAltDelete,
    LaunchTaskManager,
    Copy,
    Paste,
    ClearTerminal,
    SearchTerminal,

    /// <summary>Flyout 等浮层关闭后把键盘焦点还给会话画面（RDP ActiveX / 终端 / VNC 画面）。</summary>
    ReturnFocusToSession
}
