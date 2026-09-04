using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.App.Services;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;

namespace RemoteFlow.App.ViewModels;

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
    public ObservableCollection<CredentialItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private CredentialItemViewModel? _selectedItem;

    [ObservableProperty]
    private bool _isLoading;

    public bool IsEmpty => Items.Count == 0;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            var all = await credentials.GetAllAsync(ct);
            var previousSelection = SelectedItem?.Id;

            Items.Clear();
            foreach (var credential in all)
            {
                var usageCount = await connections.CountConnectionsUsingCredentialAsync(credential.Id, ct);
                Items.Add(new CredentialItemViewModel(credential, usageCount));
            }

            SelectedItem = previousSelection is { } id ? Items.FirstOrDefault(i => i.Id == id) : null;
            OnPropertyChanged(nameof(IsEmpty));
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>凭据名称映射，供连接列表显示「凭据」列。</summary>
    public IReadOnlyDictionary<Guid, string> GetNameMap()
        => Items.ToDictionary(i => i.Id, i => i.Name);

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

    public string TypeIcon => Credential.Type switch
    {
        CredentialType.WindowsDomain or CredentialType.LocalPassword => "",
        CredentialType.SshPassword or CredentialType.SshPrivateKey => "",
        _ => ""
    };

    /// <summary>是否已在保险库中保存了 Secret。只显示「有/无」，绝不显示内容。</summary>
    public string SecretStateDisplay => Credential.Type == CredentialType.SshPrivateKey
        ? Credential.KeyReference is not null ? "已保存私钥" : "未保存私钥"
        : Credential.SecretReference is not null ? "已保存密码" : "未保存密码";
}
