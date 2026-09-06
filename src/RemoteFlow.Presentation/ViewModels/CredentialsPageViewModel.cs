using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Presentation.ViewModels;

/// <summary>凭据类型筛选。</summary>
public enum CredentialTypeFilter
{
    All,
    WindowsDomain,
    LocalPassword,
    SshPassword,
    SshPrivateKey,
    VncPassword
}

/// <summary>筛选下拉的一项。</summary>
public sealed record CredentialTypeOption(CredentialTypeFilter Value, string Label);

/// <summary>批量“更改类型”菜单的一项（目标凭据类型）。</summary>
public sealed record CredentialTypeTargetOption(CredentialType Value, string Label);

/// <summary>
/// 「凭据」页面。只管理凭据元数据与安全存储，不承担主机列表管理。
/// <para>
/// <b>界面从不显示任何密码或私钥明文</b>，列表中只呈现名称、类型、用户名与备注。
/// </para>
/// </summary>
public sealed partial class CredentialsPageViewModel(
    CredentialService credentials,
    ConnectionService connections,
    IDialogService dialogs) : ObservableObject
{
    /// <summary>过滤后展示的行。完整集合保存在 <see cref="_all"/>。</summary>
    public ObservableCollection<CredentialItemViewModel> Items { get; } = [];

    private readonly List<CredentialItemViewModel> _all = [];

    [ObservableProperty]
    private CredentialItemViewModel? _selectedItem;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>按名称 / 用户名 / 类型筛选。</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>多选模式下被勾选的凭据。</summary>
    public ObservableCollection<CredentialItemViewModel> SelectedCredentials { get; } = [];

    [ObservableProperty]
    private bool _isMultiSelect;

    public bool HasSelection => SelectedCredentials.Count > 0;

    public string SelectionSummary => $"已选择 {SelectedCredentials.Count} 项";

    /// <summary>批量「更改类型」当前合法的目标（混合选择只给对全部合法项）。</summary>
    public IReadOnlyList<CredentialTypeTargetOption> ChangeTypeOptions { get; private set; } = [];

    /// <summary>全部类型下拉项。</summary>
    public IReadOnlyList<CredentialTypeOption> TypeOptions { get; } =
    [
        new(CredentialTypeFilter.All, "全部类型"),
        new(CredentialTypeFilter.WindowsDomain, "Windows 域账号"),
        new(CredentialTypeFilter.LocalPassword, "本地账号"),
        new(CredentialTypeFilter.SshPassword, "SSH 口令"),
        new(CredentialTypeFilter.SshPrivateKey, "SSH 私钥"),
        new(CredentialTypeFilter.VncPassword, "VNC 口令"),
    ];

    [ObservableProperty]
    private CredentialTypeOption _typeOption = new(CredentialTypeFilter.All, "全部类型");

    partial void OnIsMultiSelectChanged(bool value)
    {
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }

        SelectedCredentials.Clear();
        RefreshSelectionSummary();
    }

    partial void OnTypeOptionChanged(CredentialTypeOption value) => ApplyFilter();

    /// <summary>一条凭据都没有（与「筛选无结果」区分）。</summary>
    public bool IsEmpty => _all.Count == 0;

    /// <summary>有凭据，但当前筛选没有命中任何一条。</summary>
    public bool HasNoMatch => _all.Count > 0 && Items.Count == 0;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            var all = await credentials.GetAllAsync(ct);
            var previousSelection = SelectedItem?.Id;

            _all.Clear();
            foreach (var credential in all)
            {
                var usageCount = await connections.CountConnectionsUsingCredentialAsync(credential.Id, ct);
                _all.Add(new CredentialItemViewModel(credential, usageCount));
            }

            ApplyFilter();
            SelectedItem = previousSelection is { } id ? Items.FirstOrDefault(i => i.Id == id) : null;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        var q = SearchText?.Trim() ?? string.Empty;
        var type = TypeOption.Value;

        IEnumerable<CredentialItemViewModel> matches = _all;
        if (type != CredentialTypeFilter.All)
        {
            matches = matches.Where(i => FilterMatches(i.Credential.Type, type));
        }

        if (q.Length > 0)
        {
            matches = matches.Where(i =>
                i.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || i.Username.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || i.TypeName.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        }

        Items.Clear();
        foreach (var item in matches)
        {
            Items.Add(item);
        }

        // 筛选后不可见的已勾选项同步移出选择，避免删除/计数错乱。
        var visible = Items.Select(i => i.Id).ToHashSet();
        for (var i = SelectedCredentials.Count - 1; i >= 0; i--)
        {
            if (!visible.Contains(SelectedCredentials[i].Id))
            {
                SelectedCredentials[i].IsSelected = false;
                SelectedCredentials.RemoveAt(i);
            }
        }

        RefreshSelectionSummary();

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasNoMatch));
    }

    private static bool FilterMatches(CredentialType type, CredentialTypeFilter filter)
        => filter switch
        {
            CredentialTypeFilter.WindowsDomain => type == CredentialType.WindowsDomain,
            CredentialTypeFilter.LocalPassword => type == CredentialType.LocalPassword,
            CredentialTypeFilter.SshPassword => type == CredentialType.SshPassword,
            CredentialTypeFilter.SshPrivateKey => type == CredentialType.SshPrivateKey,
            CredentialTypeFilter.VncPassword => type == CredentialType.VncPassword,
            _ => true
        };

    /// <summary>凭据名称映射，供连接列表显示「凭据」列。</summary>
    public IReadOnlyDictionary<Guid, string> GetNameMap()
        => _all.ToDictionary(i => i.Id, i => i.Name);

    [RelayCommand]
    private async Task CreateAsync()
    {
        var result = await dialogs.EditCredentialAsync(null);
        if (result is null)
        {
            return;
        }

        await credentials.CreateAsync(result.Credential, result.Password, result.PrivateKey);
        await LoadAsync();
        SelectedItem = Items.FirstOrDefault(i => i.Id == result.Credential.Id);
    }

    [RelayCommand]
    private async Task EditAsync(CredentialItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null)
        {
            return;
        }

        var result = await dialogs.EditCredentialAsync(item.Credential);
        if (result is null)
        {
            return;
        }

        // Password / PrivateKey 为 null 表示用户未修改，服务层会保留原有 Secret。
        await credentials.UpdateAsync(result.Credential, result.Password, result.PrivateKey);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync(CredentialItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null)
        {
            return;
        }

        // 删除凭据会影响仍在引用它的连接，必须明确告知而不是静默解除引用。
        var impact = item.UsageCount > 0
            ? $"\n\n有 {item.UsageCount} 个连接正在引用该凭据，删除后这些连接将变为「未指定凭据」，需要重新选择后才能连接。"
            : string.Empty;

        var confirmed = await dialogs.ConfirmAsync(
            "删除凭据",
            $"确定要删除凭据「{item.Name}」吗？其保存的密码或私钥会被一并从保险库中清除，该操作无法撤销。{impact}",
            "删除",
            isDanger: true);

        if (!confirmed)
        {
            return;
        }

        await credentials.DeleteAsync(item.Id);
        await LoadAsync();
    }

    // ── 多选与批量删除 ────────────────────────────────────────────

    public void ToggleSelect(CredentialItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (SelectedCredentials.Contains(item))
        {
            SelectedCredentials.Remove(item);
            item.IsSelected = false;
        }
        else
        {
            SelectedCredentials.Add(item);
            item.IsSelected = true;
        }

        RefreshSelectionSummary();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        SelectedCredentials.Clear();
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }

        RefreshSelectionSummary();
    }

    [RelayCommand]
    private void ExitMultiSelect() => IsMultiSelect = false;

    /// <summary>批量删除所选凭据（强确认；汇总引用影响）。</summary>
    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var selected = SelectedCredentials.ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var names = string.Join("、", selected.Select(i => i.Name));
        var referenced = selected.Sum(i => i.UsageCount);
        var impact = referenced > 0
            ? $"\n\n其中 {referenced} 个连接引用到这些凭据，删除后它们将变为「未指定凭据」，需要重新选择后才能连接。"
            : string.Empty;

        var confirmed = await dialogs.ConfirmAsync(
            "删除凭据",
            $"确定要删除这 {selected.Count} 个凭据吗？其保存的密码或私钥会被一并从保险库中清除，该操作无法撤销。\n\n{names}{impact}",
            "删除",
            isDanger: true);

        if (!confirmed)
        {
            return;
        }

        foreach (var item in selected)
        {
            await credentials.DeleteAsync(item.Id);
        }

        await LoadAsync();
        ExitMultiSelect();
    }

    private void RefreshSelectionSummary()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummary));

        RecomputeChangeTypeOptions();
    }

    /// <summary>按当前勾选推算合法的「更改类型」目标：
    /// SSH 私钥（KeyReference）与口令型结构不同，一律不允许批量转换。</summary>
    private void RecomputeChangeTypeOptions()
    {
        var selected = SelectedCredentials.ToList();
        if (selected.Count == 0 || selected.Any(i => i.Credential.Type == CredentialType.SshPrivateKey))
        {
            ChangeTypeOptions = [];
        }
        else
        {
            var current = selected.Select(i => i.Credential.Type).ToHashSet();
            var options = PasswordTargetTypes
                .Where(t => !(current.Count == 1 && current.Contains(t)))
                .Select(t => new CredentialTypeTargetOption(t, TypeLabel(t)))
                .ToList();
            ChangeTypeOptions = options;
        }

        OnPropertyChanged(nameof(ChangeTypeOptions));
    }

    /// <summary>口令型（可互转）目标类型；SSH 私钥为独立 Secret 结构，不在此列。</summary>
    private static readonly CredentialType[] PasswordTargetTypes =
    [
        CredentialType.WindowsDomain,
        CredentialType.LocalPassword,
        CredentialType.SshPassword,
        CredentialType.VncPassword,
    ];

    private static string TypeLabel(CredentialType type) => type switch
    {
        CredentialType.WindowsDomain => "Windows 域账号",
        CredentialType.LocalPassword => "本地账号",
        CredentialType.SshPassword => "SSH 口令",
        CredentialType.SshPrivateKey => "SSH 私钥",
        _ => "VNC 口令"
    };

    /// <summary>批量更改所选凭据类型（仅口令型互转）。不触碰 Secret 内容。</summary>
    public async Task ChangeTypeSelectedAsync(CredentialType target)
    {
        var selected = SelectedCredentials.ToList();
        if (selected.Count == 0)
        {
            return;
        }

        // 校验引用兼容性：改类型不能破坏仍在引用它的连接。
        var selectedIds = selected.Select(i => i.Id).ToHashSet();
        var profiles = await connections.GetAllAsync();
        var incompatible = profiles
            .Where(p => p.CredentialId is { } cid && selectedIds.Contains(cid))
            .Where(p => !TypeAllowedForProtocol(target, p.Protocol))
            .ToList();

        if (incompatible.Count > 0)
        {
            await dialogs.ShowMessageAsync(
                "无法更改类型",
                $"有 {incompatible.Count} 个连接引用到所选凭据，但目标类型「{TypeLabel(target)}」与这些连接的协议不兼容" +
                "（如 SSH 连接不能使用 Windows 域账号）。请先处理这些引用，未做任何更改。",
                DialogKind.Warning);
            return;
        }

        var names = string.Join("、", selected.Select(i => i.Name));
        var confirmed = await dialogs.ConfirmAsync(
            "更改凭据类型",
            $"把所选 {selected.Count} 个凭据的类型统一改为「{TypeLabel(target)}」？\n\n{names}\n\n" +
            "只修改类型与元数据，不触碰已保存的密码 / 私钥内容。",
            "更改",
            isDanger: false);

        if (!confirmed)
        {
            return;
        }

        foreach (var item in selected)
        {
            item.Credential.Type = target;
            await credentials.UpdateAsync(item.Credential, null, null);
        }

        await LoadAsync();
        ExitMultiSelect();
    }

    /// <summary>目标类型对该协议是否允许（决定引用是否兼容）。</summary>
    private static bool TypeAllowedForProtocol(CredentialType type, ProtocolType protocol)
        => protocol switch
        {
            ProtocolType.Rdp => type is CredentialType.WindowsDomain or CredentialType.LocalPassword,
            ProtocolType.Ssh => type is CredentialType.SshPassword or CredentialType.SshPrivateKey,
            _ => type == CredentialType.VncPassword
        };

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();
}

/// <summary>凭据列表行。</summary>
public sealed partial class CredentialItemViewModel(Credential credential, int usageCount) : ObservableObject
{
    public Credential Credential { get; } = credential;

    public Guid Id => Credential.Id;

    public string Name => Credential.Name;

    public string Username => string.IsNullOrEmpty(Credential.Domain)
        ? Credential.Username
        : $"{Credential.Domain}\\{Credential.Username}";

    public string Description => Credential.Description;

    /// <summary>引用该凭据的连接数量，用于删除前的影响提示。</summary>
    public int UsageCount { get; } = usageCount;

    public string UsageDisplay => UsageCount == 0 ? "未被引用" : $"{UsageCount} 个连接";

    public string TypeName => Credential.Type switch
    {
        CredentialType.WindowsDomain => "Windows 域账号",
        CredentialType.LocalPassword => "本地账号",
        CredentialType.SshPassword => "SSH 口令",
        CredentialType.SshPrivateKey => "SSH 私钥",
        _ => "VNC 口令"
    };

    // Segoe Fluent Icons，\uXXXX 转义写死（直接贴字形会被文本编码吞掉）。
    public string TypeIcon => Credential.Type switch
    {
        CredentialType.WindowsDomain or CredentialType.LocalPassword => "\uE77B", // Contact
        CredentialType.SshPassword or CredentialType.SshPrivateKey => "\uE756",   // CommandPrompt
        _ => "\uE8D7"                                                             // Permissions
    };

    /// <summary>类型徽章的语义色键。与协议色系保持一致，便于扫读。</summary>
    public string TypeAccentBrushKey => Credential.Type switch
    {
        CredentialType.WindowsDomain => "Protocol.Rdp",
        CredentialType.LocalPassword => "Text.Secondary",
        CredentialType.SshPassword or CredentialType.SshPrivateKey => "Protocol.Ssh",
        _ => "Protocol.Vnc"
    };

    /// <summary>是否已在保险库中保存了 Secret。只显示「有/无」，绝不显示内容。</summary>
    public string SecretStateDisplay => Credential.Type == CredentialType.SshPrivateKey
        ? Credential.KeyReference is not null ? "已保存私钥" : "未保存私钥"
        : Credential.SecretReference is not null ? "已保存密码" : "未保存密码";

    /// <summary>Secret 是否已保存——控制列表里锁图标是否点亮。</summary>
    public bool HasSecret => Credential.Type == CredentialType.SshPrivateKey
        ? Credential.KeyReference is not null
        : Credential.SecretReference is not null;

    /// <summary>多选模式下是否被勾选。</summary>
    [ObservableProperty]
    private bool _isSelected;
}
