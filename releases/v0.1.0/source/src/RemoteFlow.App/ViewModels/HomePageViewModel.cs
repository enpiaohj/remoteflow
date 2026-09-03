using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.App.Services;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.App.ViewModels;

/// <summary>
/// 首页。只解决一件事：让用户尽快回到工作状态。
/// <para>
/// 因此这里只呈现最近连接、收藏与当前会话，
/// 不做资产统计看板或复杂图表（产品设计文档 §7.6）。
/// </para>
/// </summary>
public sealed partial class HomePageViewModel(
    ConnectionService connections,
    IHistoryRepository history,
    SessionManager sessions,
    IDialogService dialogs) : ObservableObject
{
    /// <summary>首页每个分区最多展示的条目数，超出请到「我的连接」查看。</summary>
    private const int SectionLimit = 6;

    public ObservableCollection<ConnectionItemViewModel> RecentItems { get; } = [];

    public ObservableCollection<ConnectionItemViewModel> FavoriteItems { get; } = [];

    public ObservableCollection<HistoryItemViewModel> RecentHistory { get; } = [];

    [ObservableProperty]
    private int _totalConnections;

    [ObservableProperty]
    private int _activeSessions;

    [ObservableProperty]
    private string _greeting = string.Empty;

    public bool HasRecent => RecentItems.Count > 0;

    public bool HasFavorites => FavoriteItems.Count > 0;

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

        RecentItems.Clear();
        foreach (var profile in profiles
                     .Where(p => p.LastConnectedAt is not null)
                     .OrderByDescending(p => p.LastConnectedAt)
                     .Take(SectionLimit))
        {
            RecentItems.Add(BuildItem(profile, groups));
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
        OnPropertyChanged(nameof(IsFirstRun));
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

    public string Host => entry.Host;

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

    public string ResultIcon => entry.Result switch
    {
        ConnectionResult.Success => "",
        ConnectionResult.Cancelled => "",
        _ => ""
    };

    public string ResultBrushKey => entry.Result switch
    {
        ConnectionResult.Success => "Status.Success",
        ConnectionResult.Cancelled => "Status.Idle",
        _ => "Status.Danger"
    };
}
