using System.Collections.Concurrent;
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

    /// <summary>并发会话上限。达到上限后拒绝创建新会话，由 UI 给出明确提示。</summary>
    public int MaxConcurrentSessions { get; set; } = 20;

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

    /// <summary>当前全部活动会话。</summary>
    public IReadOnlyList<IRemoteSession> ActiveSessions => _sessions.Values.Select(e => e.Session).ToList();

    /// <summary>会话被创建时触发，供 UI 新建 Tab。</summary>
    public event EventHandler<IRemoteSession>? SessionCreated;

    /// <summary>会话被关闭时触发，供 UI 移除 Tab。</summary>
    public event EventHandler<Guid>? SessionClosed;

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
        return session;
    }

    /// <summary>关闭并释放指定会话。可重复调用。</summary>
    public async Task CloseSessionAsync(Guid sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var entry))
        {
            return;
        }

        entry.Session.StateChanged -= OnSessionStateChanged;

        try
        {
            await entry.Session.DisconnectAsync();
        }
        catch (Exception ex)
        {
            // 断开阶段的异常不应阻止资源释放，记录后继续。
            _logger.LogWarning(ex, "关闭会话 {SessionId} 时断开连接失败", sessionId);
        }

        try
        {
            await entry.Session.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "释放会话 {SessionId} 资源时失败", sessionId);
        }

        await CompleteHistoryAsync(entry, ConnectionResult.Success, ConnectionErrorCode.None);

        _logger.LogInformation("已关闭会话 {SessionId}", sessionId);
        SessionClosed?.Invoke(this, sessionId);
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

    private async Task StartHistoryAsync(SessionEntry entry, IRemoteSession session)
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
        // 每个会话只补写一次结束记录，避免「失败后关闭 Tab」被记成两条。
        if (entry.HistoryId is not { } historyId || entry.HistoryCompleted)
        {
            return;
        }

        entry.HistoryCompleted = true;
        await _history.CompleteAsync(historyId, DateTimeOffset.Now, result, errorCode);
    }

    public async ValueTask DisposeAsync() => await CloseAllAsync();

    /// <summary>会话及其历史记录状态。</summary>
    private sealed class SessionEntry(IRemoteSession session)
    {
        public IRemoteSession Session { get; } = session;

        /// <summary>对应的历史记录 Id。会话进入 Connecting 后才产生。</summary>
        public Guid? HistoryId { get; set; }

        /// <summary>结束记录是否已补写，防止重复写入。</summary>
        public bool HistoryCompleted { get; set; }
    }
}
