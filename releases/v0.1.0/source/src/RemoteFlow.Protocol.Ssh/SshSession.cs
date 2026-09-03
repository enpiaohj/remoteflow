using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

// System.Net.Sockets 同样定义了 ProtocolType，此处显式指向领域模型中的协议枚举。
using ProtocolType = RemoteFlow.Core.Models.ProtocolType;

namespace RemoteFlow.Protocol.Ssh;

/// <summary>
/// SSH 会话。
/// <para>
/// <b>分层原则：</b>本类只负责 SSH 协议连接与字节流收发，
/// 不做任何 ANSI 转义解析、字符解码或屏幕缓冲管理——那是终端渲染层（xterm.js）的职责。
/// 因此这里始终以<b>原始字节</b>与上层交互：多字节 UTF-8 字符即使被 TCP 分包切断，
/// 也会由终端侧的解码器正确拼接，不会出现半个汉字变乱码的问题。
/// </para>
/// </summary>
public sealed class SshSession : IRemoteSession
{
    private readonly SessionRequest _request;
    private readonly ILogger _logger;

    private SshClient? _client;
    private ShellStream? _shell;
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoopTask;

    /// <summary>保护断开/释放路径，避免 UI 关闭 Tab 与远端断线同时触发导致重复释放。</summary>
    private readonly SemaphoreSlim _lifecycleMutex = new(1, 1);

    private ResolvedCredential? _credential;
    private volatile bool _disposed;

    /// <summary>Host Key 校验失败的具体原因，用于在连接异常时给出准确错误码。</summary>
    private ConnectionErrorCode _hostKeyFailure = ConnectionErrorCode.None;

    public SshSession(SessionRequest request, ILogger logger)
    {
        _request = request;
        _credential = request.Credential;
        _logger = logger;
    }

    public Guid SessionId { get; } = Guid.NewGuid();

    public ProtocolType Protocol => ProtocolType.Ssh;

    public ConnectionProfile Profile => _request.Profile;

    public ConnectionState State { get; private set; } = ConnectionState.Idle;

    public ConnectionErrorCode ErrorCode { get; private set; } = ConnectionErrorCode.None;

    public string? ErrorMessage { get; private set; }

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <summary>收到远端数据。参数为原始字节，交由终端渲染层解码。</summary>
    public event EventHandler<byte[]>? DataReceived;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (State is ConnectionState.Connecting or ConnectionState.Connected)
        {
            return;
        }

        SetState(ConnectionState.Connecting);

        try
        {
            var connectionInfo = BuildConnectionInfo();
            _client = new SshClient(connectionInfo);
            _client.HostKeyReceived += OnHostKeyReceived;

            if (Profile.Ssh.KeepAliveSeconds > 0)
            {
                _client.KeepAliveInterval = TimeSpan.FromSeconds(Profile.Ssh.KeepAliveSeconds);
            }

            await _client.ConnectAsync(cancellationToken);

            // 初始终端尺寸只是占位：UI 完成布局后会立即调用 Resize 传入真实行列数。
            _shell = _client.CreateShellStream(
                terminalName: string.IsNullOrWhiteSpace(Profile.Ssh.TerminalType) ? "xterm-256color" : Profile.Ssh.TerminalType,
                columns: 80,
                rows: 24,
                width: 800,
                height: 600,
                bufferSize: 16 * 1024);

            _readLoopCts = new CancellationTokenSource();
            _readLoopTask = Task.Run(() => ReadLoopAsync(_shell, _readLoopCts.Token), CancellationToken.None);

            SetState(ConnectionState.Connected);

            _logger.LogInformation(
                "SSH 会话 {SessionId} 已连接 {Host}:{Port}", SessionId, Profile.Host, Profile.Port);

            if (!string.IsNullOrWhiteSpace(Profile.Ssh.InitialCommand))
            {
                SendInput(Profile.Ssh.InitialCommand + "\n");
            }
        }
        catch (OperationCanceledException)
        {
            await CleanupAsync();
            Fail(ConnectionErrorCode.Cancelled, null);
        }
        catch (Exception ex)
        {
            await CleanupAsync();
            var code = MapError(ex);
            Fail(code, ex);
        }
        finally
        {
            // 无论成功失败，凭据都不再需要，立即释放以缩短明文存活时间。
            _credential?.Dispose();
            _credential = null;
        }
    }

    /// <summary>向远端发送用户输入。</summary>
    public void SendInput(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        SendInput(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>向远端发送原始字节。</summary>
    public void SendInput(byte[] data)
    {
        var shell = _shell;
        if (shell is null || State != ConnectionState.Connected)
        {
            return;
        }

        try
        {
            shell.Write(data, 0, data.Length);
            shell.Flush();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SSH 会话 {SessionId} 发送输入失败", SessionId);
        }
    }

    /// <summary>
    /// 调整终端尺寸。终端控件尺寸变化时调用，使远端程序（vim、top 等）正确重绘。
    /// </summary>
    public void Resize(int columns, int rows, int pixelWidth, int pixelHeight)
    {
        var shell = _shell;
        if (shell is null || State != ConnectionState.Connected || columns <= 0 || rows <= 0)
        {
            return;
        }

        try
        {
            shell.ChangeWindowSize((uint)columns, (uint)rows, (uint)Math.Max(pixelWidth, 0), (uint)Math.Max(pixelHeight, 0));
        }
        catch (Exception ex)
        {
            // 尺寸调整失败不影响会话可用性，记录即可。
            _logger.LogDebug(ex, "SSH 会话 {SessionId} 调整终端尺寸失败", SessionId);
        }
    }

    public async Task DisconnectAsync()
    {
        await CleanupAsync();

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
        await CleanupAsync();
        _lifecycleMutex.Dispose();
    }

    // ── 连接构建 ──────────────────────────────────────────────────

    private ConnectionInfo BuildConnectionInfo()
    {
        var credential = _credential;
        if (credential is null)
        {
            throw ConnectionException.FromCode(ConnectionErrorCode.CredentialMissing);
        }

        credential.ThrowIfDisposed();

        var username = credential.Username;
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ConnectionException(
                ConnectionErrorCode.AuthenticationFailed, "SSH 连接必须指定用户名。");
        }

        AuthenticationMethod authentication = credential.Type switch
        {
            CredentialType.SshPrivateKey => BuildPrivateKeyAuthentication(username, credential),
            _ => new PasswordAuthenticationMethod(username, credential.Password ?? string.Empty)
        };

        return new ConnectionInfo(Profile.Host, Profile.Port, username, authentication)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(Profile.Ssh.ConnectTimeoutSeconds, 5)),
            Encoding = ResolveEncoding(Profile.Ssh.Encoding)
        };
    }

    private static PrivateKeyAuthenticationMethod BuildPrivateKeyAuthentication(string username, ResolvedCredential credential)
    {
        if (string.IsNullOrEmpty(credential.PrivateKey))
        {
            throw new ConnectionException(
                ConnectionErrorCode.CredentialMissing, "该凭据未包含 SSH 私钥，无法使用私钥登录。");
        }

        try
        {
            using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(credential.PrivateKey));

            // Password 字段在私钥登录场景下承担 Passphrase 的角色。
            var keyFile = string.IsNullOrEmpty(credential.Password)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, credential.Password);

            return new PrivateKeyAuthenticationMethod(username, keyFile);
        }
        catch (SshException ex)
        {
            // 私钥格式错误或 Passphrase 不正确。异常信息不包含私钥内容，可安全传递。
            throw new ConnectionException(
                ConnectionErrorCode.AuthenticationFailed, "SSH 私钥无法加载，请检查私钥格式或 Passphrase 是否正确。", ex);
        }
    }

    private static Encoding ResolveEncoding(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            // 配置了不认识的编码时退回 UTF-8，而不是让连接直接失败。
            return Encoding.UTF8;
        }
    }

    // ── Host Key 校验 ─────────────────────────────────────────────

    /// <summary>
    /// SSH.NET 在连接线程上同步触发本事件。此处同步等待 UI 的确认结果：
    /// 阻塞的是 SSH 后台线程而非 UI 线程，因此不会死锁。
    /// </summary>
    private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        var policy = _request.HostKeyPolicy;
        if (policy is null)
        {
            // 没有配置校验策略时必须拒绝，绝不静默信任任意主机密钥。
            _hostKeyFailure = ConnectionErrorCode.HostKeyRejected;
            e.CanTrust = false;
            return;
        }

        var context = new SshHostKeyVerificationContext
        {
            Host = Profile.Host,
            Port = Profile.Port,
            KeyAlgorithm = e.HostKeyName,
            Fingerprint = e.FingerPrintSHA256
        };

        try
        {
            var trusted = policy.VerifyAsync(context, CancellationToken.None).GetAwaiter().GetResult();
            e.CanTrust = trusted;

            if (!trusted)
            {
                _hostKeyFailure = ConnectionErrorCode.HostKeyRejected;
            }
        }
        catch (ConnectionException ex)
        {
            // 指纹不一致等强风险场景由策略以 ConnectionException 抛出，这里保留其错误码。
            _hostKeyFailure = ex.ErrorCode;
            e.CanTrust = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSH 会话 {SessionId} 校验主机密钥时发生异常", SessionId);
            _hostKeyFailure = ConnectionErrorCode.HostKeyRejected;
            e.CanTrust = false;
        }
    }

    // ── 数据读取 ──────────────────────────────────────────────────

    /// <summary>
    /// 后台读取循环。以原始字节读取，避免在协议层做字符解码而在多字节边界处产生乱码。
    /// </summary>
    private async Task ReadLoopAsync(ShellStream shell, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await shell.ReadAsync(buffer.AsMemory(), ct);
                if (read <= 0)
                {
                    break;
                }

                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                DataReceived?.Invoke(this, chunk);
            }

            // 读到流末尾表示远端已关闭会话。
            if (!ct.IsCancellationRequested && State == ConnectionState.Connected)
            {
                _logger.LogInformation("SSH 会话 {SessionId} 被远端关闭", SessionId);
                SetState(ConnectionState.Disconnected);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭路径。
        }
        catch (ObjectDisposedException)
        {
            // 会话释放与读取循环竞争时的正常结果。
        }
        catch (Exception ex) when (State == ConnectionState.Connected)
        {
            _logger.LogWarning(ex, "SSH 会话 {SessionId} 读取数据中断", SessionId);
            Fail(ConnectionErrorCode.RemoteClosed, ex);
        }
    }

    // ── 生命周期 ──────────────────────────────────────────────────

    private async Task CleanupAsync()
    {
        await _lifecycleMutex.WaitAsync();
        try
        {
            if (_readLoopCts is not null)
            {
                await _readLoopCts.CancelAsync();
            }

            if (_readLoopTask is not null)
            {
                // 读取循环可能阻塞在网络读上，等待时给一个上限，避免关闭 Tab 卡住 UI。
                await Task.WhenAny(_readLoopTask, Task.Delay(TimeSpan.FromSeconds(2)));
                _readLoopTask = null;
            }

            _readLoopCts?.Dispose();
            _readLoopCts = null;

            _shell?.Dispose();
            _shell = null;

            if (_client is not null)
            {
                _client.HostKeyReceived -= OnHostKeyReceived;

                if (_client.IsConnected)
                {
                    _client.Disconnect();
                }

                _client.Dispose();
                _client = null;
            }

            _credential?.Dispose();
            _credential = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SSH 会话 {SessionId} 清理资源时出现异常", SessionId);
        }
        finally
        {
            _lifecycleMutex.Release();
        }
    }

    /// <summary>把底层库异常映射为标准化错误码，UI 只面对可理解的中文提示。</summary>
    private ConnectionErrorCode MapError(Exception ex)
    {
        // Host Key 校验失败会以 SshConnectionException 的形式冒泡，
        // 此处优先采用校验阶段记录的精确原因。
        if (_hostKeyFailure != ConnectionErrorCode.None)
        {
            return _hostKeyFailure;
        }

        return ex switch
        {
            ConnectionException connectionException => connectionException.ErrorCode,
            SshAuthenticationException => ConnectionErrorCode.AuthenticationFailed,
            SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData }
                => ConnectionErrorCode.HostNotFound,
            SocketException { SocketErrorCode: SocketError.TimedOut } => ConnectionErrorCode.Timeout,
            SocketException => ConnectionErrorCode.NetworkUnreachable,
            SshOperationTimeoutException => ConnectionErrorCode.Timeout,
            SshConnectionException => ConnectionErrorCode.NetworkUnreachable,
            SshException => ConnectionErrorCode.ProtocolNegotiationFailed,
            _ => ConnectionErrorCode.Unknown
        };
    }

    private void Fail(ConnectionErrorCode code, Exception? ex)
    {
        ErrorCode = code;
        ErrorMessage = ex is ConnectionException connectionException
            ? connectionException.Message
            : ConnectionException.Describe(code);

        // 原始异常只进日志用于诊断，不会出现在面向用户的提示中。
        if (ex is not null)
        {
            _logger.LogError(ex, "SSH 会话 {SessionId} 连接失败，错误码 {ErrorCode}", SessionId, code);
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
