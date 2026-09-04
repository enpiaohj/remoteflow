using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RemoteFlow.App.Services;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Infrastructure.Settings;

namespace RemoteFlow.App.ViewModels;

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
    private readonly ConnectionSearchService _search;
    private readonly SessionManager _sessions;
    private readonly IHistoryRepository _history;
    private readonly IDialogService _dialogs;
    private readonly AppSettings _settings;
    private readonly JsonSettingsStore _settingsStore;
    private readonly ILogger<ConnectionsPageViewModel> _logger;

    /// <summary>详情面板迷你图表展示的历史条数。</summary>
    private const int SparkCount = 10;

    /// <summary>全量数据。搜索与筛选都在其之上进行，避免每次都回数据库。</summary>
    private readonly List<ConnectionItemViewModel> _allItems = [];

    private IReadOnlyList<ConnectionGroup> _groups = [];
    private IReadOnlyDictionary<Guid, string> _groupNames = new Dictionary<Guid, string>();
    private IReadOnlyDictionary<Guid, string> _tagNames = new Dictionary<Guid, string>();
    private IReadOnlyDictionary<Guid, string> _tagColors = new Dictionary<Guid, string>();

    /// <summary>搜索开始前的折叠分组集合。搜索期间临时全展开，清空后据此还原。</summary>
    private HashSet<string>? _collapsedBeforeSearch;

    public ConnectionsPageViewModel(
        ConnectionService connections,
        GroupService groupService,
        ConnectionSearchService search,
        SessionManager sessions,
        IHistoryRepository history,
        IDialogService dialogs,
        AppSettings settings,
        JsonSettingsStore settingsStore,
        ILogger<ConnectionsPageViewModel> logger)
    {
        _connections = connections;
        _groupService = groupService;
        _search = search;
        _sessions = sessions;
        _history = history;
        _dialogs = dialogs;
        _settings = settings;
        _settingsStore = settingsStore;
        _logger = logger;

        Items = [];
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ApplyGrouping();
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
            OnPropertyChanged(nameof(SelectedItemHasHistory));
            return;
        }

        try
        {
            var entries = await _history.GetByConnectionAsync(item.Id, 50);

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
                    $"{entry.StartedAt:MM-dd HH:mm} · {(entry.Duration is { } d ? FormatDuration(d) : "未完成")}"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载连接历史失败：{ConnectionId}", item.Id);
        }

        OnPropertyChanged(nameof(SelectedItemHasHistory));
    }

    private static string FormatDuration(TimeSpan d) => d switch
    {
        { TotalMinutes: < 1 } => $"{(int)d.TotalSeconds} 秒",
        { TotalHours: < 1 } => $"{(int)d.TotalMinutes} 分钟",
        _ => $"{(int)d.TotalHours} 小时 {d.Minutes} 分"
    };

    /// <summary>当前显示的连接（已应用搜索与筛选）。</summary>
    public ObservableCollection<ConnectionItemViewModel> Items { get; }

    /// <summary>带分组的视图。分组折叠是连接列表的默认组织方式。</summary>
    public ICollectionView ItemsView { get; }

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
        ApplyGrouping();
        ApplyFilter();
    }


    // ── 数据加载 ──────────────────────────────────────────────────

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            DefaultGroupId = await _groupService.EnsureSeedAsync(ct);

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

        // 尽量保留原选中项，避免刷新后右侧详情面板闪烁。
        SelectedItem = previousSelection is { } id
            ? Items.FirstOrDefault(i => i.Id == id)
            : null;

        OnPropertyChanged(nameof(IsEmpty));
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

    private void ApplyGrouping()
    {
        ItemsView.GroupDescriptions.Clear();

        // 只有「最近连接」用 ICollectionView 分组（按日期桶）。
        // 「我的连接」用 GroupNodes 树，「收藏」是扁平列表。
        if (Filter == ConnectionFilter.Recent)
        {
            ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ConnectionItemViewModel.RecentBucket)));
        }
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

    /// <summary>发起连接。这是列表页最高频的操作，由双击或 Enter 触发。</summary>
    [RelayCommand]
    public async Task ConnectAsync(ConnectionItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null)
        {
            return;
        }

        try
        {
            // 只创建会话；实际连接由会话视图在控件就绪后发起。
            await _sessions.CreateSessionAsync(item.Profile);
        }
        catch (ConnectionException ex)
        {
            await _dialogs.ShowMessageAsync("无法建立连接", ex.Message, DialogKind.Error);
        }
        catch (InvalidOperationException ex)
        {
            await _dialogs.ShowMessageAsync("无法建立连接", ex.Message, DialogKind.Warning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建会话失败：{ConnectionName}", item.Name);
            await _dialogs.ShowMessageAsync("无法建立连接", "创建会话时发生未知错误，详情请查看日志。", DialogKind.Error);
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        var result = await _dialogs.EditConnectionAsync(null);
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

        var copy = await _connections.DuplicateAsync(item.Id);
        await LoadAsync();
        SelectedItem = Items.FirstOrDefault(i => i.Id == copy.Id);
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

    [RelayCommand]
    private async Task DeleteGroupAsync(ConnectionGroupNodeViewModel? node)
    {
        if (node?.GroupId is not { } groupId)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            "删除分组",
            $"确定要删除分组「{node.Name}」吗？\n\n" +
            "组内连接会移动到「未分组」，子分组会提升到上一级——不会删除任何连接。",
            "删除",
            isDanger: true);

        if (!confirmed)
        {
            return;
        }

        try
        {
            await _groupService.DeleteAsync(groupId);
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
}
