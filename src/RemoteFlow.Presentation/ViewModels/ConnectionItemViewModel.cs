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

    public string ProtocolBrushKey => Profile.Protocol switch
    {
        ProtocolType.Rdp => "Protocol.Rdp",
        ProtocolType.Ssh => "Protocol.Ssh",
        _ => "Protocol.Vnc"
    };

    public string Notes => Profile.Notes;

    /// <summary>分组名称。用于列表分组标题与详情面板。</summary>
    [ObservableProperty]
    private string _groupName = "未分组";

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
    /// 的绿色“已连接”点只认本属性。由页面 VM 在会话集合变化时按
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

    /// <summary>多选模式下是否被勾选。</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>「最近连接」视图下的日期分组名（今天 / 昨天 / 更早）。</summary>
    [ObservableProperty]
    private string _recentBucket = "更早";

    public DateTimeOffset? LastConnectedAt => Profile.LastConnectedAt;

    /// <summary>最近连接：刚刚 / N 分钟前；更早则用统一紧凑时间。从未连接给占位。</summary>
    public string LastConnectedDisplay
        => Profile.LastConnectedAt is { } time ? DateTimeDisplay.RelativeRecent(time) : "从未连接";

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
public sealed record TagChip(string Name, string Color);

/// <summary>
/// 详情面板迷你柱状图的一根柱子。
/// </summary>
/// <param name="Height">归一化高度，0~1。</param>
/// <param name="BrushKey">柱色语义键（成功 / 失败）。</param>
/// <param name="Tooltip">悬停提示：时间 + 时长。</param>
public sealed record SparkBar(double Height, string BrushKey, string Tooltip);
