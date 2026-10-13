using Microsoft.Extensions.Logging;
using Renci.SshNet.Common;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.Protocol.Ssh;

/// <summary>
/// 单个 SSH 端点的主机密钥校验闸门。<see cref="SshSession"/> 与 SFTP 工厂共用同一份逻辑，
/// 保证「同一主机 + 端口」在终端与文件传输里信任决策一致、且都不会静默信任任意密钥。
/// <para>
/// SSH.NET 在握手线程上同步触发 <c>HostKeyReceived</c>。<b>这里绝不阻塞等待 UI</b>——连接超时会先到，
/// 把弹窗结果吞掉。只做一次快速的本机指纹比对：一致则放行；否则中止握手
/// （<c>CanTrust = false</c>，此时凭据尚未发送），把待确认的密钥记下来，
/// 由调用方在握手失败后弹窗确认，用户接受则记录并重试（重试时这里的 <see cref="Decide"/> 会静默通过）。
/// </para>
/// </summary>
internal sealed class SshHostKeyGate
{
    private readonly ISshHostKeyPolicy? _policy;
    private readonly string _host;
    private readonly int _port;
    private readonly ILogger _logger;
    private readonly Guid _ownerId;

    public SshHostKeyGate(ISshHostKeyPolicy? policy, string host, int port, ILogger logger, Guid ownerId)
    {
        _policy = policy;
        _host = host;
        _port = port;
        _logger = logger;
        _ownerId = ownerId;
    }

    /// <summary>握手时发现的、尚未被本机信任的主机密钥。握手失败后据此弹窗确认。</summary>
    public SshHostKeyVerificationContext? PendingHostKey { get; private set; }

    /// <summary>校验失败的具体原因，用于在连接异常时给出准确错误码。</summary>
    public ConnectionErrorCode Failure { get; private set; } = ConnectionErrorCode.None;

    /// <summary>每轮连接尝试前重置。</summary>
    public void Reset()
    {
        PendingHostKey = null;
        Failure = ConnectionErrorCode.None;
    }

    /// <summary>弹窗确认结束后清掉待确认项（无论接受与否）。</summary>
    public void ClearPending() => PendingHostKey = null;

    /// <summary>挂到 <c>BaseClient.HostKeyReceived</c> 上的事件处理器。</summary>
    public void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        _logger.LogDebug(
            "SSH {OwnerId} 收到 HostKeyReceived，调用线程={ThreadId}", _ownerId, Environment.CurrentManagedThreadId);

        e.CanTrust = Decide(e.HostKeyName, e.FingerPrintSHA256);
    }

    /// <summary>
    /// 判定是否可信。与事件参数解耦，便于单元测试。
    /// 返回 false 时 <see cref="PendingHostKey"/>（待确认）或 <see cref="Failure"/>（直接拒绝）已被设置。
    /// </summary>
    public bool Decide(string keyAlgorithm, string fingerprintSha256)
    {
        if (_policy is null)
        {
            // 没有配置校验策略时必须拒绝，绝不静默信任任意主机密钥。
            _logger.LogWarning("SSH {OwnerId} 未配置 HostKeyPolicy，直接拒绝主机密钥", _ownerId);
            Failure = ConnectionErrorCode.HostKeyRejected;
            return false;
        }

        try
        {
            var context = _policy.Lookup(new SshHostKeyVerificationContext
            {
                Host = _host,
                Port = _port,
                KeyAlgorithm = keyAlgorithm,
                Fingerprint = fingerprintSha256,
            });

            if (context.IsKnownGood)
            {
                return true;
            }

            // 未信任 / 指纹变化：中止本次握手，稍后弹窗。
            _logger.LogDebug(
                "SSH {OwnerId} 主机密钥未信任，中止握手待确认，IsMismatch={IsMismatch}",
                _ownerId, context.IsMismatch);
            PendingHostKey = context;
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSH {OwnerId} 查询主机密钥时发生异常", _ownerId);
            Failure = ConnectionErrorCode.HostKeyRejected;
            return false;
        }
    }
}
