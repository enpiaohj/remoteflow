using System.Collections.ObjectModel;
using System.ComponentModel;
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

/// <summary>连接列表的筛选视图。</summary>
public enum ConnectionFilter
{
    All,
    Favorites,
    Recent
}

/// <summary>「最近连接」的时间范围筛选。</summary>
public enum RecentRange
{
    Today,
    Week,
    All
}

/// <summary>连接列表的排序方式。</summary>
public enum ConnectionSortMode
{
    Name,
    LastConnected,
    Protocol
}

/// <summary>协议筛选选项。<see cref="All"/> 表示不筛选。</summary>
public enum ProtocolFilterOption
{
    All,
    Rdp,
    Ssh,
    Vnc
}

/// <summary>「移动到分组」子菜单里的一个目标分组。</summary>
/// <param name="GroupId">null 表示「未分组」。</param>
public sealed record GroupTargetOption(Guid? GroupId, string Name, int Depth);

/// <summary>
/// 「我的连接」页面。承担搜索、筛选、分组与发起连接，
/// 不承担凭据密码编辑（页面职责单一，见产品设计文档 §7.6）。
/// </summary>
public sealed partial class ConnectionsPageViewModel : ObservableObject
{
    private readonly ConnectionService _connections;
    private readonly GroupService _groupService;
    private readonly DefaultGroupResolver _defaultGroupResolver;
    private readonly ConnectionSearchService _search;
    private readonly IHistoryRepository _history;
    private readonly IDialogService _dialogs;
    private readonly AppSettings _settings;
    private readonly JsonSettingsStore _settingsStore;
    private readonly ILogger<ConnectionsPageViewModel> _logger;
    private readonly IUiDispatcher _ui;

    /// <summary>只读会话管理：详情面板展示当前连接状态 / 刷新等用，不在此创建会话。
    /// 可空以允许单元测试以 null 构造（不测会话相关）。</summary>
    private readonly SessionManager? _sessions;

    /// <summary>选中连接的历史条数与最近一次时长（供详情「使用信息」）。</summary>
    private int _selectedHistoryCount;
    private TimeSpan? _selectedLastDuration;

    /// <summary>多选模式下被勾选的连接。批量动作的作用域。</summary>
    public ObservableCollection<ConnectionItemViewModel> SelectedConnections { get; } = [];

    /// <summary>详情面板迷你图表展示的历史条数。</summary>
    private const int SparkCount = 10;

    /// <summary>全量数据。搜索与筛选都在其之上进行，避免每次都回数据库。</summary>
    private readonly List<ConnectionItemViewModel> _allItems = [];

    /// <summary>本次运行是否已确立过选中项：用于“首次默认选最近连接，之后不反复跳”。</summary>
    private bool _selectionEstablished;

    private IReadOnlyList<ConnectionGroup> _groups = [];
    private IReadOnlyDictionary<Guid, string> _groupNames = new Dictionary<Guid, string>();
    private IReadOnlyDictionary<Guid, string> _tagNames = new Dictionary<Guid, string>();
    private IReadOnlyDictionary<Guid, string> _tagColors = new Dictionary<Guid, string>();

    /// <summary>搜索开始前的折叠分组集合。搜索期间临时全展开，清空后据此还原。</summary>
    private HashSet<string>? _collapsedBeforeSearch;

    /// <summary>当前是否存在受保护默认组（决定「设为默认分组」菜单是否可用）。</summary>
    private bool _hasProtectedDefault;

    public ConnectionsPageViewModel(
        ConnectionService connections,
        GroupService groupService,
        DefaultGroupResolver defaultGroup,
        ConnectionSearchService search,
        IHistoryRepository history,
        IDialogService dialogs,
        AppSettings settings,
        JsonSettingsStore settingsStore,
        ILogger<ConnectionsPageViewModel> logger,
        IUiDispatcher uiDispatcher,
        SessionManager sessions)
    {
        _connections = connections;
        _ui = uiDispatcher;
        _groupService = groupService;
        _defaultGroupResolver = defaultGroup;
        _search = search;
        _history = history;
        _dialogs = dialogs;
        _settings = settings;
        _settingsStore = settingsStore;
        _logger = logger;
        _sessions = sessions;

        Items = [];

        // 会话集合快照变化（创建 / 任意状态跳变 / 移除）会改变选中连接的实时状态与“最近连接”，
        // 统一订阅聚合 SessionsChanged 一次刷新详情，不再逐会话订阅 StateChanged。
        if (_sessions is not null)
        {
            _sessions.SessionsChanged += OnSessionsChanged;
        }
    }

    // ── 分组树（「我的连接」视图）─────────────────────────────────

    /// <summary>根级分组节点。仅 <see cref="ConnectionFilter.All"/> 视图使用。</summary>
    public ObservableCollection<ConnectionGroupNodeViewModel> GroupNodes { get; } = [];

    /// <summary>
    /// 「我的连接」视图渲染用的扁平行：<see cref="ConnectionGroupNodeViewModel"/>（分组标题）
    /// 与 <see cref="ConnectionItemViewModel"/>（连接行）交替，折叠的分组不展开其内容。
    /// 用一个 ListBox + DataTemplateSelector 承载，复用选中 / 双击 / 键盘逻辑。
    /// </summary>
    public ObservableCollection<object> GroupedRows { get; } = [];

    /// <summary>是否用分组树呈现（我的连接）。收藏 / 最近连接用扁平列表。</summary>
    public bool IsGroupedView => Filter == ConnectionFilter.All;

    /// <summary>「移动到分组」子菜单用的扁平分组列表（含「未分组」）。</summary>
    [ObservableProperty]
    private IReadOnlyList<GroupTargetOption> _groupTargets = [];

    /// <summary>新建连接对话框「分组」的默认值——默认落在「我的设备」。</summary>
    public Guid? DefaultGroupId { get; private set; }

    /// <summary>当前是否存在受保护默认组（决定「设为默认分组」菜单是否可用）。</summary>
    public bool HasProtectedDefault => _hasProtectedDefault;

    /// <summary>重算默认组保护状态，供右键菜单「设为默认分组」可用性判断。</summary>
    private void RefreshDefaultGroupState()
    {
        var def = _groups.FirstOrDefault(g => !g.IsSystem && g.IsDefault);
        _hasProtectedDefault = def is { IsProtected: true };
        OnPropertyChanged(nameof(HasProtectedDefault));
    }

    // ── 详情面板：按连接的历史与迷你图表 ─────────────────────────

    /// <summary>当前选中连接的历史记录（倒序），供详情面板「连接历史」列表。</summary>
    public ObservableCollection<HistoryItemViewModel> SelectedItemHistory { get; } = [];

    /// <summary>迷你柱状图的柱子（最近 10 次，按时间正序），高度已归一化到 0~1。</summary>
    public ObservableCollection<SparkBar> SelectedItemSparkline { get; } = [];

    public bool SelectedItemHasHistory => SelectedItemHistory.Count > 0;

    partial void OnSelectedItemChanged(ConnectionItemViewModel? value) => _ = LoadSelectedHistoryAsync(value);

    private async Task LoadSelectedHistoryAsync(ConnectionItemViewModel? item)
    {
        SelectedItemHistory.Clear();
        SelectedItemSparkline.Clear();

        if (item is null)
        {
            _selectedHistoryCount = 0;
            _selectedLastDuration = null;
            OnPropertyChanged(nameof(SelectedItemHasHistory));
            RaiseSelectedDetail();
            return;
        }

        try
        {
            var entries = await _history.GetByConnectionAsync(item.Id, 50);
            if (!ReferenceEquals(SelectedItem, item))
            {
                return; // 选中已切走：丢弃本次迟到结果，避免详情历史与当前选中错行
            }

            var count = await _history.CountByConnectionAsync(item.Id);
            if (!ReferenceEquals(SelectedItem, item))
            {
                return;
            }

            _selectedHistoryCount = count;
            _selectedLastDuration = entries.FirstOrDefault()?.Duration;

            foreach (var entry in entries)
            {
                SelectedItemHistory.Add(new HistoryItemViewModel(entry));
            }

            // 迷你图：最近 10 次，按时间正序；柱高按时长归一化，无时长（未完成 / 失败）取一个最小值。
            var recent = entries.Take(SparkCount).Reverse().ToList();
            var maxMinutes = recent
                .Select(e => e.Duration?.TotalMinutes ?? 0)
                .DefaultIfEmpty(0)
                .Max();

            foreach (var entry in recent)
            {
                var minutes = entry.Duration?.TotalMinutes ?? 0;
                var ratio = maxMinutes > 0 ? minutes / maxMinutes : 0;
                SelectedItemSparkline.Add(new SparkBar(
                    Math.Max(0.12, ratio),
                    entry.Result == ConnectionResult.Success ? "Status.Success" : "Status.Danger",
                    $"{DateTimeDisplay.Compact(entry.StartedAt)} · {(entry.Duration is { } d ? FormatDuration(d) : "未完成")}"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载连接历史失败：{ConnectionId}", item.Id);
        }

        OnPropertyChanged(nameof(SelectedItemHasHistory));
        RaiseSelectedDetail();
    }

    private static string FormatDuration(TimeSpan d) => d switch
    {
        { TotalMinutes: < 1 } => $"{(int)d.TotalSeconds} 秒",
        { TotalHours: < 1 } => $"{(int)d.TotalMinutes} 分钟",
        _ => $"{(int)d.TotalHours} 小时 {d.Minutes} 分"
    };

    // ── 详情面板：连接状态与使用信息 ──────────────────────────────

    private string _selectedStatusText = "未连接";
    private string _selectedStatusBrushKey = "Status.Idle";

    public string SelectedConnectionStatusText => _selectedStatusText;
    public string SelectedConnectionStatusBrushKey => _selectedStatusBrushKey;

    public string SelectedLastConnectedText => SelectedItem?.LastConnectedDisplay ?? "—";
    public string SelectedLastDurationText => _selectedLastDuration is { } d ? FormatDuration(d) : "—";
    public string SelectedTotalConnectionsText => $"{_selectedHistoryCount} 次";
    public string SelectedCreatedAtText => SelectedItem?.CreatedAtDisplay ?? "—";

    /// <summary>聚合 <see cref="SessionManager.SessionsChanged"/>：会话创建 / 任意状态跳变 / 移除后
    /// 重算右侧详情状态并刷新连接行的实时已连接状态。事件可在任意线程触发，
    /// 由 <see cref="RefreshSessionDependentState"/> marshal 回 UI。</summary>
    private void OnSessionsChanged(object? sender, EventArgs e) => RefreshSessionDependentState();

    private void RefreshSessionDependentState()
    {
        if (_ui.RequeueIfNeeded(RefreshSessionDependentState))
        {
            return;
        }

        RefreshItemsRealtimeState();
        RaiseSelectedDetail();
    }

    /// <summary>
    /// 把全量连接行的实时状态对齐到 SessionManager 快照：<see cref="ConnectionItemViewModel.IsConnected"/>
    /// 只认“真正已连接”，<see cref="ConnectionItemViewModel.HasActiveSession"/> 表示任意活动（含 Connecting/Failed）。
    /// 我的连接 / 收藏 / 最近连接三视图与分组树共用同一批 <see cref="_allItems"/>，改一处即全同步；不落库。
    /// </summary>
    private void RefreshItemsRealtimeState()
    {
        if (_sessions is null)
        {
            return;
        }

        foreach (var item in _allItems)
        {
            item.IsConnected = _sessions.HasConnectedSession(item.Id);
            item.HasActiveSession = _sessions.HasActiveSession(item.Id);
            item.IsConnecting = _sessions.ActiveSessions.Any(
                x => x.Profile.Id == item.Id && x.State == ConnectionState.Connecting);
        }
    }

    private void RaiseSelectedDetail()
    {
        var item = SelectedItem;
        var matches = item is null
            ? Array.Empty<IRemoteSession>()
            : (_sessions?.ActiveSessions ?? Array.Empty<IRemoteSession>())
                .Where(s => s.Profile.Id == item.Id).ToArray();

        if (matches.Any(s => s.State == ConnectionState.Connected))
        {
            _selectedStatusText = "已连接";
            _selectedStatusBrushKey = "Status.Success";
        }
        else if (matches.Any(s => s.State == ConnectionState.Connecting))
        {
            _selectedStatusText = "正在连接";
            _selectedStatusBrushKey = "Status.Info";
        }
        else if (matches.Any(s => s.State == ConnectionState.Failed))
        {
            _selectedStatusText = "连接失败";
            _selectedStatusBrushKey = "Status.Danger";
        }
        else
        {
            _selectedStatusText = "未连接";
            _selectedStatusBrushKey = "Status.Idle";
        }

        OnPropertyChanged(nameof(SelectedConnectionStatusText));
        OnPropertyChanged(nameof(SelectedConnectionStatusBrushKey));
        OnPropertyChanged(nameof(SelectedLastConnectedText));
        OnPropertyChanged(nameof(SelectedLastDurationText));
        OnPropertyChanged(nameof(SelectedTotalConnectionsText));
        OnPropertyChanged(nameof(SelectedCreatedAtText));
    }

    /// <summary>
    /// 当前显示的连接（已应用搜索与筛选）。
    /// <para>「最近连接」筛选下按 <see cref="ConnectionItemViewModel.RecentBucket"/> 分组显示，
    /// 分组由 UI 层承接（WPF 用 <c>CollectionViewSource</c>，Avalonia 用其自有分组），
    /// VM 只维护扁平集合并通过 <see cref="IsRecentView"/> 告知是否应分组。</para>
    /// </summary>
    public ObservableCollection<ConnectionItemViewModel> Items { get; }

    /// <summary>请求打开一个连接（双击 / Enter / 右键连接）。由 MainViewModel 统一开会话。</summary>
    public event EventHandler<ConnectionProfile>? OpenConnectionRequested;

    /// <summary>请求主窗口切换一级页面（详情「查看全部历史」等）。</summary>
    public event EventHandler<NavigationPage>? NavigationRequested;

    [ObservableProperty]
    private ConnectionItemViewModel? _selectedItem;

    /// <summary>搜索词。输入即过滤。</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ConnectionFilter _filter = ConnectionFilter.All;

    /// <summary>「我的连接」页内的名称 / IP / 标签筛选（独立于顶部全局搜索）。</summary>
    [ObservableProperty]
    private string _pageFilterText = string.Empty;

    /// <summary>协议筛选。</summary>
    [ObservableProperty]
    private ProtocolFilterOption _protocolFilter = ProtocolFilterOption.All;

    /// <summary>排序方式。</summary>
    [ObservableProperty]
    private ConnectionSortMode _sortMode = ConnectionSortMode.Name;

    /// <summary>「最近连接」的时间范围。</summary>
    [ObservableProperty]
    private RecentRange _recentRange = RecentRange.Today;

    public IReadOnlyList<ProtocolFilterOption> ProtocolFilterOptions { get; } =
        [ProtocolFilterOption.All, ProtocolFilterOption.Rdp, ProtocolFilterOption.Ssh, ProtocolFilterOption.Vnc];

    public IReadOnlyList<ConnectionSortMode> SortModeOptions { get; } =
        [ConnectionSortMode.Name, ConnectionSortMode.LastConnected, ConnectionSortMode.Protocol];

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>是否处于多选模式。</summary>
    [ObservableProperty]
    private bool _isMultiSelect;

    public bool HasSelection => SelectedConnections.Count > 0;

    /// <summary>批量条文案：「已选择 N 项」。</summary>
    public string SelectionSummary => $"已选择 {SelectedConnections.Count} 项";

    /// <summary>进入多选但还没勾选时，只显示提示条（不显示一排业务按钮）。</summary>
    public bool IsSelectionHintVisible => IsMultiSelect && !HasSelection;

    /// <summary>已勾选 ≥1 项时，才显示批量业务操作栏。</summary>
    public bool IsBatchBarVisible => IsMultiSelect && HasSelection;

    /// <summary>收藏动作的文案随选择变化：所选都已是收藏 → 显示「取消收藏」，否则「收藏」。</summary>
    public string FavoriteActionText => SelectedConnections.Any(i => !i.IsFavorite) ? "收藏" : "取消收藏";

    public bool IsAllSelected => SelectedConnections.Count > 0
        && SelectedConnections.Count == Items.Count;

    partial void OnIsMultiSelectChanged(bool value)
    {
        // 进入多选清空旧选择；退出多选同样清空。
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }

        SelectedConnections.Clear();
        RefreshSelectionSummary();
    }

    /// <summary>列表为空时显示的说明文字，区分「没有数据」与「没有匹配结果」。</summary>
    [ObservableProperty]
    private string _emptyMessage = "还没有任何连接。点击「新建连接」开始。";

    public bool IsEmpty => Items.Count == 0;

    /// <summary>「最近连接」视图专属：日期分组 + 时间范围筛选。</summary>
    public bool IsRecentView => Filter == ConnectionFilter.Recent;

    partial void OnSearchTextChanged(string value)
    {
        var searchingNow = !string.IsNullOrWhiteSpace(value);
        if (searchingNow && !_isSearching)
        {
            // 进入搜索：记住当前折叠状态，搜索期间树临时全展开。
            _collapsedBeforeSearch = [.. _settings.CollapsedGroupIds];
        }
        else if (!searchingNow && _isSearching && _collapsedBeforeSearch is not null)
        {
            // 退出搜索：还原用户原本的展开 / 折叠状态。
            _settings.CollapsedGroupIds.Clear();
            _settings.CollapsedGroupIds.AddRange(_collapsedBeforeSearch);
            _collapsedBeforeSearch = null;
        }
        _isSearching = searchingNow;

        ApplyFilter();
    }

    partial void OnPageFilterTextChanged(string value) => ApplyFilter();

    partial void OnProtocolFilterChanged(ProtocolFilterOption value) => ApplyFilter();

    partial void OnSortModeChanged(ConnectionSortMode value) => ApplyFilter();

    partial void OnRecentRangeChanged(RecentRange value) => ApplyFilter();

    partial void OnFilterChanged(ConnectionFilter value)
    {
        OnPropertyChanged(nameof(IsRecentView));
        OnPropertyChanged(nameof(IsGroupedView));
        OnPropertyChanged(nameof(IsGroupedEmpty));
        ApplyFilter();
    }


    // ── 数据加载 ──────────────────────────────────────────────────

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            DefaultGroupId = await _defaultGroupResolver.ResolveDefaultAsync(ct);

            var profiles = await _connections.GetAllAsync(ct);
            var groups = await _connections.GetGroupsAsync(ct);
            var tags = await _connections.GetTagsAsync(ct);

            _groups = groups;
            _groupNames = groups.ToDictionary(g => g.Id, g => g.Name);
            _tagNames = tags.ToDictionary(t => t.Id, t => t.Name);
            _tagColors = tags.ToDictionary(t => t.Id, t => t.Color);
            RebuildGroupTargets();

            _allItems.Clear();
            foreach (var profile in profiles)
            {
                _allItems.Add(CreateItem(profile));
            }

            ApplyFilter();
            RefreshDefaultGroupState();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>「移动到分组」子菜单：用户分组按层级展开 + 末尾「未分组」。</summary>
    private void RebuildGroupTargets()
    {
        var options = new List<GroupTargetOption>();
        void Walk(Guid? parentId, int depth)
        {
            foreach (var g in _groups
                         .Where(g => g.ParentId == parentId && !g.IsSystem)
                         .OrderBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                options.Add(new GroupTargetOption(g.Id, g.Name, depth));
                Walk(g.Id, depth + 1);
            }
        }
        Walk(null, 0);
        options.Add(new GroupTargetOption(null, "未分组", 0));
        GroupTargets = options;
    }

    /// <summary>凭据名称由外部注入，避免本页面直接依赖凭据服务。</summary>
    public void ApplyCredentialNames(IReadOnlyDictionary<Guid, string> credentialNames)
    {
        foreach (var item in _allItems)
        {
            item.CredentialName = item.Profile.CredentialId is { } id && credentialNames.TryGetValue(id, out var name)
                ? name
                : "未指定";
        }
    }

    private ConnectionItemViewModel CreateItem(ConnectionProfile profile)
    {
        const int MaxVisibleTags = 3;

        var item = new ConnectionItemViewModel(profile)
        {
            GroupName = profile.GroupId is { } gid && _groupNames.TryGetValue(gid, out var groupName)
                ? groupName
                : "未分组"
        };

        var chips = profile.TagIds
            .Where(_tagNames.ContainsKey)
            .Select(id => new TagChip(_tagNames[id], _tagColors.GetValueOrDefault(id, "#0F6CBD")))
            .ToList();

        item.Tags = chips.Take(MaxVisibleTags).ToList();
        item.OverflowTagCount = Math.Max(0, chips.Count - MaxVisibleTags);

        // 行级实时状态：以 SessionManager 为唯一事实来源（可能为 null 以支持单元测试）。
        item.IsConnected = _sessions?.HasConnectedSession(profile.Id) ?? false;
        item.HasActiveSession = _sessions?.HasActiveSession(profile.Id) ?? false;

        return item;
    }

    // ── 搜索与筛选 ────────────────────────────────────────────────

    private void ApplyFilter()
    {
        IEnumerable<ConnectionItemViewModel> source = _allItems;

        source = Filter switch
        {
            ConnectionFilter.Favorites => source.Where(i => i.IsFavorite),
            ConnectionFilter.Recent => source.Where(i => i.LastConnectedAt is not null),
            _ => source
        };

        // 「最近连接」的时间范围筛选。
        if (Filter == ConnectionFilter.Recent && RecentRange != RecentRange.All)
        {
            var cutoff = RecentRange == RecentRange.Today
                ? DateTimeOffset.Now.Date
                : DateTimeOffset.Now.Date.AddDays(-6);
            source = source.Where(i => i.LastConnectedAt >= cutoff);
        }

        // 「我的连接」页内筛选：协议 + 名称 / IP / 标签关键词（「最近连接」不用这套）。
        if (Filter != ConnectionFilter.Recent)
        {
            if (ProtocolFilter != ProtocolFilterOption.All)
            {
                var protocol = ProtocolFilter switch
                {
                    ProtocolFilterOption.Ssh => ProtocolType.Ssh,
                    ProtocolFilterOption.Vnc => ProtocolType.Vnc,
                    _ => ProtocolType.Rdp
                };
                source = source.Where(i => i.Profile.Protocol == protocol);
            }

            if (!string.IsNullOrWhiteSpace(PageFilterText))
            {
                var q = PageFilterText.Trim();
                source = source.Where(i =>
                    i.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                    || i.Host.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                    || i.Tags.Any(t => t.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)));
            }
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            // 顶部全局搜索结果已按相关度排序，此处保持该顺序。
            var matched = _search.Search(
                source.Select(i => i.Profile), SearchText, _groupNames, _tagNames);

            var order = matched.Select((p, index) => (p.Id, index)).ToDictionary(x => x.Id, x => x.index);
            source = source.Where(i => order.ContainsKey(i.Id)).OrderBy(i => order[i.Id]);
        }
        else
        {
            // 「最近连接」固定按时间倒序；其余视图按用户选择的排序方式。
            source = Filter == ConnectionFilter.Recent
                ? source.OrderByDescending(i => i.LastConnectedAt)
                : SortMode switch
                {
                    ConnectionSortMode.LastConnected => source
                        .OrderByDescending(i => i.LastConnectedAt ?? DateTimeOffset.MinValue),
                    ConnectionSortMode.Protocol => source
                        .OrderBy(i => i.Profile.Protocol).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
                    _ => source.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                };
        }

        var previousSelection = SelectedItem?.Id;
        var filtered = source.ToList();

        Items.Clear();
        foreach (var item in filtered)
        {
            item.RefreshComputed();
            if (Filter == ConnectionFilter.Recent)
            {
                item.RecentBucket = BucketFor(item.LastConnectedAt);
            }
            Items.Add(item);
        }

        if (IsGroupedView)
        {
            BuildGroupTree(filtered);
        }

        EmptyMessage = _allItems.Count == 0
            ? "还没有任何连接。点击「新建连接」开始。"
            : Filter switch
            {
                ConnectionFilter.Favorites when string.IsNullOrWhiteSpace(SearchText) => "还没有收藏的连接。在列表中点击星标即可收藏。",
                ConnectionFilter.Recent when string.IsNullOrWhiteSpace(SearchText) =>
                    RecentRange == RecentRange.Today ? "今天还没有连接记录。" : "这段时间还没有连接记录。",
                _ => $"没有找到匹配的连接。"
            };

        // 选中规则（产品约定）：
        // 1) 本次运行内曾选中 → 保留/恢复；2) 首次进入默认选“最近连接”最新；
        // 3) 选中项被删除或被筛选隐藏 → 选第一条可见；4) 无数据 → 不选（右侧空态）。
        ConnectionItemViewModel? selected = null;
        if (previousSelection is { } prevId)
        {
            selected = Items.FirstOrDefault(i => i.Id == prevId);
            selected ??= Items.FirstOrDefault(); // 被删除 / 被过滤掉 → 第一条可见
        }
        else if (Items.Count > 0 && !_selectionEstablished)
        {
            selected = Items
                .OrderByDescending(i => i.LastConnectedAt ?? DateTimeOffset.MinValue)
                .FirstOrDefault()
                ?? Items[0];
        }
        else if (Items.Count > 0)
        {
            selected = Items[0];
        }

        _selectionEstablished |= selected is not null;
        SelectedItem = selected;

        OnPropertyChanged(nameof(IsEmpty));

        // 筛选变化后，被滤掉的已勾选项同步移出选择集合，避免批量作用到不可见项。
        var visibleIds = Items.Select(i => i.Id).ToHashSet();
        for (var i = SelectedConnections.Count - 1; i >= 0; i--)
        {
            if (!visibleIds.Contains(SelectedConnections[i].Id))
            {
                SelectedConnections[i].IsSelected = false;
                SelectedConnections.RemoveAt(i);
            }
        }

        RefreshSelectionSummary();
    }

    /// <summary>把最近连接时间归入「今天 / 昨天 / 更早」三个桶。</summary>
    private static string BucketFor(DateTimeOffset? when)
    {
        if (when is not { } time)
        {
            return "更早";
        }

        var today = DateTimeOffset.Now.Date;
        var day = time.LocalDateTime.Date;
        if (day == today)
        {
            return "今天";
        }
        return day == today.AddDays(-1) ? "昨天" : "更早";
    }

    // ── 分组树构建 ────────────────────────────────────────────────

    private bool _isSearching;

    /// <summary>「我的连接」视图下，分组树是否一条连接都没有。</summary>
    public bool IsGroupedEmpty => IsGroupedView && GroupNodes.Count == 0;

    private void BuildGroupTree(IReadOnlyList<ConnectionItemViewModel> visibleItems)
    {
        var collapsed = _settings.CollapsedGroupIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var byGroup = new Dictionary<Guid, List<ConnectionItemViewModel>>();
        var ungroupedItems = new List<ConnectionItemViewModel>();
        foreach (var item in visibleItems.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (item.Profile.GroupId is { } gid && gid != ConnectionGroup.UngroupedId)
            {
                (byGroup.TryGetValue(gid, out var list) ? list : byGroup[gid] = []).Add(item);
            }
            else
            {
                ungroupedItems.Add(item);
            }
        }

        GroupNodes.Clear();

        ConnectionGroupNodeViewModel? Build(ConnectionGroup group, int depth)
        {
            var node = new ConnectionGroupNodeViewModel
            {
                GroupId = group.Id,
                Name = group.Name,
                Depth = depth,
                IsDefault = group.IsDefault,
                IsProtected = group.IsProtected,
                IsExpanded = _isSearching || !collapsed.Contains(group.Id.ToString())
            };

            foreach (var child in _groups
                         .Where(g => g.ParentId == group.Id && !g.IsSystem)
                         .OrderBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var childNode = Build(child, depth + 1);
                if (childNode is not null)
                {
                    node.ChildGroups.Add(childNode);
                }
            }

            if (byGroup.TryGetValue(group.Id, out var conns))
            {
                foreach (var c in conns)
                {
                    node.Connections.Add(c);
                }
            }

            node.TotalCount = node.Connections.Count + node.ChildGroups.Sum(c => c.TotalCount);

            // 搜索时：命中或子孙命中的分组自动展开；无命中的分组隐藏。
            if (_isSearching && node.TotalCount == 0)
            {
                return null;
            }

            return node;
        }

        foreach (var root in _groups
                     .Where(g => g.ParentId is null && !g.IsSystem)
                     .OrderBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var node = Build(root, 0);
            if (node is not null)
            {
                GroupNodes.Add(node);
            }
        }

        // 「未分组」：仅当有未归类连接时追加到末尾。
        if (ungroupedItems.Count > 0)
        {
            var node = new ConnectionGroupNodeViewModel
            {
                GroupId = null,
                Name = "未分组",
                Depth = 0,
                IsUngrouped = true,
                IsExpanded = _isSearching || !collapsed.Contains("ungrouped"),
                TotalCount = ungroupedItems.Count
            };
            foreach (var c in ungroupedItems)
            {
                node.Connections.Add(c);
            }
            GroupNodes.Add(node);
        }

        WireExpandPersistence(GroupNodes);
        FlattenGroupRows();
        OnPropertyChanged(nameof(IsGroupedEmpty));
    }

    /// <summary>把分组树摊平成 GroupedRows；折叠的分组只留标题行。</summary>
    private void FlattenGroupRows()
    {
        GroupedRows.Clear();

        void Emit(ConnectionGroupNodeViewModel node)
        {
            GroupedRows.Add(node);
            if (!node.IsExpanded)
            {
                return;
            }
            foreach (var child in node.ChildGroups)
            {
                Emit(child);
            }
            foreach (var conn in node.Connections)
            {
                GroupedRows.Add(conn);
            }
        }

        foreach (var root in GroupNodes)
        {
            Emit(root);
        }
    }

    private void WireExpandPersistence(IEnumerable<ConnectionGroupNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            node.PropertyChanged -= OnNodeExpandedChanged;
            node.PropertyChanged += OnNodeExpandedChanged;
            WireExpandPersistence(node.ChildGroups);
        }
    }

    private void OnNodeExpandedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConnectionGroupNodeViewModel.IsExpanded)
            || sender is not ConnectionGroupNodeViewModel node
            || _isSearching)
        {
            return;
        }

        // 展开 / 折叠即时反映到摊平列表。
        FlattenGroupRows();

        var key = node.IsUngrouped ? "ungrouped" : node.GroupId?.ToString();
        if (key is null)
        {
            return;
        }

        var set = _settings.CollapsedGroupIds;
        if (node.IsExpanded)
        {
            set.RemoveAll(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));
        }
        else if (!set.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            set.Add(key);
        }

        _ = PersistSettingsAsync();
    }

    private async Task PersistSettingsAsync()
    {
        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存分组展开状态失败");
        }
    }

    // ── 命令 ──────────────────────────────────────────────────────

    /// <summary>发起连接。这是列表页最高频的操作，由双击或 Enter 触发。
    /// 真正的开会话由 MainViewModel 的统一漏斗处理（去重 / 聚焦 / 失败提示）。</summary>
    [RelayCommand]
    public Task ConnectAsync(ConnectionItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null)
        {
            return Task.CompletedTask;
        }

        OpenConnectionRequested?.Invoke(this, item.Profile);
        return Task.CompletedTask;
    }

    /// <summary>右键「断开连接」：关闭该连接 Profile 的全部活动会话（含连接中 / 失败尚未移除的）。
    /// 会话逐个移除后自然经 <see cref="SessionManager.SessionsChanged"/> 驱动行状态与详情熄灭。</summary>
    [RelayCommand]
    private async Task DisconnectItemAsync(ConnectionItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null || _sessions is null)
        {
            return;
        }

        var sessionIds = _sessions.ActiveSessions
            .Where(s => s.Profile.Id == item.Id)
            .Select(s => s.SessionId)
            .ToList();

        foreach (var sessionId in sessionIds)
        {
            await _sessions.CloseSessionAsync(sessionId);
        }
    }

    /// <summary>详情「查看全部历史」：放开时间范围后跳到「最近连接」活动页，
    /// 让当前连接保持选中并展示它的全部历史。复用既有页面，不新增导航入口。</summary>
    [RelayCommand]
    private void ViewAllHistory()
    {
        RecentRange = RecentRange.All;
        NavigationRequested?.Invoke(this, NavigationPage.Recent);
    }

    [RelayCommand]
    private Task CreateAsync() => CreateConnectionAsync();

    /// <summary>
    /// 打开「新建连接」对话框并落库。<paramref name="preselectedProtocol"/> 供托盘
    /// 「新建连接 → 协议」入口预选协议；<paramref name="defaultGroupId"/> 供分组菜单
    /// 指定新连接的目标分组；两者未提供时沿用默认 RDP 与默认分组。保存后刷新列表并选中新连接，
    /// 勾选「保存并连接」则继续走统一开会话漏斗。
    /// </summary>
    public async Task CreateConnectionAsync(
        ProtocolType? preselectedProtocol = null,
        Guid? defaultGroupId = null)
    {
        var result = await OpenConnectionEditorAsync(
            null,
            preselectedProtocol,
            defaultGroupId);
        if (result is null)
        {
            return;
        }

        await _connections.CreateAsync(result.Profile);
        await LoadAsync();

        SelectedItem = Items.FirstOrDefault(i => i.Id == result.Profile.Id);

        if (result.ConnectImmediately && SelectedItem is not null)
        {
            await ConnectAsync(SelectedItem);
        }
    }

    private Task<ConnectionEditorResult?> OpenConnectionEditorAsync(
        ConnectionProfile? existing,
        ProtocolType? preselectedProtocol,
        Guid? defaultGroupId)
    {
        if (defaultGroupId is { } groupId
            && _dialogs is IConnectionEditorDialogService groupedDialogs)
        {
            return groupedDialogs.EditConnectionAsync(
                existing,
                preselectedProtocol,
                groupId);
        }

        return _dialogs.EditConnectionAsync(existing, preselectedProtocol);
    }

    [RelayCommand]
    private async Task EditAsync(ConnectionItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null)
        {
            return;
        }

        var result = await _dialogs.EditConnectionAsync(item.Profile);
        if (result is null)
        {
            return;
        }

        await _connections.UpdateAsync(result.Profile);
        await LoadAsync();

        SelectedItem = Items.FirstOrDefault(i => i.Id == result.Profile.Id);

        if (result.ConnectImmediately && SelectedItem is not null)
        {
            await ConnectAsync(SelectedItem);
        }
    }

    [RelayCommand]
    private async Task DuplicateAsync(ConnectionItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null)
        {
            return;
        }

        // 复制 = 深拷贝配置（Clone 已深拷贝 Rdp/Ssh/Vnc 选项与 TagIds）、
        // 生成不冲突名称「原名称 (N)」后落库，并立即对副本打开编辑对话框。
        var copy = item.Profile.Clone(await NextDuplicateNameAsync(item.Name));
        await _connections.CreateAsync(copy);
        await LoadAsync();

        // 从全量 _allItems 定位新副本，不要只在过滤后的 Items 里找：即使当前搜索 /
        // 页内筛选与副本名称不匹配，也能保证后续能选中它。随后清掉会把副本滤掉的
        // 搜索与筛选（SelectById 会置 Filter=All、清 SearchText/PageFilterText/
        // ProtocolFilter 并展开分组选中可见），确保打开编辑不静默失败。
        var copyItem = _allItems.FirstOrDefault(i => i.Id == copy.Id);
        if (copyItem is null)
        {
            return;
        }

        SelectById(copyItem.Id);
        await EditAsync(copyItem);
    }

    /// <summary>为副本生成不冲突名称：<c>原名称 (2)</c>、<c>原名称 (3)</c>…，跳过库里已存在的同名。</summary>
    private async Task<string> NextDuplicateNameAsync(string sourceName)
    {
        var existing = await _connections.GetAllAsync();
        var taken = existing.Select(p => p.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        for (var i = 2; ; i++)
        {
            var candidate = $"{sourceName} ({i})";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>打开统一「测试连接」对话框：对目标 host:port 自动执行 DNS → Ping → TCP 诊断，
    /// 不打开会话；命令入口同时被右键菜单与首页快捷操作复用。</summary>
    [RelayCommand]
    private async Task TestConnectionAsync(ConnectionItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null)
        {
            return;
        }

        await _dialogs.ShowConnectionTestAsync(item.Profile);
    }

    [RelayCommand]
    private async Task DeleteAsync(ConnectionItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            "删除连接",
            $"确定要删除连接「{item.Name}」吗？该操作无法撤销。\n\n此操作不会删除它引用的凭据。",
            "删除",
            isDanger: true);

        if (!confirmed)
        {
            return;
        }

        await _connections.DeleteAsync(item.Id);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(ConnectionItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null)
        {
            return;
        }

        item.IsFavorite = !item.IsFavorite;
        item.Profile.Favorite = item.IsFavorite;

        await _connections.SetFavoriteAsync(item.Id, item.IsFavorite);

        // 收藏视图下取消收藏应立即从列表移除。
        if (Filter == ConnectionFilter.Favorites && !item.IsFavorite)
        {
            ApplyFilter();
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    // ── 多选模式与批量操作 ───────────────────────────────────────

    [RelayCommand]
    private void ExitMultiSelect() => IsMultiSelect = false;

    /// <summary>勾选 / 取消勾选单台（多选模式下行单击或点 CheckBox）。</summary>
    public void ToggleSelect(ConnectionItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (SelectedConnections.Contains(item))
        {
            SelectedConnections.Remove(item);
            item.IsSelected = false;
        }
        else
        {
            SelectedConnections.Add(item);
            item.IsSelected = true;
        }

        RefreshSelectionSummary();
    }

    /// <summary>勾选当前可见全部。</summary>
    [RelayCommand]
    private void SelectAllVisible()
    {
        foreach (var item in Items)
        {
            if (!SelectedConnections.Contains(item))
            {
                SelectedConnections.Add(item);
                item.IsSelected = true;
            }
        }

        RefreshSelectionSummary();
    }

    /// <summary>清除全部勾选。</summary>
    [RelayCommand]
    private void ClearSelection()
    {
        SelectedConnections.Clear();
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }

        RefreshSelectionSummary();
    }

    // ── 分组头三态全选 ────────────────────────────────────────────

    /// <summary>
    /// 点击组头 CheckBox：未全选 → 选中该组全部（含子分组）连接；已全选 → 全部取消。
    /// 范围 = 该分组子树中「当前筛选结果」的连接（折叠不影响，筛选外的不含）。
    /// </summary>
    public void ToggleGroupSelection(ConnectionGroupNodeViewModel? node)
    {
        if (node is null)
        {
            return;
        }

        var items = new List<ConnectionItemViewModel>();
        CollectGroupConnections(node, items);
        if (items.Count == 0)
        {
            return;
        }

        var fullySelected = items.All(SelectedConnections.Contains);
        foreach (var item in items)
        {
            if (fullySelected)
            {
                SelectedConnections.Remove(item);
                item.IsSelected = false;
            }
            else if (!SelectedConnections.Contains(item))
            {
                SelectedConnections.Add(item);
                item.IsSelected = true;
            }
        }

        RefreshSelectionSummary();
    }

    /// <summary>递归收集该分组子树（含所有子分组）里的连接行。</summary>
    private static void CollectGroupConnections(
        ConnectionGroupNodeViewModel node, List<ConnectionItemViewModel> result)
    {
        foreach (var child in node.ChildGroups)
        {
            CollectGroupConnections(child, result);
        }

        result.AddRange(node.Connections);
    }

    /// <summary>根据当前勾选集合刷新每个组头的三态（含嵌套子分组）。</summary>
    public void RefreshGroupSelectionStates()
    {
        foreach (var root in GroupNodes)
        {
            UpdateGroupSelectionStateRecursive(root);
        }
    }

    private void UpdateGroupSelectionStateRecursive(ConnectionGroupNodeViewModel node)
    {
        var items = new List<ConnectionItemViewModel>();
        CollectGroupConnections(node, items);

        node.SelectionState = items.Count == 0
            ? false
            : items.All(SelectedConnections.Contains) ? true
            : items.Any(SelectedConnections.Contains) ? null
            : false;

        foreach (var child in node.ChildGroups)
        {
            UpdateGroupSelectionStateRecursive(child);
        }
    }

    /// <summary>批量连接：逐个走统一漏斗；已开设备由漏斗自动聚焦，不重复建。</summary>
    [RelayCommand]
    private void ConnectSelected()
    {
        foreach (var item in SelectedConnections.ToList())
        {
            OpenConnectionRequested?.Invoke(this, item.Profile);
        }
    }

    /// <summary>批量切换收藏：若存在未收藏项则全部收藏，否则全部取消收藏。</summary>
    [RelayCommand]
    private async Task ToggleFavoriteSelectedAsync()
    {
        var selected = SelectedConnections.ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var wantFavorite = selected.Any(i => !i.IsFavorite);
        foreach (var item in selected)
        {
            item.IsFavorite = wantFavorite;
            item.Profile.Favorite = wantFavorite;
            await _connections.SetFavoriteAsync(item.Id, wantFavorite);
        }

        // 收藏视图下取消收藏应立即从可见列表消失。
        if (Filter == ConnectionFilter.Favorites)
        {
            ApplyFilter();
        }

        RefreshSelectionSummary();
    }

    /// <summary>把所选连接移动到目标分组（菜单项统一入口）。</summary>
    public async Task MoveSelectedToGroupAsync(Guid? targetGroupId)
    {
        var selected = SelectedConnections.ToList();
        foreach (var item in selected)
        {
            try
            {
                await _groupService.MoveConnectionAsync(item.Id, targetGroupId);
            }
            catch (InvalidOperationException ex)
            {
                await _dialogs.ShowMessageAsync("无法移动连接", ex.Message, DialogKind.Warning);
                return;
            }
        }

        await LoadAsync();
    }

    /// <summary>批量加标签到所选（已存在则跳过）。</summary>
    [RelayCommand]
    private async Task AddTagToSelectedAsync(Tag? tag)
    {
        if (tag is null)
        {
            return;
        }

        foreach (var item in SelectedConnections.ToList())
        {
            if (!item.Profile.TagIds.Contains(tag.Id))
            {
                item.Profile.TagIds.Add(tag.Id);
                await _connections.UpdateAsync(item.Profile);
            }
        }

        await LoadAsync();
    }

    /// <summary>批量从所选移除标签。</summary>
    [RelayCommand]
    private async Task RemoveTagFromSelectedAsync(Tag? tag)
    {
        if (tag is null)
        {
            return;
        }

        foreach (var item in SelectedConnections.ToList())
        {
            if (item.Profile.TagIds.Remove(tag.Id))
            {
                await _connections.UpdateAsync(item.Profile);
            }
        }

        await LoadAsync();
    }

    /// <summary>批量删除（强确认）。</summary>
    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var selected = SelectedConnections.ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var names = string.Join("、", selected.Select(i => i.Name));
        var confirmed = await _dialogs.ConfirmAsync(
            "删除连接",
            $"确定要删除这 {selected.Count} 个连接吗？\n\n{names}\n\n该操作无法撤销。此操作不会删除它们引用的凭据。",
            "删除",
            isDanger: true);

        if (!confirmed)
        {
            return;
        }

        foreach (var item in selected)
        {
            await _connections.DeleteAsync(item.Id);
        }

        await LoadAsync();
    }

    /// <summary>供批量「标签 ▾」菜单在打开时拉取全部标签。</summary>
    public Task<IReadOnlyList<Tag>> GetTagsAsync()
        => _connections.GetTagsAsync();

    private void RefreshSelectionSummary()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(IsSelectionHintVisible));
        OnPropertyChanged(nameof(IsBatchBarVisible));
        OnPropertyChanged(nameof(FavoriteActionText));
        OnPropertyChanged(nameof(IsAllSelected));

        // 分组树视图下，同步刷新各组头的三态。
        RefreshGroupSelectionStates();
    }

    // ── 分组命令 ──────────────────────────────────────────────────

    /// <summary>新建根级分组（工具条「新建 ▾ → 新建分组」）。</summary>
    [RelayCommand]
    private Task CreateGroupAsync() => CreateGroupUnderAsync(null, null);

    /// <summary>在指定分组下新建子分组（分组右键菜单）。</summary>
    [RelayCommand]
    private Task CreateChildGroupAsync(ConnectionGroupNodeViewModel? parent)
        => CreateGroupUnderAsync(parent?.GroupId, parent?.Name);

    private async Task CreateGroupUnderAsync(Guid? parentId, string? parentName)
    {
        var name = await _dialogs.EditGroupNameAsync(new GroupNamePrompt("新建分组", ParentName: parentName));
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            await _groupService.CreateAsync(name, parentId);
            await LoadAsync();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await _dialogs.ShowMessageAsync("无法创建分组", ex.Message, DialogKind.Warning);
        }
    }

    [RelayCommand]
    private async Task RenameGroupAsync(ConnectionGroupNodeViewModel? node)
    {
        if (node?.GroupId is not { } groupId)
        {
            return;
        }

        var name = await _dialogs.EditGroupNameAsync(new GroupNamePrompt("重命名分组", node.Name));
        if (string.IsNullOrWhiteSpace(name) || name == node.Name)
        {
            return;
        }

        try
        {
            await _groupService.RenameAsync(groupId, name);
            await LoadAsync();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await _dialogs.ShowMessageAsync("无法重命名分组", ex.Message, DialogKind.Warning);
        }
    }

    /// <summary>把某分组设为默认新建连接分组（分组右键「设为默认分组」）。</summary>
    [RelayCommand]
    private async Task SetDefaultGroupAsync(ConnectionGroupNodeViewModel? node)
    {
        if (node?.GroupId is not { } groupId)
        {
            return;
        }

        try
        {
            await _groupService.SetDefaultAsync(groupId);
            await LoadAsync();
        }
        catch (InvalidOperationException ex)
        {
            await _dialogs.ShowMessageAsync("无法设为默认分组", ex.Message, DialogKind.Warning);
        }
    }

    [RelayCommand]
    private async Task DeleteGroupAsync(ConnectionGroupNodeViewModel? node)
    {
        if (node?.GroupId is not { } groupId)
        {
            return;
        }

        // 候选默认分组提前算好：确认文案只有确实存在可选的其它分组时才提示“需要指定新的默认分组”。
        List<DefaultGroupOption>? candidates = null;
        if (node.IsDefault)
        {
            candidates = _groups
                .Where(g => !g.IsSystem && g.Id != groupId)
                .OrderBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new DefaultGroupOption(g.Id, g.Name))
                .ToList();
        }

        var confirmed = await _dialogs.ConfirmAsync(
            "删除分组",
            $"确定要删除分组「{node.Name}」吗？\n\n" +
            "组内连接会移动到「未分组」，子分组会提升到上一级——不会删除任何连接。" +
            (candidates is { Count: > 0 } ? "\n\n这是当前默认新建连接分组，删除后需要指定新的默认分组。" : ""),
            "删除",
            isDanger: true);

        if (!confirmed)
        {
            return;
        }

        Guid? newDefault = null;
        if (candidates is { Count: > 0 })
        {
            var picked = await _dialogs.PickDefaultGroupAsync(node.Name, candidates);
            if (picked is null)
            {
                return; // 用户取消选默认 → 中止删除
            }
            newDefault = picked.Id;
        }

        try
        {
            await _groupService.DeleteAsync(groupId);
            if (newDefault is { } targetId)
            {
                await _groupService.SetDefaultAsync(targetId);
            }
            await LoadAsync();
        }
        catch (InvalidOperationException ex)
        {
            await _dialogs.ShowMessageAsync("无法删除分组", ex.Message, DialogKind.Warning);
        }
    }

    /// <summary>把连接移动到目标分组（连接右键「移动到分组 &gt;」）。</summary>
    public async Task MoveConnectionToGroupAsync(ConnectionItemViewModel? item, Guid? targetGroupId)
    {
        if (item is null)
        {
            return;
        }

        var currentGroup = item.Profile.GroupId == ConnectionGroup.UngroupedId ? null : item.Profile.GroupId;
        if (currentGroup == targetGroupId)
        {
            return;
        }

        try
        {
            await _groupService.MoveConnectionAsync(item.Id, targetGroupId);
            await LoadAsync();
            SelectedItem = Items.FirstOrDefault(i => i.Id == item.Id);
        }
        catch (InvalidOperationException ex)
        {
            await _dialogs.ShowMessageAsync("无法移动连接", ex.Message, DialogKind.Warning);
        }
    }

    /// <summary>
    /// 按 Id 选中某个连接，并确保它在当前列表里可见。首页 / 收藏 / 最近连接右键
    /// 「管理连接」跳转后调用：切到「全部」视图、清掉会隐藏该行的
    /// 搜索 / 页内筛选，展开所在分组。找不到目标时安全保持现状。
    /// </summary>
    public void SelectById(Guid id)
    {
        // 回到全部视图，并清掉可能把目标行滤掉的搜索与筛选词。
        if (Filter != ConnectionFilter.All)
        {
            Filter = ConnectionFilter.All;
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            SearchText = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(PageFilterText))
        {
            PageFilterText = string.Empty;
        }

        if (ProtocolFilter != ProtocolFilterOption.All)
        {
            ProtocolFilter = ProtocolFilterOption.All;
        }

        var target = _allItems.FirstOrDefault(i => i.Id == id);
        if (target is null)
        {
            return;
        }

        // 分组视图下目标可能位于折叠分组：展开祖先链，让该行进入 GroupedRows 可见并可选中。
        if (IsGroupedView)
        {
            foreach (var root in GroupNodes)
            {
                if (ExpandToConnection(root, id))
                {
                    break;
                }
            }
        }

        SelectedItem = target;
    }

    /// <summary>目标连接位于该节点子树时展开节点（含祖先链），返回是否命中。</summary>
    private static bool ExpandToConnection(ConnectionGroupNodeViewModel node, Guid targetId)
    {
        var found = node.Connections.Any(c => c.Id == targetId)
            || node.ChildGroups.Any(child => ExpandToConnection(child, targetId));

        if (found && !node.IsExpanded)
        {
            node.IsExpanded = true; // 触发 WireExpandPersistence → FlattenGroupRows + 持久化折叠状态
        }

        return found;
    }
}
