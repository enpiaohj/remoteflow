using System.IO.Pipes;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
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
public sealed partial class PowerShellSession : RemoteSessionBase, ITerminalSession, IConnectNoticeSource
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
    private readonly ITrustedHostsWriter? _trustedHosts;
    private bool _trustAttempted;
    private bool _isLocalTarget;

    public PowerShellSession(SessionRequest request, ILogger logger, ITrustedHostsWriter? trustedHosts = null)
    {
        _request = request;
        _credential = request.Credential;
        _logger = logger;
        _trustedHosts = trustedHosts;
    }

    public string ConnectNotice { get; private set; } = string.Empty;

    public event EventHandler? ConnectNoticeChanged;

    /// <summary>协议沿用主机连接的协议（RDP）；界面按终端呈现，由 <see cref="Kind"/> 与终端接口区分。</summary>
    public override ProtocolType Protocol => _request.Profile.Protocol;

    public override SessionKind Kind => SessionKind.PowerShell;

    public override ConnectionProfile Profile => _request.Profile;

    public event EventHandler<byte[]>? DataReceived;

    /// <summary>WinRM 端口。仅测试会改它。</summary>
    internal int Port { get; init; } = PowerShellEndpoint.DefaultPort;

    /// <summary>用当前 Windows 身份连本机回环。仅自动化测试使用。</summary>
    internal bool UseLoopbackNetworkAccess { get; init; }

    /// <summary>判断解析出的地址是不是本机。测试可替换。</summary>
    internal Func<IReadOnlyList<IPAddress>, bool> LocalAddressCheck { get; init; } = IsThisMachine;

    /// <summary>
    /// 实际交给 WinRM 的计算机名。目标是本机时用 <c>localhost</c>：用本机自己的 IP 连接，
    /// Windows 会按「不受信任的远程主机」拒绝，而 localhost 不需要信任列表。
    /// </summary>
    internal string ConnectionTarget => _isLocalTarget ? "localhost" : Profile.Host;

    internal static bool IsThisMachine(IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Any(IPAddress.IsLoopback))
        {
            return true;
        }

        var own = NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .ToHashSet();
        return addresses.Count > 0 && addresses.All(own.Contains);
    }

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
            SetNotice(string.Empty);
            DisposeCredential();
        }
    }

    private void SetNotice(string notice)
    {
        if (ConnectNotice != notice)
        {
            ConnectNotice = notice;
            ConnectNoticeChanged?.Invoke(this, EventArgs.Empty);
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

        _isLocalTarget = LocalAddressCheck(addresses);

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
            if (_isLocalTarget)
            {
                // 与远程目标分开：经 WMI「远程启用」不适用于本机。
                throw new ConnectionException(ConnectionErrorCode.ComponentUnavailable,
                    $"这是本机，但本机尚未启用 PowerShell 远程（WinRM 端口 {Port} 不通）。"
                    + "请以管理员身份运行 PowerShell 执行 Enable-PSRemoting，然后重新连接。", ex);
            }

            throw new ConnectionException(ConnectionErrorCode.NetworkUnreachable,
                $"无法连接到 {Profile.Host} 的 WinRM 端口 {Port}。请确认目标已启用 PowerShell 远程"
                + "（在目标上以管理员运行 Enable-PSRemoting），且防火墙放行该端口。"
                + "Windows Server 默认已启用，Windows 10 / 11 默认未启用。", ex);
        }
    }

    private async Task StartRemoteSessionAsync(CancellationToken token)
    {
        var pipeName = $"RemoteFlow-ps-{Guid.NewGuid():N}";
        var channel = PowerShellPipe.CreateServer(pipeName);
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
        PowerShellPipe.VerifyPeer(channel, terminal.ProcessId);
        await SendParametersAsync(channel, token);

        var response = ReadResponseAsync(channel, token);
        await WaitOrProcessExitAsync(response, terminal, ResponseTimeout, token);
        var (connected, errorId, errorCode, detail) = await response;

        if (!connected)
        {
            _logger.LogWarning("PowerShell 会话 {SessionId} 连接失败：{ErrorId} / {WinRmCode}", SessionId, errorId, errorCode);

            if (ShouldTrust(errorId, errorCode))
            {
                await TrustHostAsync(token);

                // 凭据已随第一次尝试发出并清除，信任后从请求方重新取一份。
                terminal.Dispose();
                channel.Dispose();
                await StartRemoteSessionAsync(token);
                return;
            }

            var (code, message) = PowerShellErrorMapper.Map(errorId, errorCode, detail, Profile.Host);
            if (_isLocalTarget && code == ConnectionErrorCode.AuthenticationFailed)
            {
                message = "连接本机被拒绝。开启 UAC 时，Windows 会限制本地账户这样登录（内置 Administrator 除外）。\n" + message;
            }

            throw new ConnectionException(code, message);
        }

        SetState(ConnectionState.Connected);
    }

    /// <summary>只在 Windows 明确以「对方不受信任」拒绝时处理，且每次连接最多一次。</summary>
    private bool ShouldTrust(string errorId, int errorCode)
        => _trustedHosts is not null && !_trustAttempted && !_isLocalTarget
           && PowerShellErrorMapper.IsNotTrusted(errorId, errorCode)
           && ElevatedTrustedHostsWriter.IsSafeHost(Profile.Host);

    private async Task TrustHostAsync(CancellationToken token)
    {
        _trustAttempted = true;
        SetNotice($"首次连接这台电脑：正在把 {Profile.Host} 加入本机的 PowerShell 信任列表。\n"
                  + "请在 Windows 管理员确认框中选择“是”（每台电脑只需一次）。");

        switch (await _trustedHosts!.AddAsync(Profile.Host, token))
        {
            case HostTrustOutcome.Declined:
                throw new ConnectionException(ConnectionErrorCode.Cancelled,
                    $"没有把 {Profile.Host} 加入本机的信任列表（管理员确认框被取消），无法用 PowerShell 连接它。"
                    + "点“重新连接”可以再次确认。");
            case HostTrustOutcome.Failed:
                var (code, message) = PowerShellErrorMapper.Map("ServerNotTrusted", 0, "自动添加没有成功", Profile.Host);
                throw new ConnectionException(code, message);
        }

        SetNotice(string.Empty);
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

    internal static string LoadBootstrapScript() => PowerShellPipe.LoadScript("bootstrap.ps1");

    internal static string PowerShellPath => Path.Combine(
        Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>命令行里只有固定脚本与随机管道名。</summary>
    internal static string BuildCommandLine(string pipeName)
        => $"\"{PowerShellPath}\" -NoLogo -NoProfile -NoExit -EncodedCommand {PowerShellPipe.Encode(LoadBootstrapScript(), pipeName)}";

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
        await PowerShellPipe.SendAsync(channel, writer =>
        {
            writer.Write(ConnectionTarget);
            writer.Write(ComposeUserName(_credential, Profile.Rdp.Domain));
            writer.Write(UseLoopbackNetworkAccess);
        }, _credential?.Password, token);
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

        // 信任主机后会换一个新进程重连，旧进程的退出与当前会话无关。
        if (IsClosing || State != ConnectionState.Connected || !ReferenceEquals(terminal, _terminal))
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
}
