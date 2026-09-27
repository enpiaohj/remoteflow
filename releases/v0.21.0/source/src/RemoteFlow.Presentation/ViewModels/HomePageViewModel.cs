using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Infrastructure.Settings;

namespace RemoteFlow.Presentation.ViewModels;

/// <summary>
/// 首页。只解决一件事：让用户尽快回到工作状态。
/// <para>
/// 因此这里只呈现最近连接、收藏与最近活动，
/// 不做资产统计看板或复杂图表（产品设计文档 §7.6）。
/// </para>
/// </summary>
public sealed partial class HomePageViewModel : ObservableObject
{
    private readonly ConnectionService _connections;
    private readonly IHistoryRepository _history;
    private readonly SessionManager _sessions;
    private readonly AppSettings _settings;
    private readonly JsonSettingsStore _settingsStore;
    private readonly ILogger<HomePageViewModel> _logger;
    private readonly IUiDispatcher _ui;
    private readonly PresenceProbeService? _probe;
    private CancellationTokenSource? _probeCts;
    private int _probeVersion;

    /// <summary>会话状态去抖全量刷新的版本号：每次收到 <see cref="SessionManager.SessionsChanged"/> 自增，
    /// 延迟结束比对版本，期间再有新事件则放弃本次刷新，保证最终以最后一次状态为准。</summary>
    private int _reloadVersion;

    /// <summary>状态跳变后全量刷新的去抖窗口：合并连接中的连续跳变，避免连打 LoadAsync。</summary>
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(150);

    public HomePageViewModel(
        ConnectionService connections,
        IHistoryRepository history,
        SessionManager sessions,
        AppSettings settings,
        JsonSettingsStore settingsStore,
        IUiDispatcher uiDispatcher,
        ILogger<HomePageViewModel> logger,
        PresenceProbeService? probe = null)
    {
        _connections = connections;
        _ui = uiDispatcher;
        _probe = probe;
        _history = history;
        _sessions = sessions;
        _settings = settings;
        _settingsStore = settingsStore;
        _logger = logger;

        // 首页是 App 单例、常驻整个生命周期：构造时订阅一次聚合的「会话集合变化」，
        // 停留在首页时任一状态跳变（连接成功点亮 / 关闭熄灭 / 最近排序 / 最近活动）都能实时刷新，
        // 不再依赖切页重新 LoadAsync。事件可能在任意线程触发，OnSessionsChanged 内 marshal 回 UI。
        // 单例常驻、App 退出由容器整体释放，不提供 Dispose 退订——与 ConnectionsPageViewModel 等其它单例 VM 一致。
        _sessions.SessionsChanged += OnSessionsChanged;
    }

    /// <summary>收藏 / 最近活动分区最多展示的条目数，超出的到对应页面查看全部。</summary>
    private const int SectionLimit = 7;

    /// <summary>「最近连接」快捷卡片的数量。</summary>
    private const int RecentCardLimit = 3;

    /// <summary>请求主窗口切换到某个一级页面（「查看全部」等）。</summary>
    public event EventHandler<NavigationPage>? NavigationRequested;

    /// <summary>请求打开一个连接（首页卡片 / 收藏行双击等）。由 MainViewModel 统一开会话。</summary>
    public event EventHandler<ConnectionProfile>? OpenConnectionRequested;

    /// <summary>
    /// 首页「最近连接 / 收藏」行右键动作（连接 / 切换到会话 / 断开连接 / 编辑 / 测试连接 / 收藏 / 管理连接）。
    /// 真正的执行桥接到「我的连接」既有命令，避免在首页复制实现。
    /// </summary>
    public event EventHandler<HomeConnectionActionEventArgs>? ConnectionActionRequested;

    public ObservableCollection<ConnectionItemViewModel> RecentItems { get; } = [];

    public ObservableCollection<ConnectionItemViewModel> FavoriteItems { get; } = [];

    public ObservableCollection<HistoryItemViewModel> RecentHistory { get; } = [];

    [ObservableProperty]
    private int _totalConnections;

    /// <summary>当前已连接会话数（仅统计 Connected）。顶部统计行使用。</summary>
    public int ConnectedSessions => _sessions.ConnectedSessionCount;

    [ObservableProperty]
    private string _greeting = string.Empty;

    /// <summary>
    /// 首页标题下方的日期行，详略由「设置 → 首页时间行」决定。
    /// 例（完整）：<c>2026年9月8日 周一 · 第37周 · 22:30</c>。
    /// </summary>
    [ObservableProperty]
    private string _dateLine = string.Empty;

    /// <summary>日期行是否含时间。供视图决定是否启动秒级刷新（只有日期时无需刷新）。</summary>
    public bool ShowHomeClock => _settings.HomeDateLine != HomeDateLine.DateOnly;

    /// <summary>底部安全提示横幅是否可见（用户可关闭，选择记入设置）。</summary>
    [ObservableProperty]
    private bool _showSecurityTip;

    public bool HasRecent => RecentItems.Count > 0;

    public bool HasFavorites => FavoriteItems.Count > 0;

    public bool HasActivity => RecentHistory.Count > 0;

    public bool IsFirstRun => TotalConnections == 0;

    /// <summary>最近一次在线探测结果（按 Profile Id）。会话变化触发的刷新会重建行对象，据此恢复状态而不重新探测。</summary>
    private readonly Dictionary<Guid, (bool Online, DateTimeOffset ProbedAt)> _presenceCache = [];

    /// <summary>当前批次仍在探测中的 Profile Id：重建行时保持「探测中」，结果回来后按 Id 写入当前行。</summary>
    private readonly HashSet<Guid> _probingIds = [];

    /// <summary>页面导航 / 手动刷新：重建数据并重新探测在线状态。</summary>
    public Task LoadAsync(CancellationToken ct = default) => LoadCoreAsync(probe: true, ct);

    /// <summary>轻量刷新（如从会话标签切回首页）：重读列表与会话状态，在线状态沿用上次探测结果。</summary>
    public Task RefreshAsync(CancellationToken ct = default) => LoadCoreAsync(probe: false, ct);

    /// <param name="probe">
    /// true：导航进入首页，重新探测；false：停留首页时随会话状态变化刷新，只同步会话与列表，
    /// 在线状态沿用上次探测结果——否则连接一次（连接中 → 已连接）就会连发多轮 TCP 探测，卡片还会闪回「探测中」。
    /// </param>
    private async Task LoadCoreAsync(bool probe, CancellationToken ct)
    {
        var probeVersion = _probeVersion;
        if (probe)
        {
            _probeCts?.Cancel();
            _probingIds.Clear();
            probeVersion = ++_probeVersion;
        }

        Greeting = DateTime.Now.Hour switch
        {
            >= 5 and < 12 => "上午好",
            >= 12 and < 18 => "下午好",
            >= 18 and < 23 => "晚上好",
            _ => "夜深了"
        };

        RefreshHeader();

        var profiles = await _connections.GetAllAsync(ct);
        var groups = (await _connections.GetGroupsAsync(ct)).ToDictionary(g => g.Id, g => g.Name);

        TotalConnections = profiles.Count;
        ShowSecurityTip = !_settings.HomeSecurityTipDismissed && profiles.Count > 0;

        // 卡片状态以 SessionManager 的实时快照为准，不持久化。

        RecentItems.Clear();
        foreach (var profile in profiles
                     .Where(p => p.LastConnectedAt is not null)
                     .OrderByDescending(p => p.LastConnectedAt)
                     .Take(RecentCardLimit))
        {
            var item = BuildItem(profile, groups);
            ApplyRealtimeState(item);
            RecentItems.Add(item);
        }

        FavoriteItems.Clear();
        foreach (var profile in profiles
                     .Where(p => p.Favorite)
                     .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                     .Take(SectionLimit))
        {
            var item = BuildItem(profile, groups);
            ApplyRealtimeState(item);
            FavoriteItems.Add(item);
        }

        RecentHistory.Clear();
        foreach (var entry in await _history.GetRecentAsync(SectionLimit, ct))
        {
            RecentHistory.Add(new HistoryItemViewModel(entry));
        }

        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(HasFavorites));
        OnPropertyChanged(nameof(HasActivity));
        OnPropertyChanged(nameof(IsFirstRun));

        // 返回首页时重算统计行：卡片会话状态读实时快照，这里显式通知计数刷新，
        // 避免「统计行 0 已连接」与「卡片仍点绿」两者矛盾。
        OnPropertyChanged(nameof(ConnectedSessions));

        if (probe)
        {
            await ProbePresenceAsync(probeVersion, ct);
        }
        else
        {
            RestorePresence();
        }
    }

    /// <summary>把缓存的探测结果 / 进行中状态套到新建的行对象上。</summary>
    private void RestorePresence()
    {
        if (!_settings.PresenceProbeEnabled)
        {
            return;
        }

        foreach (var item in RecentItems.Concat(FavoriteItems))
        {
            if (_probingIds.Contains(item.Id))
            {
                item.MarkProbing();
            }
            else if (_presenceCache.TryGetValue(item.Id, out var cached))
            {
                item.SetProbeResult(cached.Online, cached.ProbedAt);
            }
        }
    }

    /// <summary>
    /// 对首页最近连接与收藏按 Profile Id 去重探测。同一设备即使同时出现在两个分区，
    /// 也只建立一次 TCP 探测，并把结果同步到两个独立的行 ViewModel。
    /// </summary>
    private async Task ProbePresenceAsync(int version, CancellationToken outerCt)
    {
        if (_probe is null || !_settings.PresenceProbeEnabled)
        {
            return;
        }

        var targets = RecentItems
            .Concat(FavoriteItems)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToArray();
        if (targets.Length == 0)
        {
            return;
        }

        var batchCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        _probeCts = batchCts;
        var ct = batchCts.Token;

        foreach (var item in RecentItems.Concat(FavoriteItems))
        {
            item.MarkProbing();
            _probingIds.Add(item.Id);
        }

        using var gate = new SemaphoreSlim(8);
        var tasks = targets.Select(async target =>
        {
            var acquired = false;
            try
            {
                await gate.WaitAsync(ct);
                acquired = true;
                var state = await _probe.ProbeAsync(
                    target.Host,
                    target.Profile.Port,
                    PresenceProbeService.DefaultTimeout,
                    ct);

                var online = state == PresenceState.Online;
                var probedAt = DateTimeOffset.Now;
                _ui.Post(() =>
                {
                    if (ct.IsCancellationRequested || version != _probeVersion)
                    {
                        return;
                    }

                    // 结果按 Id 写入「当前」集合：期间若因会话变化重建过行对象，新行同样得到结果。
                    _probingIds.Remove(target.Id);
                    _presenceCache[target.Id] = (online, probedAt);
                    foreach (var current in RecentItems.Concat(FavoriteItems).Where(item => item.Id == target.Id))
                    {
                        current.SetProbeResult(online, probedAt);
                    }
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 页面重载 / 快速切页会取消旧批次；旧结果不得覆盖当前集合。
                // 新批次开始时已清空 _probingIds，这里无需回收。
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "首页在线探测失败：{Host}:{Port}", target.Host, target.Profile.Port);
                _ui.Post(() =>
                {
                    if (version == _probeVersion)
                    {
                        _probingIds.Remove(target.Id);
                    }
                });
            }
            finally
            {
                if (acquired)
                {
                    gate.Release();
                }
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            if (ReferenceEquals(_probeCts, batchCts))
            {
                _probeCts = null;
            }

            batchCts.Dispose();
        }
    }

    // ── 实时状态同步：订阅 SessionManager.SessionsChanged ─────────

    /// <summary>
    /// 会话集合快照变化（创建 / 任意状态跳变 / 移除）后实时刷新首页统计与卡片。
    /// <see cref="SessionManager.SessionsChanged"/> 可能在任意线程触发（状态来自协议后台线程），
    /// 因此先 marshal 回 UI 线程（沿用 MainViewModel 的 CheckAccess/BeginInvoke 模式）。
    /// </summary>
    private void OnSessionsChanged(object? sender, EventArgs e)
    {
        if (_ui.RequeueIfNeeded(() => OnSessionsChanged(sender, e)))
        {
            return;
        }

        // 1) 立即：统计行「M 个会话已连接」与已展示卡片的「已连接」点即时刷新，不等去抖。
        OnPropertyChanged(nameof(ConnectedSessions));
        RefreshCardActiveStates();

        // 2) 去抖全量：停留首页时任一跳变都可能改变最近排序 / LastConnectedDisplay / 最近活动，
        //    静置约 150ms 后再 LoadAsync 重建集合；期间再次收到事件则重置计时，避免连打。
        ScheduleDebouncedReload();
    }

    /// <summary>
    /// 遍历最近 / 收藏行，按 SessionManager 实时快照点亮 / 熄灭「已连接」并刷新“任意活动”标记：
    /// <see cref="ConnectionItemViewModel.IsConnected"/> 走 HasConnectedSession（真正已连，显示蓝色状态），
    /// <see cref="ConnectionItemViewModel.HasActiveSession"/> 走 HasActiveSession（任意活动，供右键区分）。
    /// 让 Connecting→Connected 即时点亮、关闭后即时熄灭。仅在 UI 线程调用（改写行的 ObservableProperty）。
    /// 行不在当前两列表（如连接的 Profile 尚未进入最近 / 收藏）时由去抖的 LoadAsync 重建列表补齐。
    /// </summary>
    private void RefreshCardActiveStates()
    {
        foreach (var item in RecentItems)
        {
            ApplyRealtimeState(item);
        }

        foreach (var item in FavoriteItems)
        {
            ApplyRealtimeState(item);
        }
    }

    private void ApplyRealtimeState(ConnectionItemViewModel item)
    {
        var matches = _sessions.ActiveSessions.Where(x => x.Profile.Id == item.Id).ToArray();
        item.IsConnected = matches.Any(x => x.State == ConnectionState.Connected);
        item.HasActiveSession = _sessions.HasActiveSession(item.Id);
        item.IsConnecting = matches.Any(x => x.State is ConnectionState.Connecting or ConnectionState.Reconnecting);
        item.IsFailed = matches.Any(x => x.State == ConnectionState.Failed);
    }

    /// <summary>发起一次去抖全量刷新。必须在 UI 线程调用。</summary>
    private void ScheduleDebouncedReload()
    {
        var version = ++_reloadVersion;
        _ = DebouncedReloadAsync(version);
    }

    /// <summary>去抖窗口结束后全量刷新一次；窗口内又有新事件（版本号变了）则本次作废。</summary>
    private async Task DebouncedReloadAsync(int version)
    {
        try
        {
            await Task.Delay(ReloadDebounce);

            if (version != _reloadVersion)
            {
                return; // 去抖窗口内又收到新事件：本次已过期，由最新一次负责刷新。
            }

            // 会话变化只影响会话状态 / 最近排序 / 最近活动，不改变主机可达性：沿用上次探测结果。
            await LoadCoreAsync(probe: false, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // 全量刷新失败不应影响已即时更新的统计与卡片；记录日志而非静默吞掉。
            // 首页没有统一的页面错误横幅，导航返回时 ReloadCurrentPageAsync 会再兜底刷新。
            _logger.LogWarning(ex, "首页会话变化去抖全量刷新失败");
        }
    }

    /// <summary>按「设置 → 首页时间行」的详略程度合成首页日期行。</summary>
    private void RefreshHeader()
        => DateLine = DateTimeDisplay.HomeDateLineText(DateTimeOffset.Now, _settings.HomeDateLine);

    /// <summary>供首页视图的秒级定时器调用，刷新日期行里的时间。</summary>
    public void RefreshClock() => RefreshHeader();

    [RelayCommand]
    private void ViewAllRecent() => NavigationRequested?.Invoke(this, NavigationPage.Recent);

    [RelayCommand]
    private void ViewAllFavorites() => NavigationRequested?.Invoke(this, NavigationPage.Favorites);

    [RelayCommand]
    private void ViewAllActivity() => NavigationRequested?.Invoke(this, NavigationPage.Recent);

    [RelayCommand]
    private void ViewCredentials() => NavigationRequested?.Invoke(this, NavigationPage.Credentials);

    [RelayCommand]
    private async Task DismissSecurityTipAsync()
    {
        ShowSecurityTip = false;
        _settings.HomeSecurityTipDismissed = true;
        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch
        {
            // 关闭提示只是界面偏好，保存失败不影响使用，下次启动会再出现。
        }
    }

    private static ConnectionItemViewModel BuildItem(ConnectionProfile profile, IReadOnlyDictionary<Guid, string> groups)
        => new(profile)
        {
            GroupName = profile.GroupId is { } gid && groups.TryGetValue(gid, out var name) ? name : "未分组"
        };

    [RelayCommand]
    private void Connect(ConnectionItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        // 真正的开会话由 MainViewModel 的统一漏斗处理（去重 / 聚焦 / 失败提示）。
        OpenConnectionRequested?.Invoke(this, item.Profile);
    }

    /// <summary>单选收藏行（单击选中并高亮），再双击才连接。</summary>
    public void SelectFavorite(ConnectionItemViewModel? item)
    {
        foreach (var favorite in FavoriteItems)
        {
            favorite.IsSelected = ReferenceEquals(favorite, item);
        }
    }

    /// <summary>
    /// 行右键菜单打开前把目标行设为选中（「最近连接 / 收藏」两列表内互斥），
    /// 让菜单动作的作用对象在视觉上明确。同一 profile 可能分别出现在两列表，
    /// 各自列表内选中即可；目标行不在某列表时该列表全部取消选中。
    /// </summary>
    public void SelectInContext(ConnectionItemViewModel? item)
    {
        foreach (var recent in RecentItems)
        {
            recent.IsSelected = ReferenceEquals(recent, item);
        }

        foreach (var favorite in FavoriteItems)
        {
            favorite.IsSelected = ReferenceEquals(favorite, item);
        }
    }

    /// <summary>
    /// 行右键动作的统一入口（由视图 code-behind 调用）。事件从本类内部触发，
    /// MainViewModel 订阅后桥接到「我的连接」既有命令。
    /// </summary>
    public void RequestConnectionAction(ConnectionItemViewModel? item, string action)
    {
        if (item is null)
        {
            return;
        }

        ConnectionActionRequested?.Invoke(this, new HomeConnectionActionEventArgs(item, action));
    }
}

/// <summary>连接历史行。只展示主机、协议、时间与标准化结果，不含任何凭据信息。</summary>
public sealed class HistoryItemViewModel(ConnectionHistoryEntry entry)
{
    public string ConnectionName => entry.ConnectionName;

    /// <summary>「最近活动」列表里的一行描述。</summary>
    public string ActivityText => $"连接到 {entry.ConnectionName}";

    public string Host => entry.Host;

    /// <summary>主机 + 协议，用于活动列表的副标题。</summary>
    public string HostProtocolLine => $"{entry.Host} · {ProtocolName}";

    public string ProtocolName => entry.Protocol switch
    {
        ProtocolType.Rdp => "RDP",
        ProtocolType.Ssh => "SSH",
        _ => "VNC"
    };

    public string StartedAtDisplay => RemoteFlow.Presentation.Services.DateTimeDisplay.HistoryTimestamp(entry.StartedAt);

    public string DurationDisplay => entry.Duration switch
    {
        null => "—",
        { TotalMinutes: < 1 } d => $"{(int)d.TotalSeconds} 秒",
        { TotalHours: < 1 } d => $"{(int)d.TotalMinutes} 分钟",
        var d => $"{(int)d!.Value.TotalHours} 小时 {d.Value.Minutes} 分"
    };

    /// <summary>首页活动行的短结果标签：失败原因可能很长，行内只写「失败 / 已取消」，完整原因在悬停提示。</summary>
    public string ResultShortText => entry.Result switch
    {
        ConnectionResult.Success => "成功",
        ConnectionResult.Cancelled => "已取消",
        _ => "失败"
    };

    public string ResultText => entry.Result switch
    {
        ConnectionResult.Success => "成功",
        ConnectionResult.Cancelled => "已取消",
        _ => ConnectionException.Describe(entry.ErrorCode)
    };

    // Segoe Fluent Icons，\uXXXX 转义写死。与 SessionTabViewModel.StateIcon 语义一致。
    public string ResultIcon => entry.Result switch
    {
        ConnectionResult.Success => "\uE930",   // CompletedSolid
        ConnectionResult.Cancelled => "\uE895", // 中性圆点
        _ => "\uEA39"                            // ErrorBadge
    };

    public string ResultBrushKey => entry.Result switch
    {
        ConnectionResult.Success => "Status.Success",
        ConnectionResult.Cancelled => "Status.Idle",
        _ => "Status.Danger"
    };

    /// <summary>活动行的图标块统一显示协议，与收藏 / 连接列表保持同一套视觉语言；
    /// 成功与否用右下角的小状态点表示，而不是整块红绿圆。</summary>
    public string ProtocolIcon => entry.Protocol switch
    {
        ProtocolType.Rdp => "\uE7F4",
        ProtocolType.Ssh => "\uE756",
        _ => "\uE7F8"
    };

    public string ProtocolBrushKey => entry.Protocol switch
    {
        ProtocolType.Rdp => "Protocol.Rdp",
        ProtocolType.Ssh => "Protocol.Ssh",
        _ => "Protocol.Vnc"
    };

    // 历史快照目前不持久化设备类型，明确按协议推断正式设备图标，
    // 避免 XAML 绑定到不存在的 ConnectionItemViewModel 属性而静默显示空白。
    private DeviceTypeInfo DeviceVisual => DeviceTypeCatalog.InferFromProtocol(entry.Protocol);

    public string DeviceIconKey => DeviceVisual.IconResourceKey;

    public string DeviceBrushKey => DeviceVisual.AccentBrushKey;

    /// <summary>失败 / 取消才需要在图标块上打状态点，成功是常态不必强调。</summary>
    public bool ShowResultBadge => entry.Result != ConnectionResult.Success;

    /// <summary>原始协议 / 结果，供非 WPF 前端（macOS）取色 / 取符号用。</summary>
    public ProtocolType Protocol => entry.Protocol;

    public bool Succeeded => entry.Result == ConnectionResult.Success;
}

/// <summary>首页行右键动作取值。<see cref="HomePageViewModel.ConnectionActionRequested"/> 分发的动作名。</summary>
public static class HomeRowActions
{
    public const string Connect = "connect";
    public const string Disconnect = "disconnect";
    public const string Edit = "edit";
    public const string Test = "test";
    public const string Favorite = "favorite";
    public const string Manage = "manage";
}

/// <summary>首页「最近连接 / 收藏」行右键动作的参数：目标连接与动作名。</summary>
public sealed record HomeConnectionActionEventArgs(ConnectionItemViewModel Item, string Action);
