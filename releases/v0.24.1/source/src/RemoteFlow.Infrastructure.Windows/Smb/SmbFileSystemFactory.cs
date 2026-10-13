using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.FileTransfer;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.Infrastructure.Smb;

/// <summary>
/// SMB 管理共享的文件系统工厂（不建立 RDP 会话，直接访问 <c>\\主机\C$</c> 等）。
/// 用连接已保存的 Windows 凭据经 <c>WNetAddConnection2</c> 认证，仅 Windows 版可用、固定 445 端口。
/// </summary>
public sealed class SmbFileSystemFactory : IRemoteFileSystemFactory
{
    private const int SmbPort = 445;

    /// <summary>预探测 445 端口的超时：主机不可达时尽快给出提示，而不是等 WNet 那个不可取消的长调用。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    /// <summary>WNet 认证的整体超时。</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    private readonly ILogger<SmbFileSystemFactory> _logger;
    private readonly ISmbAuthenticator _authenticator;
    private readonly Func<string, int, CancellationToken, Task> _probe;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>>>? _shareLister;

    public SmbFileSystemFactory(ILogger<SmbFileSystemFactory> logger)
        : this(logger, new WNetSmbAuthenticator(), TcpProbeAsync, null)
    {
    }

    /// <summary>测试入口：注入假认证 / 探测 / 共享枚举。</summary>
    internal SmbFileSystemFactory(
        ILogger<SmbFileSystemFactory> logger,
        ISmbAuthenticator authenticator,
        Func<string, int, CancellationToken, Task> probe,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>>? shareLister)
    {
        _logger = logger;
        _authenticator = authenticator;
        _probe = probe;
        _shareLister = shareLister;
    }

    public FileTransferChannel Channel => FileTransferChannel.Smb;

    public bool IsAvailable(out string? unavailableReason)
    {
        if (!OperatingSystem.IsWindows())
        {
            unavailableReason = "SMB 管理共享仅 Windows 版可用。";
            return false;
        }

        unavailableReason = null;
        return true;
    }

    public async Task<IRemoteFileSystem> OpenAsync(FileSystemOpenRequest request, CancellationToken cancellationToken)
    {
        if (!IsAvailable(out var reason))
        {
            throw new ConnectionException(ConnectionErrorCode.ComponentUnavailable, reason!);
        }

        var host = request.Profile.Host.Trim();
        if (host.Length == 0 || host.IndexOfAny(['\\', '/', ':']) >= 0)
        {
            // 主机名会被拼进 UNC 路径；含分隔符的值要么是配置错误，要么是想改写路径，一律拒绝。
            throw ConnectionException.FromCode(ConnectionErrorCode.HostNotFound);
        }

        var userName = SmbAccountName.Compose(host, request.Credential, request.Profile);
        var password = request.Credential.Password ?? string.Empty;

        await ProbeAsync(host, cancellationToken).ConfigureAwait(false);

        var connectTask = Task.Run(() => _authenticator.Connect(host, userName, password), CancellationToken.None);
        ISmbConnection connection;
        try
        {
            connection = await connectTask.WaitAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // WNet 调用不可取消，仍在后台运行：它若迟到成功，必须撤销那条连接，否则会遗留在用户的会话里。
            _ = connectTask.ContinueWith(
                t =>
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        t.Result.Dispose();
                    }
                },
                TaskScheduler.Default);

            if (ex is OperationCanceledException)
            {
                throw;
            }

            throw ConnectionException.FromCode(ConnectionErrorCode.Timeout, ex);
        }

        _logger.LogInformation("SMB 已建立会话（独立文件传输）");

        var mapper = new SmbPathMapper($@"\\?\UNC\{host}");
        var lister = _shareLister ?? ListSharesAsync;
        return new SmbFileSystem(mapper, ct => lister(host, ct), connection, _logger);
    }

    private async Task ProbeAsync(string host, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            await _probe(host, SmbPort, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConnectionException(
                ConnectionErrorCode.Timeout, "连接 445 端口（SMB）超时，请检查主机是否在线、防火墙是否放行。");
        }
        catch (SocketException ex)
        {
            throw new ConnectionException(
                ConnectionErrorCode.NetworkUnreachable,
                "无法连接到目标主机的 445 端口（SMB），请检查主机是否在线、防火墙是否放行。",
                ex);
        }
    }

    private static async Task TcpProbeAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 枚举磁盘共享（含 <c>C$</c> 这类管理共享，不含 IPC$ / 打印机）。非管理员账号调用 NetShareEnum 常被拒，
    /// 此时退而探测常见的管理共享是否存在，保证至少能进 <c>C$</c>。
    /// </summary>
    private static async Task<IReadOnlyList<string>> ListSharesAsync(string host, CancellationToken cancellationToken)
    {
        var shares = await Task.Run(() => EnumerateShares(host), cancellationToken).ConfigureAwait(false);
        if (shares.Count > 0)
        {
            return shares;
        }

        return await Task.Run(() =>
        {
            var found = new List<string>();
            foreach (var candidate in new[] { "C$", "D$", "E$", "F$", "ADMIN$" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (Directory.Exists($@"\\?\UNC\{host}\{candidate}"))
                    {
                        found.Add(candidate);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 探测失败即视为不存在。
                }
            }

            return (IReadOnlyList<string>)found;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static List<string> EnumerateShares(string host)
    {
        var result = new List<string>();
        var resume = 0;
        var status = SmbNative.NetShareEnum(
            $@"\\{host}", 1, out var buffer, SmbNative.MaxPreferredLength, out var read, out _, ref resume);
        if (status != 0 || buffer == 0)
        {
            return result; // 无权限或不支持：交给调用方退回探测。
        }

        try
        {
            var size = Marshal.SizeOf<SmbNative.ShareInfo1>();
            for (var i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<SmbNative.ShareInfo1>(buffer + (i * size));
                if ((info.Type & SmbNative.ShareTypeMask) != SmbNative.ShareTypeDisk)
                {
                    continue; // 只要磁盘共享：IPC$ / 打印机不是文件系统。
                }

                var name = Marshal.PtrToStringUni(info.NetName);
                if (!string.IsNullOrEmpty(name))
                {
                    result.Add(name);
                }
            }
        }
        finally
        {
            _ = SmbNative.NetApiBufferFree(buffer);
        }

        return result;
    }
}
