using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RemoteFlow.App.Services;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.App.ViewModels;

/// <summary>连接列表的筛选视图。</summary>
public enum ConnectionFilter
{
    All,
    Favorites,
    Recent
}

/// <summary>
/// 「我的连接」页面。承担搜索、筛选、分组与发起连接，
/// 不承担凭据密码编辑（页面职责单一，见产品设计文档 §7.6）。
/// </summary>
public sealed partial class ConnectionsPageViewModel : ObservableObject
{
    private readonly ConnectionService _connections;
    private readonly ConnectionSearchService _search;
    private readonly SessionManager _sessions;
    private readonly IDialogService _dialogs;
    private readonly ILogger<ConnectionsPageViewModel> _logger;

    /// <summary>全量数据。搜索与筛选都在其之上进行，避免每次都回数据库。</summary>
    private readonly List<ConnectionItemViewModel> _allItems = [];

    private IReadOnlyDictionary<Guid, string> _groupNames = new Dictionary<Guid, string>();
    private IReadOnlyDictionary<Guid, string> _tagNames = new Dictionary<Guid, string>();
    private IReadOnlyDictionary<Guid, string> _tagColors = new Dictionary<Guid, string>();

    public ConnectionsPageViewModel(
        ConnectionService connections,
        ConnectionSearchService search,
        SessionManager sessions,
        IDialogService dialogs,
        ILogger<ConnectionsPageViewModel> logger)
    {
        _connections = connections;
        _search = search;
        _sessions = sessions;
        _dialogs = dialogs;
        _logger = logger;

        Items = [];
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ApplyGrouping();
    }

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

    /// <summary>是否按分组折叠显示。</summary>
    [ObservableProperty]
    private bool _groupByFolder = true;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>列表为空时显示的说明文字，区分「没有数据」与「没有匹配结果」。</summary>
    [ObservableProperty]
    private string _emptyMessage = "还没有任何连接。点击「新建连接」开始。";

    public bool IsEmpty => Items.Count == 0;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnFilterChanged(ConnectionFilter value) => ApplyFilter();

    partial void OnGroupByFolderChanged(bool value) => ApplyGrouping();

    // ── 数据加载 ──────────────────────────────────────────────────

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            var profiles = await _connections.GetAllAsync(ct);
            var groups = await _connections.GetGroupsAsync(ct);
            var tags = await _connections.GetTagsAsync(ct);

            _groupNames = groups.ToDictionary(g => g.Id, g => g.Name);
            _tagNames = tags.ToDictionary(t => t.Id, t => t.Name);
            _tagColors = tags.ToDictionary(t => t.Id, t => t.Color);

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
            ConnectionFilter.Recent => source
                .Where(i => i.LastConnectedAt is not null)
                .OrderByDescending(i => i.LastConnectedAt),
            _ => source
        };

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            // 搜索结果已按相关度排序，此处保持该顺序。
            var matched = _search.Search(
                source.Select(i => i.Profile), SearchText, _groupNames, _tagNames);

            var order = matched.Select((p, index) => (p.Id, index)).ToDictionary(x => x.Id, x => x.index);
            source = source.Where(i => order.ContainsKey(i.Id)).OrderBy(i => order[i.Id]);
        }

        var previousSelection = SelectedItem?.Id;

        Items.Clear();
        foreach (var item in source)
        {
            item.RefreshComputed();
            Items.Add(item);
        }

        EmptyMessage = _allItems.Count == 0
            ? "还没有任何连接。点击「新建连接」开始。"
            : Filter switch
            {
                ConnectionFilter.Favorites when string.IsNullOrWhiteSpace(SearchText) => "还没有收藏的连接。在列表中点击星标即可收藏。",
                ConnectionFilter.Recent when string.IsNullOrWhiteSpace(SearchText) => "还没有连接记录。",
                _ => $"没有找到与「{SearchText}」匹配的连接。"
            };

        // 尽量保留原选中项，避免刷新后右侧详情面板闪烁。
        SelectedItem = previousSelection is { } id
            ? Items.FirstOrDefault(i => i.Id == id)
            : null;

        OnPropertyChanged(nameof(IsEmpty));
    }

    private void ApplyGrouping()
    {
        ItemsView.GroupDescriptions.Clear();

        // 「最近连接」按时间排序更有意义，此时不做分组折叠。
        if (GroupByFolder && Filter != ConnectionFilter.Recent)
        {
            ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ConnectionItemViewModel.GroupName)));
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
}
