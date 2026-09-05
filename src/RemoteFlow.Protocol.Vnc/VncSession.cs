using System.Net.Sockets;
using MarcusW.VncClient;
using MarcusW.VncClient.Protocol;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing;
using MarcusW.VncClient.Protocol.Implementation.Services.Transports;
using MarcusW.VncClient.Protocol.SecurityTypes;
using MarcusW.VncClient.Security;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using VncConnectionState = MarcusW.VncClient.ConnectionState;
using VncLibClient = MarcusW.VncClient.VncClient;

// System.Net.Sockets 与 MarcusW.VncClient 都定义了同名类型，
// 这里显式指向 RemoteFlow 领域模型中的枚举。
using ProtocolType = RemoteFlow.Core.Models.ProtocolType;
using ConnectionState = RemoteFlow.Core.Models.ConnectionState;

namespace RemoteFlow.Protocol.Vnc;

/// <summary>
/// VNC / macOS Screen Sharing 会话。基于 RFB 协议的纯托管实现，无原生依赖。
/// </summary>
public sealed class VncSession : IRemoteSession, IAuthenticationHandler
{
    private readonly SessionRequest _request;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    private RfbConnection? _connection;
    private ResolvedCredential? _credential;

    /// <summary>连接期间保留的密码副本。RFB 在重连时可能再次索取认证输入。</summary>
    private string? _password;

    private volatile bool _disposed;

    /// <summary>
    /// 会话级取消源：关闭（Disconnect / Dispose / 远端关闭释放）时取消，
    /// 使在途 <see cref="ConnectAsync"/> 能立刻中断握手 / 建流，而不是干等协议库超时。
    /// </summary>
    private readonly CancellationTokenSource _lifecycleCts = new();

    /// <summary>会话已进入收尾（Disconnect / Dispose / 远端关闭释放）。置位后不再允许新连接 / 回填复活。</summary>
    private volatile bool _closing;

    /// <summary>
    /// 连接释放认领位。同一连接的多路释放（远端关闭回调 vs UI Disconnect/Dispose）并发时，
    /// 只让第一个认领成功的调用者真正执行 Close/Dispose（Interlocked.Exchange 0→1）。
    /// </summary>
    private int _releaseClaimed;

    public VncSession(SessionRequest request, ILoggerFactory loggerFactory)
    {
        _request = request;
        _credential = request.Credential;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<VncSession>();

        RenderTarget = new VncRenderTarget();
    }

    public Guid SessionId { get; } = Guid.NewGuid();

    public ProtocolType Protocol => ProtocolType.Vnc;

    public ConnectionProfile Profile => _request.Profile;

    public ConnectionState State { get; private set; } = ConnectionState.Idle;

    public ConnectionErrorCode ErrorCode { get; private set; } = ConnectionErrorCode.None;

    public string? ErrorMessage { get; private set; }

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <summary>是否已进入关闭 / 收尾流程（Disconnect / Dispose / 远端关闭释放后为 true）。</summary>
    public bool IsClosing => _closing;

    /// <summary>
    /// 收尾：将会话置 <see cref="ConnectionState.Closed"/>（终态，幂等）。
    /// <para>SessionManager 在 teardown + 释放（_disposed 已置位）之后调用；
    /// 不能因已释放而提前返回，Closed 仍需广播，供 UI 移除 Tab / 历史收口。</para>
    /// </summary>
    public void MarkClosed() => SetState(ConnectionState.Closed);

    /// <summary>帧缓冲渲染目标。UI 侧的显示控件从这里取画面。</summary>
    public VncRenderTarget RenderTarget { get; }

    /// <summary>远端桌面尺寸。用于 1:1 显示与坐标换算。</summary>
    public MarcusW.VncClient.Size RemoteSize => _connection?.RemoteFramebufferSize ?? MarcusW.VncClient.Size.Zero;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(VncSession));
        }

        if (_closing || State is ConnectionState.Connecting or ConnectionState.Connected)
        {
            return;
        }

        SetState(ConnectionState.Connecting);

        try
        {
            // 密码在连接期间需要多次可用（认证、断线重连），
            // 因此从凭据取出后立即释放凭据对象本身。
            _password = _credential?.Password;
            _credential?.Dispose();
            _credential = null;

            var client = new VncLibClient(_loggerFactory);

            var parameters = new ConnectParameters
            {
                TransportParameters = new TcpTransportParameters
                {
                    Host = Profile.Host,
                    Port = Profile.Port
                },
                AuthenticationHandler = this,
                InitialRenderTarget = RenderTarget,
                ConnectTimeout = TimeSpan.FromSeconds(Math.Max(Profile.Vnc.ConnectTimeoutSeconds, 5)),
                AllowSharedConnection = Profile.Vnc.SharedConnection,

                // 断线由 UI 显示轻量状态层并提供「重新连接」，
                // 不让协议库在后台无限重试，避免用户看不到真实状态。
                MaxReconnectAttempts = 0
            };

            // 会话级取消：调用方 token 与会话关闭流程（Disconnect / Dispose / 远端关）任一取消，
            // 都会打断本次连接。连接中关闭会话因此能立刻中断，而不是干等库自身超时。
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifecycleCts.Token);

            var connection = await client.ConnectAsync(parameters, linkedCts.Token);

            // 连接在关闭 / 释放流程启动后才返回：禁止把迟到连接回填给已释放 / 已 closing 的会话，
            // 否则会让已 Disconnected / 已释放的会话复活。这里立即释放迟到连接。
            if (_disposed || _closing)
            {
                DisposeLateConnection(connection);
                return;
            }

            // 原子认领连接槽位（检查 + 赋值一体）：仅当槽位仍为空（无既有连接）才写入。
            // CompareExchange 返回原值：返回非 null 说明槽位已被并发路径占用 / 释放流程接管，
            // 放弃这条迟到连接，绝不覆盖既有连接。
            if (Interlocked.CompareExchange(ref _connection, connection, null) is not null)
            {
                DisposeLateConnection(connection);
                return;
            }

            connection.PropertyChanged += OnConnectionPropertyChanged;

            // 认领成功后再次确认：关闭流程若恰在「检查 + 认领」之间启动，撤销写入并释放。
            // 若释放已被并发 Disconnect/Dispose 认领，槽位中的连接由认领者负责释放，这里不再重复释放。
            if (_disposed || _closing)
            {
                connection.PropertyChanged -= OnConnectionPropertyChanged;

                if (TryClaimRelease())
                {
                    Interlocked.Exchange(ref _connection, null);
                    await CloseAndDisposeConnectionAsync(connection);
                }

                return;
            }

            SetState(ConnectionState.Connected);

            _logger.LogInformation(
                "VNC 会话 {SessionId} 已连接 {Host}:{Port}，远端桌面 {Width}x{Height}",
                SessionId, Profile.Host, Profile.Port,
                _connection.RemoteFramebufferSize.Width, _connection.RemoteFramebufferSize.Height);
        }
        catch (OperationCanceledException)
        {
            // 取消若来自关闭流程（Disconnect / Dispose / 远端关），会话已在收尾，不再标 Failed。
            if (_disposed || _closing)
            {
                return;
            }

            Fail(ConnectionErrorCode.Cancelled, null);
        }
        catch (Exception ex)
        {
            // 关闭流程已在收尾时，交由 Disconnect / Dispose 统一收敛，不把已断开的会话标成 Failed。
            if (_disposed || _closing)
            {
                return;
            }

            Fail(MapError(ex), ex);
        }
        finally
        {
            _credential?.Dispose();
            _credential = null;
        }
    }

    public async Task DisconnectAsync()
    {
        // 先标记收尾并取消会话级 CTS：在途 ConnectAsync 会立刻中断，而不是继续握手 / 建流。
        _closing = true;
        CancelLifecycle();

        // 幂等空安全：连接已 null / 已在释放中时 ReleaseConnectionAsync 直接返回。
        await ReleaseConnectionAsync();

        // Closed 为终态：若已被 MarkClosed 置 Closed，后续迟到的断开收尾不得回退到 Disconnected。
        if (State is not (ConnectionState.Failed or ConnectionState.Disconnected or ConnectionState.Closed))
        {
            SetState(ConnectionState.Disconnected);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 先走同一套幂等断开：标记收尾 → 取消会话 CTS → 释放连接 → 置 Disconnected。
        await DisconnectAsync();

        RenderTarget.Dispose();

        _credential?.Dispose();
        _credential = null;
        _password = null;

        try
        {
            _lifecycleCts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // CTS 已释放属预期，忽略。
        }
    }

    // ── 生命周期 helpers ──────────────────────────────────────────

    /// <summary>取消会话级 CTS。幂等；CTS 已被释放时静默忽略。</summary>
    private void CancelLifecycle()
    {
        try
        {
            _lifecycleCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // CTS 已释放属预期，忽略。
        }
    }

    /// <summary>
    /// 幂等释放当前连接：认领释放 → 原子摘除槽位 → 退订状态事件 → 优雅关闭（已 Closed 则跳过）→ Dispose。
    /// 连接已 null 时直接返回。Disconnect / Dispose / 远端关闭多路收敛共用此方法，同一连接只释放一次。
    /// </summary>
    private async Task ReleaseConnectionAsync()
    {
        // 槽位空（无连接，或连接在途尚未写入）时无需释放；此时由迟到的 ConnectAsync 自行释放。
        if (_connection is null)
        {
            return;
        }

        // 认领释放：远端关闭回调与 UI Disconnect/Dispose 并发时，只让第一个成功者执行真正的收尾。
        if (!TryClaimRelease())
        {
            return;
        }

        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is null)
        {
            return;
        }

        connection.PropertyChanged -= OnConnectionPropertyChanged;

        await CloseAndDisposeConnectionAsync(connection);
    }

    /// <summary>
    /// 库状态已终（Closed / Interrupted / ReconnectFailed）时立即释放连接，不再残留到关 Tab。
    /// <para>
    /// 由库的 PropertyChanged 回调触发。库在关闭 / 重连收尾路径持有内部信号量，若在本回调里
    /// 同步调用 CloseAsync / Dispose 会与库自身收尾互相等待（自锁 / 死锁）；因此这里只同步摘除
    /// 引用并退订，把真正的 Close / Dispose 放到后台异步执行。
    /// </para>
    /// </summary>
    private void ReleaseConnectionOnRemoteClose(RfbConnection connection)
    {
        // 与 UI Disconnect/Dispose 并发：只让一路真正释放同一连接。
        if (!TryClaimRelease())
        {
            return;
        }

        // 原子摘除：仅当槽位仍指向本连接才清空，避免误清并发写入的新连接。
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _connection, null, connection), connection))
        {
            return;
        }

        connection.PropertyChanged -= OnConnectionPropertyChanged;

        // 库可能在持有内部信号量的线程上触发本回调（例如重连放弃路径在持锁时置 Closed），
        // 若在这里同步执行 Close/Dispose 会与库自身收尾互相等待（自锁）。放到线程池异步执行。
        _ = Task.Run(() => CloseAndDisposeConnectionAsync(connection));
    }

    /// <summary>认领「连接释放」。0→1 成功返回 true 的调用者负责真正释放；其余并发释放路径直接返回。</summary>
    private bool TryClaimRelease() => Interlocked.Exchange(ref _releaseClaimed, 1) == 0;

    /// <summary>释放一条从未进入会话槽位（迟到 / 未认领）的连接。同步幂等，异常只记录。</summary>
    private void DisposeLateConnection(RfbConnection connection)
    {
        try
        {
            connection.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VNC 会话 {SessionId} 释放迟到连接时出现异常", SessionId);
        }
    }

    /// <summary>关闭并释放单个连接。任何异常只记录，不向调用方传播（供后台 fire-and-forget 调用）。</summary>
    private async Task CloseAndDisposeConnectionAsync(RfbConnection connection)
    {
        try
        {
            // 已 Closed 的连接无需再走 CloseAsync（会重复收尾）；直接 Dispose 即可。
            if (connection.ConnectionState != VncConnectionState.Closed)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VNC 会话 {SessionId} 关闭连接时出现异常", SessionId);
        }

        try
        {
            connection.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VNC 会话 {SessionId} 释放连接时出现异常", SessionId);
        }
    }

    // ── 输入转发 ──────────────────────────────────────────────────

    /// <summary>发送鼠标事件。<paramref name="position"/> 为远端桌面坐标系中的位置。</summary>
    public void SendPointerEvent(Position position, MouseButtons buttons)
    {
        if (Profile.Vnc.ViewOnly)
        {
            return;
        }

        var ok = TryEnqueue(new PointerEventMessage(position, buttons));
        _logger.LogInformation(
            "[VNC INPUT] PointerEvent mask=0x{mask:x2} pos={position} -> {result}",
            (int)buttons, position, ok ? "SEND OK" : "SEND FAILED");
    }

    /// <summary>发送按键事件。</summary>
    public void SendKeyEvent(KeySymbol keySymbol, bool isDown)
    {
        if (Profile.Vnc.ViewOnly)
        {
            return;
        }

        var ok = TryEnqueue(new KeyEventMessage(isDown, keySymbol));
        _logger.LogInformation(
            "[VNC INPUT] Key{action} keysym={keySymbol} -> {result}",
            isDown ? "Down" : "Up", keySymbol, ok ? "SEND OK" : "SEND FAILED");
    }

    /// <summary>发送一次完整的按键（按下并抬起），用于文本输入。</summary>
    public void SendKeyStroke(KeySymbol keySymbol)
    {
        SendKeyEvent(keySymbol, isDown: true);
        SendKeyEvent(keySymbol, isDown: false);
    }

    /// <summary>
    /// 把输入消息入队。泛型携带<b>具体</b>的消息类型描述符（TMessageType），
    /// 而不是收窄成基接口——否则库会按“任意首个”类型描述符序列化实际消息，
    /// 发送循环抛 ArgumentException 直接死亡，远端收不到任何输入。
    /// </summary>
    private bool TryEnqueue<TMessageType>(
        MarcusW.VncClient.Protocol.MessageTypes.IOutgoingMessage<TMessageType> message)
        where TMessageType : class, MarcusW.VncClient.Protocol.MessageTypes.IOutgoingMessageType
    {
        var connection = _connection;
        if (connection is null || State != ConnectionState.Connected)
        {
            return false;
        }

        try
        {
            connection.EnqueueMessage(message);
            return true;
        }
        catch (Exception ex)
        {
            // 队列已中止或连接正在关闭时发送会失败，交由状态变化事件统一处理。
            _logger.LogWarning(ex, "VNC 会话 {SessionId} 发送输入失败（发送队列可能已中止）", SessionId);
            return false;
        }
    }

    // ── 认证 ──────────────────────────────────────────────────────

    /// <summary>
    /// 提供认证输入。由协议库在握手阶段回调。
    /// <para>
    /// VNC 标准认证只需密码；部分服务端（如带用户名的 VeNCrypt）会索取用户名+密码。
    /// </para>
    /// </summary>
    public Task<TInput> ProvideAuthenticationInputAsync<TInput>(
        RfbConnection connection, ISecurityType securityType, IAuthenticationInputRequest<TInput> request)
        where TInput : class, IAuthenticationInput
    {
        if (typeof(TInput) == typeof(PasswordAuthenticationInput))
        {
            if (string.IsNullOrEmpty(_password))
            {
                throw new ConnectionException(
                    ConnectionErrorCode.CredentialMissing,
                    "该 VNC 服务器要求密码，但所选凭据未提供密码。");
            }

            return Task.FromResult((TInput)(object)new PasswordAuthenticationInput(_password));
        }

        if (typeof(TInput) == typeof(CredentialsAuthenticationInput))
        {
            return Task.FromResult((TInput)(object)new CredentialsAuthenticationInput(
                Profile.Vnc.ViewOnly ? string.Empty : GetUsername(), _password ?? string.Empty));
        }

        throw new ConnectionException(
            ConnectionErrorCode.ProtocolNegotiationFailed,
            $"该 VNC 服务器要求的认证方式（{securityType.Name}）暂不支持。");
    }

    private string GetUsername() => _request.Credential?.Username ?? string.Empty;

    // ── 状态跟踪 ──────────────────────────────────────────────────

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RfbConnection.ConnectionState))
        {
            return;
        }

        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        // 会话已释放 / 已不在连接态（已断开、失败或正在收尾）时，库后续状态漂移不再处理：
        // 状态推进与释放统一由 Disconnect / Dispose 收敛，避免把已结束的会话再标记成断开 / 失败。
        if (_disposed || State != ConnectionState.Connected)
        {
            return;
        }

        switch (connection.ConnectionState)
        {
            case VncConnectionState.Closed:
                _logger.LogInformation("VNC 会话 {SessionId} 已被远端关闭", SessionId);
                _closing = true;
                SetState(ConnectionState.Disconnected);
                ReleaseConnectionOnRemoteClose(connection);
                break;

            case VncConnectionState.Interrupted:
            case VncConnectionState.ReconnectFailed:
                ErrorMessage = ConnectionException.Describe(ConnectionErrorCode.RemoteClosed);
                _closing = true;
                Fail(ConnectionErrorCode.RemoteClosed, connection.InterruptionCause);
                ReleaseConnectionOnRemoteClose(connection);
                break;
        }
    }

    /// <summary>把协议库异常映射为标准化错误码。</summary>
    private static ConnectionErrorCode MapError(Exception ex) => ex switch
    {
        ConnectionException connectionException => connectionException.ErrorCode,
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData }
            => ConnectionErrorCode.HostNotFound,
        SocketException { SocketErrorCode: SocketError.TimedOut } => ConnectionErrorCode.Timeout,
        SocketException => ConnectionErrorCode.NetworkUnreachable,
        TimeoutException => ConnectionErrorCode.Timeout,
        HandshakeFailedException => ConnectionErrorCode.AuthenticationFailed,
        UnsupportedProtocolFeatureException => ConnectionErrorCode.ProtocolNegotiationFailed,
        RfbProtocolException => ConnectionErrorCode.ProtocolNegotiationFailed,
        _ => ConnectionErrorCode.Unknown
    };

    private void Fail(ConnectionErrorCode code, Exception? ex)
    {
        ErrorCode = code;
        ErrorMessage = ex is ConnectionException connectionException
            ? connectionException.Message
            : ConnectionException.Describe(code);

        if (ex is not null)
        {
            _logger.LogError(ex, "VNC 会话 {SessionId} 失败，错误码 {ErrorCode}", SessionId, code);
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
