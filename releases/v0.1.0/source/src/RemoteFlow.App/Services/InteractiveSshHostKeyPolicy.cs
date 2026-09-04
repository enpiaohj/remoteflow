using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.App.Services;

/// <summary>
/// 交互式 SSH 主机密钥校验策略。
/// <para>
/// 规则（产品设计文档 §13.3）：
/// <list type="number">
///   <item>首次连接：提示用户确认指纹，接受后记录。</item>
///   <item>指纹一致：静默放行。</item>
///   <item>指纹不一致：<b>必须明确警告</b>，用户显式确认后才继续，绝不静默接受。</item>
/// </list>
/// </para>
/// </summary>
public sealed class InteractiveSshHostKeyPolicy(
    IHostKeyRepository repository,
    IDialogService dialogs,
    ILogger<InteractiveSshHostKeyPolicy> logger) : ISshHostKeyPolicy
{
    public async Task<bool> VerifyAsync(SshHostKeyVerificationContext context, CancellationToken cancellationToken)
    {
        var known = await repository.GetAsync(context.Host, context.Port, cancellationToken);

        // 指纹与记录一致：这是绝大多数情况，直接放行，不打扰用户。
        if (known is not null && known.Fingerprint == context.Fingerprint)
        {
            return true;
        }

        var enriched = new SshHostKeyVerificationContext
        {
            Host = context.Host,
            Port = context.Port,
            KeyAlgorithm = context.KeyAlgorithm,
            Fingerprint = context.Fingerprint,
            KnownFingerprint = known?.Fingerprint
        };

        if (enriched.IsMismatch)
        {
            logger.LogWarning(
                "主机 {Host}:{Port} 的密钥指纹与记录不一致，已提示用户确认",
                context.Host, context.Port);
        }

        var accepted = await dialogs.ConfirmHostKeyAsync(enriched);

        if (!accepted)
        {
            logger.LogInformation("用户拒绝了 {Host}:{Port} 的主机密钥", context.Host, context.Port);

            // 指纹不一致时用更精确的错误码，让历史记录能区分「用户拒绝」与「疑似中间人」。
            throw ConnectionException.FromCode(
                enriched.IsMismatch ? ConnectionErrorCode.HostKeyMismatch : ConnectionErrorCode.HostKeyRejected);
        }

        await repository.SaveAsync(new SshHostKeyRecord
        {
            HostKey = SshHostKeyRecord.BuildHostKey(context.Host, context.Port),
            KeyAlgorithm = context.KeyAlgorithm,
            Fingerprint = context.Fingerprint,
            TrustedAt = DateTimeOffset.Now
        }, cancellationToken);

        logger.LogInformation("已记录 {Host}:{Port} 的主机密钥指纹", context.Host, context.Port);
        return true;
    }
}
