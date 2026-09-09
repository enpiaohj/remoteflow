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

    /// <summary>
    /// 会话级取消源：Disconnect / Dispose / 远端关闭时取消，使在途的
    /// <see cref="ConnectAsync"/> 能立刻中断握手/建流，而不是挂死等待 SSH.NET 自身超时。
    /// </summary>
    private readonly CancellationTokenSource _lifecycleCts = new();

    /// <summary>保护断开/释放路径，避免 UI 关闭 Tab 与远端断线同时触发导致重复释放。</summary>
    private readonly SemaphoreSlim _lifecycleMutex = new(1, 1);

    private ResolvedCredential? _credential;
    private volatile bool _disposed;

    /// <summary>会话已进入收尾（Disconnect / Dispose / 远端 EOF）。置位后不再允许重新连接。</summary>
    private volatile bool _closing;

    /// <summary>Host Key 校验失败的具体原因，用于在连接异常时给出准确错误码。</summary>
    private ConnectionErrorCode _hostKeyFailure = ConnectionErrorCode.None;

    /// <summary>握手时发现的、尚未被本机信任的主机密钥。连接失败后据此弹窗确认。</summary>
    private SshHostKeyVerificationContext? _pendingHostKey;

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

    /// <summary>是否已进入关闭 / 收尾流程（Disconnect / Dispose / 远端 EOF 后为 true）。</summary>
    public bool IsClosing => _closing;

    /// <summary>
    /// 收尾：将会话置 <see cref="ConnectionState.Closed"/>（终态，幂等）。
    /// <para>SessionManager 在 teardown + 释放（_disposed 已置位）之后调用；
    /// 不能因已释放而提前返回，Closed 仍需广播，供 UI 移除 Tab / 历史收口。</para>
    /// </summary>
    public void MarkClosed() => SetState(ConnectionState.Closed);

    /// <summary>收到远端数据。参数为原始字节，交由终端渲染层解码。</summary>
    public event EventHandler<byte[]>? DataReceived;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SshSession));
        }

        if (_closing || State is ConnectionState.Connecting or ConnectionState.Connected)
        {
            return;
        }

        SetState(ConnectionState.Connecting);

        try
        {
            // 会话级取消：调用方 token 与关闭流程（Disconnect / Dispose / 远端关）共享同一来源。
            // 连接中关闭会话会取消 _lifecycleCts，这里联动的 token 随即触发，
            // 让握手/建流立刻中断，而不是干等底层库超时。
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifecycleCts.Token);
            var connectCt = linkedCts.Token;

            // 最多两轮：首轮若因主机密钥未信任被我方中止握手，弹窗确认后再来一轮。
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await ConnectOnceAsync(connectCt);
                    return;
                }
                catch (OperationCanceledException)
                {
                    await CleanupAsync();

                    // 取消也可能是关闭流程（Disconnect/Dispose/远端关）引发的：此时会话已在
                    // 收尾，不再置 Failed，避免把「已断开/已释放」的会话又标记成失败。
                    if (!_closing && !_disposed && State is not (ConnectionState.Disconnected or ConnectionState.Failed))
                    {
                        Fail(ConnectionErrorCode.Cancelled, null);
                    }
                    return;
                }
                catch (Exception ex)
                {
                    await CleanupAsync();

                    // 关闭流程已在收尾：不弹窗也不标失败，交由 Disconnect/Dispose 统一收敛。
                    if (_closing || _disposed)
                    {
                        return;
                    }

                    // 只在失败路径打印（正常连接不经过这里），信息量小但对排查
                    // 「未按预期弹出信任确认框」这类问题至关重要，保留在 Information 级别。
                    _logger.LogInformation(
                        "SSH 会话 {SessionId} 第 {Attempt} 轮握手异常，PendingHostKey={HasPending}，" +
                        "异常类型={ExceptionType}：{Message}",
                        SessionId, attempt, _pendingHostKey is not null, ex.GetType().Name, ex.Message);

                    // 首轮握手被我方中止（主机密钥未信任），且还没弹过窗——现在弹。
                    if (attempt == 0 && _pendingHostKey is { } pending && _request.HostKeyPolicy is { } policy)
                    {
                        var accepted = await policy.ConfirmAndRememberAsync(pending, cancellationToken);
                        _pendingHostKey = null;

                        if (accepted)
                        {
                            _hostKeyFailure = ConnectionErrorCode.None;
                            continue; // 指纹已记录，重试；这轮 Lookup 会静默通过
                        }

                        Fail(pending.IsMismatch
                            ? ConnectionErrorCode.HostKeyMismatch
                            : ConnectionErrorCode.HostKeyRejected, null);
                        return;
                    }

                    Fail(MapError(ex), ex);
                    return;
                }
            }
        }
        finally
        {
            // 无论成功失败，凭据都不再需要，立即释放以缩短明文存活时间。
            _credential?.Dispose();
            _credential = null;
        }
    }

    /// <summary>单次连接尝试。失败抛异常，由 <see cref="ConnectAsync"/> 决定是否重试。</summary>
    private async Task ConnectOnceAsync(CancellationToken cancellationToken)
    {
        _pendingHostKey = null;
        _hostKeyFailure = ConnectionErrorCode.None;

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
            if (Environment.GetEnvironmentVariable("RF_VERIFY") == "1")
            {
                Console.WriteLine(
                    $"[RF][out] {data.Length}B \"{Encoding.UTF8.GetString(data).Replace("\n", "\\n").Replace("\r", "\\r")}\"");
            }

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
        // 先标记收尾并取消会话级 CTS：在途 ConnectAsync 会立刻中断，而不是继续握手。
        _closing = true;
        CancelLifecycle();

        await CleanupAsync();

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
        _closing = true;
        CancelLifecycle();
        await CleanupAsync();

        // ConnectAsync 从未跑完（或根本没调用）时凭据还在，这里兜底释放。
        _credential?.Dispose();
        _credential = null;

        try
        {
            _lifecycleCts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // CTS 已释放属预期，忽略。
        }

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
    /// SSH.NET 在握手线程上同步触发本事件。<b>这里绝不阻塞等待 UI</b>——会话超时会先到，
    /// 把弹窗结果吞掉。只做一次快速的本机指纹比对：一致则放行；否则中止握手
    /// （<c>CanTrust = false</c>，此时凭据尚未发送），把待确认的密钥记下来，
    /// 由 <see cref="ConnectAsync"/> 在握手失败后弹窗、用户接受则记录并重试。
    /// </summary>
    private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        _logger.LogDebug(
            "SSH 会话 {SessionId} 收到 HostKeyReceived，调用线程={ThreadId}", SessionId, Environment.CurrentManagedThreadId);

        var policy = _request.HostKeyPolicy;
        if (policy is null)
        {
            // 没有配置校验策略时必须拒绝，绝不静默信任任意主机密钥。
            _logger.LogWarning("SSH 会话 {SessionId} 未配置 HostKeyPolicy，直接拒绝主机密钥", SessionId);
            _hostKeyFailure = ConnectionErrorCode.HostKeyRejected;
            e.CanTrust = false;
            return;
        }

        try
        {
            var context = policy.Lookup(new SshHostKeyVerificationContext
            {
                Host = Profile.Host,
                Port = Profile.Port,
                KeyAlgorithm = e.HostKeyName,
                Fingerprint = e.FingerPrintSHA256,
            });

            if (context.IsKnownGood)
            {
                e.CanTrust = true;
                return;
            }

            // 未信任 / 指纹变化：中止本次握手，稍后弹窗。
            _logger.LogDebug(
                "SSH 会话 {SessionId} 主机密钥未信任，中止握手待确认，IsMismatch={IsMismatch}",
                SessionId, context.IsMismatch);
            _pendingHostKey = context;
            e.CanTrust = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSH 会话 {SessionId} 查询主机密钥时发生异常", SessionId);
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

            // 读到流末尾表示远端已关闭会话：置 Disconnected 后立即幂等清理 _client/_shell，
            // 不再把底层连接残留到用户关 Tab。若关闭流程（Disconnect/Dispose）已在收尾，
            // 则跳过，由该流程统一清理，避免双清理竞态。
            if (!ct.IsCancellationRequested && !_closing && !_disposed && State == ConnectionState.Connected)
            {
                _logger.LogInformation("SSH 会话 {SessionId} 被远端关闭", SessionId);
                SetState(ConnectionState.Disconnected);

                _closing = true;
                await CleanupAsync(skipReadLoopWait: true);
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
        catch (Exception ex) when (State == ConnectionState.Connected && !_closing && !_disposed)
        {
            _logger.LogWarning(ex, "SSH 会话 {SessionId} 读取数据中断", SessionId);
            Fail(ConnectionErrorCode.RemoteClosed, ex);
        }
    }

    // ── 生命周期 ──────────────────────────────────────────────────

    /// <summary>
    /// 幂等清理。多路并发进入（Disconnect / Dispose / 远端 EOF / Connect 失败）由
    /// <see cref="_lifecycleMutex"/> 串行化，首轮已把字段清空，后续轮次是幂等 no-op；
    /// DisposeAsync 之后再次进入也不会抛（互斥体已释放时静默返回）。
    /// </summary>
    /// <param name="skipReadLoopWait">
    /// 由读取循环自身触发（远端 EOF）时置 true：此时读取循环即将结束，等待它自己没有意义。
    /// </param>
    private async Task CleanupAsync(bool skipReadLoopWait = false)
    {
        try
        {
            await _lifecycleMutex.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            // DisposeAsync 已释放互斥体：完整清理已由 DisposeAsync 完成，无需重复执行。
            return;
        }

        try
        {
            if (_readLoopCts is not null)
            {
                await _readLoopCts.CancelAsync();
            }

            if (!skipReadLoopWait && _readLoopTask is not null)
            {
                // 读取循环可能阻塞在网络读上，等待时给一个上限，避免关闭 Tab 卡住 UI。
                var readLoop = _readLoopTask;
                if (!ReferenceEquals(await Task.WhenAny(readLoop, Task.Delay(TimeSpan.FromSeconds(2))), readLoop))
                {
                    _logger.LogWarning("SSH 会话 {SessionId} 读取循环在清理时未及时退出，按超时继续释放", SessionId);
                }
            }

            _readLoopTask = null;

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

            // 凭据不在这里释放：首轮握手因主机密钥未信任被中止后要重试，仍需凭据。
            // 由 ConnectAsync 的 finally（所有尝试结束后）和 DisposeAsync 统一释放。
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SSH 会话 {SessionId} 清理资源时出现异常", SessionId);
        }
        finally
        {
            try
            {
                _lifecycleMutex.Release();
            }
            catch (ObjectDisposedException)
            {
                // 互斥体在等待期间被 DisposeAsync 释放：清理结果与直接返回等价，忽略。
            }
        }
    }

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
