using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RemoteFlow.App.Services;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.App.ViewModels;

/// <summary>左侧导航的一级入口。</summary>
public enum NavigationPage
{
    Home,
    Connections,
    Favorites,
    Recent,
    Credentials,
    Settings
}

/// <summary>
/// 主窗口 ViewModel。
/// <para>
/// 布局职责对应产品设计文档 §7.5 的四个稳定区域：
/// 左侧导航（6~7 个一级入口）、中央工作区（连接列表与会话共享）、
/// 右侧详情（按需显示）、顶部两个高频动作（全局搜索 + 新建连接）。
/// </para>
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly SessionManager _sessions;
    private readonly CredentialsPageViewModel _credentialsPage;
    private readonly SettingsPageViewModel _settingsPage;
    private readonly IDialogService _dialogs;
    private readonly ILogger<MainViewModel> _logger;

    /// <summary>正在创建会话的连接 Profile.Id。用于连点去重：会话 Tab 建出前，第二次请求不重复建。</summary>
    private readonly HashSet<Guid> _openingProfileIds = [];

    public MainViewModel(
        SessionManager sessions,
        HomePageViewModel homePage,
        ConnectionsPageViewModel connectionsPage,
        CredentialsPageViewModel credentialsPage,
        SettingsPageViewModel settingsPage,
        IDialogService dialogs,
        AppSettings settings,
        ILogger<MainViewModel> logger)
    {
        _sessions = sessions;
        HomePage = homePage;
        ConnectionsPage = connectionsPage;
        _credentialsPage = credentialsPage;
        _settingsPage = settingsPage;
        _dialogs = dialogs;
        _logger = logger;

        _sessions.MaxConcurrentSessions = settings.MaxConcurrentSessions;

        WorkspaceTab = new PageTabViewModel();
        Tabs = [WorkspaceTab];
        SelectedTab = WorkspaceTab;

        // 会话由各页面通过 SessionManager 创建，主窗口只负责把它呈现为 Tab。
        _sessions.SessionCreated += OnSessionCreated;
        _sessions.SessionClosed += OnSessionClosed;

        // 设置页的「数据与备份」改动了本地数据时刷新当前页。
        _settingsPage.DataChanged += async (_, _) => await ReloadCurrentPageAsync();

        // 首页的「查看全部」等入口请求跳转。
        HomePage.NavigationRequested += (_, page) => NavigateTo(page);

        // 连接详情「查看全部历史」请求跳转到「最近连接」。
        ConnectionsPage.NavigationRequested += (_, page) => NavigateTo(page);

        // 首页 / 连接页所有「点设备→开会话」都收敛到这里统一处理：去重、聚焦、失败提示。
        HomePage.OpenConnectionRequested += async (_, profile) => await OpenSessionAsync(profile);
        ConnectionsPage.OpenConnectionRequested += async (_, profile) => await OpenSessionAsync(profile);

        NavigateTo(settings.DefaultLandingPage switch
        {
            LandingPage.Connections => NavigationPage.Connections,
            LandingPage.Favorites => NavigationPage.Favorites,
            LandingPage.Recent => NavigationPage.Recent,
            LandingPage.Credentials => NavigationPage.Credentials,
            _ => NavigationPage.Home
        });
    }

    public HomePageViewModel HomePage { get; }

    public ConnectionsPageViewModel ConnectionsPage { get; }

    /// <summary>工作区固定 Tab，承载当前导航页。</summary>
    public PageTabViewModel WorkspaceTab { get; }

    /// <summary>工作区全部 Tab：索引 0 是页面，其后为各远程会话。</summary>
    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; }

    [ObservableProperty]
    private WorkspaceTabViewModel? _selectedTab;

    [ObservableProperty]
    private NavigationPage _currentPage = NavigationPage.Home;

    /// <summary>顶部全局搜索框内容。</summary>
    [ObservableProperty]
    private string _globalSearchText = string.Empty;

    /// <summary>右侧详情面板是否展开。全屏会话时自动收起。</summary>
    [ObservableProperty]
    private bool _isDetailPanelVisible = true;

    /// <summary>会话是否处于全屏。全屏时隐藏左侧导航与右侧详情。</summary>
    [ObservableProperty]
    private bool _isSessionFullScreen;

    /// <summary>页面数据加载失败的提示。非空时中央工作区顶部显示可重试的横幅。</summary>
    [ObservableProperty]
    private string _pageLoadError = string.Empty;

    /// <summary>当前是否显示的是远程会话（而非连接列表页）。</summary>
    public bool IsSessionSelected => SelectedTab is SessionTabViewModel;

    /// <summary>
    /// 当前页面是否是连接列表（我的连接 / 收藏 / 最近连接）。
    /// <para>
    /// 右侧详情面板只服务于连接列表——首页、凭据、设置等页面没有「选中的连接」可展示，
    /// 让面板在那里空占一栏会白白挤压中央工作区。
    /// </para>
    /// </summary>
    public bool IsConnectionListPage =>
        CurrentPage is NavigationPage.Connections or NavigationPage.Favorites or NavigationPage.Recent;

    partial void OnCurrentPageChanged(NavigationPage value)
    {
        OnPropertyChanged(nameof(IsConnectionListPage));

        // 多选是临时上下文：一旦离开对应页面（导航常经 RadioButton 的 TwoWay 提前改
        // CurrentPage，不能只依赖 NavigateTo 里的判断），立即退出并清空选择。
        if (ConnectionsPage.IsMultiSelect)
        {
            ConnectionsPage.ExitMultiSelectCommand.Execute(null);
        }

        if (_credentialsPage.IsMultiSelect)
        {
            _credentialsPage.ExitMultiSelectCommand.Execute(null);
        }
    }

    /// <summary>状态栏文案。</summary>
    public string SessionStatusText => _sessions.ActiveSessionCount == 0
        ? "无活动会话"
        : $"已连接 {_sessions.ActiveSessionCount} 个会话";

    /// <summary>会话状态圆点的语义色键：有活动会话时用成功绿，否则用中性灰。</summary>
    public string SessionStatusBrushKey => _sessions.ActiveSessionCount == 0
        ? "Status.Idle"
        : "Status.Success";

    /// <summary>
    /// 版本号，放底部状态栏常驻显示——之前只在设置页「常规」标签最底下才能看到，
    /// 每次要看都得点进设置，不方便。
    /// </summary>
    public string AppVersion { get; } =
        "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");

    partial void OnGlobalSearchTextChanged(string value)
    {
        // 全局搜索直接驱动连接列表，并自动切到「我的连接」以便看到结果。
        ConnectionsPage.SearchText = value;

        if (!string.IsNullOrWhiteSpace(value) && CurrentPage is not NavigationPage.Connections)
        {
            NavigateTo(NavigationPage.Connections);
        }
    }

    partial void OnSelectedTabChanged(WorkspaceTabViewModel? value)
    {
        // 多选是临时上下文：从连接/凭据列表切到某个会话 Tab 即退出并清空选择。
        if (value is SessionTabViewModel && IsConnectionListPage)
        {
            ConnectionsPage.ExitMultiSelectCommand.Execute(null);
        }

        if (value is SessionTabViewModel && CurrentPage == NavigationPage.Credentials)
        {
            _credentialsPage.ExitMultiSelectCommand.Execute(null);
        }

        // 所有 Tab 内容常驻可视树，靠 IsActive 切换可见性，
        // 这样切换 Tab 不会销毁 RDP 控件或终端 WebView。
        foreach (var tab in Tabs)
        {
            tab.IsActive = ReferenceEquals(tab, value);
        }

        OnPropertyChanged(nameof(IsSessionSelected));

        // 离开会话时退出全屏，避免用户回到列表却看不到导航。
        if (value is not SessionTabViewModel && IsSessionFullScreen)
        {
            IsSessionFullScreen = false;
        }
    }

    // ── 导航 ──────────────────────────────────────────────────────

    [RelayCommand]
    public void NavigateTo(NavigationPage page)
    {
        CurrentPage = page;

        // 收藏与最近本质上是「我的连接」的两个筛选视图，
        // 复用同一页面而不是各做一份，避免逻辑重复。
        switch (page)
        {
            case NavigationPage.Home:
                WorkspaceTab.Page = HomePage;
                WorkspaceTab.Title = "首页";
                break;

            case NavigationPage.Connections:
                ConnectionsPage.Filter = ConnectionFilter.All;
                WorkspaceTab.Page = ConnectionsPage;
                WorkspaceTab.Title = "我的连接";
                break;

            case NavigationPage.Favorites:
                ConnectionsPage.Filter = ConnectionFilter.Favorites;
                WorkspaceTab.Page = ConnectionsPage;
                WorkspaceTab.Title = "收藏";
                break;

            case NavigationPage.Recent:
                ConnectionsPage.Filter = ConnectionFilter.Recent;
                WorkspaceTab.Page = ConnectionsPage;
                WorkspaceTab.Title = "最近连接";
                break;

            case NavigationPage.Credentials:
                WorkspaceTab.Page = _credentialsPage;
                WorkspaceTab.Title = "凭据";
                break;

            case NavigationPage.Settings:
                WorkspaceTab.Page = _settingsPage;
                WorkspaceTab.Title = "设置";
                break;
        }

        SelectedTab = WorkspaceTab;
        _ = ReloadCurrentPageAsync();
    }

    /// <summary>首次显示与切换页面时加载对应数据。</summary>
    public async Task ReloadCurrentPageAsync()
    {
        try
        {
            PageLoadError = string.Empty;

            switch (CurrentPage)
            {
                case NavigationPage.Home:
                    await HomePage.LoadAsync();
                    break;

                case NavigationPage.Connections:
                case NavigationPage.Favorites:
                case NavigationPage.Recent:
                    await _credentialsPage.LoadAsync();
                    await ConnectionsPage.LoadAsync();
                    ConnectionsPage.ApplyCredentialNames(_credentialsPage.GetNameMap());
                    break;

                case NavigationPage.Credentials:
                    await _credentialsPage.LoadAsync();
                    break;

                case NavigationPage.Settings:
                    await _settingsPage.LoadHostKeysAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载页面数据失败：{Page}", CurrentPage);
            PageLoadError = "页面数据加载失败——数据库可能被占用或损坏。点「重试」；若持续，关闭所有 RemoteFlow 窗口后重开。";
        }
    }

    [RelayCommand]
    private Task RetryPageLoadAsync() => ReloadCurrentPageAsync();

    // ── 顶部动作 ──────────────────────────────────────────────────

    [RelayCommand]
    private async Task NewConnectionAsync()
    {
        NavigateTo(NavigationPage.Connections);
        await ConnectionsPage.CreateCommand.ExecuteAsync(null);
    }

    /// <summary>Ctrl+K：聚焦全局搜索。实际聚焦由视图处理。</summary>
    public event EventHandler? FocusSearchRequested;

    [RelayCommand]
    private void FocusSearch() => FocusSearchRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ToggleDetailPanel() => IsDetailPanelVisible = !IsDetailPanelVisible;

    // ── 会话 Tab 生命周期 ─────────────────────────────────────────

    private void OnSessionCreated(object? sender, IRemoteSession session)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnSessionCreated(sender, session));
            return;
        }

        var tab = new SessionTabViewModel(session, CloseSessionAsync, ReconnectAsync);

        // 会话工具条里的「全屏」由主窗口层处理（隐藏导航/详情、窗口去边框铺满）；
        // 协议专属动作（缩放、Ctrl+Alt+Del 等）由各协议视图自己订阅处理。
        tab.ActionRequested += OnSessionActionRequested;

        Tabs.Add(tab);
        SelectedTab = tab;

        // 会话 Tab 已建出，放开该 Profile 的占位；此刻再点可再次触发（走“聚焦既有”分支）。
        _openingProfileIds.Remove(session.Profile.Id);

        OnPropertyChanged(nameof(SessionStatusText));
        OnPropertyChanged(nameof(SessionStatusBrushKey));
    }

    private void OnSessionActionRequested(object? sender, SessionAction action)
    {
        if (action == SessionAction.ToggleFullScreen)
        {
            IsSessionFullScreen = !IsSessionFullScreen;
        }
    }

    private void OnSessionClosed(object? sender, Guid sessionId)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnSessionClosed(sender, sessionId));
            return;
        }

        var tab = Tabs.OfType<SessionTabViewModel>().FirstOrDefault(t => t.Session.SessionId == sessionId);
        if (tab is null)
        {
            return;
        }

        var wasSelected = ReferenceEquals(SelectedTab, tab);

        tab.ActionRequested -= OnSessionActionRequested;
        tab.Dispose();
        Tabs.Remove(tab);

        // 最后一个会话关闭时退出全屏，否则用户会停在没有导航的空白全屏里。
        if (Tabs.OfType<SessionTabViewModel>().Any() is false && IsSessionFullScreen)
        {
            IsSessionFullScreen = false;
        }

        // 关闭当前 Tab 后回到最后一个会话，没有会话则回到工作区。
        if (wasSelected)
        {
            SelectedTab = Tabs.OfType<SessionTabViewModel>().LastOrDefault() ?? (WorkspaceTabViewModel)WorkspaceTab;
        }

        OnPropertyChanged(nameof(SessionStatusText));
        OnPropertyChanged(nameof(SessionStatusBrushKey));
    }

    private async Task CloseSessionAsync(Guid sessionId) => await _sessions.CloseSessionAsync(sessionId);

    /// <summary>
    /// 统一「点设备→开会话」漏斗。同一 Profile 已有会话（连接中或已连）则聚焦其 Tab，
    /// 不新建；正在创建中（连点）则忽略本次；否则创建新会话，由 SessionCreated 开新 Tab。
    /// </summary>
    public async Task OpenSessionAsync(ConnectionProfile profile)
    {
        var existing = Tabs.OfType<SessionTabViewModel>()
            .FirstOrDefault(t => t.Session.Profile.Id == profile.Id);
        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }

        if (!_openingProfileIds.Add(profile.Id))
        {
            return; // 已有同设备的创建请求在跑，聚焦等它建完由 SessionCreated 切过去。
        }

        try
        {
            // 成功路径不在此移除占位：等 OnSessionCreated 建出 Tab 时移除，堵住连点竞态。
            await _sessions.CreateSessionAsync(profile);
        }
        catch (ConnectionException ex)
        {
            _openingProfileIds.Remove(profile.Id);
            await _dialogs.ShowMessageAsync("无法建立连接", ex.Message, DialogKind.Error);
        }
        catch (InvalidOperationException ex)
        {
            _openingProfileIds.Remove(profile.Id);
            await _dialogs.ShowMessageAsync("无法建立连接", ex.Message, DialogKind.Warning);
        }
        catch (Exception ex)
        {
            _openingProfileIds.Remove(profile.Id);
            _logger.LogError(ex, "创建会话失败：{ConnectionName}", profile.Name);
            await _dialogs.ShowMessageAsync("无法建立连接", "创建会话时发生未知错误，详情请查看日志。", DialogKind.Error);
        }
    }

    private async Task ReconnectAsync(ConnectionProfile profile)
    {
        try
        {
            await _sessions.CreateSessionAsync(profile);
        }
        catch (ConnectionException ex)
        {
            await _dialogs.ShowMessageAsync("无法重新连接", ex.Message, DialogKind.Error);
        }
        catch (InvalidOperationException ex)
        {
            await _dialogs.ShowMessageAsync("无法重新连接", ex.Message, DialogKind.Warning);
        }
    }

    /// <summary>关闭除指定 Tab 外的全部会话。</summary>
    [RelayCommand]
    private async Task CloseOtherSessionsAsync(SessionTabViewModel? keep)
    {
        foreach (var tab in Tabs.OfType<SessionTabViewModel>().Where(t => !ReferenceEquals(t, keep)).ToList())
        {
            await _sessions.CloseSessionAsync(tab.Session.SessionId);
        }
    }

    [RelayCommand]
    private async Task CloseAllSessionsAsync() => await _sessions.CloseAllAsync();

    public void Dispose()
    {
        _sessions.SessionCreated -= OnSessionCreated;
        _sessions.SessionClosed -= OnSessionClosed;

        foreach (var tab in Tabs.OfType<SessionTabViewModel>())
        {
            tab.ActionRequested -= OnSessionActionRequested;
            tab.Dispose();
        }
    }
}
