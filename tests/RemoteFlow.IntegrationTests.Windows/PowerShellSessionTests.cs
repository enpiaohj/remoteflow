using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Protocol.PowerShell;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>PowerShell 远程会话中不依赖 WinRM 的部分：启动脚本约束、账号拼接、错误提示、输入放行规则。</summary>
public sealed class PowerShellSessionTests
{
    [Fact]
    public void 本机提示符保护定义在脚本最前面_任何失败都落不到本机命令行()
    {
        var script = PowerShellSession.LoadBootstrapScript().ReplaceLineEndings("\n");
        var code = string.Join('\n', script.Split('\n').Where(l => !l.TrimStart().StartsWith('#') && l.Trim().Length > 0));

        Assert.StartsWith("function global:prompt {", code, StringComparison.Ordinal);
        var guard = code[..code.IndexOf("$ErrorActionPreference", StringComparison.Ordinal)];
        // 用 cmdlet 结束进程：受限语言模式下 .NET 方法调用会被禁止。
        Assert.Contains("Stop-Process -Id $PID -Force", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("[Environment]::Exit", code, StringComparison.Ordinal);
        Assert.EndsWith("Enter-PSSession -Session $global:rfSession", code.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void 脚本关闭本机命令历史记录()
        => Assert.Contains("Set-PSReadLineOption -HistorySaveStyle SaveNothing",
            PowerShellSession.LoadBootstrapScript(), StringComparison.Ordinal);

    [Fact]
    public void 命令行只含固定脚本与随机管道名_长度在系统限制内()
    {
        var commandLine = PowerShellSession.BuildCommandLine("RemoteFlow-ps-sample");
        var encoded = commandLine[(commandLine.LastIndexOf(' ') + 1)..];
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

        Assert.Contains("RemoteFlow-ps-sample", script, StringComparison.Ordinal);
        Assert.DoesNotContain("__PIPE__", script, StringComparison.Ordinal);
        Assert.Contains("-NoProfile", commandLine, StringComparison.Ordinal);
        Assert.True(commandLine.Length < 30000, $"命令行过长：{commandLine.Length}");
    }

    [Theory]
    [InlineData("admin", "CORP", null, "CORP\\admin")]
    [InlineData("admin", "", "PROFILEDOM", "PROFILEDOM\\admin")]
    [InlineData("admin", "", null, "admin")]
    [InlineData("CORP\\admin", "OTHER", null, "CORP\\admin")]
    [InlineData("admin@corp.example.test", "CORP", null, "admin@corp.example.test")]
    [InlineData("", "CORP", null, "")]
    public void 账号按凭据与连接的域拼接(string user, string domain, string? profileDomain, string expected)
        => Assert.Equal(expected, PowerShellSession.ComposeUserName(
            new ResolvedCredential { Type = CredentialType.WindowsDomain, Username = user, Domain = domain }, profileDomain));

    [Theory]
    [InlineData("\x1b[1;1R", true)]
    [InlineData("\x1b[?1;2c", true)]
    [InlineData("\x1b[24;80R\x1b[?1;2c", true)]
    [InlineData("whoami\r", false)]
    [InlineData("\x1b[A", false)]          // 方向键不是应答
    [InlineData("\r", false)]
    [InlineData("\x1b[1;1Rwhoami\r", false)] // 应答后夹带输入也不放行
    public void 连接阶段只放行终端协议应答(string input, bool allowed)
        => Assert.Equal(allowed, PowerShellSession.IsTerminalReply(Encoding.ASCII.GetBytes(input)));

    [Fact]
    public void 启用脚本先检查公用网络_再做任何改动()
    {
        var script = PowerShellRemotingEnabler.LoadScript();
        var gate = script.IndexOf("throw 'RF_PUBLIC_NETWORK'", StringComparison.Ordinal);
        var firstChange = script.IndexOf("-MethodName Create", StringComparison.Ordinal);

        Assert.True(gate > 0 && firstChange > gate);
        // 只读检查模式不会进入任何改动分支。
        var enableBranch = script.IndexOf("if ($mode -eq 'enable')", StringComparison.Ordinal);
        Assert.True(enableBranch > 0 && enableBranch < gate);
        foreach (var method in new[] { "-MethodName DeleteValue", "-MethodName SetDWORDValue" })
        {
            Assert.True(script.IndexOf(method, StringComparison.Ordinal) > enableBranch, method);
        }
    }

    [Theory]
    [InlineData("x;AccessDenied;HRESULT 0x80070005", 0, ConnectionErrorCode.AuthenticationFailed)]
    [InlineData("x", unchecked((int)0x80070005), ConnectionErrorCode.AuthenticationFailed)]
    [InlineData("x;Failed;HRESULT 0x800706ba", 0, ConnectionErrorCode.NetworkUnreachable)]
    [InlineData("x", 0, ConnectionErrorCode.Unknown)]
    public void 远程启用的错误按WMI错误码归类(string errorId, int hresult, ConnectionErrorCode expected)
        => Assert.Equal(expected, PowerShellRemotingEnabler.MapError(errorId, hresult, "详情").ErrorCode);

    [Fact]
    public void 检查结果按键值解析()
    {
        var info = PowerShellRemotingEnabler.ParseTarget(new Dictionary<string, string>
        {
            ["before.name"] = "PC", ["before.os"] = "Windows 10", ["before.server"] = "False", ["before.domain"] = "False",
            ["before.state"] = "Stopped", ["before.startMode"] = "Manual", ["before.public"] = "以太网|WLAN",
            ["before.uac"] = "False", ["before.policy"] = ""
        }, "before.");

        Assert.Equal(["以太网", "WLAN"], info.PublicNetworks);
        Assert.False(info.UacEnabled);
        Assert.Null(info.TokenFilterPolicy);
    }

    [Fact]
    public void 认证被拒给出账号与权限提示()
    {
        var (code, message) = PowerShellErrorMapper.Map("AccessDenied,PSSessionOpenFailed", 5, "拒绝访问。", "host.example.test");
        Assert.Equal(ConnectionErrorCode.AuthenticationFailed, code);
        Assert.Contains("Remote Management Users", message, StringComparison.Ordinal);
    }

    [Fact]
    public void 认证方式受限时给出可复制的TrustedHosts命令并说明风险()
    {
        var (code, message) = PowerShellErrorMapper.Map("ServerNotTrusted,PSSessionOpenFailed", -2144108316, "x", "192.0.2.10");
        Assert.Equal(ConnectionErrorCode.ProtocolNegotiationFailed, code);
        Assert.Contains("-Value '192.0.2.10' -Concatenate", message, StringComparison.Ordinal);
        Assert.Contains("不会被验证身份", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("192.0.2.10", true)]
    [InlineData("srv01.corp.example.test", true)]
    [InlineData("[2001:db8::1]", true)]
    [InlineData("*", false)]
    [InlineData("192.0.2.*", false)]
    [InlineData("a,b", false)]
    [InlineData("x'; Start-Process calc; '", false)]
    [InlineData("host$(calc)", false)]
    [InlineData("a\r\nb", false)]
    [InlineData("", false)]
    public void 提权脚本只接受单个主机地址(string host, bool accepted)
    {
        Assert.Equal(accepted, ElevatedTrustedHostsWriter.IsSafeHost(host));
        if (accepted)
        {
            var script = ElevatedTrustedHostsWriter.BuildScript(host);
            Assert.Contains($"$name = '{host}'", script, StringComparison.Ordinal);
            // 只追加，从不覆盖原有条目；临时启动的服务要停回去。
            Assert.Contains("-Value $name -Concatenate -Force", script, StringComparison.Ordinal);
            Assert.Contains("if ($started) { Stop-Service WinRM }", script, StringComparison.Ordinal);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => ElevatedTrustedHostsWriter.BuildScript(host));
        }
    }

    [Fact]
    public void 本机地址识别_回环与本机网卡地址算本机_其他不算()
    {
        Assert.True(PowerShellSession.IsThisMachine([System.Net.IPAddress.Loopback]));
        Assert.True(PowerShellSession.IsThisMachine([System.Net.IPAddress.IPv6Loopback]));
        Assert.False(PowerShellSession.IsThisMachine([System.Net.IPAddress.Parse("192.0.2.10")]));
        Assert.False(PowerShellSession.IsThisMachine([]));

        var own = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .FirstOrDefault(a => !System.Net.IPAddress.IsLoopback(a));
        if (own is not null)
        {
            Assert.True(PowerShellSession.IsThisMachine([own]));
        }
    }

    [Theory]
    [InlineData("x'; Remove-Item C:\\ -Recurse; '")]
    [InlineData("a b")]
    [InlineData("*")]            // 通配会让 TrustedHosts 信任所有主机
    [InlineData("host$(calc)")]
    public void 主机地址含可疑字符时不生成可复制命令(string host)
    {
        var (_, message) = PowerShellErrorMapper.Map("ServerNotTrusted", -2144108316, "x", host);
        Assert.DoesNotContain("Set-Item", message, StringComparison.Ordinal);
    }

    [Fact]
    public void 只在远程桌面主机上提供_虚拟机控制台除外()
    {
        var provider = new PowerShellSessionProvider(NullLoggerFactory.Instance);
        var rdp = new ConnectionProfile { Protocol = ProtocolType.Rdp };
        var console = new ConnectionProfile { Protocol = ProtocolType.Rdp };
        console.Vmware.Enabled = true;

        Assert.True(provider.CanOpen(rdp));
        Assert.False(provider.CanOpen(console));
        Assert.False(provider.CanOpen(new ConnectionProfile { Protocol = ProtocolType.Ssh }));
        Assert.False(provider.CanOpen(new ConnectionProfile { Protocol = ProtocolType.Vnc }));
    }

    [Fact]
    public async Task 经接口读到的会话种类是PowerShell()
    {
        await using var session = new PowerShellSession(
            new SessionRequest { Profile = new ConnectionProfile { Protocol = ProtocolType.Rdp, Host = "h" } },
            NullLogger.Instance);
        Assert.Equal(SessionKind.PowerShell, ((IRemoteSession)session).Kind);
        Assert.Equal(ProtocolType.Rdp, session.Protocol);
    }

    [Fact]
    public async Task 主机名无法解析时给出明确错误_不启动PowerShell()
    {
        await using var session = new PowerShellSession(
            new SessionRequest { Profile = new ConnectionProfile { Protocol = ProtocolType.Rdp, Host = "rf-no-such-host.invalid" } },
            NullLogger.Instance);
        await session.ConnectAsync();
        Assert.Equal(ConnectionState.Failed, session.State);
        Assert.Equal(ConnectionErrorCode.HostNotFound, session.ErrorCode);
        Assert.Null(session.ProcessId);
    }

    [Fact]
    public async Task WinRM端口不通时提示如何启用()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var session = new PowerShellSession(
            new SessionRequest { Profile = new ConnectionProfile { Protocol = ProtocolType.Rdp, Host = "127.0.0.1" } },
            NullLogger.Instance) { Port = closedPort, LocalAddressCheck = _ => false };

        await session.ConnectAsync();

        Assert.Equal(ConnectionErrorCode.NetworkUnreachable, session.ErrorCode);
        Assert.Contains("Enable-PSRemoting", session.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(session.ProcessId);
    }

    [Fact]
    public async Task 本机未启用远程时单独提示_不提供远程启用()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var session = new PowerShellSession(
            new SessionRequest { Profile = new ConnectionProfile { Protocol = ProtocolType.Rdp, Host = "127.0.0.1" } },
            NullLogger.Instance) { Port = closedPort };

        await session.ConnectAsync();

        // 错误码不是 NetworkUnreachable：界面据此不显示「远程启用」按钮（WMI 不支持对本机用账号密码）。
        Assert.Equal(ConnectionErrorCode.ComponentUnavailable, session.ErrorCode);
        Assert.Contains("这是本机", session.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("localhost", session.ConnectionTarget);
    }
}
