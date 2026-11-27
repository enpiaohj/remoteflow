using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Protocol.PowerShell;
using Xunit;
using ProtocolType = RemoteFlow.Core.Models.ProtocolType;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 需要本机已启用 PowerShell 远程且以管理员运行，因此默认跳过；
/// 设置环境变量 <c>REMOTEFLOW_PSREMOTING_TESTS=1</c> 后执行。只连本机回环，不改任何系统设置。
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PsRemotingFactAttribute : FactAttribute
{
    public PsRemotingFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("REMOTEFLOW_PSREMOTING_TESTS") != "1")
        {
            Skip = "需要本机 WinRM 回环：设置 REMOTEFLOW_PSREMOTING_TESTS=1 后运行。";
        }
    }
}

/// <summary>参数化用例版本的同一开关：未开启时如实显示为「跳过」，而不是空跑后显示通过。</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PsRemotingTheoryAttribute : TheoryAttribute
{
    public PsRemotingTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("REMOTEFLOW_PSREMOTING_TESTS") != "1")
        {
            Skip = "需要本机 WinRM 回环：设置 REMOTEFLOW_PSREMOTING_TESTS=1 后运行。";
        }
    }
}

/// <summary>用正式的 <see cref="PowerShellSession"/> 对本机做端到端验证。</summary>
[Collection(PowerShellLoopbackCollection.Name)]
public sealed class PowerShellRemotingLoopbackTests
{
    private const string RemotePrompt = @"\[localhost\]: PS [^\r\n]*?>";

    [PsRemotingFact]
    public async Task 连接后命令在远端执行_中文正常_按连接时宽度排版()
    {
        await using var terminal = await Terminal.ConnectAsync(columns: 170);

        Assert.Equal("wsmprovhost", terminal.Ask("(Get-Process -Id $PID).ProcessName"));
        Assert.Equal("中文输入与输出", terminal.Ask("'中文' + '输入与输出'"));
        Assert.Equal("170", terminal.Ask("$Host.UI.RawUI.WindowSize.Width"));
    }

    [PsRemotingFact]
    public async Task 命令历史文件不被写入()
    {
        var history = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "PowerShell", "PSReadLine", "ConsoleHost_history.txt");
        var before = File.Exists(history) ? SHA256.HashData(File.ReadAllBytes(history)) : [];

        await using (var terminal = await Terminal.ConnectAsync())
        {
            terminal.Ask("'history-' + 'probe'");
        }

        var after = File.Exists(history) ? SHA256.HashData(File.ReadAllBytes(history)) : [];
        Assert.Equal(before, after);
    }

    public static TheoryData<string> LeaveWays => new() { "exit", "exit-pssession", "crash", "killed-idle", "killed-idle-burst" };

    [PsRemotingTheory]
    [MemberData(nameof(LeaveWays))]
    public async Task 离开远程会话后进程立即结束_提前敲的命令不在本机执行(string way)
    {
        var marker = Path.Combine(Path.GetTempPath(), $"rf-ps-local-{Guid.NewGuid():N}.txt");
        await using var terminal = await Terminal.ConnectAsync();
        var localProcess = Process.GetProcessById(terminal.Session.ProcessId!.Value);
        // 回环下「远端」与「本机」是同一块磁盘，靠进程名区分：只有本机 powershell.exe 才写得出标记。
        var probe = $"if ((Get-Process -Id $PID).ProcessName -eq 'powershell') {{ Set-Content -LiteralPath '{marker}' -Value 'LOCAL' }}\r";

        switch (way)
        {
            case "exit":
                terminal.Send("exit\r" + probe);
                break;
            case "exit-pssession":
                terminal.Send("Exit-PSSession\r" + probe);
                break;
            case "crash":
                terminal.Send("Stop-Process -Id $PID -Force\r" + probe);
                await Task.Delay(3000);
                terminal.Send(probe);
                break;
            default:
                var remote = Process.GetProcessById(int.Parse(terminal.Ask("$PID")));
                Assert.Equal("wsmprovhost", remote.ProcessName);
                remote.Kill();
                remote.WaitForExit(5000);
                await Task.Delay(2500);
                terminal.Send(way == "killed-idle" ? probe : probe + probe + probe);
                await Task.Delay(4000);
                terminal.Send(probe);
                break;
        }

        try
        {
            Assert.True(localProcess.WaitForExit(25000), "本机 PowerShell 进程仍在运行：\n" + terminal.Screen());
            await Task.Delay(500);
            Assert.False(File.Exists(marker), "命令在本机执行了：\n" + terminal.Screen());
            Assert.DoesNotMatch(@"(?m)^\s*PS [A-Z]:\\[^\r\n]*>", terminal.Screen());
            await terminal.WaitForStateAsync(ConnectionState.Disconnected);
            Assert.Equal(ConnectionErrorCode.RemoteClosed, terminal.Session.ErrorCode);
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [PsRemotingFact]
    public async Task 关闭会话时远端会话随之关闭_命令执行中也一样()
    {
        foreach (var busy in new[] { false, true })
        {
            var terminal = await Terminal.ConnectAsync();
            var remoteId = int.Parse(terminal.Ask("$PID"));
            var localProcess = Process.GetProcessById(terminal.Session.ProcessId!.Value);
            if (busy)
            {
                terminal.Send("Start-Sleep -Seconds 300\r");
                await Task.Delay(2500);
            }

            var watch = Stopwatch.StartNew();
            await terminal.DisposeAsync();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"关闭耗时 {watch.Elapsed.TotalSeconds:N1}s");
            Assert.True(localProcess.WaitForExit(5000));
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (IsRemoteAlive(remoteId) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
            }

            Assert.False(IsRemoteAlive(remoteId), $"远端会话残留（执行中={busy}）");
        }
    }

    [PsRemotingFact]
    public async Task 密码错误时报告认证失败且不给出任何命令行()
    {
        var profile = new ConnectionProfile { Name = "回环", Host = "localhost", Protocol = ProtocolType.Rdp };
        var credential = new ResolvedCredential
        {
            Type = CredentialType.LocalPassword,
            Username = ".\\rf-test-no-such-user",
            Password = "错误密码 '\"$`;| " + Guid.NewGuid()
        };
        await using var session = new PowerShellSession(
            new SessionRequest { Profile = profile, Credential = credential }, NullLogger.Instance);
        var output = new StringBuilder();
        session.DataReceived += (_, data) =>
        {
            lock (output) output.Append(Encoding.UTF8.GetString(data));
            if (Encoding.UTF8.GetString(data).Contains("\x1b[6n")) session.SendInput("\x1b[1;1R");
        };

        await session.ConnectAsync();

        Assert.Equal(ConnectionState.Failed, session.State);
        Assert.Equal(ConnectionErrorCode.AuthenticationFailed, session.ErrorCode);
        Assert.DoesNotContain("???", session.ErrorMessage, StringComparison.Ordinal); // 中文错误详情不乱码
        lock (output) Assert.DoesNotMatch(@"PS [A-Z]:\\", output.ToString());
    }

    private static bool IsRemoteAlive(int id)
    {
        try
        {
            return Process.GetProcessById(id).ProcessName == "wsmprovhost";
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>扮演终端控件：收集输出、应答光标位置查询、发送按键。</summary>
    private sealed class Terminal : IAsyncDisposable
    {
        private readonly StringBuilder _text = new();
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

        public required PowerShellSession Session { get; init; }

        public static async Task<Terminal> ConnectAsync(int columns = 120)
        {
            var profile = new ConnectionProfile { Name = "回环", Host = "localhost", Protocol = ProtocolType.Rdp };
            var session = new PowerShellSession(new SessionRequest { Profile = profile }, NullLogger.Instance)
            {
                UseLoopbackNetworkAccess = true
            };
            var terminal = new Terminal { Session = session };
            session.DataReceived += terminal.OnData;
            session.Resize(columns, 30, 0, 0);

            await session.ConnectAsync();

            Assert.True(session.State == ConnectionState.Connected, $"{session.ErrorCode}: {session.ErrorMessage}");
            Assert.True(terminal.Expect(RemotePrompt, 0), "没有等到远程提示符：\n" + terminal.Screen());
            return terminal;
        }

        private void OnData(object? sender, byte[] data)
        {
            var chars = new char[data.Length + 4];
            string chunk;
            lock (_text)
            {
                chunk = new string(chars, 0, _decoder.GetChars(data, 0, data.Length, chars, 0));
                _text.Append(chunk);
            }

            if (chunk.Contains("\x1b[6n", StringComparison.Ordinal))
            {
                Session.SendInput("\x1b[1;1R");
            }
        }

        public void Send(string text) => Session.SendInput(text);

        public string Screen(int from = 0)
        {
            string raw;
            lock (_text) raw = _text.ToString(from, _text.Length - from);
            raw = Regex.Replace(raw, "\x1b\\[[0-9;]*H", "\n");
            raw = Regex.Replace(raw, "\x1b\\][^\x07]*(\x07|\x1b\\\\)", "");
            return Regex.Replace(raw, "\x1b\\[[0-?]*[ -/]*[@-~]", "").Replace("\x1b", "");
        }

        public bool Expect(string pattern, int from, int timeoutMs = 20000)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (Regex.IsMatch(Screen(from), pattern, RegexOptions.Singleline)) return true;
                Thread.Sleep(80);
            }

            return false;
        }

        /// <summary>让远端求值并取回结果；标记用拼接写法，避免匹配到命令回显本身。</summary>
        public string Ask(string expression)
        {
            int mark;
            lock (_text) mark = _text.Length;
            Send($"'<<'+'RF:' + ({expression}) + ':RF'+'>>'\r");
            Assert.True(Expect("<<RF:.*?:RF>>", mark), "没有等到结果：\n" + Screen(mark));
            return Regex.Match(Screen(mark), "<<RF:(.*?):RF>>", RegexOptions.Singleline).Groups[1].Value
                .Replace("\r", "").Replace("\n", "");
        }

        public async Task WaitForStateAsync(ConnectionState state)
        {
            for (var i = 0; i < 100 && Session.State != state; i++)
            {
                await Task.Delay(100);
            }

            Assert.Equal(state, Session.State);
        }

        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PowerShellLoopbackCollection
{
    public const string Name = "PowerShell 回环";
}
