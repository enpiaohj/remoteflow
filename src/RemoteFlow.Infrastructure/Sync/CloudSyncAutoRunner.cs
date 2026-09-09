using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Cloud;

namespace RemoteFlow.Infrastructure.Sync;

/// <summary>
/// 应用启动时恢复云会话，并在 Vault 解锁时周期性执行同步循环。
/// 组合根构建容器后 <see cref="Start"/>，退出前 <see cref="Dispose"/>。
/// </summary>
public sealed class CloudSyncAutoRunner(ICloudSyncService sync, ILogger<CloudSyncAutoRunner> logger) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _disposed;

    public void Start(TimeSpan interval)
    {
        _loop ??= Task.Run(() => RunAsync(interval, _cts.Token));
    }

    private async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            await sync.TryResumeAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "启动时恢复云会话失败");
        }

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!sync.IsVaultUnlocked)
                {
                    continue;
                }

                try
                {
                    await sync.SyncNowAsync(ct);
                }
                catch (CloudAuthRequiredException)
                {
                    // 会话失效——等用户在设置面板重新登录。
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "后台同步失败，下个周期重试");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出。
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略。
        }

        _cts.Dispose();
    }
}
