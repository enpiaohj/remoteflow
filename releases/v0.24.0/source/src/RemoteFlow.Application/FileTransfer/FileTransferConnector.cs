using Microsoft.Extensions.Logging;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.FileTransfer;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.Application.FileTransfer;

/// <summary>某个连接能否做文件传输，以及用哪个通道 / 不能的原因。</summary>
public sealed record FileTransferSupport(bool IsSupported, FileTransferChannel? Channel, string? Reason);

/// <summary>「不建立会话直接传文件」的入口：按连接选通道、解析凭据、打开文件系统。</summary>
public interface IFileTransferConnector
{
    /// <summary>不做任何网络访问，只判断该连接是否支持文件传输（用于菜单 / 按钮是否可用及提示原因）。</summary>
    FileTransferSupport GetSupport(ConnectionProfile profile);

    /// <summary>
    /// 连接并返回文件系统，归调用方释放。失败抛 <see cref="ConnectionException"/>（中文消息，不含账号密码）。
    /// </summary>
    Task<IRemoteFileSystem> OpenAsync(ConnectionProfile profile, CancellationToken cancellationToken);
}

/// <summary>
/// 按 <see cref="FileTransferOptions.ResolveChannel"/> 解析出的通道选择对应工厂。
/// <para>
/// 凭据处理与 <c>SessionManager.CreateSessionAsync</c> 保持一致：连接引用的凭据缺失或其密钥已从保险库丢失时
/// 快速失败并给出准确原因（不带空密码去连，否则表象是「认证失败」，误导用户去查服务端）；
/// 明文凭据只在打开文件系统这一瞬间存在，<b>工厂返回后立即释放</b>。
/// </para>
/// </summary>
public sealed class FileTransferConnector : IFileTransferConnector
{
    private readonly IReadOnlyDictionary<FileTransferChannel, IRemoteFileSystemFactory> _factories;
    private readonly CredentialService _credentials;
    private readonly ISshHostKeyPolicy _hostKeyPolicy;
    private readonly ILogger<FileTransferConnector> _logger;

    public FileTransferConnector(
        IEnumerable<IRemoteFileSystemFactory> factories,
        CredentialService credentials,
        ISshHostKeyPolicy hostKeyPolicy,
        ILogger<FileTransferConnector> logger)
    {
        _factories = factories.ToDictionary(f => f.Channel);
        _credentials = credentials;
        _hostKeyPolicy = hostKeyPolicy;
        _logger = logger;
    }

    public FileTransferSupport GetSupport(ConnectionProfile profile)
    {
        var channel = profile.FileTransfer.ResolveChannel(profile.Protocol);
        if (channel is null)
        {
            return new FileTransferSupport(false, null, "VNC 协议本身不支持文件传输。");
        }

        if (!_factories.TryGetValue(channel.Value, out var factory))
        {
            return new FileTransferSupport(false, channel, "本机没有提供该文件传输通道。");
        }

        return factory.IsAvailable(out var reason)
            ? new FileTransferSupport(true, channel, null)
            : new FileTransferSupport(false, channel, reason ?? "该文件传输通道当前不可用。");
    }

    public async Task<IRemoteFileSystem> OpenAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        var support = GetSupport(profile);
        if (!support.IsSupported)
        {
            throw new ConnectionException(
                ConnectionErrorCode.ComponentUnavailable,
                support.Reason ?? ConnectionException.Describe(ConnectionErrorCode.ComponentUnavailable));
        }

        if (profile.CredentialId is not { } credentialId)
        {
            throw new ConnectionException(
                ConnectionErrorCode.CredentialMissing,
                "该连接未指定凭据，请先在连接编辑器中选择凭据后再使用文件传输。");
        }

        var credential = await _credentials.ResolveAsync(credentialId, cancellationToken).ConfigureAwait(false)
            ?? throw ConnectionException.FromCode(ConnectionErrorCode.CredentialMissing);

        try
        {
            if (credential.SecretMissing)
            {
                throw ConnectionException.FromCode(ConnectionErrorCode.CredentialSecretMissing);
            }

            var factory = _factories[support.Channel!.Value];
            var fileSystem = await factory
                .OpenAsync(new FileSystemOpenRequest(profile, credential, _hostKeyPolicy), cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "已打开文件传输通道 {Channel}（连接 {ConnectionId}）", support.Channel, profile.Id);
            return fileSystem;
        }
        finally
        {
            // 无论成功失败都立即释放：工厂需要保留的认证材料已在它自己的对象里，不再依赖这个凭据。
            credential.Dispose();
        }
    }
}
