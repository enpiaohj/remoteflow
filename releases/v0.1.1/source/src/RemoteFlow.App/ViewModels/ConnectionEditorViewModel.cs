using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Core.Models;

namespace RemoteFlow.App.ViewModels;

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
        AppSettings defaults)
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
            // 新建连接时套用设置页中的协议默认值。
            _profile.Protocol = ProtocolType.Rdp;
            _profile.Port = ConnectionProfile.GetDefaultPort(ProtocolType.Rdp);
            _profile.Rdp.DisplayMode = defaults.RdpDefaultDisplayMode;
            _profile.Rdp.RedirectClipboard = defaults.RdpDefaultRedirectClipboard;
            _profile.Rdp.RedirectAudio = defaults.RdpDefaultRedirectAudio;
            _profile.Rdp.UseMultimon = defaults.RdpDefaultUseMultimon;
            _profile.Ssh.KeepAliveSeconds = defaults.SshDefaultKeepAliveSeconds;
            _profile.Ssh.TerminalType = defaults.SshDefaultTerminalType;
            _profile.Ssh.Encoding = defaults.SshDefaultEncoding;
        }

        Title = _isNew ? "新建连接" : "编辑连接";

        // 「未指定」与「未分组」用 null 作为哨兵项，避免额外的可空判断散落在界面里。
        AvailableCredentials = [new CredentialOption(null, "未指定"),
            .. credentials.Select(c => new CredentialOption(c.Id, c.Name))];
        AvailableGroups = [new GroupOption(null, "未分组"),
            .. groups.Select(g => new GroupOption(g.Id, g.Name))];

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
    }

    public string Title { get; }

    public IReadOnlyList<CredentialOption> AvailableCredentials { get; }

    public IReadOnlyList<GroupOption> AvailableGroups { get; }

    public ObservableCollection<TagSelection> AvailableTags { get; }

    public IReadOnlyList<ProtocolType> AvailableProtocols { get; } =
        [ProtocolType.Rdp, ProtocolType.Ssh, ProtocolType.Vnc];

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

    [RelayCommand]
    private void ToggleAdvanced() => IsAdvancedExpanded = !IsAdvancedExpanded;
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
