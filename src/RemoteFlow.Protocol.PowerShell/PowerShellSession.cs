using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

// System.Net.Sockets 同样定义了 ProtocolType。
using ProtocolType = RemoteFlow.Core.Models.ProtocolType;

namespace RemoteFlow.Protocol.PowerShell;

/// <summary>
/// PowerShell 远程会话：在伪终端里运行系统 <c>powershell.exe</c>，由启动脚本建立远程会话并进入。
/// <para>
/// 两条安全约束：
/// <list type="number">
///   <item><b>绝不让用户落到本机命令行。</b>启动脚本让本机提示符一被调用就结束进程；本类在远程会话建立成功之前
///         也不把键盘输入送进终端（终端协议应答除外）。</item>
///   <item><b>Secret 只走管道。</b>主机、账号、密码经仅当前用户可访问、且核对过对端进程号的一次性命名管道传入。</item>
/// </list>
/// </para>
/// </summary>
public sealed partial class PowerShellSession : RemoteSessionBase, ITerminalSession
{
    private static readonly TimeSpan PipeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3.5);

    private readonly SessionRequest _request;
    private readonly ILogger _logger;
    private ResolvedCredential? _credential;
    private ConPtyProcess? _terminal;
    private NamedPipeServerStream? _channel;
    private int _columns = 120;
    private int _rows = 30;

    public PowerShellSession(SessionRequest request, ILogger logger)
    {
        _request = request;
        _credential = request.Credential;
        _logger = logger;
    }

    /// <summary>协议沿用主机连接的协议（RDP）；界面按终端呈现，由 <see cref="Kind"/> 与终端接口区分。</summary>
    public override ProtocolType Protocol => _request.Profile.Protocol;

    public override SessionKind Kind => SessionKind.PowerShell;

    public override ConnectionProfile Profile => _request.Profile;

    public event EventHandler<byte[]>? DataReceived;

    /// <summary>WinRM 端口。仅测试会改它。</summary>
    internal int Port { get; init; } = PowerShellEndpoint.DefaultPort;

    /// <summary>用当前 Windows 身份连本机回环。仅自动化测试使用。</summary>
    internal bool UseLoopbackNetworkAccess { get; init; }

    /// <summary>伪终端进程号。仅测试使用。</summary>
    internal int? ProcessId => _terminal?.ProcessId;

    public override async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (State != ConnectionState.Idle || IsClosing)
        {
            return;
        }

        SetState(ConnectionState.Connecting);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, LifecycleToken);
            await CheckReachableAsync(linked.Token);
            await StartRemoteSessionAsync(linked.Token);
        }
        catch (ConnectionException ex)
        {
            Fail(ex.ErrorCode, ex.Message);
        }
        catch (OperationCanceledException)
        {
            if (!IsClosing)
            {
                Fail(ConnectionErrorCode.Cancelled, ConnectionException.Describe(ConnectionErrorCode.Cancelled));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("启动 PowerShell 会话 {SessionId} 失败：{ErrorType}", SessionId, ex.GetType().Name);
            if (!IsClosing)
            {
                Fail(ConnectionErrorCode.Unknown, "启动 PowerShell 失败。请确认系统自带的 Windows PowerShell 可以运行。");
            }
        }
        finally
        {
            DisposeCredential();
        }
    }

    /// <summary>
    /// 先单独检查名字解析与端口：WinRM 对「名字解析不了」与「认证方式受限」返回同一个错误码，
    /// 端口不通时它自己的等待时间也不稳定。
    /// </summary>
    private async Task CheckReachableAsync(CancellationToken token)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(Profile.Host, token);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw new ConnectionException(ConnectionErrorCode.HostNotFound,
                ConnectionException.Describe(ConnectionErrorCode.HostNotFound), ex);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(addresses, Port, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException
                                   || (ex is OperationCanceledException && !token.IsCancellationRequested))
        {
            throw new ConnectionException(ConnectionErrorCode.NetworkUnreachable,
                $"无法连接到 {Profile.Host} 的 WinRM 端口 {Port}。请确认目标已启用 PowerShell 远程"
                + "（在目标上以管理员运行 Enable-PSRemoting），且防火墙放行该端口。", ex);
        }
    }

    private async Task StartRemoteSessionAsync(CancellationToken token)
    {
        var pipeName = $"RemoteFlow-ps-{Guid.NewGuid():N}";
        var channel = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _channel = channel;

        var terminal = ConPtyProcess.Start(BuildCommandLine(pipeName), _columns, _rows,
            data => DataReceived?.Invoke(this, data));
        _terminal = terminal;
        _ = WatchExitAsync(terminal);

        if (IsClosing)
        {
            // 启动过程中标签已被关闭。
            terminal.Dispose();
            channel.Dispose();
            token.ThrowIfCancellationRequested();
            return;
        }

        await WaitOrProcessExitAsync(channel.WaitForConnectionAsync(token), terminal, PipeTimeout, token);
        VerifyPeer(channel, terminal.ProcessId);
        await SendParametersAsync(channel, token);

        var response = ReadResponseAsync(channel, token);
        await WaitOrProcessExitAsync(response, terminal, ResponseTimeout, token);
        var (connected, errorId, errorCode, detail) = await response;

        if (!connected)
        {
            var (code, message) = PowerShellErrorMapper.Map(errorId, errorCode, detail, Profile.Host);
            _logger.LogWarning("PowerShell 会话 {SessionId} 连接失败：{ErrorId} / {WinRmCode}", SessionId, errorId, errorCode);
            throw new ConnectionException(code, message);
        }

        SetState(ConnectionState.Connected);
    }

    private static async Task WaitOrProcessExitAsync(Task work, ConPtyProcess terminal, TimeSpan timeout, CancellationToken token)
    {
        var finished = await Task.WhenAny(work, terminal.Exited, Task.Delay(timeout, token));
        if (finished == work)
        {
            await work;
            return;
        }

        token.ThrowIfCancellationRequested();

        if (finished == terminal.Exited)
        {
            // 进程可能刚写完结果就退出：再给读取一点时间。
            if (await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(2), token)) == work)
            {
                await work;
                return;
            }

            throw new ConnectionException(ConnectionErrorCode.ComponentUnavailable,
                "PowerShell 在建立连接前就退出了，可能被系统策略或安全软件限制。");
        }

        throw new ConnectionException(ConnectionErrorCode.Timeout, ConnectionException.Describe(ConnectionErrorCode.Timeout));
    }

    internal static string LoadBootstrapScript()
    {
        using var stream = typeof(PowerShellSession).Assembly
            .GetManifestResourceStream("RemoteFlow.Protocol.PowerShell.bootstrap.ps1")
            ?? throw new InvalidOperationException("缺少 PowerShell 启动脚本资源。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static string PowerShellPath => Path.Combine(
        Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>命令行里只有固定脚本与随机管道名。用 -EncodedCommand 是为了不依赖脚本执行策略、也无需落盘。</summary>
    internal static string BuildCommandLine(string pipeName)
    {
        // 去掉注释与空行以缩短命令行；脚本内没有跨行字符串。
        var lines = LoadBootstrapScript().ReplaceLineEndings("\n").Split('\n')
            .Where(line => line.Trim() is { Length: > 0 } trimmed && !trimmed.StartsWith('#'));
        var script = string.Join('\n', lines).Replace("__PIPE__", pipeName);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return $"\"{PowerShellPath}\" -NoLogo -NoProfile -NoExit -EncodedCommand {encoded}";
    }

    private static void VerifyPeer(NamedPipeServerStream channel, int expectedProcessId)
    {
        // CurrentUserOnly 挡住其他用户；再核对对端就是刚启动的子进程，挡住同一用户下抢先连接的其他进程。
        if (!GetNamedPipeClientProcessId(channel.SafePipeHandle, out var processId) || processId != (uint)expectedProcessId)
        {
            throw new ConnectionException(ConnectionErrorCode.Unknown, "PowerShell 启动通道校验失败，已中止连接。");
        }
    }

    internal static string ComposeUserName(ResolvedCredential? credential, string? profileDomain)
    {
        var user = credential?.Username ?? string.Empty;
        if (string.IsNullOrWhiteSpace(user))
        {
            return string.Empty; // 不带账号：使用当前 Windows 身份
        }

        var domain = string.IsNullOrEmpty(credential!.Domain) ? profileDomain : credential.Domain;
        return string.IsNullOrWhiteSpace(domain) || user.Contains('\\') || user.Contains('@')
            ? user
            : $"{domain}\\{user}";
    }

    private async Task SendParametersAsync(Stream channel, CancellationToken token)
    {
        var password = (_credential?.Password ?? string.Empty).ToCharArray();
        using var buffer = new MemoryStream();
        try
        {
            using (var writer = new BinaryWriter(buffer, new UTF8Encoding(false), leaveOpen: true))
            {
                writer.Write(Profile.Host);
                writer.Write(ComposeUserName(_credential, Profile.Rdp.Domain));
                writer.Write(UseLoopbackNetworkAccess);
                writer.Write(password.Length);
                writer.Write(password);
            }

            await channel.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), token);
            await channel.FlushAsync(token);
        }
        finally
        {
            Array.Clear(password);
            Array.Clear(buffer.GetBuffer());
            DisposeCredential();
        }
    }

    private static Task<(bool Connected, string ErrorId, int ErrorCode, string Detail)> ReadResponseAsync(
        Stream channel, CancellationToken token)
        => Task.Run(() =>
        {
            using var reader = new BinaryReader(channel, Encoding.UTF8, leaveOpen: true);
            return reader.ReadBoolean()
                ? (true, string.Empty, 0, string.Empty)
                : (false, reader.ReadString(), reader.ReadInt32(), reader.ReadString());
        }, token);

    private async Task WatchExitAsync(ConPtyProcess terminal)
    {
        await terminal.Exited;

        if (IsClosing || State != ConnectionState.Connected)
        {
            return; // 主动关闭，或连接阶段（由 ConnectAsync 给出具体原因）
        }

        ErrorCode = ConnectionErrorCode.RemoteClosed;
        ErrorMessage = "PowerShell 远程会话已结束。";
        SetState(ConnectionState.Disconnected);
    }

    private void Fail(ConnectionErrorCode code, string message)
    {
        ErrorCode = code;
        ErrorMessage = message;
        SetState(ConnectionState.Failed);
        _channel?.Dispose();
        _terminal?.Dispose();
    }

    public void SendInput(string text) => SendInput(Encoding.UTF8.GetBytes(text));

    public void SendInput(byte[] data)
    {
        var state = State;
        if (state == ConnectionState.Connected
            // 连接阶段终端控件会应答「查询光标位置」等请求，不放行会让子进程一直等待；其余输入一律丢弃。
            || (state == ConnectionState.Connecting && IsTerminalReply(data)))
        {
            _terminal?.Write(data);
        }
    }

    internal static bool IsTerminalReply(byte[] data)
        => data.Length is > 0 and <= 32 && TerminalReply().IsMatch(Encoding.ASCII.GetString(data));

    public void Resize(int columns, int rows, int pixelWidth, int pixelHeight)
    {
        if (columns <= 0 || rows <= 0)
        {
            return;
        }

        // 连接前记下尺寸用于启动：远端按连接那一刻的宽度排版，之后缩放只影响本机显示。
        _columns = columns;
        _rows = rows;
        _terminal?.Resize(columns, rows);
    }

    protected override async ValueTask PerformTeardownAsync()
    {
        // 先断开管道：启动脚本据此关闭远端会话后自行退出；超时再强制结束。
        var terminal = _terminal;
        try
        {
            _channel?.Dispose();
        }
        catch (IOException)
        {
        }

        if (terminal is not null)
        {
            await Task.WhenAny(terminal.Exited, Task.Delay(CloseTimeout)).ConfigureAwait(false);
            if (!terminal.HasExited)
            {
                _logger.LogWarning("PowerShell 会话 {SessionId} 未在时限内退出，强制结束", SessionId);
            }

            terminal.Dispose();
        }

        DisposeCredential();
    }

    private void DisposeCredential() => Interlocked.Exchange(ref _credential, null)?.Dispose();

    [GeneratedRegex(@"\A(\x1b\[[0-9;?>]*[Rc])+\z")]
    private static partial Regex TerminalReply();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
