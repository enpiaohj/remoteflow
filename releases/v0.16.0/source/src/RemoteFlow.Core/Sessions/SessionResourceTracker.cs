namespace RemoteFlow.Core.Sessions;

/// <summary>
/// 每个 Session 的资源登记表：后台 Task / CTS / 一次性释放物 / 事件退订回调。
/// <para>
/// 会话把需要保证清理的非确定性资源登记进来，关闭/释放时统一收敛：
/// 取消 CTS → 退订回调 → Dispose 一次性资源。整体幂等，可重复调用。
/// 不强制所有资源都登记——关键后台任务 / Timer / 事件订阅必须先登记。
/// </para>
/// <para>
/// 不依赖 ILogger：需要告警时由调用方注入 <see cref="Action{T}"/> 回调（默认空）。
/// </para>
/// </summary>
public sealed class SessionResourceTracker
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly Action<string>? _warn;
    private bool _disposed;

    private readonly struct Entry(
        string name,
        Task? task,
        CancellationTokenSource? cts,
        IDisposable? disposable,
        Action? unsubscribe)
    {
        public string Name { get; } = name;
        public Task? Task { get; } = task;
        public CancellationTokenSource? Cts { get; } = cts;
        public IDisposable? Disposable { get; } = disposable;
        public Action? Unsubscribe { get; } = unsubscribe;
    }

    /// <param name="warn">可选告警回调（默认空），仅用于登记/释放阶段的异常提示，不得抛异常。</param>
    public SessionResourceTracker(Action<string>? warn = null) => _warn = warn;

    /// <summary>登记一个需要随会话清理的资源。各参数可空，按需填写。</summary>
    public void Track(
        string name,
        Task? task = null,
        CancellationTokenSource? cts = null,
        IDisposable? disposable = null,
        Action? unsubscribe = null)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                SafeWarn($"SessionResourceTracker 已释放后仍登记资源「{name}」，已忽略（编程错误）。");
                return;
            }

            _entries.Add(new Entry(name, task, cts, disposable, unsubscribe));
        }
    }

    /// <summary>取消全部 CTS → 执行退订回调 → 释放一次性资源 → Dispose CTS。幂等。</summary>
    public void DisposeAll()
    {
        Entry[] snapshot;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            snapshot = [.. _entries];
            _entries.Clear();
        }

        foreach (var entry in snapshot)
        {
            try
            {
                entry.Cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // CTS 已释放属预期，忽略。
            }
            catch (Exception ex)
            {
                SafeWarn($"取消资源「{entry.Name}」失败：{ex.Message}");
            }

            try
            {
                entry.Unsubscribe?.Invoke();
            }
            catch (Exception ex)
            {
                SafeWarn($"退订资源「{entry.Name}」失败：{ex.Message}");
            }

            try
            {
                entry.Disposable?.Dispose();
            }
            catch (Exception ex)
            {
                SafeWarn($"释放资源「{entry.Name}」失败：{ex.Message}");
            }

            try
            {
                entry.Cts?.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // CTS 已释放属预期，忽略。
            }
            catch (Exception ex)
            {
                SafeWarn($"释放 CTS「{entry.Name}」失败：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 等待已登记的后台任务收敛。超时返回是否仍有任务在运行（true = 仍在运行 / 超时）。
    /// <para>建议在 DisposeAll 之前调用：先取消 CTS（发出停止信号），再有界等待，最后整体释放。</para>
    /// </summary>
    public async Task<bool> WaitAllAsync(TimeSpan timeout)
    {
        Task[] tasks;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            tasks = _entries
                .Where(e => e.Task is not null)
                .Select(e => e.Task!)
                .ToArray();
        }

        if (tasks.Length == 0)
        {
            return false;
        }

        if (timeout < TimeSpan.Zero)
        {
            timeout = TimeSpan.Zero;
        }

        var all = Task.WhenAll(tasks);
        var winner = await Task.WhenAny(all, Task.Delay(timeout)).ConfigureAwait(false);
        if (!ReferenceEquals(winner, all))
        {
            // 超时：仍有任务在运行。
            return true;
        }

        // 全部结束：吞掉任务异常（清理阶段取消/失败属预期），仅告警提示。
        try
        {
            await all.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SafeWarn($"后台任务结束但带异常：{ex.Message}");
        }

        return false;
    }

    /// <summary>输出当前仍未释放的登记资源名称（DEBUG / 观察用）。</summary>
    public string Dump()
    {
        lock (_gate)
        {
            return string.Join("; ", _entries.Select(e => e.Name));
        }
    }

    /// <summary>当前仍未释放的登记资源数量。</summary>
    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    private void SafeWarn(string message)
    {
        try
        {
            _warn?.Invoke(message);
        }
        catch
        {
            // 告警回调自身不得影响清理流程。
        }
    }
}
