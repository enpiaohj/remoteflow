using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Application.FileTransfer;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Infrastructure.Settings;

namespace RemoteFlow.Presentation.ViewModels;

/// <summary>左侧导航的一级入口。</summary>
public enum NavigationPage
{
    Home,
    Connections,
    Favorites,
    Recent,
    Credentials,
    Settings,

    /// <summary>使用指南（内嵌页面）。Windows 独有入口；macOS 侧栏无此按钮，
    /// 其 NavigateTo switch 的 default 分支兜底，无需同步改。</summary>
    Help,

    /// <summary>关于（内嵌页面）。同 <see cref="Help"/>，macOS 侧由菜单栏「关于」承担。</summary>
    About,

    /// <summary>虚拟化（VMware ESXi / vCenter 来源与虚拟机）。Windows 独有入口；macOS 侧栏无此按钮，
    /// 其 NavigateTo switch 的 default 分支兜底。放在枚举末尾以保持既有取值不变。</summary>
    Vmware,
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
    private readonly HelpPageViewModel _helpPage = new();
    private readonly AboutPageViewModel _aboutPage;
    private readonly IDialogService _dialogs;
    private readonly ILogger<MainViewModel> _logger;
    private readonly IUiDispatcher _ui;
    private readonly IUiTimerFactory _timerFactory;
    private readonly IFileTransferLauncher? _fileTransferLauncher;
    private readonly IFileTransferConnector? _fileTransferConnector;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly AppSettings _settings;
    private readonly JsonSettingsStore? _settingsStore;
    private readonly RememberedCredentialService? _rememberedCredentials;
    private bool _restoreOffered;
    private CancellationTokenSource? _pageReloadCts;
    private Task _pageReloadTask = Task.CompletedTask;
    private int _pageReloadVersion;
    private bool _activatingWorkspacePage;

    /// <summary>正在创建会话的（连接 Id, 会话种类）。用于连点去重：会话 Tab 建出前，第二次请求不重复建。
    /// 按种类区分：同一主机的远程桌面与 PowerShell 互不阻挡。</summary>
    private readonly HashSet<(Guid ProfileId, SessionKind Kind)> _openingProfileIds = [];

    public MainViewModel(
        SessionManager sessions,
        HomePageViewModel homePage,
        ConnectionsPageViewModel connectionsPage,
        CredentialsPageViewModel credentialsPage,
        SettingsPageViewModel settingsPage,
        IDialogService dialogs,
        AppSettings settings,
        IUiDispatcher uiDispatcher,
        IUiTimerFactory timerFactory,
        ILogger<MainViewModel> logger,
        IFileTransferLauncher? fileTransferLauncher = null,
        IFileTransferConnector? fileTransferConnector = null,
        ILoggerFactory? loggerFactory = null,
        UpdateViewModel? update = null,
        VmwarePageViewModel? vmwarePage = null,
        JsonSettingsStore? settingsStore = null,
        RememberedCredentialService? rememberedCredentials = null)
    {
        _rememberedCredentials = rememberedCredentials;
        _sessions = sessions;
        _settings = settings;
        _settingsStore = settingsStore;
        Update = update;
        _aboutPage = new AboutPageViewModel { Update = update };
        _ui = uiDispatcher;
        _timerFactory = timerFactory;
        HomePage = homePage;
        ConnectionsPage = connectionsPage;
        _credentialsPage = credentialsPage;
        _settingsPage = settingsPage;
        _settingsPage.About = _aboutPage;
        _dialogs = dialogs;
        _logger = logger;
        _fileTransferLauncher = fileTransferLauncher;
        _fileTransferConnector = fileTransferConnector;
        _loggerFactory = loggerFactory;

        _sessions.MaxConcurrentSessions = settings.MaxConcurrentSessions;

        WorkspaceTab = new PageTabViewModel();
        Tabs = [WorkspaceTab];
        HostTabs = [WorkspaceTab];
        SelectedTab = WorkspaceTab;

        // 内容区的画面宿主跟随增删，但忽略换位：WPF 把集合的 Move 当成「移除 + 新增」，
        // 画面控件（RDP ActiveX / WebView2 / VNC）会被销毁重建而黑屏。
        Tabs.CollectionChanged += OnTabsChangedForHosts;

        // 会话增减时刷新导航栏的会话数角标与中断状态。
        Tabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(OpenSessionCount));
            OnPropertyChanged(nameof(ConnectionSessionCount));
            OnPropertyChanged(nameof(VmwareSessionCount));
            if (ReturnTab is { } returnTab && !Tabs.Contains(returnTab))
            {
                ReturnTab = Tabs.LastOrDefault(t => t is SessionTabViewModel or FileTransferTabViewModel);
            }

            OnPropertyChanged(nameof(CanReturnToSession));
            RaiseInterruptedChanged();
        };

        // 会话由各页面通过 SessionManager 创建，主窗口只负责把它呈现为 Tab。
        _sessions.SessionCreated += OnSessionCreated;
        _sessions.SessionClosed += OnSessionClosed;

        // 状态栏“已连接 N 个会话”改为订阅聚合 SessionsChanged 一次：会话创建 / 任意状态跳变 /
        // 移除都会触发，不再逐会话订阅 StateChanged。
        _sessions.SessionsChanged += OnSessionsChanged;

        // 设置页的「数据与备份」改动了本地数据时刷新当前页。
        _settingsPage.DataChanged += async (_, _) => await ReloadCurrentPageAsync();

        // 首页的「查看全部」等入口请求跳转。
        HomePage.NavigationRequested += (_, page) => NavigateTo(page);

        // 连接详情「查看全部历史」请求跳转到「最近连接」。
        ConnectionsPage.NavigationRequested += (_, page) => NavigateTo(page);

        // 首页 / 连接页所有「点设备→开会话」都收敛到这里统一处理：去重、聚焦、失败提示。
        HomePage.OpenConnectionRequested += async (_, profile) => await OpenSessionAsync(profile);
        ConnectionsPage.OpenConnectionRequested += async (_, profile) => await OpenSessionAsync(profile);

        // 批量连接：按顺序逐个打开，每个会话等到真正开始连接再开下一个（见 OpenSessionsSequentiallyAsync）。
        ConnectionsPage.OpenConnectionsRequested += async (_, profiles) => await OpenSessionsSequentiallyAsync(profiles);

        // 详情「打开会话 ▾ → 新建会话」：跳过去重聚焦，强制为同一设备再开一个会话。
        ConnectionsPage.NewSessionRequested += async (_, profile) => await OpenSessionAsync(profile, forceNew: true);

        // 同一主机的 PowerShell 远程会话：与远程桌面会话相互独立。
        ConnectionsPage.OpenPowerShellRequested += async (_, profile) => await OpenPowerShellAsync(profile);

        // 凭据页 → 新建主机连接（可带「引用此凭据」「连接名称」预填）；连接页 → 新建凭据。
        // 两页互相只发请求，由主视图模型切页并打开对应编辑器，页面 VM 之间不互相引用。
        _credentialsPage.NewConnectionRequested += async (_, seed) => await CreateConnectionFromSeedAsync(seed);
        ConnectionsPage.NewCredentialRequested += async (_, _) => await NewCredentialAsync();

        // 虚拟化页：打开虚拟机控制台同样走统一的「开会话」漏斗（去重聚焦、失败提示）。
        VmwarePage = vmwarePage;
        if (VmwarePage is not null)
        {
            VmwarePage.OpenConsoleRequested += async (_, profile) => await OpenSessionAsync(profile);
        }

        // 「文件传输…」入口：不建会话，直接在工作区开一个文件传输 Tab。
        if (_fileTransferLauncher is not null)
        {
            _fileTransferLauncher.OpenRequested += OnFileTransferOpenRequested;
        }

        // 首页行右键动作（连接 / 编辑 / 测试连接 / 收藏 / 定位）桥接到「我的连接」既有命令。
        HomePage.ConnectionActionRequested += async (_, args) => await HandleHomeConnectionActionAsync(args);

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

    /// <summary>应用内更新；没有更新实现的平台为 null。状态栏的「有新版本」提示绑定它。</summary>
    public UpdateViewModel? Update { get; }

    public ConnectionsPageViewModel ConnectionsPage { get; }

    /// <summary>工作区固定 Tab，承载当前导航页。</summary>
    public PageTabViewModel WorkspaceTab { get; }

    /// <summary>工作区全部 Tab：索引 0 是页面，其后为各远程会话。</summary>
    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; }

    /// <summary>
    /// 内容区画面宿主所绑定的集合：与 <see cref="Tabs"/> 成员一致，但<b>顺序固定</b>（只在末尾追加），
    /// 标签换位（<see cref="MoveTab"/>）不会让会话画面被销毁重建。顺序只影响标题栏，不影响内容区。
    /// </summary>
    public ObservableCollection<WorkspaceTabViewModel> HostTabs { get; } = [];

    private void OnTabsChangedForHosts(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case System.Collections.Specialized.NotifyCollectionChangedAction.Move:
                return;
            case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
                HostTabs.Clear();
                foreach (var tab in Tabs)
                {
                    HostTabs.Add(tab);
                }

                return;
        }

        foreach (var removed in e.OldItems?.OfType<WorkspaceTabViewModel>() ?? [])
        {
            HostTabs.Remove(removed);
        }

        foreach (var added in e.NewItems?.OfType<WorkspaceTabViewModel>() ?? [])
        {
            HostTabs.Add(added);
        }
    }

    /// <summary>
    /// 拖动调整标签顺序。只有会话 / 文件传输标签可移动，且只能在它们之间换位：
    /// 页面标签（工作台等）固定在最前，不参与排序。返回是否真的移动了。
    /// </summary>
    public bool MoveTab(WorkspaceTabViewModel tab, int newIndex)
    {
        var oldIndex = Tabs.IndexOf(tab);
        if (oldIndex < 0 || newIndex < 0 || newIndex >= Tabs.Count || newIndex == oldIndex
            || tab is PageTabViewModel || Tabs[newIndex] is PageTabViewModel)
        {
            return false;
        }

        Tabs.Move(oldIndex, newIndex);
        return true;
    }

    /// <summary>
    /// 相对移动：<paramref name="delta"/> 为 -1（前移 / 上移 / 左移）或 +1。
    /// 只在可移动标签（会话 / 文件传输）之间换位，已在边界时返回 false。
    /// </summary>
    public bool MoveTabBy(WorkspaceTabViewModel tab, int delta)
        => TryGetNeighbor(tab, delta) is { } neighbor && MoveTab(tab, Tabs.IndexOf(neighbor));

    /// <summary>该标签在 <paramref name="delta"/> 方向上是否还能移动（菜单项可用状态）。</summary>
    public bool CanMoveTabBy(WorkspaceTabViewModel tab, int delta) => TryGetNeighbor(tab, delta) is not null;

    private WorkspaceTabViewModel? TryGetNeighbor(WorkspaceTabViewModel tab, int delta)
    {
        if (tab is PageTabViewModel || delta == 0)
        {
            return null;
        }

        var movable = Tabs.Where(static t => t is not PageTabViewModel).ToList();
        var position = movable.IndexOf(tab);
        var target = position + Math.Sign(delta);
        return position >= 0 && target >= 0 && target < movable.Count ? movable[target] : null;
    }

    [ObservableProperty]
    private WorkspaceTabViewModel? _selectedTab;

    /// <summary>
    /// 从会话（或文件传输）切到页面（建凭据、改设置等）时记下离开的那个 Tab，
    /// 页面上据此给出「返回」入口，一键回到离开前的会话。切回任何会话 Tab、或该 Tab 被关闭后清除。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReturnToSession))]
    [NotifyPropertyChangedFor(nameof(ReturnTabLabel))]
    [NotifyPropertyChangedFor(nameof(ReturnTabToolTip))]
    private WorkspaceTabViewModel? _returnTab;

    /// <summary>是否存在可返回的会话（且它还开着）。</summary>
    public bool CanReturnToSession => ReturnTab is not null && Tabs.Contains(ReturnTab);

    public string ReturnTabLabel => ReturnTab is null ? string.Empty : $"返回 {ReturnTab.Title}";

    public string ReturnTabToolTip => ReturnTab switch
    {
        SessionTabViewModel session => session.TabToolTip,
        FileTransferTabViewModel transfer => transfer.TabToolTip,
        _ => string.Empty
    };

    partial void OnReturnTabChanged(WorkspaceTabViewModel? oldValue, WorkspaceTabViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnReturnTabPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnReturnTabPropertyChanged;
        }
    }

    private void OnReturnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceTabViewModel.Title))
        {
            OnPropertyChanged(nameof(ReturnTabLabel));
        }

        if (e.PropertyName is nameof(WorkspaceTabViewModel.Title) or nameof(SessionTabViewModel.TabToolTip))
        {
            OnPropertyChanged(nameof(ReturnTabToolTip));
        }
    }

    [RelayCommand]
    private void ReturnToSession()
    {
        if (ReturnTab is { } tab && Tabs.Contains(tab))
        {
            SelectedTab = tab;
        }
    }

    [ObservableProperty]
    private NavigationPage _currentPage = NavigationPage.Home;

    /// <summary>顶部全局搜索框内容。</summary>
    [ObservableProperty]
    private string _globalSearchText = string.Empty;

    /// <summary>右侧详情面板是否展开。全屏会话时自动收起。</summary>
    [ObservableProperty]
    private bool _isDetailPanelVisible = true;

    /// <summary>会话画面的档位。常规 / 窗口最大化 / 完全全屏，见 <see cref="SessionViewMode"/>。</summary>
    [ObservableProperty]
    private SessionViewMode _viewMode = SessionViewMode.Normal;

    partial void OnViewModeChanged(SessionViewMode value)
    {
        // 两个派生属性都必须通知，否则 XAML 侧绑定会静默失效（全屏了但 chrome 还在）。
        OnPropertyChanged(nameof(IsSessionFullScreen));
        OnPropertyChanged(nameof(IsScreenFull));
        OnPropertyChanged(nameof(IsNavRailHidden));
    }

    /// <summary>
    /// 左侧模块导航栏是否收起：当前选中会话（任意档位）或处于全屏档时收起，让远程画面铺满。
    /// <para>
    /// 会话里导航栏用不上（回工作区走标题栏的工作区 Tab），浅色窄栏贴着深色远程画面还很跳，
    /// 且它按「当前页面」高亮，会话中仍亮着「连接工作台」，语义上是误导。切回页面 Tab 时自动出现。
    /// </para>
    /// </summary>
    public bool IsNavRailHidden => IsSessionSelected || IsSessionFullScreen;

    /// <summary>
    /// 是否处于任意全屏档（窗口最大化 / 完全全屏）。此档位下折起左侧导航、
    /// Tab 栏、右侧详情与底部状态栏。
    /// </summary>
    public bool IsSessionFullScreen => ViewMode.IsFullScreenLevel();

    /// <summary>是否处于完全全屏档。此档位下连应用标题栏也隐藏。</summary>
    public bool IsScreenFull => ViewMode.IsScreenFull();

    /// <summary>逐档进：常规 → 窗口最大化 → 完全全屏 → 常规（F11）。</summary>
    public void AdvanceSessionView() => ViewMode = SessionViewModeRules.Advance(ViewMode);

    /// <summary>一路退到底：无论哪一档都直接回常规（药丸「退出全屏」）。</summary>
    public void ExitSessionFullScreen() => ViewMode = SessionViewModeRules.ExitFullScreen(ViewMode);

    /// <summary>完全全屏开关（药丸「完全全屏」）。</summary>
    public void ToggleScreenFullSessionView() => ViewMode = SessionViewModeRules.ToggleScreenFull(ViewMode);

    /// <summary>直达完全全屏，只进不退（RDP「启动后进入全屏」）。</summary>
    public void EnterScreenFullSessionView() => ViewMode = SessionViewMode.ScreenFull;

    /// <summary>回到常规档。所有「离开会话语境」的回退路径统一走这里。</summary>
    public void ResetSessionView() => ViewMode = SessionViewMode.Normal;

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

    /// <summary>虚拟化页（可能为 null：未注册 VMware 能力的平台 / 测试）。</summary>
    public VmwarePageViewModel? VmwarePage { get; }

    partial void OnCurrentPageChanged(NavigationPage value)
    {
        OnPropertyChanged(nameof(IsConnectionListPage));

        // 虚拟化页只在可见时自动刷新虚拟机状态。
        VmwarePage?.SetActive(value == NavigationPage.Vmware);

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

        // 页面切换统一由 CurrentPage 驱动：导航钮以 TwoWay 绑定回写 CurrentPage，
        // 这里同时负责落位和刷新——鼠标、键盘、UI Automation 与程序导航走同一条链。
        ActivateWorkspacePage(value);
        RequestPageReload(value);
    }

    /// <summary>状态栏文案。</summary>
    public string SessionStatusText => _sessions.ConnectedSessionCount == 0
        ? "无活动会话"
        : $"已连接 {_sessions.ConnectedSessionCount} 个会话";

    /// <summary>会话状态圆点的语义色键：有已连接会话时用「已连接」蓝（与列表一致），否则用中性灰。</summary>
    public string SessionStatusBrushKey => _sessions.ConnectedSessionCount == 0
        ? "Status.Idle"
        : "Status.Info";

    /// <summary>
    /// 当前打开的会话标签页数量，含连接中 / 重连中 / 已断开——只要 Tab 还在，
    /// 退出就意味着用户得重新连一遍，因此都计入。
    /// <para>
    /// 与 <see cref="SessionStatusText"/> 不同，这里刻意不做变更通知：它只在关闭主窗口的
    /// 那一刻被同步读取一次，没有任何绑定会订阅它。
    /// </para>
    /// </summary>
    public int OpenSessionCount => Tabs.Count(t => t is SessionTabViewModel);

    /// <summary>
    /// 是否有会话处于中断（失败 / 已断开）状态。左侧导航「连接工作台」的会话数角标据此转警示色。
    /// 直接读会话状态（状态事件触发时已更新），不读 Tab 的派生属性——后者的更新与本通知谁先到不确定。
    /// </summary>
    public bool HasInterruptedSession => Tabs.OfType<SessionTabViewModel>()
        .Any(t => t.Session.State is ConnectionState.Failed or ConnectionState.Disconnected);

    /// <summary>
    /// 「连接工作台」图标的角标：只算普通连接的会话。VMware 控制台会话属于「虚拟化」页，
    /// 不能算进这里（否则开一个虚拟机控制台，计数却跑到连接工作台图标上）。
    /// </summary>
    public int ConnectionSessionCount => Tabs.OfType<SessionTabViewModel>().Count(t => !t.IsVmwareConsole);

    /// <summary>「虚拟化」图标的角标：只算 VMware 控制台会话。</summary>
    public int VmwareSessionCount => Tabs.OfType<SessionTabViewModel>().Count(t => t.IsVmwareConsole);

    public bool HasInterruptedConnectionSession => Tabs.OfType<SessionTabViewModel>()
        .Any(t => !t.IsVmwareConsole && t.Session.State is ConnectionState.Failed or ConnectionState.Disconnected);

    public bool HasInterruptedVmwareSession => Tabs.OfType<SessionTabViewModel>()
        .Any(t => t.IsVmwareConsole && t.Session.State is ConnectionState.Failed or ConnectionState.Disconnected);

    private void RaiseInterruptedChanged()
    {
        OnPropertyChanged(nameof(HasInterruptedSession));
        OnPropertyChanged(nameof(HasInterruptedConnectionSession));
        OnPropertyChanged(nameof(HasInterruptedVmwareSession));
    }

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

    partial void OnSelectedTabChanged(WorkspaceTabViewModel? oldValue, WorkspaceTabViewModel? newValue)
    {
        // 多选是临时上下文：从连接/凭据列表切到某个会话 Tab 即退出并清空选择。
        if (newValue is SessionTabViewModel && IsConnectionListPage)
        {
            ConnectionsPage.ExitMultiSelectCommand.Execute(null);
        }

        if (newValue is SessionTabViewModel && CurrentPage == NavigationPage.Credentials)
        {
            _credentialsPage.ExitMultiSelectCommand.Execute(null);
        }

        // 所有 Tab 内容常驻可视树，靠 IsActive 切换可见性，
        // 这样切换 Tab 不会销毁 RDP 控件或终端 WebView。
        foreach (var tab in Tabs)
        {
            tab.IsActive = ReferenceEquals(tab, newValue);
        }

        OnPropertyChanged(nameof(IsSessionSelected));
        OnPropertyChanged(nameof(IsNavRailHidden));

        // 离开会话去页面：记下来源，页面上给「返回」；回到任何会话 / 传输 Tab 则清除。
        if (newValue is PageTabViewModel && oldValue is SessionTabViewModel or FileTransferTabViewModel)
        {
            ReturnTab = oldValue;
        }
        else if (newValue is SessionTabViewModel or FileTransferTabViewModel)
        {
            ReturnTab = null;
        }

        // 离开会话时回到常规档，避免用户回到列表却看不到导航。
        if (newValue is not SessionTabViewModel)
        {
            ResetSessionView();
        }

        // 会话期间页面数据可能已变化；用户直接点工作区 Tab 返回时做一次轻量刷新（不重新探测主机）。
        // 导航切页内部也会选中 WorkspaceTab，那条路径由 CurrentPage hook 负责，避免重复加载。
        if (!_activatingWorkspacePage
            && oldValue is SessionTabViewModel
            && ReferenceEquals(newValue, WorkspaceTab))
        {
            RequestPageReload(CurrentPage, lightweight: true);
        }
    }

    // ── 导航 ──────────────────────────────────────────────────────

    [RelayCommand]
    public void NavigateTo(NavigationPage page)
    {
        // 「关于」（含软件更新）是设置页的一个选项卡：按「设置页 + 关于选项卡」导航，
        // 让左侧导航高亮落在「设置」上（侧栏不再单独放「关于」）。
        if (page == NavigationPage.About)
        {
            _settingsPage.ActiveTab = SettingsTab.About;
            page = NavigationPage.Settings;
        }

        if (CurrentPage != page)
        {
            // 属性变更 hook 是切页与刷新的唯一入口。
            CurrentPage = page;
            return;
        }

        // 同页再次点击没有 PropertyChanged，仍应重新读取数据和实时状态。
        ActivateWorkspacePage(page);
        RequestPageReload(page);
    }

    /// <summary>切换工作区页面内容、标题并选中工作区 Tab；数据刷新由统一导航链另行触发。</summary>
    private void ActivateWorkspacePage(NavigationPage page)
    {
        // 收藏与最近本质上是「我的连接」的两个筛选视图，
        // 复用同一页面而不是各做一份，避免逻辑重复。
        switch (page)
        {
            case NavigationPage.Home:
                WorkspaceTab.Page = HomePage;
                WorkspaceTab.Title = "首页";
                WorkspaceTab.Icon = ""; // Icon.Home
                WorkspaceTab.IconKey = "Ui.Home";
                break;

            case NavigationPage.Connections:
                ConnectionsPage.Filter = ConnectionFilter.All;
                WorkspaceTab.Page = ConnectionsPage;
                WorkspaceTab.Title = "连接工作台";
                WorkspaceTab.Icon = ""; // Icon.Connections
                WorkspaceTab.IconKey = "Ui.Connections";
                break;

            case NavigationPage.Favorites:
                ConnectionsPage.Filter = ConnectionFilter.Favorites;
                WorkspaceTab.Page = ConnectionsPage;
                WorkspaceTab.Title = "收藏";
                WorkspaceTab.Icon = ""; // Icon.Favorite
                WorkspaceTab.IconKey = "Ui.Favorite";
                break;

            case NavigationPage.Recent:
                ConnectionsPage.Filter = ConnectionFilter.Recent;
                WorkspaceTab.Page = ConnectionsPage;
                WorkspaceTab.Title = "最近连接";
                WorkspaceTab.Icon = ""; // Icon.Recent
                WorkspaceTab.IconKey = "Ui.Recent";
                break;

            case NavigationPage.Credentials:
                WorkspaceTab.Page = _credentialsPage;
                WorkspaceTab.Title = "凭据";
                WorkspaceTab.Icon = ""; // Icon.Credential
                WorkspaceTab.IconKey = "Ui.Credential";
                break;

            case NavigationPage.Vmware when VmwarePage is not null:
                WorkspaceTab.Page = VmwarePage;
                WorkspaceTab.Title = "虚拟化";
                WorkspaceTab.Icon = ""; // Icon.Server
                WorkspaceTab.IconKey = "Ui.Server";
                break;

            case NavigationPage.Settings:
                // 托盘可经 SettingsPageViewModel.SetLaunchOnStartup 改开机启动，
                // 进入设置页时把 AppSettings 最新值同步回开关，保证双向一致。
                _settingsPage.ReloadStartup();
                WorkspaceTab.Page = _settingsPage;
                WorkspaceTab.Title = "设置";
                WorkspaceTab.Icon = ""; // Icon.Settings
                WorkspaceTab.IconKey = "Ui.Settings";
                break;

            case NavigationPage.Help:
                WorkspaceTab.Page = _helpPage;
                WorkspaceTab.Title = "使用指南";
                WorkspaceTab.Icon = ""; // Icon.Help
                WorkspaceTab.IconKey = "Ui.Help";
                break;

            case NavigationPage.About:
                // 「关于」（含软件更新）放在 设置 → 关于 选项卡里：用户去设置里找更新入口就能找到。
                // 导航栏 ℹ、托盘「检查更新」、状态栏更新提示等所有「关于」入口都落到这里。
                _settingsPage.ReloadStartup();
                _settingsPage.ActiveTab = SettingsTab.About;
                WorkspaceTab.Page = _settingsPage;
                WorkspaceTab.Title = "设置";
                WorkspaceTab.Icon = ""; // Icon.Settings
                WorkspaceTab.IconKey = "Ui.Settings";
                break;
        }

        _activatingWorkspacePage = true;
        try
        {
            SelectedTab = WorkspaceTab;
        }
        finally
        {
            _activatingWorkspacePage = false;
        }
    }

    /// <summary>重新加载当前页；会取消上一批页面加载，并丢弃过期批次的错误结果。</summary>
    public Task ReloadCurrentPageAsync()
    {
        RequestPageReload(CurrentPage);
        return _pageReloadTask;
    }

    /// <param name="lightweight">
    /// true：从会话 Tab 切回工作区。首页只重读列表、沿用上次在线探测结果；连接列表的会话状态
    /// 已由 SessionsChanged 实时同步，不整表重建（避免来回切换会话时反复重建列表并探测全部主机）。
    /// </param>
    private void RequestPageReload(NavigationPage page, bool lightweight = false)
    {
        if (lightweight && page is NavigationPage.Connections or NavigationPage.Favorites or NavigationPage.Recent)
        {
            return;
        }

        _pageReloadCts?.Cancel();

        var cts = new CancellationTokenSource();
        _pageReloadCts = cts;
        var version = ++_pageReloadVersion;
        _pageReloadTask = ReloadPageAsync(page, version, cts, lightweight);
    }

    private async Task ReloadPageAsync(
        NavigationPage page,
        int version,
        CancellationTokenSource batchCts,
        bool lightweight)
    {
        var ct = batchCts.Token;
        try
        {
            if (version == _pageReloadVersion)
            {
                PageLoadError = string.Empty;
            }

            switch (page)
            {
                case NavigationPage.Home:
                    await (lightweight ? HomePage.RefreshAsync(ct) : HomePage.LoadAsync(ct));
                    break;

                case NavigationPage.Connections:
                case NavigationPage.Favorites:
                case NavigationPage.Recent:
                    await _credentialsPage.LoadAsync(ct);
                    ct.ThrowIfCancellationRequested();
                    await ConnectionsPage.LoadAsync(ct);
                    ct.ThrowIfCancellationRequested();
                    if (version == _pageReloadVersion && CurrentPage == page)
                    {
                        ConnectionsPage.ApplyCredentialNames(_credentialsPage.GetNameMap());
                    }
                    break;

                case NavigationPage.Credentials:
                    await _credentialsPage.LoadAsync(ct);
                    break;

                case NavigationPage.Vmware when VmwarePage is not null:
                    await VmwarePage.LoadAsync(ct);
                    break;

                case NavigationPage.Settings:
                    await _settingsPage.LoadHostKeysAsync(ct);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 快速切页或重复刷新：旧批次静默退出，不覆盖当前页状态。
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载页面数据失败：{Page}", page);
            if (version == _pageReloadVersion && CurrentPage == page)
            {
                PageLoadError = "页面数据加载失败——数据库可能被占用或损坏。点「重试」；若持续，关闭所有 RemoteFlow 窗口后重开。";
            }
        }
        finally
        {
            if (ReferenceEquals(_pageReloadCts, batchCts))
            {
                _pageReloadCts = null;
            }

            batchCts.Dispose();
        }
    }

    [RelayCommand]
    private Task RetryPageLoadAsync() => ReloadCurrentPageAsync();

    // ── 首页行右键动作 ─────────────────────────────────────────────

    /// <summary>
    /// 首页「最近连接 / 收藏」行右键动作的统一分发：桥接到「我的连接」既有命令，
    /// 不在首页复制编辑 / 删除 / 收藏等实现。首页为快速访问，不含复制 / 删除。
    /// </summary>
    private async Task HandleHomeConnectionActionAsync(HomeConnectionActionEventArgs args)
    {
        var item = args.Item;
        if (item is null)
        {
            return;
        }

        try
        {
            switch (args.Action)
            {
                case HomeRowActions.Connect:
                    await OpenSessionAsync(item.Profile);
                    return;

                case HomeRowActions.Disconnect:
                    // 断开该 Profile 的全部活动会话；行状态熄灭由 SessionsChanged 驱动，无需在此刷新首页。
                    await ConnectionsPage.DisconnectItemCommand.ExecuteAsync(item);
                    return;

                case HomeRowActions.NewSession:
                    // 已有会话时强制再开一个（跳过聚焦去重）。
                    ConnectionsPage.NewSessionCommand.Execute(item);
                    return;

                case HomeRowActions.Reconnect:
                    await ConnectionsPage.ReconnectItemCommand.ExecuteAsync(item);
                    return;

                case HomeRowActions.PowerShell:
                await OpenPowerShellAsync(item.Profile);
                break;
            case HomeRowActions.Manage:
                    // 跳到「我的连接」全部视图并让该连接可见、选中。
                    GlobalSearchText = string.Empty;
                    NavigateTo(NavigationPage.Connections);
                    await _pageReloadTask;
                    ConnectionsPage.SelectById(item.Id);
                    return;

                case HomeRowActions.Edit:
                    await ConnectionsPage.EditCommand.ExecuteAsync(item);
                    break;

                case HomeRowActions.Test:
                    await ConnectionsPage.TestConnectionCommand.ExecuteAsync(item);
                    break;

                case HomeRowActions.FileTransfer:
                    // 在工作区开 Tab，不改变首页列表，不需要刷新。
                    ConnectionsPage.OpenFileTransferCommand.Execute(item);
                    return;

                case HomeRowActions.Favorite:
                    await ConnectionsPage.ToggleFavoriteCommand.ExecuteAsync(item);
                    break;

                default:
                    return;
            }

            // 编辑 / 收藏等会改变首页列表（名称 / 收藏状态），回到首页前刷新。
            if (CurrentPage == NavigationPage.Home)
            {
                await HomePage.LoadAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理首页行右键动作失败：{Action}", args.Action);
            await _dialogs.ShowMessageAsync("操作失败", "执行该操作时出错，详情请查看日志。", DialogKind.Error);
        }
    }

    // ── 顶部动作 ──────────────────────────────────────────────────

    [RelayCommand]
    private Task NewConnectionAsync() => CreateConnectionAsync(null);

    /// <summary>
    /// 打开「新建连接」对话框并落库。托盘「新建连接 → RDP / SSH / VNC」入口会传入
    /// <paramref name="preselectedProtocol"/>，让编辑器新建分支按该协议初始化（含默认端口与
    /// 全局协议默认值）；顶部「新建连接」按钮不传协议，沿用默认 RDP。复用
    /// ConnectionsPageViewModel 的落库 / 刷新 / 选中 / 可选立即连接链路，不在调用侧重复实现。
    /// </summary>
    public Task CreateConnectionAsync(ProtocolType? preselectedProtocol = null)
    {
        NavigateTo(NavigationPage.Connections);
        return ConnectionsPage.CreateConnectionAsync(preselectedProtocol);
    }

    private Task CreateConnectionFromSeedAsync(ConnectionEditorSeed? seed)
    {
        NavigateTo(NavigationPage.Connections);
        return ConnectionsPage.CreateConnectionAsync(seed?.Protocol, seed?.GroupId, seed);
    }

    [RelayCommand]
    private async Task NewCredentialAsync()
    {
        NavigateTo(NavigationPage.Credentials);
        await _credentialsPage.CreateCommand.ExecuteAsync(null);
    }

    /// <summary>标题栏「+」→ 新建分组：切到连接工作台并打开新建分组对话框。</summary>
    [RelayCommand]
    private async Task NewGroupAsync()
    {
        NavigateTo(NavigationPage.Connections);
        await ConnectionsPage.CreateGroupCommand.ExecuteAsync(null);
    }

    /// <summary>标题栏「+」→ 添加 ESXi / vCenter 来源：切到虚拟化页并打开来源编辑器。</summary>
    [RelayCommand]
    private async Task AddVmwareSourceAsync()
    {
        if (VmwarePage is null)
        {
            return;
        }

        NavigateTo(NavigationPage.Vmware);
        await VmwarePage.AddSourceCommand.ExecuteAsync(null);
    }

    // ── 记住并恢复上次的会话 ──────────────────────────────────────

    /// <summary>
    /// 退出前记下仍开着的会话所对应的连接 Id（只记 Id，不含凭据；VMware 控制台会话不记）。
    /// 在用户已确认退出、会话尚未关闭时调用。写盘走线程池，避免在 UI 线程上阻塞等待异步 IO。
    /// </summary>
    public void SaveSessionSnapshot()
    {
        _settings.LastSessionProfileIds =
        [
            .. Tabs.OfType<SessionTabViewModel>()
                // 附加会话（PowerShell）不记：快照只存连接 Id，恢复时会变成远程桌面。
                .Where(t => !t.IsVmwareConsole && t.Session.Kind == SessionKind.Default)
                .Select(t => t.Session.Profile.Id)
                .Distinct()
                .Select(id => id.ToString())
        ];
        PersistSettings();
    }

    private void PersistSettings()
    {
        if (_settingsStore is null)
        {
            return;
        }

        try
        {
            Task.Run(() => _settingsStore.SaveAsync(_settings)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存会话列表失败（尽力而为，不影响退出）");
        }
    }

    /// <summary>
    /// 启动后询问是否恢复上次退出时还开着的会话。每次启动只询问一次；记录询问后即清空，
    /// 不论用户选择什么都不会重复提示。需要输入密码的连接会在打开时照常弹出输入框。
    /// </summary>
    public async Task OfferRestoreSessionsAsync()
    {
        if (_restoreOffered)
        {
            return;
        }

        _restoreOffered = true;
        if (!_settings.OfferRestoreSessions || _settings.LastSessionProfileIds.Count == 0)
        {
            return;
        }

        var ids = _settings.LastSessionProfileIds
            .Select(s => Guid.TryParse(s, out var id) ? id : (Guid?)null)
            .OfType<Guid>()
            .ToList();

        _settings.LastSessionProfileIds = [];
        PersistSettings();

        var profiles = await _sessions.GetProfilesAsync(ids);
        if (profiles.Count == 0 || Tabs.OfType<SessionTabViewModel>().Any())
        {
            return;
        }

        // 一行一个、带协议，比用顿号连成一长串好读；太多时只列前 8 个。
        const int listed = 8;
        var lines = profiles.Take(listed).Select(p => $"•  {p.Name}（{p.Protocol.ToString().ToUpperInvariant()}）").ToList();
        if (profiles.Count > listed)
        {
            lines.Add($"……另有 {profiles.Count - listed} 个");
        }

        if (await _dialogs.ConfirmAsync(
                "恢复上次的会话",
                $"上次退出时有 {profiles.Count} 个会话还开着：\n\n{string.Join("\n", lines)}\n\n要现在重新打开吗？需要输入密码的连接会照常询问。",
                "重新打开"))
        {
            await OpenSessionsSequentiallyAsync(profiles);
        }
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
        if (_ui.RequeueIfNeeded(() => OnSessionCreated(sender, session)))
        {
            return;
        }

        SessionTabViewModel tab;
        try
        {
            var kind = session.Kind;
            tab = new SessionTabViewModel(
                session, CloseSessionAsync, profile => ReconnectAsync(profile, kind), _ui, _timerFactory, _dialogs, _logger,
                _fileTransferLauncher is null ? null : _fileTransferLauncher.Open,
                account: _sessions.GetSessionAccount(session.SessionId),
                openPowerShell: _sessions.SupportsSessionKind(session.Profile, SessionKind.PowerShell) ? OpenPowerShellAsync : null);

            // 会话工具条里的「全屏」由主窗口层处理（隐藏导航/详情、窗口去边框铺满）；
            // 协议专属动作（缩放、Ctrl+Alt+Del 等）由各协议视图自己订阅处理。
            tab.ActionRequested += OnSessionActionRequested;

            Tabs.Add(tab);
            SelectedTab = tab;
        }
        catch (Exception ex)
        {
            // Tab 没建出来，会话却已登记在 SessionManager 里：不收尾的话它会一直计入状态栏 / 托盘 /
            // 「已有会话」判断，却在工作区里看不到也关不掉；同时该连接的创建占位也不会释放，
            // 之后再点这台设备会被当成「正在创建」而静默忽略。
            _logger.LogError(ex, "为会话 {SessionId} 创建 Tab 失败，关闭该会话", session.SessionId);
            _openingProfileIds.Remove((session.Profile.Id, session.Kind));
            _ = CloseOrphanSessionAsync(session.SessionId);
            return;
        }

        // 状态栏文案 / 圆点刷新统一由 _sessions.SessionsChanged → OnSessionsChanged 覆盖（创建即触发），
        // 这里不再逐会话订阅 StateChanged；下方即时 OnPropertyChanged 可保留作立刻刷新（无害）。

        // 会话 Tab 已建出，放开该 Profile 的占位；此刻再点可再次触发（走“聚焦既有”分支）。
        _openingProfileIds.Remove((session.Profile.Id, session.Kind));

        OnPropertyChanged(nameof(SessionStatusText));
        OnPropertyChanged(nameof(SessionStatusBrushKey));
    }

    private async Task CloseOrphanSessionAsync(Guid sessionId)
    {
        try
        {
            await _sessions.CloseSessionAsync(sessionId);
            await _dialogs.ShowMessageAsync("无法建立连接", "创建会话窗口时发生错误，该会话已关闭，详情请查看日志。", DialogKind.Error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "关闭无 Tab 的会话 {SessionId} 失败", sessionId);
        }
    }

    private void OnSessionActionRequested(object? sender, SessionAction action)
    {
        switch (action)
        {
            case SessionAction.AdvanceFullScreen:
                AdvanceSessionView();
                break;

            case SessionAction.ExitFullScreen:
                ExitSessionFullScreen();
                break;

            case SessionAction.ToggleScreenFull:
                ToggleScreenFullSessionView();
                break;

            case SessionAction.EnterScreenFull:
                // 只进不退：供「启动后进入全屏」在连接成功后把应用切入完全全屏。
                EnterScreenFullSessionView();
                break;
        }
    }

    private void OnSessionClosed(object? sender, Guid sessionId)
    {
        if (_ui.RequeueIfNeeded(() => OnSessionClosed(sender, sessionId)))
        {
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
        if (ReferenceEquals(ReturnTab, tab))
        {
            ReturnTab = null;
        }

        // 最后一个会话关闭时回到常规档，否则用户会停在没有导航的空白全屏里。
        if (Tabs.OfType<SessionTabViewModel>().Any() is false)
        {
            ResetSessionView();
        }

        // 关闭当前 Tab 后回到最后一个会话，没有会话则回到工作区。
        if (wasSelected)
        {
            SelectedTab = Tabs.OfType<SessionTabViewModel>().LastOrDefault() ?? (WorkspaceTabViewModel)WorkspaceTab;
        }

        OnPropertyChanged(nameof(SessionStatusText));
        OnPropertyChanged(nameof(SessionStatusBrushKey));
    }

    /// <summary>
    /// 会话集合快照变化（创建 / 任意状态跳变 / 移除）后刷新状态栏“已连接 N 个会话”文案与圆点。
    /// 订阅 SessionManager 聚合的 <see cref="SessionManager.SessionsChanged"/> 一次即可，
    /// 不再逐会话订阅 StateChanged。
    /// </summary>
    private void OnSessionsChanged(object? sender, EventArgs e)
        => RaiseSessionStatusChanged();

    /// <summary>
    /// 状态事件可能来自协议库的后台线程（状态跳变在后台触发）；切回 UI 线程再触发
    /// <see cref="SessionStatusText"/> / <see cref="SessionStatusBrushKey"/> 的刷新。
    /// </summary>
    private void RaiseSessionStatusChanged()
    {
        if (_ui.RequeueIfNeeded(RaiseSessionStatusChanged))
        {
            return;
        }

        OnPropertyChanged(nameof(SessionStatusText));
        OnPropertyChanged(nameof(SessionStatusBrushKey));
        RaiseInterruptedChanged();
    }

    // ── 文件传输 Tab ──────────────────────────────────────────────

    /// <summary>是否有文件传输 Tab 存在进行中的传输。退出应用前据此确认。</summary>
    public bool HasActiveFileTransfers =>
        Tabs.OfType<FileTransferTabViewModel>().Any(t => t.FileTransfer.HasActiveTransfers);

    private void OnFileTransferOpenRequested(object? sender, ConnectionProfile profile)
    {
        if (_ui.RequeueIfNeeded(() => OnFileTransferOpenRequested(sender, profile)))
        {
            return;
        }

        // 同一连接只开一个 Tab：再次打开则聚焦，避免对同一台主机重复建立传输连接。
        var existing = Tabs.OfType<FileTransferTabViewModel>().FirstOrDefault(t => t.Profile.Id == profile.Id);
        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }

        if (_fileTransferConnector is null)
        {
            return;
        }

        var viewModel = new FileTransferViewModel(
            ct => _fileTransferConnector.OpenAsync(profile, ct),
            _dialogs,
            _ui,
            _loggerFactory?.CreateLogger<FileTransferViewModel>() ?? NullLogger<FileTransferViewModel>.Instance,
            $"文件传输 · {profile.Name}");

        var tab = new FileTransferTabViewModel(profile, viewModel, CloseFileTransferTabAsync);
        Tabs.Add(tab);
        SelectedTab = tab;

        // Tab 已经可见，用户能立刻看到「正在连接…」；失败不抛异常，由面板显示并可重试。
        _ = viewModel.EnsureConnectedAsync();
    }

    private async Task CloseFileTransferTabAsync(FileTransferTabViewModel tab)
    {
        if (tab.FileTransfer.HasActiveTransfers
            && !await _dialogs.ConfirmAsync(
                "关闭文件传输",
                "还有文件正在传输，关闭会取消这些传输，并清理尚未完成的临时文件。\n\n确定要关闭吗？",
                "关闭并取消",
                isDanger: true))
        {
            return;
        }

        var wasSelected = ReferenceEquals(SelectedTab, tab);
        Tabs.Remove(tab);
        tab.Dispose();
        if (ReferenceEquals(ReturnTab, tab))
        {
            ReturnTab = null;
        }

        if (wasSelected)
        {
            SelectedTab = Tabs.LastOrDefault(t => t is not PageTabViewModel) ?? (WorkspaceTabViewModel)WorkspaceTab;
        }
    }

    /// <summary>会话还连着（含连接中 / 重连中）：关闭它会真的断开远端，值得先确认。已断开 / 失败的会话直接关。</summary>
    private static bool IsLive(SessionTabViewModel tab)
        => tab.Session.State is ConnectionState.Connected or ConnectionState.Connecting or ConnectionState.Reconnecting;

    /// <summary>
    /// 关闭会话前的确认（设置里可关）。一批会话只问一次；没有仍连着的会话时不打扰。
    /// 只对「用户主动关闭」的入口生效，退出应用等流程有各自的确认。
    /// </summary>
    private async Task<bool> ConfirmCloseAsync(IReadOnlyList<SessionTabViewModel> tabs)
    {
        if (!_settings.ConfirmCloseSession)
        {
            return true;
        }

        var live = tabs.Where(IsLive).ToList();
        if (live.Count == 0)
        {
            return true;
        }

        var what = live.Count == 1 ? $"「{live[0].Title}」" : $"{live.Count} 个会话";
        return await _dialogs.ConfirmAsync(
            "关闭当前会话",
            $"关闭后将断开与远端主机的连接：{what}。\n\n是否关闭当前会话？",
            "关闭会话",
            isDanger: true);
    }

    private async Task CloseSessionAsync(Guid sessionId)
    {
        // 只对「当前正在看的会话」确认（防误点关闭）；关闭别的标签、批量关闭都是有意为之，不打扰。
        var tab = Tabs.OfType<SessionTabViewModel>().FirstOrDefault(t => t.Session.SessionId == sessionId);
        if (tab is not null && ReferenceEquals(tab, SelectedTab) && !await ConfirmCloseAsync([tab]))
        {
            return;
        }

        await _sessions.CloseSessionAsync(sessionId);
    }

    /// <summary>
    /// 统一「点设备→开会话」漏斗。同一 Profile 已有会话（连接中或已连）则聚焦其 Tab，
    /// 不新建；正在创建中（连点）则忽略本次；否则创建新会话，由 SessionCreated 开新 Tab。
    /// <paramref name="forceNew"/> 供「新建会话」动作跳过去重聚焦，强制再开一个会话。
    /// </summary>
    public Task OpenSessionAsync(ConnectionProfile profile, bool forceNew = false)
        => OpenSessionCoreAsync(profile, forceNew, failures: null, SessionKind.Default);

    /// <summary>打开该主机的 PowerShell 远程会话；已有则聚焦它。不影响同一主机的远程桌面会话。</summary>
    public Task OpenPowerShellAsync(ConnectionProfile profile)
        => OpenSessionCoreAsync(profile, forceNew: false, failures: null, SessionKind.PowerShell);

    /// <summary>一台主机在批量连接里没能打开的原因。</summary>
    private sealed record BatchFailure(string Name, string Reason);

    /// <param name="failures">
    /// 非 null（批量连接）时，创建失败只记录到这里、不逐个弹框：模态框会卡住整批后面的连接，
    /// 失败几台就连弹几次。由批量流程在结束时统一汇总提示。null 时保持单个连接的原有弹框行为。
    /// </param>
    private async Task OpenSessionCoreAsync(
        ConnectionProfile profile, bool forceNew, List<BatchFailure>? failures, SessionKind kind)
    {
        if (!forceNew)
        {
            var existing = Tabs.OfType<SessionTabViewModel>()
                .FirstOrDefault(t => t.Session.Profile.Id == profile.Id && t.Session.Kind == kind);
            if (existing is not null)
            {
                SelectedTab = existing;
                return;
            }
        }

        var placeholder = (profile.Id, kind);
        if (!_openingProfileIds.Add(placeholder))
        {
            return; // 已有同设备的创建请求在跑，聚焦等它建完由 SessionCreated 切过去。
        }

        ResolvedCredential? entered = null;
        try
        {
            var (proceed, credential) = await PromptCredentialIfNeededAsync(profile, kind);
            if (!proceed)
            {
                _openingProfileIds.Remove(placeholder); // 用户取消输入：放弃本次连接，不算失败。
                return;
            }

            entered = credential;

            // 成功路径不在此移除占位：等 OnSessionCreated 建出 Tab 时移除，堵住连点竞态。
            await _sessions.CreateSessionAsync(profile, credentialOverride: entered, kind: kind);
        }
        catch (ConnectionException ex)
        {
            entered?.Dispose();
            _openingProfileIds.Remove(placeholder);
            await ReportOpenFailureAsync(profile, ex.Message, DialogKind.Error, failures);
        }
        catch (InvalidOperationException ex)
        {
            entered?.Dispose();
            _openingProfileIds.Remove(placeholder);
            await ReportOpenFailureAsync(profile, ex.Message, DialogKind.Warning, failures);
        }
        catch (Exception ex)
        {
            entered?.Dispose();
            _openingProfileIds.Remove(placeholder);
            _logger.LogError(ex, "创建会话失败：{ConnectionName}", profile.Name);
            await ReportOpenFailureAsync(profile, "创建会话时发生未知错误，详情请查看日志。", DialogKind.Error, failures);
        }
    }

    /// <summary>
    /// 连接没分配凭据 / 凭据只有账户名时，弹框让用户当场输入（每次连接都输入，不保存）。
    /// <c>Proceed=false</c> 表示用户取消，调用方放弃本次连接。平台不提供该对话框（macOS）时原样放行。
    /// </summary>
    private async Task<(bool Proceed, ResolvedCredential? Credential)> PromptCredentialIfNeededAsync(
        ConnectionProfile profile, SessionKind kind)
    {
        if (!_dialogs.SupportsConnectionCredentialPrompt)
        {
            return (true, null);
        }

        var requirement = await _sessions.GetCredentialRequirementAsync(profile);
        if (requirement.Prompt == ConnectionCredentialPrompt.None)
        {
            return (true, null);
        }

        var saved = (await _sessions.GetSelectableCredentialsAsync(profile))
            .Where(c => c.Id != profile.CredentialId)
            .Select(c => new SavedCredentialChoice(
                c.Id,
                c.Name,
                string.IsNullOrEmpty(c.Domain) ? c.Username : $"{c.Domain}\\{c.Username}"))
            .ToList();

        var input = await _dialogs.PromptConnectionCredentialAsync(
            new ConnectionCredentialRequest(profile.Name,
                // PowerShell 远程不走连接里填的 RDP 端口，别让输入框写错目标。
                kind == SessionKind.PowerShell ? $"{profile.Host}（PowerShell 远程）" : $"{profile.Host}:{profile.Port}",
                requirement, saved));
        if (input is null)
        {
            return (false, null);
        }

        // 在「更多选项」里选了已保存的凭据：直接用它连接；勾选记住则把它分配给该连接。
        if (input.SavedCredentialId is { } savedId)
        {
            var resolved = await _sessions.ResolveSavedCredentialAsync(savedId);
            if (resolved is null)
            {
                await _dialogs.ShowMessageAsync("无法使用该凭据", "该凭据的密码或密钥已不在本机保险库中，请重新选择或编辑该凭据。", DialogKind.Warning);
                return (false, null);
            }

            if (input.Remember && _rememberedCredentials is not null)
            {
                try
                {
                    await _rememberedCredentials.AssignAsync(profile, savedId);
                    _ = ReloadCurrentPageAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "分配凭据失败：{ConnectionName}", profile.Name);
                }
            }

            return (true, resolved);
        }

        // VNC 允许留空（服务器可能不要口令）：此时不带凭据，由协议层按「无认证」处理。
        if (input.Password.Length == 0 && !requirement.NeedsUsername)
        {
            return (true, null);
        }

        // 勾选「记住我的凭据」：新建 / 补全凭据并分配给该连接，下次不再询问。
        // 保存失败不挡住这次连接，只提示一下。
        if (input.Remember && input.Password.Length > 0 && _rememberedCredentials is not null)
        {
            try
            {
                await _rememberedCredentials.RememberAsync(
                    profile, requirement, input.Username, input.Domain, input.Password,
                    input.CredentialName, input.CredentialType);
                _ = ReloadCurrentPageAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "记住凭据失败：{ConnectionName}", profile.Name);
                await _dialogs.ShowMessageAsync("无法保存凭据", "这次连接会继续，但凭据没有保存，下次仍需输入。", DialogKind.Warning);
            }
        }

        return (true, new ResolvedCredential
        {
            Type = requirement.Type,
            Username = input.Username,
            Domain = input.Domain,
            Password = input.Password,
        });
    }

    private Task ReportOpenFailureAsync(
        ConnectionProfile profile, string message, DialogKind kind, List<BatchFailure>? failures)
    {
        if (failures is null)
        {
            return _dialogs.ShowMessageAsync("无法建立连接", message, kind);
        }

        failures.Add(new BatchFailure(profile.Name, message));
        return Task.CompletedTask;
    }

    /// <summary>单个会话「开始连接」的最长等待。超时就继续下一个，不让一个迟迟不动的会话卡住整批。</summary>
    public TimeSpan BatchStartTimeout { get; set; } = TimeSpan.FromSeconds(8);

    private static readonly TimeSpan BatchStartPoll = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// 批量打开会话：一个一个来，每个新会话保持选中（可见），等它离开「准备中」、真正开始连接后再开下一个。
    /// <para>
    /// RDP / SSH 的画面控件要可见才会发起连接，而新 Tab 创建时就会被选中；
    /// 连发的话每个 Tab 只被选中一瞬间，来不及开始连接就被顶掉，停在「准备中」，要手动点开才连。
    /// 逐个等待把这件事变成顺序保证。已有会话的设备由 <see cref="OpenSessionAsync"/> 聚焦，不重复建也不等待。
    /// </para>
    /// 全部处理完后回到这批里第一个新建的会话。
    /// </summary>
    public async Task OpenSessionsSequentiallyAsync(IReadOnlyList<ConnectionProfile> profiles)
    {
        SessionTabViewModel? first = null;
        var createdTabs = new List<SessionTabViewModel>();
        var failures = new List<BatchFailure>();
        var notOpened = new List<string>();

        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];

            // 已达并发上限：后面没有已开会话的设备都开不了，提前结束并统一说明，
            // 而不是对剩下的每一台都弹一次「已达到并发会话上限」。
            if (_sessions.ActiveSessionCount >= _sessions.MaxConcurrentSessions
                && !Tabs.OfType<SessionTabViewModel>().Any(t => t.Session.Profile.Id == profile.Id && t.Session.Kind == SessionKind.Default))
            {
                notOpened.AddRange(profiles.Skip(i)
                    .Where(p => !Tabs.OfType<SessionTabViewModel>().Any(t => t.Session.Profile.Id == p.Id && t.Session.Kind == SessionKind.Default))
                    .Select(p => p.Name));
                break;
            }

            var before = Tabs.OfType<SessionTabViewModel>().ToHashSet();
            await OpenSessionCoreAsync(profile, forceNew: false, failures, SessionKind.Default);

            // 本次新建出来的 Tab（聚焦已有会话 / 创建失败时没有新 Tab，直接下一个）。
            var created = Tabs.OfType<SessionTabViewModel>()
                .FirstOrDefault(t => !before.Contains(t) && t.Session.Profile.Id == profile.Id);
            if (created is null)
            {
                continue;
            }

            first ??= created;
            createdTabs.Add(created);
            await WaitForSessionToStartAsync(created);
        }

        if (first is not null && Tabs.Contains(first))
        {
            SelectedTab = first;
        }

        // 给刚发起的会话一点时间出结果，把连接中途失败的也并进同一条汇总。
        await WaitForSessionsToSettleAsync(createdTabs);
        foreach (var tab in createdTabs.Where(t => Tabs.Contains(t) && t.Session.State == ConnectionState.Failed))
        {
            var reason = string.IsNullOrWhiteSpace(tab.Session.ErrorMessage)
                ? ConnectionException.Describe(tab.Session.ErrorCode)
                : tab.Session.ErrorMessage;
            failures.Add(new BatchFailure(tab.Session.Profile.Name, reason));
        }

        var summary = BuildBatchSummary(createdTabs.Count(t => Tabs.Contains(t) && t.Session.State != ConnectionState.Failed),
            failures, notOpened, _sessions.MaxConcurrentSessions);
        if (summary is not null)
        {
            await _dialogs.ShowMessageAsync("批量连接", summary, DialogKind.Warning);
        }
    }

    /// <summary>批量连接结束后等刚发起的会话出结果（离开「连接中」）的最长时间。</summary>
    public TimeSpan BatchSettleTimeout { get; set; } = TimeSpan.FromSeconds(10);

    private async Task WaitForSessionsToSettleAsync(IReadOnlyList<SessionTabViewModel> tabs)
    {
        var deadline = DateTime.UtcNow + BatchSettleTimeout;
        while (DateTime.UtcNow < deadline
               && tabs.Any(t => Tabs.Contains(t)
                                && t.Session.State is ConnectionState.Idle or ConnectionState.Connecting or ConnectionState.Reconnecting))
        {
            await Task.Delay(BatchStartPoll);
        }
    }

    /// <summary>
    /// 批量连接的汇总文案。全部成功返回 null（不打扰）；否则列出没打开 / 失败的主机及原因，
    /// 太长时只列前几条。失败的会话仍保留在各自的 Tab 里，可以在那里「重新连接」。
    /// </summary>
    private static string? BuildBatchSummary(
        int opened, IReadOnlyList<BatchFailure> failures, IReadOnlyList<string> notOpened, int concurrencyLimit)
    {
        if (failures.Count == 0 && notOpened.Count == 0)
        {
            return null;
        }

        const int MaxListed = 8;
        var lines = new List<string>
        {
            opened > 0 ? $"已发起 {opened} 台连接。" : "没有成功发起任何连接。",
        };

        if (failures.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"{failures.Count} 台未能连接：");
            lines.AddRange(failures.Take(MaxListed).Select(f => $"• {f.Name}：{f.Reason}"));
            if (failures.Count > MaxListed)
            {
                lines.Add($"…另有 {failures.Count - MaxListed} 台，详见各自的会话标签页。");
            }

            lines.Add("连接失败的会话保留在标签页里，可在其中点「重新连接」。");
        }

        if (notOpened.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"已达到并发会话上限（{concurrencyLimit} 个），以下 {notOpened.Count} 台没有打开，请先关闭部分会话再试：");
            lines.AddRange(notOpened.Take(MaxListed).Select(n => $"• {n}"));
            if (notOpened.Count > MaxListed)
            {
                lines.Add($"…另有 {notOpened.Count - MaxListed} 台。");
            }
        }

        return string.Join("\n", lines);
    }

    private async Task WaitForSessionToStartAsync(SessionTabViewModel tab)
    {
        var deadline = DateTime.UtcNow + BatchStartTimeout;
        while (tab.Session.State == ConnectionState.Idle
               && DateTime.UtcNow < deadline
               && Tabs.Contains(tab))
        {
            // 此期间保持选中：它的视图要可见才会开始连接。用户手动切走则不抢回，直接放行。
            if (!ReferenceEquals(SelectedTab, tab))
            {
                return;
            }

            await Task.Delay(BatchStartPoll);
        }
    }

    /// <summary>
    /// 从托盘等外部入口激活指定会话：若其 Tab 存在则选中它，并退出全屏让常规窗口可见。
    /// 可能在非 UI 线程触发（托盘事件线程不定），统一切回 UI 线程处理。
    /// </summary>
    public void ActivateSession(Guid sessionId)
    {
        if (_ui.RequeueIfNeeded(() => ActivateSession(sessionId)))
        {
            return;
        }

        var tab = Tabs.OfType<SessionTabViewModel>().FirstOrDefault(t => t.Session.SessionId == sessionId);
        if (tab is null)
        {
            return;
        }

        // 托盘点选会话意味着用户要回到常规窗口上下文；全屏下无标题栏 / 导航，
        // 先回到常规档再切 Tab，让切换过程与 Tab 条可见。
        ResetSessionView();

        SelectedTab = tab;
    }

    private async Task ReconnectAsync(ConnectionProfile profile, SessionKind kind)
    {
        // 与 OpenSessionAsync 共用创建占位：OnSessionCreated 无论来源都会释放该 Profile 的占位，
        // 重连若不登记，会把同一设备上正在进行的「打开」的占位提前放掉，连点就能再开出重复会话。
        var registered = _openingProfileIds.Add((profile.Id, kind));
        ResolvedCredential? entered = null;
        try
        {
            // 会话里持有的是连接时的配置对象；期间若编辑过（换凭据 / 改地址），重连必须取最新保存的版本。
            if ((await _sessions.GetProfilesAsync([profile.Id])).FirstOrDefault() is { } latest)
            {
                profile = latest;
            }

            var (proceed, credential) = await PromptCredentialIfNeededAsync(profile, kind);
            if (!proceed)
            {
                ReleaseOpeningPlaceholder((profile.Id, kind), registered);
                return;
            }

            entered = credential;
            await _sessions.CreateSessionAsync(profile, credentialOverride: entered, kind: kind);
        }
        catch (ConnectionException ex)
        {
            entered?.Dispose();
            ReleaseOpeningPlaceholder((profile.Id, kind), registered);
            await _dialogs.ShowMessageAsync("无法重新连接", ex.Message, DialogKind.Error);
        }
        catch (InvalidOperationException ex)
        {
            entered?.Dispose();
            ReleaseOpeningPlaceholder((profile.Id, kind), registered);
            await _dialogs.ShowMessageAsync("无法重新连接", ex.Message, DialogKind.Warning);
        }
        catch
        {
            entered?.Dispose();
            ReleaseOpeningPlaceholder((profile.Id, kind), registered);
            throw;
        }
    }

    /// <summary>仅释放本次调用自己登记的占位，不动别的创建请求留下的。</summary>
    private void ReleaseOpeningPlaceholder((Guid ProfileId, SessionKind Kind) placeholder, bool registered)
    {
        if (registered)
        {
            _openingProfileIds.Remove(placeholder);
        }
    }

    /// <summary>关闭除指定 Tab 外的全部会话。</summary>
    [RelayCommand]
    private async Task CloseOtherSessionsAsync(SessionTabViewModel? keep)
    {
        var others = Tabs.OfType<SessionTabViewModel>().Where(t => !ReferenceEquals(t, keep)).ToList();
        foreach (var tab in others)
        {
            await _sessions.CloseSessionAsync(tab.Session.SessionId);
        }
    }

    /// <summary>
    /// 关闭指定会话 Tab 右侧的全部会话（不含常驻页面 Tab）。
    /// <para>
    /// 以当前 Tab 为锚点，只关索引更大（更晚打开）的会话；左侧更早的会话保持不变，
    /// 便于多会话场景下快速收拢到最关心的那批。语义与「关闭其他会话」相反。
    /// </para>
    /// </summary>
    public async Task CloseRightSessionsAsync(SessionTabViewModel anchor)
    {
        var anchorIndex = Tabs.IndexOf(anchor);
        if (anchorIndex < 0)
        {
            return;
        }

        // Skip(anchorIndex + 1) 已天然排除锚点本身；OfType 只收 SessionTab，
        // 常驻页面 Tab（索引 0，PageTabViewModel）不可能出现在锚点右侧，此处兜底过滤。
        var right = Tabs.Skip(anchorIndex + 1).OfType<SessionTabViewModel>().ToList();
        foreach (var tab in right)
        {
            await _sessions.CloseSessionAsync(tab.Session.SessionId);
        }
    }

    [RelayCommand]
    private async Task CloseAllSessionsAsync() => await _sessions.CloseAllAsync();

    private bool _disposed;

    /// <summary>幂等：App.OnExit 先显式 dispose 本对象，容器 DisposeAsync 兜底时又会 dispose 一次（单例）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (ReturnTab is not null)
        {
            ReturnTab.PropertyChanged -= OnReturnTabPropertyChanged;
        }

        _pageReloadCts?.Cancel();
        _sessions.SessionCreated -= OnSessionCreated;
        _sessions.SessionClosed -= OnSessionClosed;
        _sessions.SessionsChanged -= OnSessionsChanged;

        if (_fileTransferLauncher is not null)
        {
            _fileTransferLauncher.OpenRequested -= OnFileTransferOpenRequested;
        }

        foreach (var tab in Tabs.OfType<SessionTabViewModel>())
        {
            tab.ActionRequested -= OnSessionActionRequested;
            tab.Dispose();
        }

        foreach (var tab in Tabs.OfType<FileTransferTabViewModel>())
        {
            tab.Dispose();
        }
    }
}
