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
/// <para>
/// <see cref="Lookup"/> 在 SSH 握手线程上跑，只查库、不弹 UI；
/// <see cref="ConfirmAndRememberAsync"/> 在握手中止后的异步流程里弹窗，
/// 避免在握手线程上阻塞等待用户（会话超时会先到，把弹窗结果吞掉）。
/// </para>
/// </summary>
public sealed class InteractiveSshHostKeyPolicy(
    IHostKeyRepository repository,
    IDialogService dialogs,
    ILogger<InteractiveSshHostKeyPolicy> logger) : ISshHostKeyPolicy
{
    public SshHostKeyVerificationContext Lookup(SshHostKeyVerificationContext context)
    {
        // 握手线程（线程池线程）上没有 SynchronizationContext，这里的 sync-over-async
        // 是安全的；查询是主键索引命中，很快。
        var known = repository
            .GetAsync(context.Host, context.Port, CancellationToken.None)
            .GetAwaiter().GetResult();

        return new SshHostKeyVerificationContext
        {
            Host = context.Host,
            Port = context.Port,
            KeyAlgorithm = context.KeyAlgorithm,
            Fingerprint = context.Fingerprint,
            KnownFingerprint = known?.Fingerprint,
        };
    }

    public async Task<bool> ConfirmAndRememberAsync(
        SshHostKeyVerificationContext context, CancellationToken cancellationToken)
    {
        if (context.IsMismatch)
        {
            logger.LogWarning(
                "主机 {Host}:{Port} 的密钥指纹与记录不一致，已提示用户确认",
                context.Host, context.Port);
        }

        var accepted = await dialogs.ConfirmHostKeyAsync(context);

        if (!accepted)
        {
            logger.LogInformation("用户拒绝了 {Host}:{Port} 的主机密钥", context.Host, context.Port);
            return false;
        }

        await repository.SaveAsync(new SshHostKeyRecord
        {
            HostKey = SshHostKeyRecord.BuildHostKey(context.Host, context.Port),
            KeyAlgorithm = context.KeyAlgorithm,
            Fingerprint = context.Fingerprint,
            TrustedAt = DateTimeOffset.Now,
        }, cancellationToken);

        logger.LogInformation("已记录 {Host}:{Port} 的主机密钥指纹", context.Host, context.Port);
        return true;
    }
}
