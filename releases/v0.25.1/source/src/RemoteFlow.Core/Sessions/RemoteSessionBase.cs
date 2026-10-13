using RemoteFlow.Core.Models;

namespace RemoteFlow.Core.Sessions;

/// <summary>
/// 会话生命周期基类（平台无关）：线程安全状态机 + 幂等关闭守卫 + 会话级 CTS + 资源登记。
/// <para>
/// 只承载三协议共有的生命周期逻辑；协议特有内容由子类提供：
/// <see cref="Protocol"/> / <see cref="Profile"/> / <see cref="ConnectAsync(CancellationToken)"/> /
/// <see cref="PerformTeardownAsync"/>。
/// </para>
/// <para>
/// 关闭语义：<see cref="DisconnectAsync"/> 是幂等关闭模板，只推进到
/// <see cref="ConnectionState.Disconnected"/>；最终 <see cref="ConnectionState.Closed"/>（终态）
/// 由 SessionManager 从活动集合移除后调用 <see cref="MarkClosed"/> 完成。会话单次使用，
/// 已 <c>Disconnected</c> / <c>Closed</c> 后不允许再回到 <c>Connected</c>。
/// </para>
/// <remarks>
/// 命名说明：任务评审允许「组合 helper」路线；为保持与下游任务（SessionManager Fake 会话 /
/// 三协议迁移）一致，本类直接实现 <see cref="IRemoteSession"/> 并留协议抽象成员给子类。
/// RDP / SSH / VNC 三协议会话尚未迁移到本基类（后续任务进行），届时可继承，也可仅组合复用
/// 其中的状态机与守卫逻辑。
/// </remarks>
public abstract class RemoteSessionBase : IRemoteSession
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifecycleCts = new();
    private readonly SessionResourceTracker _tracker;
    private ConnectionState _state = ConnectionState.Idle;
    private bool _closing;
    private bool _disposed;

    protected RemoteSessionBase()
        : this(null)
    {
    }

    /// <param name="warn">可选告警回调（默认空），透传给 <see cref="Tracker"/>，不依赖 ILogger。</param>
    protected RemoteSessionBase(Action<string>? warn)
    {
        _tracker = new SessionResourceTracker(warn);
    }

    public Guid SessionId { get; } = Guid.NewGuid();

    /// <summary>本会话的协议类型。子类返回固定值。</summary>
    public abstract ProtocolType Protocol { get; }

    /// <summary>本会话对应的连接配置快照。子类返回其持有的 Profile。</summary>
    public abstract ConnectionProfile Profile { get; }

    /// <summary>当前连接状态（线程安全读取）。</summary>
    public ConnectionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public ConnectionErrorCode ErrorCode { get; protected set; } = ConnectionErrorCode.None;

    public string? ErrorMessage { get; protected set; }

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 是否已进入关闭 / 收尾流程。首次 <see cref="DisconnectAsync"/> 或 <see cref="MarkClosed"/> 后为 true。
    /// SessionManager 用它防止对同一会话重复发起关闭。
    /// </summary>
    public bool IsClosing
    {
        get
        {
            lock (_gate)
            {
                return _closing;
            }
        }
    }

    /// <summary>会话级 CTS：供 Connect 取消 + Close 触发共用。</summary>
    protected CancellationTokenSource LifecycleCts => _lifecycleCts;

    /// <summary>会话级取消令牌。后台任务应观察它以便关闭时立刻中断。</summary>
    protected CancellationToken LifecycleToken => _lifecycleCts.Token;

    /// <summary>本会话的资源登记表。</summary>
    public SessionResourceTracker Tracker => _tracker;

    /// <summary>
    /// 建立连接（子类实现）。应在真正开始时 <see cref="SetState"/> 到 Connecting，
    /// 成功进入 Connected；失败经 ErrorCode / ErrorMessage + SetState(Failed)。
    /// </summary>
    public abstract Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 幂等关闭模板：Disconnecting → 取消本地 CTS → <see cref="PerformTeardownAsync"/> → Disconnected。
    /// <para>仅首次真正执行；重复调用 / 已 Closed / 已 Disconnected 直接返回。virtual，子类可覆盖或组合调用。</para>
    /// </summary>
    public virtual async Task DisconnectAsync()
    {
        bool first;
        lock (_gate)
        {
            first = !_closing && _state is not (ConnectionState.Closed or ConnectionState.Disconnected);
            if (first)
            {
                _closing = true;
            }
        }

        if (!first)
        {
            return;
        }

        SetState(ConnectionState.Disconnecting);

        try
        {
            try
            {
                _lifecycleCts.Cancel();
            }
            catch
            {
                // 取消回调若抛异常，Cancel() 会抛 AggregateException；不得让 teardown 被阻断。
            }

            await PerformTeardownAsync().ConfigureAwait(false);
        }
        finally
        {
            // 无论 Cancel / teardown 是否成功都必须离开 Disconnecting 死态；若期间已被
            // MarkClosed 置为 Closed，此处 SetState(Disconnected) 会被终态规则忽略，状态保持 Closed。
            SetState(ConnectionState.Disconnected);
        }
    }

    /// <summary>
    /// 幂等收尾：将会话置为 <see cref="ConnectionState.Closed"/>（终态）并标记关闭中。
    /// <para>
    /// 约定在会话已 teardown 到 <see cref="ConnectionState.Disconnected"/> 后由 SessionManager
    /// 将其从活动集合移除时调用；非终态下调用即进入 Closed，重复调用无副作用。
    /// </para>
    /// </summary>
    public void MarkClosed()
    {
        lock (_gate)
        {
            _closing = true;
        }

        SetState(ConnectionState.Closed);
    }

    /// <summary>
    /// 幂等释放：等价于「若未关闭则先执行关闭模板，再释放会话 CTS 与 Tracker 登记的全部资源」。
    /// 防重入。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        bool proceed;
        lock (_gate)
        {
            proceed = !_disposed;
            if (proceed)
            {
                _disposed = true;
            }
        }

        if (!proceed)
        {
            return;
        }

        try
        {
            // DisconnectAsync 内部已保证离开 Disconnecting 死态；若其抛出，仍要在 finally 中释放自有资源。
            await DisconnectAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _lifecycleCts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // CTS 已释放属预期，忽略。
            }

            _tracker.DisposeAll();
        }
    }

    /// <summary>
    /// 子类实现真正的 teardown：协议断开 → 释放流/句柄 → 退订事件 → 释放 provider / 凭据资源。
    /// 必须幂等（可在清理阶段被多次调用或与 DisposeAsync 并发到达）。
    /// </summary>
    protected abstract ValueTask PerformTeardownAsync();

    /// <summary>
    /// 线程安全状态变更：符合状态机迁移规则才生效并触发 <see cref="StateChanged"/>；
    /// 非法跳转被静默忽略。仅在状态真实变化时触发事件，事件参数携带当前 ErrorCode / ErrorMessage。
    /// </summary>
    protected void SetState(ConnectionState next)
    {
        ConnectionState old;
        bool changed;
        lock (_gate)
        {
            old = _state;
            if (old == next || !IsTransitionAllowed(old, next))
            {
                return;
            }

            _state = next;
            changed = true;
        }

        if (changed)
        {
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(old, next, ErrorCode, ErrorMessage));
        }
    }

    /// <summary>
    /// 状态机迁移规则：Closed 为终态不再变化；Disconnected 为单次使用尾部（只能进 Closed）；
    /// Disconnecting 为退出态不允许回退到活动态；其余常规推进方向合法。
    /// </summary>
    private static bool IsTransitionAllowed(ConnectionState current, ConnectionState next)
    {
        return next switch
        {
            // 任何非终态都可被强制置 Closed（MarkClosed / 统一收尾）。
            ConnectionState.Closed => current is not ConnectionState.Closed,
            // 不允许回退到 Idle。
            ConnectionState.Idle => false,
            ConnectionState.Connecting => current == ConnectionState.Idle,
            ConnectionState.Connected => current is ConnectionState.Connecting or ConnectionState.Reconnecting,
            ConnectionState.Reconnecting => current == ConnectionState.Connected,
            ConnectionState.Disconnecting => current is
                ConnectionState.Idle or ConnectionState.Connecting or ConnectionState.Connected
                or ConnectionState.Reconnecting or ConnectionState.Failed,
            ConnectionState.Disconnected => current is
                ConnectionState.Idle or ConnectionState.Connecting or ConnectionState.Connected
                or ConnectionState.Reconnecting or ConnectionState.Disconnecting or ConnectionState.Failed,
            ConnectionState.Failed => current is
                ConnectionState.Idle or ConnectionState.Connecting or ConnectionState.Connected
                or ConnectionState.Reconnecting or ConnectionState.Disconnecting,
            _ => false,
        };
    }
}
