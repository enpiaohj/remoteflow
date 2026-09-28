using CommunityToolkit.Mvvm.ComponentModel;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Presentation.ViewModels;

/// <summary>
/// 连接列表中的一行。把领域对象包装为界面直接可用的显示属性，
/// 避免在 XAML 里堆转换器。
/// </summary>
public sealed partial class ConnectionItemViewModel(ConnectionProfile profile) : ObservableObject
{
    public ConnectionProfile Profile { get; } = profile;

    public Guid Id => Profile.Id;

    public string Name => Profile.Name;

    public string Host => Profile.Host;

    /// <summary>端口（详情面板展示）。</summary>
    public string PortDisplay => Profile.Port.ToString();

    /// <summary>端口为协议默认值时不显示，减少列表噪音。</summary>
    public string HostDisplay => Profile.Port == ConnectionProfile.GetDefaultPort(Profile.Protocol)
        ? Profile.Host
        : $"{Profile.Host}:{Profile.Port}";

    public string ProtocolName => Profile.Protocol switch
    {
        ProtocolType.Rdp => "RDP",
        ProtocolType.Ssh => "SSH",
        _ => "VNC"
    };

    // Segoe Fluent Icons：与 Icons.xaml 的 Icon.Rdp/Ssh/Vnc 一致。
    public string ProtocolIcon => Profile.Protocol switch
    {
        ProtocolType.Rdp => "\uE7F4",
        ProtocolType.Ssh => "\uE756",
        _ => "\uE7F8"
    };

    public string ProtocolIconKey => Profile.Protocol switch
    {
        ProtocolType.Rdp => "ProtocolIcon.Rdp",
        ProtocolType.Ssh => "ProtocolIcon.Ssh",
        _ => "ProtocolIcon.Vnc"
    };

    public string ProtocolBrushKey => Profile.Protocol switch
    {
        ProtocolType.Rdp => "Protocol.Rdp",
        ProtocolType.Ssh => "Protocol.Ssh",
        _ => "Protocol.Vnc"
    };

    // ── 设备主图标（正式多色矢量套系）：显式指定设备类型时用对应资源键，
    //    未指定（Unknown）按协议推断——与升级前行为兼容。 ──
    private readonly DeviceTypeInfo _deviceVisual = DeviceTypeCatalog.Resolve(profile.DeviceType)
        ?? DeviceTypeCatalog.InferFromProtocol(profile.Protocol);

    /// <summary>设备类型矢量图标资源键。列表 / 详情 / 首页共用。</summary>
    public string DeviceIconKey => _deviceVisual.IconResourceKey;

    /// <summary>设备类型配色资源键。列表 / 详情 / 首页共用。</summary>
    public string DeviceBrushKey => _deviceVisual.AccentBrushKey;

    /// <summary>设备类型显示名（详情面板）。未指定时显示「自动（按协议）」。</summary>
    public string DeviceTypeDisplay => DeviceTypeCatalog.DisplayName(Profile.DeviceType);

    public string Notes => Profile.Notes;

    /// <summary>分组名称。用于列表分组标题与详情面板。</summary>
    [ObservableProperty]
    private string _groupName = "未分组";

    /// <summary>分组完整路径（如「生产环境 / Windows / 应用服务器」）。详情面板展示。</summary>
    [ObservableProperty]
    private string _groupPath = "未分组";

    /// <summary>凭据名称。详情面板只显示名称，绝不显示密码。</summary>
    [ObservableProperty]
    private string _credentialName = "未指定";

    /// <summary>标签名称列表。列表中最多展示 3 个，超出以 +N 表示。</summary>
    [ObservableProperty]
    private IReadOnlyList<TagChip> _tags = [];

    /// <summary>超出展示上限的标签数量。</summary>
    [ObservableProperty]
    private int _overflowTagCount;

    [ObservableProperty]
    private bool _isFavorite = profile.Favorite;

    /// <summary>
    /// 该连接是否存在任意活动会话（含 Connecting / Failed 尚未移除）。
    /// 用于右键「连接 / 切换到会话 / 断开连接」的动作区分——只要有会话在跑，
    /// 首操作就应是聚焦既有会话而非新建。由页面 VM 按 SessionManager 快照统一写入。
    /// </summary>
    [ObservableProperty]
    private bool _hasActiveSession;

    /// <summary>
    /// 该连接是否存在「真正已连接」（State == Connected）的会话。
    /// 与 <see cref="HasActiveSession"/>（任意活动）语义区分：列表「最近连接」列
    /// 的蓝色“已连接”点只认本属性。由页面 VM 在会话集合变化时按
    /// SessionManager.HasConnectedSession 统一写入，不落库。
    /// </summary>
    [ObservableProperty]
    private bool _isConnected;

    /// <summary>
    /// 该连接是否有会话正处于 <c>Connecting</c>。
    /// 与 <see cref="HasActiveSession"/> 区分：后者把断开中 / 失败未清理也算「活动」，
    /// 拿它当「正在连接」会出现「已经关掉了却还显示连接中」的错误状态。
    /// </summary>
    [ObservableProperty]
    private bool _isConnecting;

    /// <summary>该连接是否有仍保留在活动集合中的失败会话。</summary>
    [ObservableProperty]
    private bool _isFailed;

    /// <summary>多选模式下是否被勾选。</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>「最近连接」视图下的日期分组名（今天 / 昨天 / 更早）。</summary>
    [ObservableProperty]
    private string _recentBucket = "更早";

    /// <summary>
    /// 在线探测状态（列表「状态」列）。与 <see cref="IsConnected"/>（存在活动会话）语义区分：
    /// 探测的是「现在能否连通主机」，有活动会话时无论探测结果如何都按在线呈现。
    /// </summary>
    [ObservableProperty]
    private PresenceState _presence = PresenceState.Unknown;

    /// <summary>最近一次探测完成的时间（tooltip 用）。null 表示从未探测。</summary>
    private DateTimeOffset? _lastProbedAt;

    partial void OnPresenceChanged(PresenceState value) => RaisePresenceDisplays();

    partial void OnIsConnectedChanged(bool value) => RaisePresenceDisplays();

    partial void OnIsConnectingChanged(bool value) => RaisePresenceDisplays();

    partial void OnIsFailedChanged(bool value) => RaisePresenceDisplays();

    partial void OnHasActiveSessionChanged(bool value)
        => OnPropertyChanged(nameof(QuickActionText));

    /// <summary>
    /// 行 / 详情主按钮文案：无活动会话 = 「连接」；有活动会话（含连接中）= 「打开会话」
    /// （点击走统一漏斗聚焦既有 Tab，不再二次发起连接）。
    /// </summary>
    public string QuickActionText => HasActiveSession ? "打开会话" : "连接";

    /// <summary>
    /// 原始在线探测文字。列表统一展示使用 <see cref="ConnectionStatusDisplay"/>，
    /// 会话状态为空时才回退到此值。
    /// </summary>
    public string PresenceDisplay => Presence switch
    {
        PresenceState.Probing => "探测中",
        PresenceState.Online => "在线",
        PresenceState.Offline => "离线",
        _ => "—"
    };

    /// <summary>探测状态点 / 文字的颜色语义键。离线与未知均使用中性灰。</summary>
    public string PresenceBrushKey => Presence switch
    {
        PresenceState.Probing => "Status.Warning",
        PresenceState.Online => "Status.Success",
        _ => "Status.Idle"
    };

    /// <summary>
    /// 列表统一状态：会话实时状态优先，未启动会话时回退到主机在线探测。
    /// 已连接使用蓝色，与绿色「在线」明确区分。
    /// </summary>
    public string ConnectionStatusDisplay => IsConnected
        ? "已连接"
        : IsConnecting
            ? "连接中"
            : IsFailed
                ? "异常"
                : PresenceDisplay;

    public string ConnectionStatusBrushKey => IsConnected
        ? "Status.Info"
        : IsConnecting
            ? "Status.Warning"
            : IsFailed
                ? "Status.Danger"
                : PresenceBrushKey;

    public string ConnectionStatusTooltip => IsConnected
        ? "存在已连接会话，双击或“打开会话”可聚焦"
        : IsConnecting
            ? "会话正在建立连接"
            : IsFailed
                ? "最近的活动会话连接异常"
                : PresenceTooltip;

    /// <summary>状态列 tooltip：说明数据来自什么时候，避免把陈旧结果当实时。</summary>
    public string PresenceTooltip => Presence switch
    {
        PresenceState.Probing => "正在探测…",
        PresenceState.Online or PresenceState.Offline when _lastProbedAt is { } at
            => $"探测于 {DateTimeDisplay.Compact(at)}",
        _ => "尚未探测，点击工具条「探测」立即检查"
    };

    private void RaisePresenceDisplays()
    {
        OnPropertyChanged(nameof(PresenceDisplay));
        OnPropertyChanged(nameof(PresenceBrushKey));
        OnPropertyChanged(nameof(PresenceTooltip));
        OnPropertyChanged(nameof(ConnectionStatusDisplay));
        OnPropertyChanged(nameof(ConnectionStatusBrushKey));
        OnPropertyChanged(nameof(ConnectionStatusTooltip));
    }

    /// <summary>标记进入探测中（发起批量探测前统一置位）。</summary>
    public void MarkProbing() => Presence = PresenceState.Probing;

    /// <summary>写入一次探测结果并记录探测时间。</summary>
    public void SetProbeResult(bool online) => SetProbeResult(online, DateTimeOffset.Now);

    /// <summary>
    /// 写入一次探测结果并保留原始探测时间。行对象重建（如首页随会话变化刷新）时复用上次结果，
    /// 提示里的「探测于」仍是真实探测时刻。
    /// </summary>
    public void SetProbeResult(bool online, DateTimeOffset probedAt)
    {
        _lastProbedAt = probedAt;
        Presence = online ? PresenceState.Online : PresenceState.Offline;
    }

    /// <summary>清空探测状态（关闭探测开关时）。</summary>
    public void ClearProbe()
    {
        _lastProbedAt = null;
        Presence = PresenceState.Unknown;
    }

    public DateTimeOffset? LastConnectedAt => Profile.LastConnectedAt;

    /// <summary>最近连接：刚刚 / N 分钟前；更早则用统一紧凑时间。从未连接给占位。</summary>
    public string LastConnectedDisplay
        => Profile.LastConnectedAt is { } time ? DateTimeDisplay.RelativeRecent(time) : "从未连接";

    /// <summary>列表「最近连接」列用的超短时间（09-23 15:46），固定数字格式利于扫读。</summary>
    public string LastConnectedCompact
        => Profile.LastConnectedAt is { } time ? DateTimeDisplay.ShortDateTime(time) : "从未连接";

    /// <summary>详情面板快捷宫格的收藏切换文案。</summary>
    public string FavoriteToggleText => IsFavorite ? "取消收藏" : "收藏";

    partial void OnIsFavoriteChanged(bool value) => OnPropertyChanged(nameof(FavoriteToggleText));

    /// <summary>创建时间等档案信息用绝对时间（统一格式）。</summary>
    public string CreatedAtDisplay => DateTimeDisplay.Absolute(Profile.CreatedAt);

    /// <summary>刷新那些依赖外部数据或时间的显示属性。</summary>
    public void RefreshComputed()
    {
        OnPropertyChanged(nameof(LastConnectedDisplay));
        OnPropertyChanged(nameof(HostDisplay));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Notes));
    }
}

/// <summary>标签展示单元。</summary>
public sealed record TagChip(string Name, string Color, string Icon = TagIconCatalog.DefaultKey);

/// <summary>
/// 详情面板迷你柱状图的一根柱子。
/// </summary>
/// <param name="Height">归一化高度，0~1。</param>
/// <param name="BrushKey">柱色语义键（成功 / 失败）。</param>
/// <param name="Tooltip">悬停提示：时间 + 时长。</param>
public sealed record SparkBar(double Height, string BrushKey, string Tooltip);
