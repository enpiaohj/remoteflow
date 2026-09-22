using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.Presentation.Services;

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
/// <see cref="Lookup"/> 在 SSH.NET 触发 <c>HostKeyReceived</c> 的那个调用线程上跑，
/// 只查库、不弹 UI；<see cref="ConfirmAndRememberAsync"/> 在握手中止后的异步流程里
/// 弹窗，避免在握手回调里阻塞等待用户（会话超时会先到，把弹窗结果吞掉）。
/// <b>调用线程不保证是无 SynchronizationContext 的线程池线程</b>——SSH 会话的连接
/// 发起方（例如 UI 线程的 async void 事件处理器）在某些库版本/路径下可能就是
/// 触发该事件的线程，因此 <see cref="Lookup"/> 内部用 <c>Task.Run</c> 隔离 DB 查询，
/// 不直接在调用线程上做 sync-over-async。
/// </para>
/// </summary>
public sealed class InteractiveSshHostKeyPolicy(
    IHostKeyRepository repository,
    IDialogService dialogs,
    ILogger<InteractiveSshHostKeyPolicy> logger) : ISshHostKeyPolicy
{
    public SshHostKeyVerificationContext Lookup(SshHostKeyVerificationContext context)
    {
        // SSH.NET 触发 HostKeyReceived 的调用线程不受我们控制——不同版本/不同调用路径
        // （例如从 UI 线程的 async void 事件处理器里发起 ConnectAsync）都可能导致这里
        // 实际跑在持有 SynchronizationContext 的线程上。用 Task.Run 把 DB 查询丢到线程池、
        // 在纯线程池上下文里 GetResult，无论调用线程是谁都不会因为等待自己而死锁。
        var threadId = Environment.CurrentManagedThreadId;
        var hasSyncContext = SynchronizationContext.Current is not null;

        var known = Task.Run(() =>
                repository.GetAsync(context.Host, context.Port, CancellationToken.None))
            .GetAwaiter().GetResult();

        logger.LogDebug(
            "SSH Host Key Lookup {Host}:{Port} 调用线程={ThreadId} 有SyncContext={HasSyncContext} " +
            "已知指纹={KnownFingerprint} 本次指纹={Fingerprint}",
            context.Host, context.Port, threadId, hasSyncContext, known?.Fingerprint, context.Fingerprint);

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
        logger.LogInformation(
            "SSH Host Key 待确认 {Host}:{Port} IsMismatch={IsMismatch} 调用线程={ThreadId}",
            context.Host, context.Port, context.IsMismatch, Environment.CurrentManagedThreadId);

        if (context.IsMismatch)
        {
            logger.LogWarning(
                "主机 {Host}:{Port} 的密钥指纹与记录不一致，已提示用户确认",
                context.Host, context.Port);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var accepted = await dialogs.ConfirmHostKeyAsync(context);
        stopwatch.Stop();

        // 耗时是关键诊断信号：明显短于宽限期（400ms）就返回，通常意味着弹窗是被残留输入
        // 误关的，不是用户真的看清楚做了选择——而不是把这类情况和真实拒绝混为一谈。
        logger.LogInformation(
            "SSH Host Key 确认框返回 {Host}:{Port} Accepted={Accepted} 耗时={ElapsedMs}ms",
            context.Host, context.Port, accepted, stopwatch.ElapsedMilliseconds);

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
