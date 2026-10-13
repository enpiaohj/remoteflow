using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.FileTransfer;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Infrastructure.Smb;
using Xunit;
using ProtocolType = RemoteFlow.Core.Models.ProtocolType;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// SMB 通道：路径映射 / 账号拼接 / 错误翻译 / 文件操作（用本机临时目录扮演「服务器根」，其下的子目录扮演共享）/
/// 工厂流程（假认证器）。真实 SMB 服务器上的收发由实机验证覆盖。
/// </summary>
public sealed class SmbFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rf-smb-" + Guid.NewGuid().ToString("N")[..8]);

    public SmbFileSystemTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "C$"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论。
        }
    }

    // ── 路径映射 ──

    [Theory]
    [InlineData("/", 0)]
    [InlineData("/C$", 1)]
    [InlineData("/C$/Users", 2)]
    [InlineData("/C$/Users/x/y", 4)]
    [InlineData("/../..", 0)]
    public void 层数按虚拟路径计算(string path, int depth)
        => Assert.Equal(depth, SmbPathMapper.Depth(path));

    [Fact]
    public void 映射把虚拟路径拼到服务器根之下()
    {
        var mapper = new SmbPathMapper(@"\\?\UNC\host");

        Assert.Equal(@"\\?\UNC\host", mapper.ToPhysical("/"));
        Assert.Equal(@"\\?\UNC\host\C$", mapper.ToPhysical("/C$"));
        Assert.Equal(@"\\?\UNC\host\C$\Users\x", mapper.ToPhysical("/C$/Users/x"));
    }

    [Fact]
    public void 点点上溯被规范化吃掉不会越出服务器根()
    {
        var mapper = new SmbPathMapper(@"\\?\UNC\host");

        Assert.Equal(@"\\?\UNC\host\C$", mapper.ToPhysical("/C$/../../C$"));
        Assert.Equal(@"\\?\UNC\host", mapper.ToPhysical("/../.."));
    }

    [Theory]
    [InlineData("/C$/a\\..\\..\\evil")]
    [InlineData("/C$/a\\b")]
    [InlineData("/C$/file:stream")]
    [InlineData("/C$/we*ird")]
    [InlineData("/C$/q?")]
    [InlineData("/C$/pipe|x")]
    [InlineData("/C$/tab\tname")]
    [InlineData("/C$\\..\\..\\other")]
    public void 单段里夹带反斜杠冒号等一律拒绝(string path)
    {
        var mapper = new SmbPathMapper(@"\\?\UNC\host");

        var ex = Assert.Throws<FileTransferException>(() => mapper.ToPhysical(path));

        Assert.Equal(FileTransferErrorCode.InvalidName, ex.Code);
    }

    // ── 账号拼接 ──

    [Fact]
    public void 域账号用域反斜杠用户()
    {
        using var credential = new ResolvedCredential
        {
            Type = CredentialType.WindowsDomain, Username = "alice", Domain = "CORP"
        };

        Assert.Equal(@"CORP\alice", SmbAccountName.Compose("srv1", credential, new ConnectionProfile()));
    }

    [Fact]
    public void 本地账号用主机反斜杠用户让远端按本地账户库校验()
    {
        using var credential = new ResolvedCredential { Type = CredentialType.LocalPassword, Username = "admin" };

        Assert.Equal(@"srv1\admin", SmbAccountName.Compose("srv1", credential, new ConnectionProfile()));
    }

    [Theory]
    [InlineData(@"CORP\bob")]
    [InlineData("bob@corp.example")]
    public void 用户已带域或UPN时原样使用(string user)
    {
        using var credential = new ResolvedCredential { Type = CredentialType.WindowsDomain, Username = user, Domain = "IGNORED" };

        Assert.Equal(user, SmbAccountName.Compose("srv1", credential, new ConnectionProfile()));
    }

    [Fact]
    public void 域账号凭据没写域时借用连接的RDP登录域()
    {
        using var credential = new ResolvedCredential { Type = CredentialType.WindowsDomain, Username = "alice" };
        var profile = new ConnectionProfile { Rdp = { Domain = "FALLBACK" } };

        Assert.Equal(@"FALLBACK\alice", SmbAccountName.Compose("srv1", credential, profile));
    }

    // ── 错误翻译 ──

    [Fact]
    public void 已用另一账号连接过时给出可操作的提示()
    {
        var ex = SmbErrors.FromWin32(1219);

        Assert.Equal(ConnectionErrorCode.AuthenticationFailed, ex.ErrorCode);
        Assert.Contains("另一个账号", ex.Message);
        Assert.Contains("net use", ex.Message);
    }

    [Theory]
    [InlineData(1326, ConnectionErrorCode.AuthenticationFailed)]
    [InlineData(5, ConnectionErrorCode.AuthenticationFailed)]
    [InlineData(1909, ConnectionErrorCode.AuthenticationFailed)]
    [InlineData(53, ConnectionErrorCode.NetworkUnreachable)]
    [InlineData(67, ConnectionErrorCode.NetworkUnreachable)]
    [InlineData(1231, ConnectionErrorCode.NetworkUnreachable)]
    [InlineData(1460, ConnectionErrorCode.Timeout)]
    [InlineData(99999, ConnectionErrorCode.Unknown)]
    public void Win32错误码映射到标准错误码(int win32, ConnectionErrorCode expected)
        => Assert.Equal(expected, SmbErrors.FromWin32(win32).ErrorCode);

    [Fact]
    public void 未知错误码消息带码值便于排查()
        => Assert.Contains("99999", SmbErrors.FromWin32(99999).Message);

    // ── 文件操作（本机临时目录扮演服务器） ──

    [Fact]
    public async Task 根目录列出共享列表且不含非法名()
    {
        await using var fs = NewFileSystem(shares: ["C$", "D$", "bad/name"]);

        var entries = await fs.ListAsync("/", CancellationToken.None);

        Assert.Equal(["C$", "D$"], entries.Select(e => e.Name).Order());
        Assert.All(entries, e => Assert.True(e.IsDirectory));
        Assert.Equal("/C$", entries.First(e => e.Name == "C$").FullPath);
    }

    [Fact]
    public async Task 列目录返回文件与目录的类型大小和修改时间()
    {
        File.WriteAllText(Path.Combine(_root, "C$", "a.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(_root, "C$", "sub"));
        await using var fs = NewFileSystem();

        var entries = await fs.ListAsync("/C$", CancellationToken.None);

        var file = Assert.Single(entries, e => e.Name == "a.txt");
        Assert.False(file.IsDirectory);
        Assert.Equal(5, file.Size);
        Assert.NotNull(file.Modified);
        Assert.Equal("/C$/a.txt", file.FullPath);
        Assert.True(Assert.Single(entries, e => e.Name == "sub").IsDirectory);
    }

    [Fact]
    public async Task 目录联接点被标记为链接()
    {
        var target = Path.Combine(_root, "C$", "real");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_root, "C$", "jump");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
               { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true }))
        {
            process!.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }

        await using var fs = NewFileSystem();
        var entries = await fs.ListAsync("/C$", CancellationToken.None);

        var junction = Assert.Single(entries, e => e.Name == "jump");
        Assert.True(junction.IsSymlink);
        Assert.True(junction.IsDirectory);
        Assert.False(Assert.Single(entries, e => e.Name == "real").IsSymlink);
    }

    [Fact]
    public async Task 取信息区分存在与不存在共享根不访问网络()
    {
        File.WriteAllText(Path.Combine(_root, "C$", "x.bin"), "1");
        await using var fs = NewFileSystem();

        Assert.NotNull(await fs.StatAsync("/C$/x.bin", CancellationToken.None));
        Assert.Null(await fs.StatAsync("/C$/missing", CancellationToken.None));
        Assert.True((await fs.StatAsync("/", CancellationToken.None))!.IsDirectory);
        Assert.True((await fs.StatAsync("/C$", CancellationToken.None))!.IsDirectory);
    }

    [Fact]
    public async Task 上传再下载内容一致且进度单调到总长()
    {
        var payload = new byte[700_000];
        Random.Shared.NextBytes(payload);
        await using var fs = NewFileSystem();

        var upProgress = new List<long>();
        await fs.UploadAsync(new MemoryStream(payload), "/C$/blob.bin", new ListProgress(upProgress), CancellationToken.None);

        var downloaded = new MemoryStream();
        var downProgress = new List<long>();
        await fs.DownloadAsync("/C$/blob.bin", downloaded, new ListProgress(downProgress), CancellationToken.None);

        Assert.Equal(payload, downloaded.ToArray());
        Assert.Equal(payload.Length, upProgress[^1]);
        Assert.Equal(payload.Length, downProgress[^1]);
        Assert.Equal(upProgress.Order(), upProgress);
        Assert.Equal(downProgress.Order(), downProgress);
    }

    [Fact]
    public async Task 传输中取消立即停止且抛取消异常()
    {
        var payload = new byte[6_000_000];
        File.WriteAllBytes(Path.Combine(_root, "C$", "big.bin"), payload);
        await using var fs = NewFileSystem();
        using var cts = new CancellationTokenSource();
        var progress = new CancelOnFirstProgress(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fs.DownloadAsync("/C$/big.bin", new MemoryStream(), progress, cts.Token));

        Assert.True(progress.LastReported < payload.Length, "取消后应在传完之前停下");
    }

    [Fact]
    public async Task 新建目录幂等且同名文件冲突时报已存在()
    {
        await using var fs = NewFileSystem();

        await fs.CreateDirectoryAsync("/C$/newdir", CancellationToken.None);
        await fs.CreateDirectoryAsync("/C$/newdir", CancellationToken.None);
        Assert.True(Directory.Exists(Path.Combine(_root, "C$", "newdir")));

        File.WriteAllText(Path.Combine(_root, "C$", "taken"), "x");
        var ex = await Assert.ThrowsAsync<FileTransferException>(
            () => fs.CreateDirectoryAsync("/C$/taken", CancellationToken.None));
        Assert.Equal(FileTransferErrorCode.AlreadyExists, ex.Code);
    }

    [Fact]
    public async Task 改名不覆盖时目标存在报已存在覆盖时原子替换()
    {
        var a = Path.Combine(_root, "C$", "a.txt");
        var b = Path.Combine(_root, "C$", "b.txt");
        File.WriteAllText(a, "AAA");
        File.WriteAllText(b, "BBB");
        await using var fs = NewFileSystem();

        var ex = await Assert.ThrowsAsync<FileTransferException>(
            () => fs.RenameAsync("/C$/a.txt", "/C$/b.txt", overwrite: false, CancellationToken.None));
        Assert.Equal(FileTransferErrorCode.AlreadyExists, ex.Code);
        Assert.Equal("BBB", File.ReadAllText(b)); // 冲突时目标保持原样

        await fs.RenameAsync("/C$/a.txt", "/C$/b.txt", overwrite: true, CancellationToken.None);
        Assert.False(File.Exists(a));
        Assert.Equal("AAA", File.ReadAllText(b));
    }

    [Fact]
    public async Task 目录同名时即使允许覆盖也不替换目录()
    {
        Directory.CreateDirectory(Path.Combine(_root, "C$", "d1"));
        Directory.CreateDirectory(Path.Combine(_root, "C$", "d2"));
        await using var fs = NewFileSystem();

        var ex = await Assert.ThrowsAsync<FileTransferException>(
            () => fs.RenameAsync("/C$/d1", "/C$/d2", overwrite: true, CancellationToken.None));

        Assert.Equal(FileTransferErrorCode.AlreadyExists, ex.Code);
    }

    [Fact]
    public async Task 删除文件与空目录且非空目录报无法执行()
    {
        var file = Path.Combine(_root, "C$", "f.txt");
        var empty = Path.Combine(_root, "C$", "empty");
        var full = Path.Combine(_root, "C$", "full");
        File.WriteAllText(file, "x");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(full);
        File.WriteAllText(Path.Combine(full, "child.txt"), "y");
        await using var fs = NewFileSystem();

        await fs.DeleteAsync("/C$/f.txt", isDirectory: false, CancellationToken.None);
        await fs.DeleteAsync("/C$/empty", isDirectory: true, CancellationToken.None);
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(empty));

        var ex = await Assert.ThrowsAsync<FileTransferException>(
            () => fs.DeleteAsync("/C$/full", isDirectory: true, CancellationToken.None));
        Assert.Equal(FileTransferErrorCode.InvalidOperation, ex.Code);
        Assert.True(File.Exists(Path.Combine(full, "child.txt")));
    }

    [Fact]
    public async Task 只读文件不会被擅自清除属性而删除()
    {
        var file = Path.Combine(_root, "C$", "ro.txt");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        await using var fs = NewFileSystem();

        var ex = await Assert.ThrowsAsync<FileTransferException>(
            () => fs.DeleteAsync("/C$/ro.txt", isDirectory: false, CancellationToken.None));

        Assert.Equal(FileTransferErrorCode.PermissionDenied, ex.Code);
        Assert.True(File.Exists(file));
        File.SetAttributes(file, FileAttributes.Normal);
    }

    [Fact]
    public async Task 不存在的路径报不存在()
    {
        await using var fs = NewFileSystem();

        var ex = await Assert.ThrowsAsync<FileTransferException>(
            () => fs.ListAsync("/C$/nope", CancellationToken.None));

        Assert.Equal(FileTransferErrorCode.NotFound, ex.Code);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/C$")]
    public async Task 根与共享根不能新建删除改名(string path)
    {
        await using var fs = NewFileSystem();

        foreach (var action in new Func<Task>[]
                 {
                     () => fs.CreateDirectoryAsync(path, CancellationToken.None),
                     () => fs.DeleteAsync(path, true, CancellationToken.None),
                     () => fs.RenameAsync(path, "/C$/x", true, CancellationToken.None),
                     () => fs.UploadAsync(new MemoryStream(), path, null, CancellationToken.None),
                 })
        {
            var ex = await Assert.ThrowsAsync<FileTransferException>(action);
            Assert.Equal(FileTransferErrorCode.InvalidOperation, ex.Code);
        }
    }

    [Fact]
    public async Task 穿越路径被拒绝且目标目录之外不产生文件()
    {
        var outside = Path.Combine(_root, "evil.txt");
        await using var fs = NewFileSystem();

        var ex = await Assert.ThrowsAsync<FileTransferException>(
            () => fs.UploadAsync(new MemoryStream([1, 2, 3]), "/C$/a\\..\\..\\evil.txt", null, CancellationToken.None));

        Assert.Equal(FileTransferErrorCode.InvalidName, ex.Code);
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public async Task 释放时撤销连接且释放后不可再用()
    {
        var connection = new FakeConnection();
        var fs = NewFileSystem(connection: connection);

        await fs.DisposeAsync();
        await fs.DisposeAsync(); // 幂等

        Assert.Equal(1, connection.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fs.ListAsync("/", CancellationToken.None));
    }

    // ── 工厂流程（假认证器） ──

    [Fact]
    public async Task 工厂先探测445再用拼好的账号认证并返回SMB文件系统()
    {
        var authenticator = new FakeAuthenticator();
        var probes = new List<(string Host, int Port)>();
        var factory = NewFactory(authenticator, probe: (h, p, _) => { probes.Add((h, p)); return Task.CompletedTask; });
        using var credential = new ResolvedCredential
        {
            Type = CredentialType.LocalPassword, Username = "admin", Password = "pw"
        };

        await using var fs = await factory.OpenAsync(
            new FileSystemOpenRequest(new ConnectionProfile { Host = "srv1", Protocol = ProtocolType.Rdp }, credential, null),
            CancellationToken.None);

        Assert.Equal(("srv1", 445), Assert.Single(probes));
        Assert.Equal(@"srv1\admin", authenticator.LastUser);
        Assert.Equal("pw", authenticator.LastPassword);
        Assert.Equal(FileTransferChannel.Smb, fs.Channel);
        Assert.Equal("/", fs.InitialPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(@"srv\share")]
    [InlineData("srv/share")]
    [InlineData("srv:445")]
    public async Task 主机名含分隔符时在探测与认证之前就拒绝(string host)
    {
        var authenticator = new FakeAuthenticator();
        var probed = false;
        var factory = NewFactory(authenticator, probe: (_, _, _) => { probed = true; return Task.CompletedTask; });
        using var credential = new ResolvedCredential { Type = CredentialType.LocalPassword, Username = "a" };

        var ex = await Assert.ThrowsAsync<ConnectionException>(() => factory.OpenAsync(
            new FileSystemOpenRequest(new ConnectionProfile { Host = host }, credential, null), CancellationToken.None));

        Assert.Equal(ConnectionErrorCode.HostNotFound, ex.ErrorCode);
        Assert.False(probed);
        Assert.Equal(0, authenticator.Calls);
    }

    [Fact]
    public async Task 端口探测失败时提示445且不发起认证()
    {
        var authenticator = new FakeAuthenticator();
        var factory = NewFactory(authenticator, probe: (_, _, _) => throw new SocketException((int)SocketError.ConnectionRefused));
        using var credential = new ResolvedCredential { Type = CredentialType.LocalPassword, Username = "a" };

        var ex = await Assert.ThrowsAsync<ConnectionException>(() => factory.OpenAsync(
            new FileSystemOpenRequest(new ConnectionProfile { Host = "srv1" }, credential, null), CancellationToken.None));

        Assert.Equal(ConnectionErrorCode.NetworkUnreachable, ex.ErrorCode);
        Assert.Contains("445", ex.Message);
        Assert.Equal(0, authenticator.Calls);
    }

    [Fact]
    public async Task 端口探测超时按超时处理()
    {
        var factory = NewFactory(new FakeAuthenticator(), probe: (_, _, _) => throw new OperationCanceledException());
        using var credential = new ResolvedCredential { Type = CredentialType.LocalPassword, Username = "a" };

        var ex = await Assert.ThrowsAsync<ConnectionException>(() => factory.OpenAsync(
            new FileSystemOpenRequest(new ConnectionProfile { Host = "srv1" }, credential, null), CancellationToken.None));

        Assert.Equal(ConnectionErrorCode.Timeout, ex.ErrorCode);
    }

    [Fact]
    public async Task 认证失败的连接异常原样抛出不带账号信息()
    {
        var authenticator = new FakeAuthenticator { Failure = SmbErrors.FromWin32(1326) };
        var factory = NewFactory(authenticator);
        using var credential = new ResolvedCredential
        {
            Type = CredentialType.LocalPassword, Username = "secret-user", Password = "secret-pw"
        };

        var ex = await Assert.ThrowsAsync<ConnectionException>(() => factory.OpenAsync(
            new FileSystemOpenRequest(new ConnectionProfile { Host = "srv1" }, credential, null), CancellationToken.None));

        Assert.Equal(ConnectionErrorCode.AuthenticationFailed, ex.ErrorCode);
        Assert.DoesNotContain("secret-user", ex.Message);
        Assert.DoesNotContain("secret-pw", ex.Message);
    }

    [Fact]
    public async Task 取消时迟到成功的连接会被撤销而不是遗留()
    {
        var gate = new ManualResetEventSlim(false);
        var connection = new FakeConnection();
        var authenticator = new FakeAuthenticator { Block = gate, Result = connection };
        var factory = NewFactory(authenticator);
        using var credential = new ResolvedCredential { Type = CredentialType.LocalPassword, Username = "a" };
        using var cts = new CancellationTokenSource();

        var open = factory.OpenAsync(
            new FileSystemOpenRequest(new ConnectionProfile { Host = "srv1" }, credential, null), cts.Token);
        await Task.Delay(100); // 让认证调用进入阻塞
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);

        gate.Set(); // WNet 迟到返回成功
        for (var i = 0; i < 40 && connection.DisposeCount == 0; i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public void Windows上通道可用()
    {
        var factory = NewFactory(new FakeAuthenticator());

        Assert.True(factory.IsAvailable(out var reason));
        Assert.Null(reason);
        Assert.Equal(FileTransferChannel.Smb, factory.Channel);
    }

    // ── 辅助 ──

    private SmbFileSystem NewFileSystem(IReadOnlyList<string>? shares = null, ISmbConnection? connection = null)
        => new(
            new SmbPathMapper(_root),
            _ => Task.FromResult(shares ?? (IReadOnlyList<string>)["C$"]),
            connection,
            NullLogger.Instance);

    private static SmbFileSystemFactory NewFactory(
        FakeAuthenticator authenticator, Func<string, int, CancellationToken, Task>? probe = null)
        => new(
            NullLogger<SmbFileSystemFactory>.Instance,
            authenticator,
            probe ?? ((_, _, _) => Task.CompletedTask),
            (_, _) => Task.FromResult<IReadOnlyList<string>>(["C$"]));

    private sealed class ListProgress(List<long> sink) : IProgress<long>
    {
        public void Report(long value) => sink.Add(value);
    }

    private sealed class CancelOnFirstProgress(CancellationTokenSource cts) : IProgress<long>
    {
        public long LastReported { get; private set; }

        public void Report(long value)
        {
            LastReported = value;
            cts.Cancel();
        }
    }

    private sealed class FakeConnection : ISmbConnection
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class FakeAuthenticator : ISmbAuthenticator
    {
        public int Calls { get; private set; }

        public string? LastUser { get; private set; }

        public string? LastPassword { get; private set; }

        public Exception? Failure { get; init; }

        public ManualResetEventSlim? Block { get; init; }

        public ISmbConnection Result { get; init; } = new FakeConnection();

        public ISmbConnection Connect(string host, string userName, string password)
        {
            Calls++;
            LastUser = userName;
            LastPassword = password;
            Block?.Wait();
            if (Failure is not null)
            {
                throw Failure;
            }

            return Result;
        }
    }
}
