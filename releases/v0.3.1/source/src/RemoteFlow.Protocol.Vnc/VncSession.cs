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

    /// <summary>帧缓冲渲染目标。UI 侧的显示控件从这里取画面。</summary>
    public VncRenderTarget RenderTarget { get; }

    /// <summary>远端桌面尺寸。用于 1:1 显示与坐标换算。</summary>
    public MarcusW.VncClient.Size RemoteSize => _connection?.RemoteFramebufferSize ?? MarcusW.VncClient.Size.Zero;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (State is ConnectionState.Connecting or ConnectionState.Connected)
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

            _connection = await client.ConnectAsync(parameters, cancellationToken);
            _connection.PropertyChanged += OnConnectionPropertyChanged;

            SetState(ConnectionState.Connected);

            _logger.LogInformation(
                "VNC 会话 {SessionId} 已连接 {Host}:{Port}，远端桌面 {Width}x{Height}",
                SessionId, Profile.Host, Profile.Port,
                _connection.RemoteFramebufferSize.Width, _connection.RemoteFramebufferSize.Height);
        }
        catch (OperationCanceledException)
        {
            Fail(ConnectionErrorCode.Cancelled, null);
        }
        catch (Exception ex)
        {
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
        var connection = _connection;
        if (connection is not null)
        {
            connection.PropertyChanged -= OnConnectionPropertyChanged;

            try
            {
                await connection.CloseAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VNC 会话 {SessionId} 关闭连接时出现异常", SessionId);
            }
        }

        if (State is not (ConnectionState.Failed or ConnectionState.Disconnected))
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

        await DisconnectAsync();

        _connection?.Dispose();
        _connection = null;

        RenderTarget.Dispose();

        _credential?.Dispose();
        _credential = null;
        _password = null;
    }

    // ── 输入转发 ──────────────────────────────────────────────────

    /// <summary>发送鼠标事件。<paramref name="position"/> 为远端桌面坐标系中的位置。</summary>
    public void SendPointerEvent(Position position, MouseButtons buttons)
    {
        if (Profile.Vnc.ViewOnly)
        {
            return;
        }

        TryEnqueue(new PointerEventMessage(position, buttons));
    }

    /// <summary>发送按键事件。</summary>
    public void SendKeyEvent(KeySymbol keySymbol, bool isDown)
    {
        if (Profile.Vnc.ViewOnly)
        {
            return;
        }

        TryEnqueue(new KeyEventMessage(isDown, keySymbol));
    }

    /// <summary>发送一次完整的按键（按下并抬起），用于文本输入。</summary>
    public void SendKeyStroke(KeySymbol keySymbol)
    {
        SendKeyEvent(keySymbol, isDown: true);
        SendKeyEvent(keySymbol, isDown: false);
    }

    private void TryEnqueue<TMessage>(TMessage message)
        where TMessage : class, MarcusW.VncClient.Protocol.MessageTypes.IOutgoingMessage<MarcusW.VncClient.Protocol.MessageTypes.IOutgoingMessageType>
    {
        var connection = _connection;
        if (connection is null || State != ConnectionState.Connected)
        {
            return;
        }

        try
        {
            connection.EnqueueMessage(message);
        }
        catch (Exception ex)
        {
            // 输入发送失败通常意味着连接已断，交由状态变化事件统一处理。
            _logger.LogDebug(ex, "VNC 会话 {SessionId} 发送输入失败", SessionId);
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
        if (e.PropertyName != nameof(RfbConnection.ConnectionState) || _connection is null)
        {
            return;
        }

        switch (_connection.ConnectionState)
        {
            case VncConnectionState.Closed:
                if (State == ConnectionState.Connected)
                {
                    _logger.LogInformation("VNC 会话 {SessionId} 已被远端关闭", SessionId);
                    SetState(ConnectionState.Disconnected);
                }
                break;

            case VncConnectionState.Interrupted:
            case VncConnectionState.ReconnectFailed:
                if (State == ConnectionState.Connected)
                {
                    ErrorMessage = ConnectionException.Describe(ConnectionErrorCode.RemoteClosed);
                    Fail(ConnectionErrorCode.RemoteClosed, _connection.InterruptionCause);
                }
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
