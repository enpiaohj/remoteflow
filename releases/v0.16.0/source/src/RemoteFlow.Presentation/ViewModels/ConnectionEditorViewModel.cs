using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Presentation.ViewModels;

/// <summary>
/// 新建 / 编辑连接对话框。
/// <para>
/// 首屏只暴露最少的必填字段（协议、名称、主机、端口、凭据、分组、标签），
/// 协议专项参数收进「高级设置」折叠区（产品设计文档 §7.7）。
/// </para>
/// </summary>
public sealed partial class ConnectionEditorViewModel : ObservableObject
{
    private readonly ConnectionProfile _profile;
    private readonly bool _isNew;

    /// <summary>端口是否被用户手工改过。未改过时切换协议会自动带出新默认端口。</summary>
    private bool _portManuallyEdited;

    public ConnectionEditorViewModel(
        ConnectionProfile? existing,
        IReadOnlyList<Credential> credentials,
        IReadOnlyList<ConnectionGroup> groups,
        IReadOnlyList<Tag> tags,
        AppSettings defaults,
        Guid? defaultGroupId = null,
        ProtocolType? preselectedProtocol = null)
    {
        _isNew = existing is null;
        _profile = existing is null ? new ConnectionProfile() : existing.Clone(existing.Name);

        if (existing is not null)
        {
            // Clone 会生成新 Id，编辑场景必须保留原 Id 与创建时间。
            _profile.Id = existing.Id;
            _profile.CreatedAt = existing.CreatedAt;
            _profile.LastConnectedAt = existing.LastConnectedAt;
            _profile.Favorite = existing.Favorite;
        }
        else
        {
            // 新建连接时套用设置页中的协议默认值；协议按托盘入口预选，null 默认 RDP。
            var initialProtocol = preselectedProtocol ?? ProtocolType.Rdp;
            _profile.Protocol = initialProtocol;
            _profile.Port = ConnectionProfile.GetDefaultPort(initialProtocol);
            _profile.Rdp.DisplayMode = defaults.RdpDefaultDisplayMode;
            _profile.Rdp.RedirectClipboard = defaults.RdpDefaultRedirectClipboard;
            _profile.Rdp.RedirectAudio = defaults.RdpDefaultRedirectAudio;
            _profile.Rdp.UseMultimon = defaults.RdpDefaultUseMultimon;
            // 「使用全部显示器」依赖全屏才真正生效；若全局默认开启了多显示器，
            // 新建连接联动默认进入全屏，避免出现「勾了多显示器却不全屏」的无效态。
            if (_profile.Rdp.UseMultimon)
            {
                _profile.Rdp.StartFullScreen = true;
            }
            _profile.Ssh.KeepAliveSeconds = defaults.SshDefaultKeepAliveSeconds;
            _profile.Ssh.TerminalType = defaults.SshDefaultTerminalType;
            _profile.Ssh.Encoding = defaults.SshDefaultEncoding;
            _profile.Vnc.ScaleMode = defaults.VncDefaultScaleMode;
            _profile.Vnc.ViewOnly = defaults.VncDefaultViewOnly;
            _profile.Vnc.SharedConnection = defaults.VncDefaultSharedConnection;
            _profile.Vnc.ClipboardToLocal = defaults.VncDefaultClipboardToLocal;
            // 新建连接默认进入默认分组「我的设备」（§9）。
            _profile.GroupId = defaultGroupId;
        }

        Title = _isNew ? "新建连接" : "编辑连接";

        // 「未指定」与「未分组」用 null 作为哨兵项，避免额外的可空判断散落在界面里。
        AvailableCredentials = [new CredentialOption(null, "未指定"),
            .. credentials.Select(c => new CredentialOption(c.Id, c.Name))];
        // 「未分组」用 null 哨兵项表示；数据库里的系统「未分组」行不重复列出。
        AvailableGroups = [new GroupOption(null, "未分组"),
            .. groups.Where(g => !g.IsSystem)
                     .OrderBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                     .Select(g => new GroupOption(g.Id, g.Name))];

        AvailableTags = [.. tags.Select(t => new TagSelection(t.Id, t.Name, t.Color)
        {
            IsSelected = _profile.TagIds.Contains(t.Id)
        })];

        _name = _profile.Name;
        _host = _profile.Host;
        _port = _profile.Port;
        _protocol = _profile.Protocol;
        _notes = _profile.Notes;
        _portManuallyEdited = !_isNew;

        _selectedCredential = AvailableCredentials.FirstOrDefault(c => c.Id == _profile.CredentialId)
                              ?? AvailableCredentials[0];
        _selectedGroup = AvailableGroups.FirstOrDefault(g => g.Id == _profile.GroupId)
                         ?? AvailableGroups[0];

        // 用既有 Profile 的 RDP 状态初始化编辑器；直接写字段避免构造期触发联动。
        // 分辨率始终按已存桌面尺寸回填：适应窗口时该行隐藏，切到固定分辨率时即为当前预设。
        _rdpDisplayMode = _profile.Rdp.DisplayMode;
        _rdpCustomWidth = _profile.Rdp.DesktopWidth.ToString(CultureInfo.InvariantCulture);
        _rdpCustomHeight = _profile.Rdp.DesktopHeight.ToString(CultureInfo.InvariantCulture);
        _selectedResolution = MatchPreset(_profile.Rdp.DesktopWidth, _profile.Rdp.DesktopHeight) ?? CustomResolution;
        _rdpStartFullScreen = _profile.Rdp.StartFullScreen;
        _rdpUseMultimon = _profile.Rdp.UseMultimon;
        _rdpRedirectAudio = _profile.Rdp.RedirectAudio;
        _rdpRedirectMicrophone = _profile.Rdp.RedirectMicrophone;
        _selectedConnectionQuality = MatchQuality(_profile.Rdp.ConnectionQuality);
    }

    private ResolutionOption? MatchPreset(int width, int height)
        => AvailableResolutions.FirstOrDefault(r => r.Width == width && r.Height == height);

    public string Title { get; }

    public IReadOnlyList<CredentialOption> AvailableCredentials { get; }

    public IReadOnlyList<GroupOption> AvailableGroups { get; }

    public ObservableCollection<TagSelection> AvailableTags { get; }

    public IReadOnlyList<ProtocolType> AvailableProtocols { get; } =
        [ProtocolType.Rdp, ProtocolType.Ssh, ProtocolType.Vnc];

    // ── RDP 显示 / 分辨率 ─────────────────────────────────────────

    private static readonly ResolutionOption CustomResolution = new(RdpDisplayResolution.Custom, "自定义…", null, null);

    /// <summary>
    /// 分辨率预设下拉项（固定分辨率模式可见）。不包含「自动」项——
    /// 适应窗口是独立的显示模式（<see cref="RdpDisplayMode.FitToWindow"/>），
    /// 由会话随窗口自适应，与固定分辨率互斥，语义上不应混入分辨率下拉。
    /// </summary>
    public IReadOnlyList<ResolutionOption> AvailableResolutions { get; } =
    [
        new(RdpDisplayResolution.Res1280x720, "1280 × 720", 1280, 720),
        new(RdpDisplayResolution.Res1366x768, "1366 × 768", 1366, 768),
        new(RdpDisplayResolution.Res1600x900, "1600 × 900", 1600, 900),
        new(RdpDisplayResolution.Res1920x1080, "1920 × 1080", 1920, 1080),
        new(RdpDisplayResolution.Res2560x1440, "2560 × 1440", 2560, 1440),
        CustomResolution
    ];

    /// <summary>「体验」连接质量下拉项。Auto 表示不主动写入连接类型。</summary>
    public IReadOnlyList<ConnectionQualityOption> ConnectionQualityOptions => ConnectionQualityOptionItems;

    private static readonly IReadOnlyList<ConnectionQualityOption> ConnectionQualityOptionItems =
    [
        new(RdpConnectionQuality.Auto, "自动"),
        new(RdpConnectionQuality.Lan, "局域网（LAN）"),
        new(RdpConnectionQuality.HighSpeed, "高速宽带"),
        new(RdpConnectionQuality.LowBandwidth, "低带宽")
    ];

    private static ConnectionQualityOption MatchQuality(RdpConnectionQuality value)
        => ConnectionQualityOptionItems.FirstOrDefault(o => o.Value == value) ?? ConnectionQualityOptionItems[0];

    /// <summary>RDP 显示模式。写回 <see cref="RdpOptions.DisplayMode"/>。</summary>
    [ObservableProperty]
    private RdpDisplayMode _rdpDisplayMode;

    /// <summary>当前选中的分辨率预设。</summary>
    [ObservableProperty]
    private ResolutionOption? _selectedResolution;

    /// <summary>自定义分辨率宽度输入（仅「自定义」时生效）。</summary>
    [ObservableProperty]
    private string _rdpCustomWidth = "1920";

    /// <summary>自定义分辨率高度输入（仅「自定义」时生效）。</summary>
    [ObservableProperty]
    private string _rdpCustomHeight = "1080";

    /// <summary>是否处于「固定分辨率」显示模式。适应窗口时分辨率下拉 / 自定义输入整体隐藏。</summary>
    public bool IsFixedResolution => RdpDisplayMode == RdpDisplayMode.FixedResolution;

    /// <summary>当前是否选择了「自定义」分辨率（且处于固定分辨率模式）。</summary>
    public bool IsCustomResolution => IsFixedResolution && SelectedResolution == CustomResolution;

    /// <summary>当前选中的连接质量预设。写回 <see cref="RdpOptions.ConnectionQuality"/>。</summary>
    [ObservableProperty]
    private ConnectionQualityOption? _selectedConnectionQuality;

    /// <summary>「启动后进入全屏」。勾选「使用全部显示器」会自动联动勾选此项。</summary>
    [ObservableProperty]
    private bool _rdpStartFullScreen;

    /// <summary>「使用全部显示器」。取消勾选不会联动取消「启动后进入全屏」。</summary>
    [ObservableProperty]
    private bool _rdpUseMultimon;

    /// <summary>
    /// 多显示器相关的行内提示文案。未勾选多显示器时为 null（隐藏）；
    /// 勾选后给出说明，若「启动后全屏」被取消则明确指出其仍生效需全屏配合。
    /// </summary>
    public string? UseMultimonHint => RdpUseMultimon switch
    {
        false => null,
        _ when RdpStartFullScreen => "已联动勾选「启动后进入全屏」：多显示器布局需要会话进入全屏后才会真正扩展。",
        _ => "已保留「使用全部显示器」，但它需要「启动后进入全屏」才会真正生效。"
    };

    /// <summary>「音频播放到本机」。勾选「麦克风重定向」会自动联动勾选此项（音频捕获依赖本机播放通道）。</summary>
    [ObservableProperty]
    private bool _rdpRedirectAudio;

    /// <summary>「麦克风重定向」。勾选时自动联动「音频播放到本机」。</summary>
    [ObservableProperty]
    private bool _rdpRedirectMicrophone;

    /// <summary>分辨率选择 / 显示模式互斥联动是否在进行中，用于抑制回调递归。</summary>
    private bool _syncingDisplay;

    // ── 基本字段 ──────────────────────────────────────────────────

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _host;

    [ObservableProperty]
    private int _port;

    [ObservableProperty]
    private ProtocolType _protocol;

    [ObservableProperty]
    private string _notes;

    [ObservableProperty]
    private CredentialOption _selectedCredential;

    [ObservableProperty]
    private GroupOption _selectedGroup;

    /// <summary>高级设置折叠区是否展开。默认收起，保持首屏简洁。</summary>
    [ObservableProperty]
    private bool _isAdvancedExpanded;

    [ObservableProperty]
    private string _validationMessage = string.Empty;

    public bool HasValidationMessage => !string.IsNullOrEmpty(ValidationMessage);

    // 按协议显示对应的高级设置分区
    public bool IsRdp => Protocol == ProtocolType.Rdp;
    public bool IsSsh => Protocol == ProtocolType.Ssh;
    public bool IsVnc => Protocol == ProtocolType.Vnc;

    // ── 协议专项参数（直接绑定到 profile 上的选项对象） ──────────

    public RdpOptions Rdp => _profile.Rdp;
    public SshOptions Ssh => _profile.Ssh;
    public VncOptions Vnc => _profile.Vnc;

    public IReadOnlyList<string> EncodingOptions { get; } = ["UTF-8", "GBK", "GB18030", "Big5", "ISO-8859-1"];
    public IReadOnlyList<string> TerminalTypeOptions { get; } = ["xterm-256color", "xterm", "vt100", "linux"];

    partial void OnProtocolChanged(ProtocolType value)
    {
        // 用户没手工改过端口时，切换协议自动带出该协议的标准端口。
        if (!_portManuallyEdited)
        {
            Port = ConnectionProfile.GetDefaultPort(value);
        }

        OnPropertyChanged(nameof(IsRdp));
        OnPropertyChanged(nameof(IsSsh));
        OnPropertyChanged(nameof(IsVnc));
    }

    partial void OnPortChanged(int value)
    {
        // 由 OnProtocolChanged 自动带出的端口不算手工修改。
        if (value != ConnectionProfile.GetDefaultPort(Protocol))
        {
            _portManuallyEdited = true;
        }
    }

    partial void OnValidationMessageChanged(string value) => OnPropertyChanged(nameof(HasValidationMessage));

    partial void OnRdpDisplayModeChanged(RdpDisplayMode value)
    {
        OnPropertyChanged(nameof(IsCustomResolution));
        OnPropertyChanged(nameof(IsFixedResolution));

        // 由分辨率下拉联动设置显示模式时跳过，避免覆盖用户刚选的分辨率。
        if (_syncingDisplay)
        {
            return;
        }

        if (value == RdpDisplayMode.FixedResolution)
        {
            // 固定分辨率：按连接里已存宽高回填分辨率预设（或自定义）。
            SyncResolutionFromProfile();
        }
        // 适应窗口：分辨率随窗口自动调整，不写死桌面尺寸；不改动已存宽高，
        // 分辨率行整体隐藏，切回固定分辨率时仍按已存宽高回填。
    }

    partial void OnSelectedConnectionQualityChanged(ConnectionQualityOption? value)
    {
        if (value is not null)
        {
            _profile.Rdp.ConnectionQuality = value.Value;
        }
    }

    partial void OnRdpStartFullScreenChanged(bool value)
    {
        _profile.Rdp.StartFullScreen = value;
        OnPropertyChanged(nameof(UseMultimonHint));
    }

    partial void OnRdpUseMultimonChanged(bool value)
    {
        _profile.Rdp.UseMultimon = value;

        // 勾选「使用全部显示器」自动联动勾选「启动后进入全屏」（多显示器需在全屏下才真正生效）；
        // 取消多显示器不联动取消全屏，避免打断用户对全屏的独立选择。
        if (value && !_profile.Rdp.StartFullScreen)
        {
            RdpStartFullScreen = true;
        }

        OnPropertyChanged(nameof(UseMultimonHint));
    }

    partial void OnRdpRedirectAudioChanged(bool value)
    {
        _profile.Rdp.RedirectAudio = value;
    }

    partial void OnRdpRedirectMicrophoneChanged(bool value)
    {
        _profile.Rdp.RedirectMicrophone = value;

        // 麦克风捕获依赖远端音频通道：勾选「麦克风重定向」时自动勾选「音频播放到本机」。
        // 取消麦克风不联动取消音频播放，避免打断用户对音频输出的独立选择。
        if (value && !_profile.Rdp.RedirectAudio)
        {
            RdpRedirectAudio = true;
        }
    }

    partial void OnSelectedResolutionChanged(ResolutionOption? value)
    {
        OnPropertyChanged(nameof(IsCustomResolution));

        if (value is null)
        {
            return;
        }

        switch (value.Value)
        {
            case RdpDisplayResolution.Custom:
                // 先落显示模式再展开自定义输入，自定义宽高回填当前已存桌面尺寸。
                _syncingDisplay = true;
                try
                {
                    RdpDisplayMode = RdpDisplayMode.FixedResolution;
                }
                finally
                {
                    _syncingDisplay = false;
                }

                RdpCustomWidth = _profile.Rdp.DesktopWidth.ToString(CultureInfo.InvariantCulture);
                RdpCustomHeight = _profile.Rdp.DesktopHeight.ToString(CultureInfo.InvariantCulture);
                break;

            default:
                if (value.Width is { } w && value.Height is { } h)
                {
                    // 先写宽高、再切固定分辨率，使固定分辨率回调按新宽高回填而非旧值。
                    _profile.Rdp.DesktopWidth = w;
                    _profile.Rdp.DesktopHeight = h;
                    RdpCustomWidth = w.ToString(CultureInfo.InvariantCulture);
                    RdpCustomHeight = h.ToString(CultureInfo.InvariantCulture);

                    _syncingDisplay = true;
                    try
                    {
                        RdpDisplayMode = RdpDisplayMode.FixedResolution;
                    }
                    finally
                    {
                        _syncingDisplay = false;
                    }
                }
                break;
        }
    }

    /// <summary>固定分辨率下，按 Profile 已存的桌面尺寸回填分辨率预设 / 自定义输入。</summary>
    private void SyncResolutionFromProfile()
    {
        var match = MatchPreset(_profile.Rdp.DesktopWidth, _profile.Rdp.DesktopHeight);
        if (match is not null)
        {
            if (SelectedResolution != match)
            {
                SelectedResolution = match;
            }
        }
        else
        {
            RdpCustomWidth = _profile.Rdp.DesktopWidth.ToString(CultureInfo.InvariantCulture);
            RdpCustomHeight = _profile.Rdp.DesktopHeight.ToString(CultureInfo.InvariantCulture);
            if (SelectedResolution != CustomResolution)
            {
                SelectedResolution = CustomResolution;
            }
        }
    }

    /// <summary>校验并生成最终的连接配置。校验不通过返回 null 并设置提示。</summary>
    public ConnectionProfile? Build()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            ValidationMessage = "请填写连接名称。";
            return null;
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            ValidationMessage = "请填写主机地址或 IP。";
            return null;
        }

        if (Port is < 1 or > 65535)
        {
            ValidationMessage = "端口必须在 1~65535 之间。";
            return null;
        }

        // RDP 显示：把编辑器里的显示模式 / 分辨率写回 Profile。
        // 适应窗口时宽高对协议无意义（随窗口），保留已存值、不覆盖。
        _profile.Rdp.DisplayMode = RdpDisplayMode;
        if (RdpDisplayMode == RdpDisplayMode.FixedResolution)
        {
            if (SelectedResolution is { Value: RdpDisplayResolution.Custom })
            {
                if (!TryParseDimension(RdpCustomWidth, out var customWidth)
                    || !TryParseDimension(RdpCustomHeight, out var customHeight))
                {
                    ValidationMessage = "自定义分辨率需填写有效的宽与高（如 1920 与 1080）。";
                    return null;
                }

                _profile.Rdp.DesktopWidth = customWidth;
                _profile.Rdp.DesktopHeight = customHeight;
            }
            else if (SelectedResolution is { Width: { } presetWidth, Height: { } presetHeight })
            {
                _profile.Rdp.DesktopWidth = presetWidth;
                _profile.Rdp.DesktopHeight = presetHeight;
            }
        }

        // 全屏 / 多显示器 / 音频重定向 / 连接质量由编辑器属性写回（属性已 write-through，这里再确保与 VM 一致）。
        _profile.Rdp.StartFullScreen = RdpStartFullScreen;
        _profile.Rdp.UseMultimon = RdpUseMultimon;
        _profile.Rdp.RedirectAudio = RdpRedirectAudio;
        _profile.Rdp.RedirectMicrophone = RdpRedirectMicrophone;
        _profile.Rdp.ConnectionQuality = SelectedConnectionQuality?.Value ?? RdpConnectionQuality.Auto;

        ValidationMessage = string.Empty;

        _profile.Name = Name.Trim();
        _profile.Host = Host.Trim();
        _profile.Port = Port;
        _profile.Protocol = Protocol;
        _profile.Notes = Notes;
        _profile.CredentialId = SelectedCredential.Id;
        _profile.GroupId = SelectedGroup.Id;
        _profile.TagIds = AvailableTags.Where(t => t.IsSelected).Select(t => t.Id).ToList();
        _profile.UpdatedAt = DateTimeOffset.Now;

        return _profile;
    }

    /// <summary>解析自定义分辨率输入。仅接受 1~16384 的正整数。</summary>
    private static bool TryParseDimension(string? text, out int value)
        => int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value)
           && value is > 0 and <= 16384;

    [RelayCommand]
    private void ToggleAdvanced() => IsAdvancedExpanded = !IsAdvancedExpanded;

    /// <summary>
    /// 标签管理对话框关闭后调用，用最新的标签列表刷新 <see cref="AvailableTags"/>。
    /// 已勾选的标签按 Id 保留选中状态；被删除的标签自然从列表消失（选中状态一并丢弃）。
    /// </summary>
    public void RefreshAvailableTags(IReadOnlyList<Tag> tags)
    {
        var selectedIds = AvailableTags.Where(t => t.IsSelected).Select(t => t.Id).ToHashSet();

        AvailableTags.Clear();
        foreach (var tag in tags)
        {
            AvailableTags.Add(new TagSelection(tag.Id, tag.Name, tag.Color)
            {
                IsSelected = selectedIds.Contains(tag.Id)
            });
        }
    }
}

/// <summary>凭据下拉项。Id 为 null 表示「未指定」。</summary>
public sealed record CredentialOption(Guid? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>分组下拉项。Id 为 null 表示「未分组」。</summary>
public sealed record GroupOption(Guid? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>可勾选的标签。</summary>
public sealed partial class TagSelection(Guid id, string name, string color) : ObservableObject
{
    public Guid Id { get; } = id;

    public string Name { get; } = name;

    public string Color { get; } = color;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>RDP 分辨率下拉项。Width/Height 为 null 表示「自定义」（无固定尺寸）。</summary>
public sealed record ResolutionOption(RdpDisplayResolution Value, string Label, int? Width, int? Height)
{
    public override string ToString() => Label;
}

/// <summary>RDP 连接质量下拉项。</summary>
public sealed record ConnectionQualityOption(RdpConnectionQuality Value, string Label)
{
    public override string ToString() => Label;
}
