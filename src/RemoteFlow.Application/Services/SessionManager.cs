using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.Application.Services;

/// <summary>
/// 会话管理器。统一管理所有远程会话的生命周期，是多 Tab 能力的核心。
/// <para>
/// 职责边界：
/// <list type="bullet">
///   <item>按协议选择 Provider，创建会话实例。</item>
///   <item>在连接前的最后一刻解析凭据，并把其生命周期交给会话。</item>
///   <item>自动记录连接历史（开始 / 结束 / 标准化失败原因）。</item>
///   <item>限制并发会话数，防止异常情况下无限创建重复连接。</item>
///   <item>关闭会话时确保协议资源被释放。</item>
/// </list>
/// 本类不负责 UI 布局，也不决定 Tab 的呈现方式。
/// </para>
/// </summary>
public sealed class SessionManager : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<ProtocolType, IConnectionProvider> _providers;
    private readonly CredentialService _credentials;
    private readonly IConnectionRepository _connections;
    private readonly IHistoryRepository _history;
    private readonly ISshHostKeyPolicy _hostKeyPolicy;
    private readonly ILogger<SessionManager> _logger;

    private readonly ConcurrentDictionary<Guid, SessionEntry> _sessions = new();

    /// <summary>单个会话关闭流程（断开 + 释放）的总时限。超时不抛、只告警并 best-effort 强制收尾。</summary>
    private static readonly TimeSpan DefaultCloseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>并发会话上限。达到上限后拒绝创建新会话，由 UI 给出明确提示。</summary>
    public int MaxConcurrentSessions { get; set; } = 20;

    /// <summary>单会话关闭总时限（spec §4 约 5s）。测试可调小以便回归。</summary>
    public TimeSpan CloseTimeout { get; set; } = DefaultCloseTimeout;

    public SessionManager(
        IEnumerable<IConnectionProvider> providers,
        CredentialService credentials,
        IConnectionRepository connections,
        IHistoryRepository history,
        ISshHostKeyPolicy hostKeyPolicy,
        ILogger<SessionManager> logger)
    {
        _providers = providers.ToDictionary(p => p.Protocol);
        _credentials = credentials;
        _connections = connections;
        _history = history;
        _hostKeyPolicy = hostKeyPolicy;
        _logger = logger;
    }

    /// <summary>当前活动会话数量。</summary>
    public int ActiveSessionCount => _sessions.Count;

    /// <summary>当前已连接会话数量（仅统计 State == Connected 的会话，不含正在连接 / 失败 / 已关闭）。</summary>
    public int ConnectedSessionCount => _sessions.Values.Count(e => e.Session.State == ConnectionState.Connected);

    /// <summary>当前全部活动会话。</summary>
    public IReadOnlyList<IRemoteSession> ActiveSessions => _sessions.Values.Select(e => e.Session).ToList();

    /// <summary>指定 Profile 是否存在于活动集合（含 Connecting / Connected / Failed 尚未移除）。纯内存派生。</summary>
    public bool HasActiveSession(Guid connectionProfileId)
        => _sessions.Values.Any(e => e.Session.Profile.Id == connectionProfileId);

    /// <summary>指定 Profile 是否存在 <see cref="ConnectionState.Connected"/> 的会话。纯内存派生。</summary>
    public bool HasConnectedSession(Guid connectionProfileId)
        => _sessions.Values.Any(e => e.Session.State == ConnectionState.Connected
                                     && e.Session.Profile.Id == connectionProfileId);

    /// <summary>
    /// 取指定 Profile 的“最佳”会话状态。优先级：Connected &gt; (Connecting | Reconnecting)
    /// &gt; Failed &gt; 其它（Idle / Disconnecting / Disconnected）。无会话返回 <see langword="null"/>，
    /// 以区分「无会话」与「存在 Idle 会话」。同一 Profile 存在多个活动会话时返回状态优先级最高的那个状态。
    /// </summary>
    public ConnectionState? GetSessionState(Guid connectionProfileId)
    {
        ConnectionState? best = null;
        var bestRank = -1;
        foreach (var session in _sessions.Values.Select(e => e.Session))
        {
            if (session.Profile.Id != connectionProfileId)
            {
                continue;
            }

            var rank = RankForBestState(session.State);
            if (rank > bestRank)
            {
                bestRank = rank;
                best = session.State;
            }
        }

        return best;
    }

    /// <summary>状态聚合排序：值越大代表对用户越“关键 / 越接近已连接”。</summary>
    private static int RankForBestState(ConnectionState state) => state switch
    {
        ConnectionState.Connected => 4,
        ConnectionState.Connecting or ConnectionState.Reconnecting => 3,
        ConnectionState.Failed => 2,
        // Idle / Disconnecting / Disconnected（Closed 为终态，正常不会存在于活动集合）。
        _ => 1,
    };

    /// <summary>会话被创建时触发，供 UI 新建 Tab。</summary>
    public event EventHandler<IRemoteSession>? SessionCreated;

    /// <summary>会话被关闭时触发，供 UI 移除 Tab。</summary>
    public event EventHandler<Guid>? SessionClosed;

    /// <summary>
    /// 会话集合快照失效 / 需重算的信号：会话创建、任意状态跳变、会话移除后都会触发
    /// （会话成员增减与会话状态变化都可能引发）。无负载：消费者订阅后自行调用
    /// <see cref="HasActiveSession"/> / <see cref="HasConnectedSession"/> /
    /// <see cref="GetSessionState"/> 等快照查询重算 UI。
    /// 事件可在任意线程触发（状态来自协议后台线程），消费者需自行 marshal 到 UI 线程。
    /// </summary>
    public event EventHandler? SessionsChanged;

    /// <summary>
    /// 每条会话状态跳变经聚合后转发，携带会话身份与所属 Profile 身份
    /// （<see cref="SessionStateChangedAggregatedEventArgs.SessionId"/> /
    /// <see cref="SessionStateChangedAggregatedEventArgs.ConnectionProfileId"/> /
    /// OldState / NewState / ErrorCode / ErrorMessage）。
    /// 会话创建 / 移除本身不触发本事件（分别由 <see cref="SessionCreated"/> /
    /// <see cref="SessionClosed"/> 与 <see cref="SessionsChanged"/> 覆盖）；
    /// 关闭流程期间已退订该会话 <see cref="IRemoteSession.StateChanged"/>，不再转发其收尾跳变。
    /// 事件可在任意线程触发，消费者需自行 marshal 到 UI 线程。
    /// </summary>
    public event EventHandler<SessionStateChangedAggregatedEventArgs>? SessionStateChanged;

    /// <summary>查询指定协议的 Provider 是否可用。</summary>
    public bool IsProtocolAvailable(ProtocolType protocol, out string? reason)
    {
        if (!_providers.TryGetValue(protocol, out var provider))
        {
            reason = $"未注册 {protocol} 协议的连接实现。";
            return false;
        }

        return provider.IsAvailable(out reason);
    }

    /// <summary>
    /// 创建一个尚未连接的会话。
    /// <para>
    /// 刻意不在此处发起连接：RDP / VNC 的宿主控件必须先进入可视树、取得窗口句柄，
    /// 才能安全地开始连接。因此由 UI 在 Tab 就绪后调用
    /// <see cref="IRemoteSession.ConnectAsync"/>，本类通过状态事件接管历史记录。
    /// </para>
    /// </summary>
    public async Task<IRemoteSession> CreateSessionAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        if (_sessions.Count >= MaxConcurrentSessions)
        {
            throw new InvalidOperationException(
                $"已达到并发会话上限（{MaxConcurrentSessions} 个），请先关闭部分会话再新建连接。");
        }

        if (!_providers.TryGetValue(profile.Protocol, out var provider))
        {
            throw ConnectionException.FromCode(ConnectionErrorCode.ComponentUnavailable);
        }

        if (!provider.IsAvailable(out var unavailableReason))
        {
            throw new ConnectionException(
                ConnectionErrorCode.ComponentUnavailable,
                unavailableReason ?? ConnectionException.Describe(ConnectionErrorCode.ComponentUnavailable));
        }

        // 凭据在此刻才解析，且只在会话存续期间持有。
        ResolvedCredential? credential = null;
        if (profile.CredentialId is { } credentialId)
        {
            credential = await _credentials.ResolveAsync(credentialId, ct)
                ?? throw ConnectionException.FromCode(ConnectionErrorCode.CredentialMissing);
        }

        IRemoteSession session;
        try
        {
            session = provider.CreateSession(new SessionRequest
            {
                Profile = profile,
                Credential = credential,
                HostKeyPolicy = _hostKeyPolicy
            });
        }
        catch
        {
            // 会话创建失败时凭据不会被会话接管，必须由这里负责释放。
            credential?.Dispose();
            throw;
        }

        var entry = new SessionEntry(session);
        _sessions[session.SessionId] = entry;

        session.StateChanged += OnSessionStateChanged;

        _logger.LogInformation(
            "已创建会话 {SessionId}：{ConnectionName} ({Protocol} {Host}:{Port})",
            session.SessionId, profile.Name, profile.Protocol, profile.Host, profile.Port);

        SessionCreated?.Invoke(this, session);
        RaiseSessionsChanged();
        return session;
    }

    /// <summary>关闭并释放指定会话。可重复调用。</summary>
    public async Task CloseSessionAsync(Guid sessionId)
    {
        // 幂等守卫：条目不存在，或已有一条关闭流程在途（并发 / 重复调用）→ 直接返回。
        if (!_sessions.TryGetValue(sessionId, out var entry) || !entry.TryClaimClose())
        {
            return;
        }

        var session = entry.Session;

        // 关闭期间保持对该会话状态变化的观察：Disconnecting/Disconnected 等跳变仍会经
        // OnSessionStateChanged 广播（让 UI 的计数与「已连接」在会话移除前即回落）；
        // 但 Closing 会话的历史补写被跳过，统一由下方 CompleteHistoryAsync 原子单写，
        // 避免与状态事件的后台补写竞争或重复写。

        var timeout = CloseTimeout;
        var sw = Stopwatch.StartNew();

        // 统一关闭模板（spec §4）：先断开（Disconnecting→Cancel→teardown→Disconnected），
        // 再释放会话自有资源（协议句柄 / CTS / Tracker）；两阶段共享同一总时限。
        await DisconnectSessionAsync(session, sessionId, timeout, sw);
        await DisposeSessionAsync(session, sessionId, timeout, sw);

        try
        {
            // 结束历史只补写一次；此前若已因 Failed/Disconnected 补写则去重跳过。
            // 结果按会话终态判定：最终停在 Failed（错误码 Cancelled → Cancelled，其余 → Failed）记失败，
            // 避免「取消 / 失败被记成成功」；正常关闭（teardown 后 Disconnected）记 Success。
            var (result, errorCode) = ResolveTerminalOutcome(session);
            await CompleteHistoryAsync(entry, result, errorCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "补写会话 {SessionId} 结束历史失败", sessionId);
        }

        // 先移出活动集合，再置 Closed，并保证 SessionClosed 在 MarkClosed 之后触发。
        _sessions.TryRemove(sessionId, out _);

        try
        {
            session.MarkClosed();
        }
        catch (Exception ex)
        {
            // MarkClosed 默认空实现 / 终态幂等，正常不会抛；这里兜底防止阻断 SessionClosed。
            _logger.LogWarning(ex, "将会话 {SessionId} 标记 Closed 失败", sessionId);
        }

        _logger.LogInformation("已关闭会话 {SessionId}", sessionId);
        RaiseSessionsChanged();
        SessionClosed?.Invoke(this, sessionId);
    }

    /// <summary>有界等待：返回 <see langword="true"/> 表示 <paramref name="task"/> 在时限内完成。</summary>
    private static async Task<bool> WaitBoundedAsync(Task task, TimeSpan timeout)
    {
        var winner = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        return ReferenceEquals(winner, task);
    }

    private static TimeSpan RemainingTimeout(TimeSpan total, Stopwatch sw)
    {
        var remaining = total - sw.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private async Task DisconnectSessionAsync(IRemoteSession session, Guid sessionId, TimeSpan totalTimeout, Stopwatch sw)
    {
        Task disconnect;
        try
        {
            disconnect = session.DisconnectAsync();
        }
        catch (Exception ex)
        {
            // 同步启动即失败（非 async 实现的同步抛异常）也按可继续强制释放处理。
            _logger.LogWarning(ex, "启动断开会话 {SessionId} 失败，继续释放资源。", sessionId);
            return;
        }

        try
        {
            if (!await WaitBoundedAsync(disconnect, RemainingTimeout(totalTimeout, sw)).ConfigureAwait(false))
            {
                _logger.LogWarning("关闭会话 {SessionId}：断开超过时限未完成，转为 best-effort 强制释放。", sessionId);
                return;
            }

            await disconnect.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 断开阶段的异常不应阻止资源释放，记录后继续。
            _logger.LogWarning(ex, "断开会话 {SessionId} 失败，继续释放资源。", sessionId);
        }
    }

    private async Task DisposeSessionAsync(IRemoteSession session, Guid sessionId, TimeSpan totalTimeout, Stopwatch sw)
    {
        Task dispose;
        try
        {
            dispose = session.DisposeAsync().AsTask();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "启动释放会话 {SessionId} 失败。", sessionId);
            return;
        }

        try
        {
            if (!await WaitBoundedAsync(dispose, RemainingTimeout(totalTimeout, sw)).ConfigureAwait(false))
            {
                _logger.LogWarning("关闭会话 {SessionId}：释放超过时限未完成，放弃等待（best-effort）。", sessionId);
                return;
            }

            await dispose.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "释放会话 {SessionId} 资源时失败。", sessionId);
        }
    }

    /// <summary>关闭全部会话。应用退出时调用，确保协议资源被完整释放。</summary>
    public async Task CloseAllAsync()
    {
        foreach (var sessionId in _sessions.Keys.ToList())
        {
            await CloseSessionAsync(sessionId);
        }
    }

    // ── 历史记录 ──────────────────────────────────────────────────

    private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        if (sender is not IRemoteSession session || !_sessions.TryGetValue(session.SessionId, out var entry))
        {
            return;
        }

        // 先做无负载的聚合转发（同步，保证状态跳变立即被 UI 观察到）。
        RaiseSessionStateChanged(new SessionStateChangedAggregatedEventArgs(
            session.SessionId,
            session.Profile.Id,
            e.OldState,
            e.NewState,
            e.ErrorCode,
            e.ErrorMessage));
        RaiseSessionsChanged();

        // 关闭编排期（Closing）：状态跳变仍广播（Disconnecting 即让 UI 回落），但历史补写
        // 由关闭路径 CompleteHistoryAsync 原子单写，这里跳过，避免与关闭路径竞争或重复写。
        if (entry.Closing)
        {
            return;
        }

        // 事件可能来自协议库的后台线程，历史写入放到线程池执行，避免阻塞协议回调。
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleStateChangeAsync(entry, session, e);
            }
            catch (Exception ex)
            {
                // 历史记录失败不能影响会话本身。
                _logger.LogError(ex, "处理会话 {SessionId} 状态变化时失败", session.SessionId);
            }
        });
    }

    /// <summary>触发 <see cref="SessionsChanged"/>（会话集合变化后广播一次，覆盖 UI 重算）。</summary>
    private void RaiseSessionsChanged() => SessionsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>转发一条聚合的会话状态跳变（<see cref="SessionStateChanged"/>）。</summary>
    private void RaiseSessionStateChanged(SessionStateChangedAggregatedEventArgs args) => SessionStateChanged?.Invoke(this, args);

    private async Task HandleStateChangeAsync(SessionEntry entry, IRemoteSession session, SessionStateChangedEventArgs e)
    {
        switch (e.NewState)
        {
            case ConnectionState.Connecting when entry.HistoryId is null:
                await StartHistoryAsync(entry, session);
                break;

            case ConnectionState.Connected:
                await _connections.TouchLastConnectedAsync(session.Profile.Id, DateTimeOffset.Now);
                break;

            case ConnectionState.Failed:
                await CompleteHistoryAsync(
                    entry,
                    e.ErrorCode == ConnectionErrorCode.Cancelled ? ConnectionResult.Cancelled : ConnectionResult.Failed,
                    e.ErrorCode);

                _logger.LogWarning(
                    "会话 {SessionId} 连接失败：{Host} 协议 {Protocol} 错误码 {ErrorCode}",
                    session.SessionId, session.Profile.Host, session.Profile.Protocol, e.ErrorCode);
                break;

            case ConnectionState.Disconnected:
                await CompleteHistoryAsync(entry, ConnectionResult.Success, ConnectionErrorCode.None);
                break;
        }
    }

    /// <summary>
    /// 依据会话的终态（最后一次观察到的状态）决定关闭路径补写历史的结项结果。
    /// <para>关闭编排会先把会话 teardown 到 Disconnected（若此前不是 Failed），因此这里读取
    /// <see cref="IRemoteSession.State"/>：最终停在 Failed 则按 <see cref="IRemoteSession.ErrorCode"/>
    /// 记 Failed / Cancelled；其余（正常断开 / 从未连接成功）一律记 Success。</para>
    /// </summary>
    private static (ConnectionResult Result, ConnectionErrorCode ErrorCode) ResolveTerminalOutcome(IRemoteSession session)
    {
        if (session.State == ConnectionState.Failed)
        {
            var code = session.ErrorCode;
            return (code == ConnectionErrorCode.Cancelled ? ConnectionResult.Cancelled : ConnectionResult.Failed, code);
        }

        return (ConnectionResult.Success, ConnectionErrorCode.None);
    }

    /// <summary>
    /// 开始一条历史记录并回填 HistoryId。返回回填任务并登记到条目上：
    /// 供 <see cref="CompleteHistoryAsync"/> 在 HistoryId 尚未就绪时 await，避免竞态产生悬空行。
    /// </summary>
    private Task StartHistoryAsync(SessionEntry entry, IRemoteSession session)
    {
        var startTask = DoStartHistoryAsync(entry, session);
        entry.HistoryStartTask = startTask;
        return startTask;
    }

    private async Task DoStartHistoryAsync(SessionEntry entry, IRemoteSession session)
    {
        var historyEntry = new ConnectionHistoryEntry
        {
            ConnectionId = session.Profile.Id,
            ConnectionName = session.Profile.Name,
            Host = session.Profile.Host,
            Protocol = session.Profile.Protocol,
            StartedAt = DateTimeOffset.Now,
            Result = ConnectionResult.Failed,
            ErrorCode = ConnectionErrorCode.None
        };

        await _history.AddAsync(historyEntry);
        entry.HistoryId = historyEntry.Id;
    }

    private async Task CompleteHistoryAsync(SessionEntry entry, ConnectionResult result, ConnectionErrorCode errorCode)
    {
        // 开始回填（HistoryId）与结束补写是两条独立异步任务，可能交错：Connecting 的
        // StartHistoryAsync 尚未完成（AddAsync + 回填 Id）时，Failed / Disconnected / 关闭路径的
        // Complete 就已到达。若此时因 HistoryId 为空直接返回，会留下只有 StartedAt、没有 EndedAt
        // 的悬空历史行。同一会话的状态事件按序入队（ThreadPool 全局队列 FIFO），Connecting 处理器
        // 会在任何后续 Complete 前先把 HistoryStartTask 登记上，因此这里 await 它即可等到 HistoryId
        // 就绪。开始记录只会注册一次；await 已完成的任务立即返回。开始失败（AddAsync 抛）时行未写入，
        // 无需补写，悬空行不会产生。
        if (entry.HistoryId is null && entry.HistoryStartTask is { } startTask)
        {
            try
            {
                await startTask;
            }
            catch
            {
                return;
            }
        }

        // 每个会话只补写一次结束记录；用原子认领保证「失败后关闭 Tab」在 close 路径与后台补写并发下也只写一次。
        if (entry.HistoryId is not { } historyId || !entry.TryCompleteHistory())
        {
            return;
        }

        await _history.CompleteAsync(historyId, DateTimeOffset.Now, result, errorCode);
    }

    public async ValueTask DisposeAsync() => await CloseAllAsync();

    /// <summary>会话及其历史记录状态。</summary>
    private sealed class SessionEntry(IRemoteSession session)
    {
        public IRemoteSession Session { get; } = session;

        /// <summary>对应的历史记录 Id。会话进入 Connecting 后才产生。</summary>
        public Guid? HistoryId { get; set; }

        /// <summary>开始历史回填任务（AddAsync + HistoryId 赋值）。Complete 发现 HistoryId 为空时 await 它，消除悬空 StartedAt 行。</summary>
        public Task? HistoryStartTask { get; set; }

        private int _historyCompletionClaimed;
        private int _closeClaimed;
        private volatile bool _closing;

        /// <summary>原子认领一次「结束历史补写」；并发/重复补写只会有一个调用者成功。</summary>
        public bool TryCompleteHistory() => Interlocked.Exchange(ref _historyCompletionClaimed, 1) == 0;

        /// <summary>该会话是否已进入关闭编排期（<see cref="TryClaimClose"/> 成功后即置位）。
        /// 关闭期状态跳变仍会广播，但历史补写统一由关闭路径收口，<see cref="SessionManager"/> 将跳过。</summary>
        public bool Closing => _closing;

        /// <summary>原子认领一次关闭流程；同一条目并发 / 重复关闭只会有一个调用者成功，成功时置 <see cref="Closing"/>。</summary>
        public bool TryClaimClose()
        {
            if (Interlocked.Exchange(ref _closeClaimed, 1) != 0)
            {
                return false;
            }

            _closing = true;
            return true;
        }
    }
}

/// <summary>
/// <see cref="SessionManager.SessionStateChanged"/> 聚合事件参数（定义于 Application 层，
/// 名称与 <c>RemoteFlow.Core.Sessions.SessionStateChangedEventArgs</c> 区分，避免命名空间混用歧义）。
/// 携带会话身份（<see cref="SessionId"/>）与所属 Profile 身份（<see cref="ConnectionProfileId"/>），
/// 便于 UI 精确定位到具体连接资产 / Tab。
/// </summary>
public sealed class SessionStateChangedAggregatedEventArgs : EventArgs
{
    public SessionStateChangedAggregatedEventArgs(
        Guid sessionId,
        Guid connectionProfileId,
        ConnectionState oldState,
        ConnectionState newState,
        ConnectionErrorCode errorCode = ConnectionErrorCode.None,
        string? errorMessage = null)
    {
        SessionId = sessionId;
        ConnectionProfileId = connectionProfileId;
        OldState = oldState;
        NewState = newState;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    /// <summary>发生状态跳变的会话 Id。</summary>
    public Guid SessionId { get; }

    /// <summary>该会话对应的连接配置（Profile）Id。</summary>
    public Guid ConnectionProfileId { get; }

    public ConnectionState OldState { get; }

    public ConnectionState NewState { get; }

    public ConnectionErrorCode ErrorCode { get; }

    public string? ErrorMessage { get; }
}
