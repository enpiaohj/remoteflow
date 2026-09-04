using CommunityToolkit.Mvvm.ComponentModel;
using RemoteFlow.Core.Models;

namespace RemoteFlow.App.ViewModels;

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

    /// <summary>该连接当前是否有进行中的会话。首页最近连接卡片据此高亮。</summary>
    [ObservableProperty]
    private bool _hasActiveSession;

    public DateTimeOffset? LastConnectedAt => Profile.LastConnectedAt;

    /// <summary>最近连接时间的相对描述，比绝对时间更利于快速扫读。</summary>
    public string LastConnectedDisplay
    {
        get
        {
            if (Profile.LastConnectedAt is not { } time)
            {
                return "从未连接";
            }

            var elapsed = DateTimeOffset.Now - time;

            return elapsed switch
            {
                { TotalMinutes: < 1 } => "刚刚",
                { TotalMinutes: < 60 } => $"{(int)elapsed.TotalMinutes} 分钟前",
                { TotalHours: < 24 } => $"{(int)elapsed.TotalHours} 小时前",
                { TotalDays: < 30 } => $"{(int)elapsed.TotalDays} 天前",
                _ => time.ToString("yyyy-MM-dd")
            };
        }
    }

    public string CreatedAtDisplay => Profile.CreatedAt.ToString("yyyy-MM-dd HH:mm");

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
