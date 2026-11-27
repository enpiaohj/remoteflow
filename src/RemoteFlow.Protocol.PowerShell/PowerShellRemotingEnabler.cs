using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.Protocol.PowerShell;

/// <summary>
/// 经 WMI（DCOM）远程启用目标电脑的 PowerShell 远程。由隐藏的系统 <c>powershell.exe</c> 执行固定脚本，
/// 账号密码走与会话相同的受保护管道。只在用户确认后调用 <see cref="EnableAsync"/>。
/// </summary>
public sealed class PowerShellRemotingEnabler(ILogger<PowerShellRemotingEnabler> logger) : IRemotingEnabler
{
    private static readonly TimeSpan InspectTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan EnableTimeout = TimeSpan.FromSeconds(240);

    public async Task<RemotingTargetInfo> InspectAsync(
        string host, ResolvedCredential credential, string? domain, CancellationToken ct = default)
    {
        var values = await RunAsync(host, credential, domain, "inspect", InspectTimeout, ct);
        return ParseTarget(values, "before.");
    }

    public async Task<RemotingEnableResult> EnableAsync(
        string host, ResolvedCredential credential, string? domain, CancellationToken ct = default)
    {
        logger.LogInformation("开始远程启用 PowerShell 远程：{Host}", host);
        var values = await RunAsync(host, credential, domain, "enable", EnableTimeout, ct);
        var result = new RemotingEnableResult(
            ParseTarget(values, "before."),
            ParseTarget(values, "after."),
            Enum.Parse<TokenFilterPolicyOutcome>(values["outcome"]),
            await ProbePortAsync(host, ct));

        // 改动了目标电脑的系统设置，留下可追溯的记录（不含账号与密码）。
        logger.LogInformation(
            "远程启用完成：{Host}，WinRM {State}/{StartMode}，令牌过滤策略 {Outcome}，端口{Port}",
            host, result.After.WinRmState, result.After.WinRmStartMode, result.PolicyOutcome, result.PortOpen ? "开放" : "不通");
        return result;
    }

    /// <summary>RPC 端点映射端口。仅测试会改它。</summary>
    internal int RpcPort { get; init; } = 135;

    /// <summary>
    /// WMI（DCOM）要先连目标的 135 端口。Windows 10 / 11 的防火墙默认通常不放行，
    /// 不先查的话只会得到一个看不懂的 WMI 错误码。
    /// </summary>
    private async Task EnsureRpcReachableAsync(string host, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, RpcPort, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("远程启用不可用：{Host} 的 RPC 端口 {Port} 不通", host, RpcPort);
            throw new ConnectionException(ConnectionErrorCode.NetworkUnreachable,
                $"这台电脑无法远程启用：连不上它的 {RpcPort} 端口（RPC）。远程启用依赖 WMI，"
                + "Windows 10 / 11 的防火墙默认通常不放行。\n\n"
                + "请到目标电脑上以管理员身份运行 PowerShell，执行 Enable-PSRemoting -Force（错误框里可以直接复制），然后重新连接。", ex);
        }
    }

    private static async Task<bool> ProbePortAsync(string host, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, PowerShellEndpoint.DefaultPort, timeout.Token);
                return true;
            }
            catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                await Task.Delay(1500, ct);
            }
        }

        return false;
    }

    internal static string LoadScript() => PowerShellPipe.LoadScript("enable.ps1");

    private async Task<Dictionary<string, string>> RunAsync(
        string host, ResolvedCredential credential, string? domain, string mode, TimeSpan limit, CancellationToken ct)
    {
        var user = PowerShellSession.ComposeUserName(credential, domain);
        if (user.Length == 0 || string.IsNullOrEmpty(credential.Password))
        {
            throw new ConnectionException(ConnectionErrorCode.AuthenticationFailed, "远程启用需要目标电脑的管理员账号和密码。");
        }

        await EnsureRpcReachableAsync(host, ct);

        var pipeName = $"RemoteFlow-ps-{Guid.NewGuid():N}";
        await using var channel = PowerShellPipe.CreateServer(pipeName);
        var start = new ProcessStartInfo(PowerShellSession.PowerShellPath)
        {
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + PowerShellPipe.Encode(LoadScript(), pipeName),
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(start)
            ?? throw new ConnectionException(ConnectionErrorCode.ComponentUnavailable, "无法启动 PowerShell。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        try
        {
            await channel.WaitForConnectionAsync(timeout.Token);
            PowerShellPipe.VerifyPeer(channel, process.Id);
            await PowerShellPipe.SendAsync(channel, writer =>
            {
                writer.Write(host);
                writer.Write(user);
                writer.Write(mode);
            }, credential.Password, timeout.Token);

            var read = Task.Run(() => ReadResponse(channel), timeout.Token);
            var (ok, values, errorId, hresult, detail) = await read.WaitAsync(timeout.Token);
            if (!ok)
            {
                logger.LogWarning("远程{Mode}失败：{Host}，{ErrorId} / 0x{HResult:X8}", mode, host, errorId, hresult);
                throw MapError(errorId, hresult, detail);
            }

            return values;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ConnectionException(ConnectionErrorCode.Timeout,
                mode == "enable"
                    ? "远程启用超时。目标电脑上的设置可能已部分生效，请到目标电脑上确认。"
                    : "检查目标电脑超时。");
        }
        catch (EndOfStreamException ex)
        {
            throw new ConnectionException(ConnectionErrorCode.ComponentUnavailable,
                "PowerShell 提前退出，可能被系统策略或安全软件限制。", ex);
        }
        finally
        {
            try
            {
                if (!process.WaitForExit(3000))
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static (bool Ok, Dictionary<string, string> Values, string ErrorId, int HResult, string Detail) ReadResponse(Stream channel)
    {
        using var reader = new BinaryReader(channel, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBoolean())
        {
            return (false, [], reader.ReadString(), reader.ReadInt32(), reader.ReadString());
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var count = reader.ReadInt32(); count > 0; count--)
        {
            values[reader.ReadString()] = reader.ReadString();
        }

        return (true, values, string.Empty, 0, string.Empty);
    }

    internal static RemotingTargetInfo ParseTarget(IReadOnlyDictionary<string, string> values, string prefix)
    {
        string Get(string key) => values.TryGetValue(prefix + key, out var value) ? value : string.Empty;
        return new RemotingTargetInfo(
            Get("name"), Get("os"),
            string.Equals(Get("server"), "True", StringComparison.OrdinalIgnoreCase),
            string.Equals(Get("domain"), "True", StringComparison.OrdinalIgnoreCase),
            Get("state"), Get("startMode"),
            Get("public").Split('|', StringSplitOptions.RemoveEmptyEntries),
            !string.Equals(Get("uac"), "False", StringComparison.OrdinalIgnoreCase),
            int.TryParse(Get("policy"), out var policy) ? policy : null);
    }

    internal static ConnectionException MapError(string errorId, int hresult, string detail)
    {
        bool Matches(string code, string? name = null)
            => unchecked((uint)hresult) == Convert.ToUInt32(code, 16)
               || errorId.Contains(code, StringComparison.OrdinalIgnoreCase)
               || (name is not null && errorId.Contains(name, StringComparison.OrdinalIgnoreCase));

        if (detail.Contains("RF_PUBLIC_NETWORK", StringComparison.Ordinal))
        {
            return new ConnectionException(ConnectionErrorCode.ProtocolNegotiationFailed,
                "目标电脑有网络处于「公用」类别，未做任何改动。请先在目标电脑上确认网络可信并改为「专用」。");
        }

        if (detail.Contains("RF_START_FAILED", StringComparison.Ordinal))
        {
            return new ConnectionException(ConnectionErrorCode.Unknown,
                $"无法在目标电脑上启动启用命令。请在目标电脑上以管理员运行 Enable-PSRemoting。\n详细信息：{detail}");
        }

        if (detail.Contains("RF_VERIFY_FAILED", StringComparison.Ordinal))
        {
            return new ConnectionException(ConnectionErrorCode.Unknown,
                "启用命令已执行，但随后无法读取目标电脑的状态。请到目标电脑上确认。");
        }

        if (Matches("80070005", "AccessDenied"))
        {
            return new ConnectionException(ConnectionErrorCode.AuthenticationFailed,
                "目标电脑拒绝访问：请检查账号密码，且账号须是目标电脑的管理员。非域电脑开启 UAC 时，"
                + "本地管理员账户（内置 Administrator 除外）经网络登录会被限制，这种情况只能到目标电脑上手动启用。");
        }

        if (Matches("800706BA"))
        {
            return new ConnectionException(ConnectionErrorCode.NetworkUnreachable,
                "目标电脑不接受远程管理（RPC / WMI 不通，常见原因是防火墙未放行「Windows Management Instrumentation」）。"
                + "请到目标电脑上以管理员运行 Enable-PSRemoting。");
        }

        return new ConnectionException(ConnectionErrorCode.Unknown,
            $"无法远程管理目标电脑。请到目标电脑上以管理员运行 Enable-PSRemoting。\n详细信息：{detail}");
    }
}
