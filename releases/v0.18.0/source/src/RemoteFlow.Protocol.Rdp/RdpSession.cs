using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Protocol.Rdp.Interop;

namespace RemoteFlow.Protocol.Rdp;

/// <summary>
/// RDP 会话。基于系统自带的 Microsoft RDP Client ActiveX 控件，不自研 RDP 协议栈。
/// <para>
/// <b>宿主时序约束（这是 ActiveX 内嵌最容易出错的地方）：</b>
/// ActiveX 控件必须先进入可视树、创建出窗口句柄，才能取到 COM 对象并配置属性。
/// 因此 <see cref="HostControl"/> 在构造时即创建，但
/// <see cref="ConnectAsync"/> 会等待句柄就绪后才开始配置与连接。
/// </para>
/// </summary>
public sealed class RdpSession : IRemoteSession, IMsTscAxEvents
{
    private readonly SessionRequest _request;
    private readonly ILogger _logger;

    private readonly RdpAxHost _host;
    private IConnectionPoint? _connectionPoint;
    private int _adviseCookie;

    private ResolvedCredential? _credential;
    private volatile bool _disposed;

    /// <summary>
    /// 会话级连接取消源：连接中关闭会话（Disconnect / Dispose）时取消它，
    /// 使在途 <see cref="ConnectAsync"/> 立刻中断，而不是挂死在 _connectSignal 上。
    /// </summary>
    private readonly CancellationTokenSource _lifecycleCts = new();

    /// <summary>连接完成（成功或失败）的信号，用于让 ConnectAsync 等待 COM 事件回调。</summary>
    private TaskCompletionSource<bool>? _connectSignal;

    public RdpSession(SessionRequest request, string clsid, ILogger logger)
    {
        _request = request;
        _credential = request.Credential;
        _logger = logger;

        _host = new RdpAxHost(clsid)
        {
            Dock = DockStyle.Fill
        };
    }

    public Guid SessionId { get; } = Guid.NewGuid();

    public ProtocolType Protocol => ProtocolType.Rdp;

    public ConnectionProfile Profile => _request.Profile;

    public ConnectionState State { get; private set; } = ConnectionState.Idle;

    public ConnectionErrorCode ErrorCode { get; private set; } = ConnectionErrorCode.None;

    public string? ErrorMessage { get; private set; }

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <summary>是否已进入关闭 / 收尾流程。RDP 的收尾（取消 + 退订 + 断开 + 释放控件）集中在
    /// <see cref="DisposeAsync"/>，故以释放位（_disposed）近似表示。</summary>
    public bool IsClosing => _disposed;

    /// <summary>
    /// 收尾：将会话置 <see cref="ConnectionState.Closed"/>（终态，幂等）。
    /// <para>SessionManager 在 teardown + 释放（_disposed 已置位）之后调用；
    /// 不能因已释放而提前返回，Closed 仍需广播，供 UI 移除 Tab / 历史收口。</para>
    /// </summary>
    public void MarkClosed() => SetState(ConnectionState.Closed);

    /// <summary>
    /// 供 UI 放入 <c>WindowsFormsHost</c> 的宿主控件。
    /// 必须先加入可视树，<see cref="ConnectAsync"/> 才能成功。
    /// </summary>
    public Control HostControl => _host;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (State is ConnectionState.Connecting or ConnectionState.Connected)
        {
            return;
        }

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RdpSession));
        }

        SetState(ConnectionState.Connecting);

        try
        {
            // 会话级取消：视图/调用方传入的外部 token 与会话自身生命周期 CTS 任一取消，
            // 都会打断本次连接。连接中关闭会话（Manager 关闭或视图释放）因此能立刻中断。
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifecycleCts.Token);

            // 等待宿主控件完成句柄创建，否则 GetOcx() 返回 null。
            var ocx = await WaitForActiveXAsync(linkedCts.Token)
                ?? throw new ConnectionException(
                    ConnectionErrorCode.ComponentUnavailable,
                    "远程桌面控件未能初始化，请确认本机的远程桌面客户端组件完整。");

            Configure(ocx);
            SubscribeEvents(ocx);

            _connectSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            dynamic client = ocx;
            client.Connect();

            // 连接结果通过 COM 事件回调返回，这里等待信号或取消。
            await using (linkedCts.Token.Register(() => _connectSignal?.TrySetCanceled()))
            {
                var ok = await _connectSignal.Task;

                // 信号返回但未真正进入 Connected（失败回调已 Fail）：兜底执行与 Dispose
                // 同源的收口，确保本对象不会残留 COM Advise（同对象重试不会重复订阅）。
                if (!ok && State != ConnectionState.Connected)
                {
                    UnsubscribeEvents();
                    SafeDisconnect();
                }
            }
        }
        catch (OperationCanceledException)
        {
            FailCleanup(ConnectionErrorCode.Cancelled, null);
        }
        catch (ConnectionException ex)
        {
            FailCleanup(ex.ErrorCode, ex);
        }
        catch (Exception ex)
        {
            FailCleanup(ConnectionErrorCode.Unknown, ex);
        }
        finally
        {
            _connectSignal = null;

            // 密码已交给控件，托管侧不再保留。
            _credential?.Dispose();
            _credential = null;
        }
    }

    public Task DisconnectAsync()
    {
        // 中断进行中的连接等待：连接中关闭会话时让 ConnectAsync 立即退出，而不是挂死等待。
        try
        {
            _lifecycleCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // CTS 已释放属预期，忽略。
        }

        SafeDisconnect();

        // Closed 为终态：若已被 MarkClosed 置 Closed，后续迟到的断开收尾不得回退到 Disconnected。
        if (State is not (ConnectionState.Failed or ConnectionState.Disconnected or ConnectionState.Closed))
        {
            SetState(ConnectionState.Disconnected);
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        // 先取消在途连接等待，让 ConnectAsync 退出，避免后续退订事件后它永远等不到回调。
        try
        {
            _lifecycleCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // CTS 已释放属预期，忽略。
        }

        // 顺序很重要：先解除事件挂接，再断开，最后释放控件。
        // 否则控件会持有托管事件接收对象，导致会话对象无法回收。
        UnsubscribeEvents();
        SafeDisconnect();

        try
        {
            _host.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RDP 会话 {SessionId} 释放宿主控件失败", SessionId);
        }

        try
        {
            _lifecycleCts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // CTS 已释放属预期，忽略。
        }

        _credential?.Dispose();
        _credential = null;

        return ValueTask.CompletedTask;
    }

    // ── 会话操作 ──────────────────────────────────────────────────

    /// <summary>切换全屏。</summary>
    public void SetFullScreen(bool fullScreen)
    {
        TryInvoke("切换全屏", ocx =>
        {
            dynamic client = ocx;
            client.FullScreen = fullScreen;
        });
    }

    /// <summary>通过 RDP 控件在远端会话中启动任务管理器。</summary>
    public bool LaunchTaskManager()
    {
        if (_disposed || State != ConnectionState.Connected)
        {
            _logger.LogWarning(
                "RDP 会话 {SessionId} 忽略「启动任务管理器」：当前状态 {State}",
                SessionId,
                State);
            return false;
        }

        var ocx = _host.ActiveXInstance;
        if (ocx is null)
        {
            _logger.LogWarning("RDP 会话 {SessionId} 无法启动远端任务管理器：控件未就绪", SessionId);
            return false;
        }

        try
        {
            dynamic client = ocx;
            client.SendRemoteAction((int)RdpRemoteSessionAction.TaskManager);

            _logger.LogInformation(
                "RDP 会话 {SessionId} 已发送远端任务管理器语义动作",
                SessionId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "RDP 会话 {SessionId} 发送任务管理器语义动作失败，回退到协议按键",
                SessionId);
        }

        try
        {
            if (ocx is not IMsRdpClientNonScriptable remoteInput)
            {
                _logger.LogWarning(
                    "RDP 会话 {SessionId} 无法启动远端任务管理器：控件不支持协议级按键输入",
                    SessionId);
                return false;
            }

            var sequence = RdpKeyboardSequence.TaskManager;
            var keyUpStates = new short[sequence.Count];
            var keyData = new int[sequence.Count];

            for (var index = 0; index < sequence.Count; index++)
            {
                var stroke = sequence[index];
                // VARIANT_BOOL 的 true 是 16 位 -1，不能按 Win32 BOOL 或托管 bool 数组传递。
                keyUpStates[index] = stroke.IsKeyUp ? (short)-1 : (short)0;
                keyData[index] = stroke.KeyData;
            }

            var keyUpHandle = GCHandle.Alloc(keyUpStates, GCHandleType.Pinned);
            try
            {
                var keyDataHandle = GCHandle.Alloc(keyData, GCHandleType.Pinned);
                try
                {
                    remoteInput.SendKeys(
                        sequence.Count,
                        keyUpHandle.AddrOfPinnedObject(),
                        keyDataHandle.AddrOfPinnedObject());
                }
                finally
                {
                    keyDataHandle.Free();
                }
            }
            finally
            {
                keyUpHandle.Free();
            }

            _logger.LogInformation(
                "RDP 会话 {SessionId} 已通过协议按键回退发送 Ctrl+Shift+Esc",
                SessionId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RDP 会话 {SessionId} 发送远端任务管理器快捷键失败", SessionId);
            return false;
        }
    }

    /// <summary>
    /// 通知远程会话按新尺寸重绘。
    /// <para>
    /// 优先使用 IMsRdpClient9 的 <c>UpdateSessionDisplaySettings</c> 做无缝动态调整；
    /// 控件版本过低时退回 SmartSizing 缩放，保证功能可用而不是直接失败。
    /// </para>
    /// </summary>
    public void UpdateDisplaySize(int width, int height)
    {
        if (State != ConnectionState.Connected || width <= 0 || height <= 0)
        {
            return;
        }

        var ocx = _host.ActiveXInstance;
        if (ocx is null)
        {
            return;
        }

        try
        {
            dynamic client = ocx;
            client.UpdateSessionDisplaySettings(
                (uint)width, (uint)height, (uint)width, (uint)height,
                /* orientation */ 0u, /* desktopScaleFactor */ 100u, /* deviceScaleFactor */ 100u);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RDP 会话 {SessionId} 动态调整分辨率失败，回退到 SmartSizing", SessionId);
            TrySetSmartSizing(ocx, true);
        }
    }

    /// <summary>设置是否缩放画面以适应窗口。</summary>
    public void SetSmartSizing(bool enabled)
    {
        var ocx = _host.ActiveXInstance;
        if (ocx is not null)
        {
            TrySetSmartSizing(ocx, enabled);
        }
    }

    private void TrySetSmartSizing(object ocx, bool enabled)
    {
        try
        {
            dynamic client = ocx;
            client.AdvancedSettings9.SmartSizing = enabled;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RDP 会话 {SessionId} 设置 SmartSizing 失败", SessionId);
        }
    }

    // ── 控件配置 ──────────────────────────────────────────────────

    /// <summary>
    /// 等待 ActiveX 控件就绪。控件由 UI 加入可视树后才创建句柄，
    /// 这里以轮询方式等待，避免在 UI 尚未布局完成时访问 COM 对象。
    /// </summary>
    private async Task<object?> WaitForActiveXAsync(CancellationToken ct)
    {
        const int TimeoutMilliseconds = 10_000;
        const int PollIntervalMilliseconds = 50;

        var waited = 0;
        while (waited < TimeoutMilliseconds)
        {
            ct.ThrowIfCancellationRequested();

            if (_host.ActiveXInstance is { } ocx)
            {
                return ocx;
            }

            await Task.Delay(PollIntervalMilliseconds, ct);
            waited += PollIntervalMilliseconds;
        }

        return null;
    }

    private void Configure(object ocx)
    {
        dynamic client = ocx;
        var options = Profile.Rdp;

        client.Server = Profile.Host;

        var credential = _credential;
        if (credential is not null)
        {
            credential.ThrowIfDisposed();
            client.UserName = credential.Username;

            if (!string.IsNullOrEmpty(credential.Domain))
            {
                client.Domain = credential.Domain;
            }
            else if (!string.IsNullOrEmpty(options.Domain))
            {
                client.Domain = options.Domain;
            }
        }
        else if (!string.IsNullOrEmpty(options.Domain))
        {
            client.Domain = options.Domain;
        }

        // 桌面尺寸必须在 Connect 之前确定。适应窗口模式下先用宿主控件的当前尺寸，
        // 连接建立后再通过 UpdateDisplaySize 动态跟随。
        var (width, height) = options.DisplayMode == RdpDisplayMode.FixedResolution
            ? (options.DesktopWidth, options.DesktopHeight)
            : (Math.Max(_host.Width, 1024), Math.Max(_host.Height, 768));

        client.DesktopWidth = width;
        client.DesktopHeight = height;
        client.ColorDepth = options.ColorDepth;

        ConfigureAdvancedSettings(client, options, credential);
    }

    /// <summary>
    /// 配置高级设置。不同控件版本暴露的属性集合不同，
    /// 逐项容错设置：单个属性不受支持时跳过并记录，而不是让整个连接失败。
    /// </summary>
    private void ConfigureAdvancedSettings(dynamic client, RdpOptions options, ResolvedCredential? credential)
    {
        dynamic advanced;
        try
        {
            advanced = client.AdvancedSettings9;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RDP 会话 {SessionId} 无法访问 AdvancedSettings9，将使用控件默认设置", SessionId);
            return;
        }

        TrySet("RDPPort", () => advanced.RDPPort = Profile.Port);

        // 明文密码只在此处交给控件，随后立即释放托管侧的凭据对象。
        // ClearTextPassword 是只写属性，无法被读回。
        if (!string.IsNullOrEmpty(credential?.Password))
        {
            TrySet("ClearTextPassword", () => advanced.ClearTextPassword = credential.Password);
        }

        TrySet("SmartSizing", () => advanced.SmartSizing = options.DisplayMode == RdpDisplayMode.FitToWindow);
        TrySet("RedirectClipboard", () => advanced.RedirectClipboard = options.RedirectClipboard);
        TrySet("RedirectPrinters", () => advanced.RedirectPrinters = options.RedirectPrinters);
        TrySet("RedirectDrives", () => advanced.RedirectDrives = options.RedirectDrives);

        // 0 = 在本机播放，2 = 不播放。
        TrySet("AudioRedirectionMode", () => advanced.AudioRedirectionMode = options.RedirectAudio ? 0 : 2);

        // 麦克风重定向（音频输入）。属性来自 IMsRdpClientAdvancedSettings7 及更高版本，
        // 属于 RDP 8+ 的音频捕获能力；本机 / 远端不支持时该 set 会失败并被 TrySet 跳过。
        TrySet("AudioCaptureRedirectionMode", () => advanced.AudioCaptureRedirectionMode = options.RedirectMicrophone);

        // NLA（CredSSP）。关闭后服务器若强制要求 NLA 仍会拒绝连接。
        TrySet("EnableCredSspSupport", () => advanced.EnableCredSspSupport = options.EnableNla);

        // authenticationLevel = 2：服务器身份验证失败时给出警告而非静默继续，
        // 与「证书异常必须可见」的安全原则一致。
        TrySet("authenticationLevel", () => advanced.authenticationLevel = 2u);

        // 显式启用控件库内自动重连，使 Reconnecting 状态映射不依赖控件默认值。
        TrySet("EnableAutoReconnect", () => advanced.EnableAutoReconnect = true);

        // 连接超时相关（单位：秒）。
        TrySet("singleConnectionTimeout", () => advanced.singleConnectionTimeout = 30);
        TrySet("overallConnectionTimeout", () => advanced.overallConnectionTimeout = 30);

        // 连接质量（体验）：Auto 不写入，让控件/系统自行决定；其余按 IMsRdpClientAdvancedSettings7
        // NetworkConnectionType 的文档常量映射（LAN=6 / 高速宽带=4 / 低带宽=2），远端据此调整体验。
        // 该映射是 ActiveX 上已文档化、可 TrySet 的唯一“连接质量”入口，不额外伪造逐位体验开关。
        if (options.ConnectionQuality != RdpConnectionQuality.Auto)
        {
            var connectionType = options.ConnectionQuality switch
            {
                RdpConnectionQuality.Lan => 6u,
                RdpConnectionQuality.HighSpeed => 4u,
                RdpConnectionQuality.LowBandwidth => 2u,
                _ => 6u
            };
            TrySet("NetworkConnectionType", () => advanced.NetworkConnectionType = connectionType);
        }

        if (options.UseMultimon)
        {
            // 多显示器属性只在较新控件上存在，属于增强项，失败不影响主流程。
            TrySet("UseMultimon", () => client.UseMultimon = true);
        }
    }

    /// <summary>容错设置单个 COM 属性。</summary>
    private void TrySet(string propertyName, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RDP 会话 {SessionId} 不支持设置 {Property}，已跳过", SessionId, propertyName);
        }
    }

    private void TryInvoke(string operation, Action<object> action)
    {
        var ocx = _host.ActiveXInstance;
        if (ocx is null)
        {
            return;
        }

        try
        {
            action(ocx);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RDP 会话 {SessionId} 执行「{Operation}」失败", SessionId, operation);
        }
    }

    // ── COM 事件挂接 ──────────────────────────────────────────────

    private void SubscribeEvents(object ocx)
    {
        // 同对象重试（失败后再次 Connect）时上一轮订阅可能尚未清理：先退订再订，
        // 保证控件上永远只有一份 Advise，不会覆盖丢失旧 cookie。
        UnsubscribeEvents();

        try
        {
            var container = (IConnectionPointContainer)ocx;
            var iid = typeof(IMsTscAxEvents).GUID;

            container.FindConnectionPoint(ref iid, out var connectionPoint);
            if (connectionPoint is null)
            {
                throw new InvalidOperationException($"控件未暴露事件接口 {iid:B} 的连接点。");
            }

            connectionPoint.Advise(this, out _adviseCookie);
            _connectionPoint = connectionPoint;

            _logger.LogDebug("RDP 会话 {SessionId} 已挂接控件事件，Cookie {Cookie}", SessionId, _adviseCookie);
        }
        catch (Exception ex)
        {
            // 没有事件就无法感知连接结果，属于致命问题，必须让连接失败而不是静默继续。
            throw new ConnectionException(
                ConnectionErrorCode.ComponentUnavailable,
                "无法挂接远程桌面控件事件，连接已中止。", ex);
        }
    }

    /// <summary>解除 COM 事件挂接。幂等：从未订阅 / 已退订再调用直接返回，不抛。</summary>
    private void UnsubscribeEvents()
    {
        var connectionPoint = _connectionPoint;
        if (connectionPoint is null)
        {
            return;
        }

        // 先置空再 Unadvise：重入 / 重复调用时第二次直接走上面的空判返回。
        _connectionPoint = null;
        var cookie = _adviseCookie;
        _adviseCookie = 0;

        try
        {
            connectionPoint.Unadvise(cookie);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RDP 会话 {SessionId} 解除事件挂接失败", SessionId);
        }
        finally
        {
            try
            {
                Marshal.ReleaseComObject(connectionPoint);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RDP 会话 {SessionId} 释放事件连接点失败", SessionId);
            }
        }
    }

    private void SafeDisconnect()
    {
        TryInvoke("断开连接", ocx =>
        {
            dynamic client = ocx;
            // Connected: 0 = 已断开，1 = 连接中，2 = 已连接。
            if ((int)client.Connected != 0)
            {
                client.Disconnect();
            }
        });
    }

    /// <summary>
    /// 失败统一收口：与 <see cref="DisposeAsync"/> 同一顺序——先退订 COM 事件、再断开，最后置 Failed。
    /// 保证失败 / 取消 / 致命错误 / 连接中远端断开等所有失败出口都不会在控件上残留 Advise。
    /// 会话已释放（<see cref="_disposed"/>）时只清理不广播状态，避免收尾期间向订阅者发出 Failed。
    /// </summary>
    private void FailCleanup(ConnectionErrorCode code, Exception? ex)
    {
        UnsubscribeEvents();
        SafeDisconnect();

        if (!_disposed)
        {
            Fail(code, ex);
        }

        // 唤醒仍在等待的 ConnectAsync；对已取消的信号是 no-op。
        _connectSignal?.TrySetResult(false);
    }

    // ── IMsTscAxEvents 回调 ───────────────────────────────────────

    public void OnConnecting()
        => _logger.LogDebug("RDP 会话 {SessionId} 正在连接 {Host}", SessionId, Profile.Host);

    public void OnConnected()
    {
        if (_disposed)
        {
            return;
        }

        // 自动重连成功后回到已连接。初次连接阶段不置 Connected：真正的「可用」由
        // OnLoginComplete 完成，避免认证尚未通过就把会话标记为已连接（否则登录失败
        // 会走 OnDisconnected 的已连接分支被记成正常断开而非 Failed）。
        if (State == ConnectionState.Reconnecting)
        {
            SetState(ConnectionState.Connected);
        }

        _logger.LogDebug("RDP 会话 {SessionId} 传输层已连接", SessionId);
    }

    /// <summary>登录完成才算真正建立可用会话。</summary>
    public void OnLoginComplete()
    {
        if (_disposed)
        {
            return;
        }

        // 仅当仍处于连接中 / 自动重连中才推进到 Connected：迟到的成功回调不得把
        // 已 Failed / 已断开 / 已释放的会话复活为 Connected。
        if (State is ConnectionState.Connecting or ConnectionState.Reconnecting)
        {
            SetState(ConnectionState.Connected);
            _connectSignal?.TrySetResult(true);

            _logger.LogInformation("RDP 会话 {SessionId} 已连接 {Host}:{Port}", SessionId, Profile.Host, Profile.Port);
        }
    }

    public void OnDisconnected(int discReason)
    {
        // 已连接后的断开是正常结束；连接过程中的断开则是失败。
        if (State == ConnectionState.Connected)
        {
            SetState(ConnectionState.Disconnected);
            _logger.LogInformation("RDP 会话 {SessionId} 已断开，原因码 {Reason}", SessionId, discReason);
            return;
        }

        // 连接中 / 自动重连失败等非已连接阶段断开：按失败统一收口（退订 + 断开 + Failed）。
        var code = MapDisconnectReason(discReason);
        ErrorMessage = DescribeDisconnect(discReason) ?? ConnectionException.Describe(code);
        _logger.LogDebug("RDP 会话 {SessionId} 连接阶段断开，原因码 {Reason}", SessionId, discReason);
        FailCleanup(code, null);
    }

    public void OnFatalError(int errorCode)
    {
        _logger.LogError("RDP 会话 {SessionId} 控件致命错误，错误码 {ErrorCode}", SessionId, errorCode);

        ErrorMessage = $"远程桌面控件发生错误（代码 {errorCode}）。";
        FailCleanup(ConnectionErrorCode.Unknown, null);
    }

    public void OnLogonError(int lError)
    {
        // -2 表示「正在使用已保存的凭据登录」，属于正常提示而非错误。
        if (lError == -2)
        {
            return;
        }

        _logger.LogWarning("RDP 会话 {SessionId} 登录失败，错误码 {ErrorCode}", SessionId, lError);
        ErrorMessage = ConnectionException.Describe(ConnectionErrorCode.AuthenticationFailed);
        ErrorCode = ConnectionErrorCode.AuthenticationFailed;
    }

    public void OnAutoReconnecting(int disconnectReason, int attemptCount, ref bool continueReconnecting)
    {
        _logger.LogInformation(
            "RDP 会话 {SessionId} 正在自动重连（第 {Attempt} 次）", SessionId, attemptCount);

        // 已连接后掉线进入自动重连：把状态推进到 Reconnecting，让 UI/管理器感知断线中。
        if (State == ConnectionState.Connected)
        {
            SetState(ConnectionState.Reconnecting);
        }

        // 交由控件按自身策略继续重连，UI 通过状态层展示断线提示。
        continueReconnecting = true;
    }

    /// <summary>取控件自带的本地化错误描述，比自造文案更准确。</summary>
    private string? DescribeDisconnect(int discReason)
    {
        var ocx = _host.ActiveXInstance;
        if (ocx is null)
        {
            return null;
        }

        try
        {
            dynamic client = ocx;
            int extendedReason = client.ExtendedDisconnectReason;
            string description = client.GetErrorDescription((uint)discReason, (uint)extendedReason);
            return string.IsNullOrWhiteSpace(description) ? null : description;
        }
        catch
        {
            // 描述属于锦上添花，取不到就退回标准文案。
            return null;
        }
    }

    /// <summary>把 RDP 断开原因码映射为标准化错误码。</summary>
    private static ConnectionErrorCode MapDisconnectReason(int discReason) => discReason switch
    {
        0 or 1 or 2 or 3 => ConnectionErrorCode.RemoteClosed,
        260 or 520 or 1288 => ConnectionErrorCode.HostNotFound,
        264 or 774 => ConnectionErrorCode.Timeout,
        516 or 518 or 2308 or 2311 => ConnectionErrorCode.NetworkUnreachable,
        2825 or 3079 or 3080 or 5639 or 50331656 => ConnectionErrorCode.AuthenticationFailed,
        2822 or 2823 or 2824 => ConnectionErrorCode.ProtocolNegotiationFailed,
        _ => ConnectionErrorCode.Unknown
    };

    private void Fail(ConnectionErrorCode code, Exception? ex)
    {
        ErrorCode = code;
        ErrorMessage ??= ConnectionException.Describe(code);

        if (ex is not null)
        {
            _logger.LogError(ex, "RDP 会话 {SessionId} 连接失败，错误码 {ErrorCode}", SessionId, code);
        }

        SetState(ConnectionState.Failed);
    }

    private void SetState(ConnectionState newState)
    {
        if (State == newState)
        {
            return;
        }

        var oldState = State;
        State = newState;
        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(oldState, newState, ErrorCode, ErrorMessage));
    }
}
