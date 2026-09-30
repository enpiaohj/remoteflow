using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using RemoteFlow.Core.FileTransfer;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.Protocol.Ssh;

/// <summary>
/// 不建立终端会话、直接连 SFTP 的工厂（文件传输 Tab 用）。
/// 主机密钥走与终端会话相同的两轮握手：首轮遇到未信任的密钥就中止（凭据尚未发送），
/// 弹窗确认并记录后重试；同一 <c>主机:端口</c> 已在终端里信任过的，静默通过。
/// </summary>
public sealed class SftpFileSystemFactory : IRemoteFileSystemFactory
{
    private readonly ILogger<SftpFileSystemFactory> _logger;

    public SftpFileSystemFactory(ILogger<SftpFileSystemFactory> logger)
    {
        _logger = logger;
    }

    public FileTransferChannel Channel => FileTransferChannel.Sftp;

    public bool IsAvailable(out string? unavailableReason)
    {
        unavailableReason = null;
        return true;
    }

    public async Task<IRemoteFileSystem> OpenAsync(FileSystemOpenRequest request, CancellationToken cancellationToken)
    {
        var profile = request.Profile;
        var host = profile.Host;
        var port = ResolvePort(profile);
        var attemptId = Guid.NewGuid();
        var gate = new SshHostKeyGate(request.HostKeyPolicy, host, port, _logger, attemptId);

        // 最多两轮：首轮若因主机密钥未信任被我方中止，确认后再来一轮（与 SshSession.ConnectAsync 一致）。
        for (var attempt = 0; attempt < 2; attempt++)
        {
            gate.Reset();

            // 每轮重建 ConnectionInfo：认证方法对象带状态，不复用上一轮的。
            var info = SshConnectionInfoFactory.Build(
                host, port, request.Credential, profile.Ssh.ConnectTimeoutSeconds, profile.Ssh.Encoding);
            var client = new SftpClient(info);
            client.HostKeyReceived += gate.OnHostKeyReceived;
            if (profile.Ssh.KeepAliveSeconds > 0)
            {
                client.KeepAliveInterval = TimeSpan.FromSeconds(profile.Ssh.KeepAliveSeconds);
            }

            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                client.HostKeyReceived -= gate.OnHostKeyReceived;
                _logger.LogInformation("SFTP 已连接（独立文件传输），尝试 {Attempt}", attempt + 1);
                return new SftpFileSystem(client, _logger);
            }
            catch (Exception ex)
            {
                client.HostKeyReceived -= gate.OnHostKeyReceived;
                client.Dispose();

                if (ex is OperationCanceledException)
                {
                    throw;
                }

                if (attempt == 0 && gate.PendingHostKey is { } pending && request.HostKeyPolicy is { } policy)
                {
                    var accepted = await policy.ConfirmAndRememberAsync(pending, cancellationToken).ConfigureAwait(false);
                    gate.ClearPending();
                    if (accepted)
                    {
                        continue; // 指纹已记录，重试；这轮 Lookup 会静默通过
                    }

                    throw ConnectionException.FromCode(
                        pending.IsMismatch ? ConnectionErrorCode.HostKeyMismatch : ConnectionErrorCode.HostKeyRejected);
                }

                throw ToConnectionException(ex, gate);
            }
        }

        // 循环只会在两轮内 return / throw；走到这里说明两轮都因未信任被中止且用户接受后仍失败。
        throw ConnectionException.FromCode(ConnectionErrorCode.HostKeyRejected);
    }

    /// <summary>SSH 连接用连接自身端口；RDP 主机走 OpenSSH 时用其配置的 SFTP 端口（非法值退回 22）。</summary>
    internal static int ResolvePort(ConnectionProfile profile)
    {
        var port = profile.Protocol == ProtocolType.Ssh ? profile.Port : profile.FileTransfer.SftpPort;
        return port is > 0 and <= 65535 ? port : 22;
    }

    /// <summary>
    /// 把连接阶段的底层异常翻译为标准连接异常。<paramref name="gate"/> 记录了本轮握手的主机密钥判定：
    /// 有待确认的密钥说明它<b>不在已信任列表里</b>（调用方没有走确认弹窗，例如会话内 SFTP 遇到意外指纹），
    /// 一律按拒绝处理，绝不放行。
    /// </summary>
    internal static ConnectionException ToConnectionException(Exception ex, SshHostKeyGate gate)
    {
        if (ex is ConnectionException already)
        {
            return already;
        }

        if (gate.PendingHostKey is { } pending)
        {
            return ConnectionException.FromCode(
                pending.IsMismatch ? ConnectionErrorCode.HostKeyMismatch : ConnectionErrorCode.HostKeyRejected, ex);
        }

        // 服务端没有启用 sftp 子系统：SSH 登录成功，但打开 sftp 通道被拒。给出比「协议协商失败」更准确的提示。
        if (ex is SshException and not (SshAuthenticationException or SshConnectionException or SshOperationTimeoutException)
            && ex.Message.Contains("subsystem", StringComparison.OrdinalIgnoreCase))
        {
            return new ConnectionException(
                ConnectionErrorCode.ProtocolNegotiationFailed,
                "服务器没有启用 SFTP 子系统（sshd_config 中的 Subsystem sftp），无法进行文件传输。",
                ex);
        }

        return ConnectionException.FromCode(SshErrorMapper.Map(ex, gate.Failure), ex);
    }
}
