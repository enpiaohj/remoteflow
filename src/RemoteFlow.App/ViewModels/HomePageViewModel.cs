using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.App.Services;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Infrastructure.Settings;

namespace RemoteFlow.App.ViewModels;

/// <summary>
/// 首页。只解决一件事：让用户尽快回到工作状态。
/// <para>
/// 因此这里只呈现最近连接、收藏与最近活动，
/// 不做资产统计看板或复杂图表（产品设计文档 §7.6）。
/// </para>
/// </summary>
public sealed partial class HomePageViewModel(
    ConnectionService connections,
    IHistoryRepository history,
    SessionManager sessions,
    AppSettings settings,
    JsonSettingsStore settingsStore) : ObservableObject
{
    /// <summary>收藏 / 最近活动分区最多展示的条目数，超出的到对应页面查看全部。</summary>
    private const int SectionLimit = 7;

    /// <summary>「最近连接」快捷卡片的数量。</summary>
    private const int RecentCardLimit = 3;

    /// <summary>请求主窗口切换到某个一级页面（「查看全部」等）。</summary>
    public event EventHandler<NavigationPage>? NavigationRequested;

    /// <summary>请求打开一个连接（首页卡片 / 收藏行双击等）。由 MainViewModel 统一开会话。</summary>
    public event EventHandler<ConnectionProfile>? OpenConnectionRequested;

    /// <summary>
    /// 首页「最近连接 / 收藏」行右键动作（编辑 / 复制 / 收藏 / 删除 / 在「我的连接」中管理）。
    /// 真正的执行桥接到「我的连接」既有命令，避免在首页复制实现。
    /// </summary>
    public event EventHandler<HomeConnectionActionEventArgs>? ConnectionActionRequested;

    public ObservableCollection<ConnectionItemViewModel> RecentItems { get; } = [];

    public ObservableCollection<ConnectionItemViewModel> FavoriteItems { get; } = [];

    public ObservableCollection<HistoryItemViewModel> RecentHistory { get; } = [];

    [ObservableProperty]
    private int _totalConnections;

    /// <summary>当前已连接会话数（仅统计 Connected）。顶部统计行使用。</summary>
    public int ConnectedSessions => sessions.ConnectedSessionCount;

    [ObservableProperty]
    private string _greeting = string.Empty;

    /// <summary>
    /// 首页标题日期行：日期恒在行首，星期 / 周数 / 时间按「显示顺序」取舍拼接。
    /// 例：<c>2026年9月6日 · 周日 · 第37周 · 14:05</c>；「时间单独一行」时此行不含时间。
    /// </summary>
    [ObservableProperty]
    private string _dateLine = string.Empty;

    /// <summary>首页时钟行（仅「显示顺序 = 单独一行」且开启时间时显示），例：<c>14:05:09</c>。</summary>
    [ObservableProperty]
    private string _clockLine = string.Empty;

    /// <summary>时钟行是否可见：单独一行时非空。</summary>
    public bool HasClockLine => !string.IsNullOrEmpty(ClockLine);

    /// <summary>用户是否开启「显示时间」。供视图决定是否启动秒级刷新。</summary>
    public bool ShowHomeTimeEnabled => settings.ShowHomeTime;

    /// <summary>底部安全提示横幅是否可见（用户可关闭，选择记入设置）。</summary>
    [ObservableProperty]
    private bool _showSecurityTip;

    public bool HasRecent => RecentItems.Count > 0;

    public bool HasFavorites => FavoriteItems.Count > 0;

    public bool HasActivity => RecentHistory.Count > 0;

    public bool IsFirstRun => TotalConnections == 0;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        Greeting = DateTime.Now.Hour switch
        {
            >= 5 and < 12 => "上午好",
            >= 12 and < 18 => "下午好",
            >= 18 and < 23 => "晚上好",
            _ => "夜深了"
        };

        RefreshHeader();

        var profiles = await connections.GetAllAsync(ct);
        var groups = (await connections.GetGroupsAsync(ct)).ToDictionary(g => g.Id, g => g.Name);

        TotalConnections = profiles.Count;
        ShowSecurityTip = !settings.HomeSecurityTipDismissed && profiles.Count > 0;

        // 卡片高亮只认“真正已连接”的会话：正在连接 / 失败不点亮“已连接”标签。
        var connectedProfileIds = sessions.ActiveSessions
            .Where(s => s.State == ConnectionState.Connected)
            .Select(s => s.Profile.Id)
            .ToHashSet();

        RecentItems.Clear();
        foreach (var profile in profiles
                     .Where(p => p.LastConnectedAt is not null)
                     .OrderByDescending(p => p.LastConnectedAt)
                     .Take(RecentCardLimit))
        {
            var item = BuildItem(profile, groups);
            item.HasActiveSession = connectedProfileIds.Contains(profile.Id);
            RecentItems.Add(item);
        }

        FavoriteItems.Clear();
        foreach (var profile in profiles
                     .Where(p => p.Favorite)
                     .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                     .Take(SectionLimit))
        {
            FavoriteItems.Add(BuildItem(profile, groups));
        }

        RecentHistory.Clear();
        foreach (var entry in await history.GetRecentAsync(SectionLimit, ct))
        {
            RecentHistory.Add(new HistoryItemViewModel(entry));
        }

        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(HasFavorites));
        OnPropertyChanged(nameof(HasActivity));
        OnPropertyChanged(nameof(IsFirstRun));

        // 返回首页时重算统计行：卡片绿点读会话实时状态，这里显式通知计数刷新，
        // 避免「统计行 0 已连接」与「卡片仍点绿」两者矛盾。
        OnPropertyChanged(nameof(ConnectedSessions));
    }

    /// <summary>
    /// 合成首页标题日期行与可选的时钟行。日期恒在行首，其余段（星期 / 周数 / 时间）
    /// 依 <see cref="HomeTimeOrder"/> 决定的槽位顺序拼接，且各段只在对应开关开启时插入；
    /// 「时间单独一行」布局中星期 / 周数留在日期行，时间进入 <see cref="ClockLine"/>。
    /// </summary>
    private void RefreshHeader()
    {
        var now = DateTimeOffset.Now;
        var date = DateTimeDisplay.Date(now);
        var clock = settings.ShowHomeTime ? DateTimeDisplay.Clock(now, settings.ShowHomeSeconds) : null;

        var parts = new List<string> { date };
        string? clockLine = null;

        switch (settings.ShowHomeTimeOrder)
        {
            case HomeTimeOrder.SeparateLine:
                // 日期 · 星期 · 周数，时间单独一行。
                if (settings.ShowWeekday) parts.Add(DateTimeDisplay.Weekday(now));
                if (settings.ShowHomeWeekNumber) parts.Add($"第{DateTimeDisplay.IsoWeek(now)}周");
                clockLine = clock;
                break;

            case HomeTimeOrder.DateTimeWeekdayWeek:
                // 日期 · 时间 · 星期 · 周数。
                if (clock is not null) parts.Add(clock);
                if (settings.ShowWeekday) parts.Add(DateTimeDisplay.Weekday(now));
                if (settings.ShowHomeWeekNumber) parts.Add($"第{DateTimeDisplay.IsoWeek(now)}周");
                break;

            case HomeTimeOrder.DateWeekdayTimeWeek:
                // 日期 · 星期 · 时间 · 周数。
                if (settings.ShowWeekday) parts.Add(DateTimeDisplay.Weekday(now));
                if (clock is not null) parts.Add(clock);
                if (settings.ShowHomeWeekNumber) parts.Add($"第{DateTimeDisplay.IsoWeek(now)}周");
                break;

            default: // DateWeekdayWeekTime —— 日期 · 星期 · 周数 · 时间
                if (settings.ShowWeekday) parts.Add(DateTimeDisplay.Weekday(now));
                if (settings.ShowHomeWeekNumber) parts.Add($"第{DateTimeDisplay.IsoWeek(now)}周");
                if (clock is not null) parts.Add(clock);
                break;
        }

        DateLine = string.Join(" · ", parts);
        ClockLine = clockLine ?? "";
        OnPropertyChanged(nameof(HasClockLine));
    }

    /// <summary>供首页视图的秒级定时器调用，刷新日期行与时钟行。</summary>
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
        settings.HomeSecurityTipDismissed = true;
        try
        {
            await settingsStore.SaveAsync(settings);
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

    public string StartedAtDisplay => RemoteFlow.App.Services.DateTimeDisplay.HistoryTimestamp(entry.StartedAt);

    public string DurationDisplay => entry.Duration switch
    {
        null => "—",
        { TotalMinutes: < 1 } d => $"{(int)d.TotalSeconds} 秒",
        { TotalHours: < 1 } d => $"{(int)d.TotalMinutes} 分钟",
        var d => $"{(int)d!.Value.TotalHours} 小时 {d.Value.Minutes} 分"
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

    /// <summary>失败 / 取消才需要在图标块上打状态点，成功是常态不必强调。</summary>
    public bool ShowResultBadge => entry.Result != ConnectionResult.Success;
}

/// <summary>首页行右键动作取值。<see cref="HomePageViewModel.ConnectionActionRequested"/> 分发的动作名。</summary>
public static class HomeRowActions
{
    public const string Connect = "connect";
    public const string Edit = "edit";
    public const string Duplicate = "duplicate";
    public const string Favorite = "favorite";
    public const string Manage = "manage";
    public const string Delete = "delete";
}

/// <summary>首页「最近连接 / 收藏」行右键动作的参数：目标连接与动作名。</summary>
public sealed record HomeConnectionActionEventArgs(ConnectionItemViewModel Item, string Action);
