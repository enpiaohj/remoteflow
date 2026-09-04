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
    IDialogService dialogs,
    AppSettings settings,
    JsonSettingsStore settingsStore) : ObservableObject
{
    /// <summary>收藏 / 最近活动分区最多展示的条目数，超出的到对应页面查看全部。</summary>
    private const int SectionLimit = 7;

    /// <summary>「最近连接」快捷卡片的数量。</summary>
    private const int RecentCardLimit = 3;

    /// <summary>请求主窗口切换到某个一级页面（「查看全部」等）。</summary>
    public event EventHandler<NavigationPage>? NavigationRequested;

    public ObservableCollection<ConnectionItemViewModel> RecentItems { get; } = [];

    public ObservableCollection<ConnectionItemViewModel> FavoriteItems { get; } = [];

    public ObservableCollection<HistoryItemViewModel> RecentHistory { get; } = [];

    [ObservableProperty]
    private int _totalConnections;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveSessions))]
    private int _activeSessions;

    public bool HasActiveSessions => ActiveSessions > 0;

    [ObservableProperty]
    private string _greeting = string.Empty;

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

        var profiles = await connections.GetAllAsync(ct);
        var groups = (await connections.GetGroupsAsync(ct)).ToDictionary(g => g.Id, g => g.Name);

        TotalConnections = profiles.Count;
        ActiveSessions = sessions.ActiveSessionCount;
        ShowSecurityTip = !settings.HomeSecurityTipDismissed && profiles.Count > 0;

        var activeProfileIds = sessions.ActiveSessions.Select(s => s.Profile.Id).ToHashSet();

        RecentItems.Clear();
        foreach (var profile in profiles
                     .Where(p => p.LastConnectedAt is not null)
                     .OrderByDescending(p => p.LastConnectedAt)
                     .Take(RecentCardLimit))
        {
            var item = BuildItem(profile, groups);
            item.HasActiveSession = activeProfileIds.Contains(profile.Id);
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
    }

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
    private async Task ConnectAsync(ConnectionItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            await sessions.CreateSessionAsync(item.Profile);
        }
        catch (ConnectionException ex)
        {
            await dialogs.ShowMessageAsync("无法建立连接", ex.Message, DialogKind.Error);
        }
        catch (InvalidOperationException ex)
        {
            await dialogs.ShowMessageAsync("无法建立连接", ex.Message, DialogKind.Warning);
        }
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

    public string StartedAtDisplay => entry.StartedAt.ToString("MM-dd HH:mm");

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
