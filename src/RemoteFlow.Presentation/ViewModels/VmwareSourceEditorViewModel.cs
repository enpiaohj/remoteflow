using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Services;
using RemoteFlow.Application.Vmware;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Vmware;

namespace RemoteFlow.Presentation.ViewModels;

/// <summary>凭据下拉的一项：「新建凭据」或一个已有凭据。</summary>
public sealed record VmwareCredentialChoice(Guid? CredentialId, string Label);

/// <summary>
/// 添加 / 编辑 ESXi 主机或 vCenter。流程强制两步：先取证书指纹由用户核对，再登录验证并保存。
/// 密码只在内存里短暂存在，保存后立即清空，不回填界面。
/// </summary>
public sealed partial class VmwareSourceEditorViewModel : ObservableObject
{
    private readonly VmwareService _service;
    private readonly VmwareSource _source;
    private readonly bool _isNew;

    public VmwareSourceEditorViewModel(
        VmwareService service, VmwareSource? existing, IReadOnlyList<Credential> credentials)
    {
        _service = service;
        _isNew = existing is null;
        _source = existing?.Clone() ?? new VmwareSource();

        Title = _isNew ? "添加 ESXi / vCenter" : "编辑 ESXi / vCenter";
        _name = _source.Name;
        _host = _source.Host;
        _port = _source.Port.ToString();
        _certPin = _source.CertPinSha256;
        _isPinVerified = !string.IsNullOrEmpty(_source.CertPinSha256);

        CredentialChoices.Add(new VmwareCredentialChoice(null, "新建凭据（输入用户名和密码）"));
        foreach (var credential in credentials.Where(c => Credential.RequiresUsername(c.Type)))
        {
            CredentialChoices.Add(new VmwareCredentialChoice(credential.Id, $"{credential.Name}（{credential.Username}）"));
        }

        _selectedCredential = CredentialChoices.FirstOrDefault(c => c.CredentialId == _source.CredentialId && c.CredentialId is not null)
            ?? CredentialChoices[0];
        _statusText = _isNew ? "先获取证书指纹，核对无误后再登录保存。" : "来源已保存；修改主机后需要重新核对证书指纹。";
    }

    public string Title { get; }

    public ObservableCollection<VmwareCredentialChoice> CredentialChoices { get; } = [];

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchPinCommand))]
    private string _host;

    [ObservableProperty]
    private string _port;

    [ObservableProperty]
    private VmwareCredentialChoice _selectedCredential;

    [ObservableProperty]
    private string _userName = string.Empty;

    [ObservableProperty]
    private string _certPin;

    /// <summary>用户已确认过当前指纹。主机或端口变化会使其失效，必须重新获取。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveNow))]
    private bool _isPinVerified;

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private bool _isStatusError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchPinCommand))]
    [NotifyPropertyChangedFor(nameof(CanSaveNow))]
    private bool _isBusy;

    public bool UsesNewCredential => SelectedCredential.CredentialId is null;

    /// <summary>保存成功后的来源（对话框据此关闭）。</summary>
    public VmwareSource? Saved { get; private set; }

    partial void OnSelectedCredentialChanged(VmwareCredentialChoice value) => OnPropertyChanged(nameof(UsesNewCredential));

    partial void OnHostChanged(string value) => InvalidatePin();

    partial void OnPortChanged(string value) => InvalidatePin();

    /// <summary>端点变化后，之前核对过的指纹不再适用于新的地址，必须重新获取并核对。</summary>
    private void InvalidatePin()
    {
        if (!IsPinVerified) return;
        IsPinVerified = false;
        CertPin = string.Empty;
        SetStatus("地址已修改，请重新获取并核对证书指纹。", error: false);
    }

    private bool CanFetchPin() => !IsBusy && !string.IsNullOrWhiteSpace(Host);

    [RelayCommand(CanExecute = nameof(CanFetchPin))]
    private async Task FetchPinAsync()
    {
        if (!TryReadPort(out var port)) return;
        IsBusy = true;
        try
        {
            SetStatus("正在读取证书指纹…", error: false);
            var pin = await _service.FetchCertificatePinAsync(Host, port);
            CertPin = pin;
            IsPinVerified = true;
            SetStatus("已读取证书指纹。请与 ESXi / vCenter 上显示的证书指纹核对，一致再保存；之后只接受这张证书。", error: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IsPinVerified = false;
            SetStatus("无法连接到该主机：请检查地址、端口和网络。", error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>保存按钮可用：指纹已核对且当前没有操作在进行。</summary>
    public bool CanSaveNow => !IsBusy && IsPinVerified;

    /// <summary>
    /// 登录验证并保存。<paramref name="newPassword"/> 由对话框直接从 PasswordBox 传入，仅在新建凭据时使用；
    /// 密码不作为属性保存在视图模型里，用完即弃。
    /// </summary>
    public async Task SaveAsync(string? newPassword)
    {
        if (!CanSaveNow) return;
        if (!TryReadPort(out var port)) return;
        if (UsesNewCredential && (string.IsNullOrWhiteSpace(UserName) || string.IsNullOrEmpty(newPassword)))
        {
            SetStatus("请填写 ESXi / vCenter 的用户名和密码，或选择已有凭据。", error: true);
            return;
        }

        IsBusy = true;
        try
        {
            SetStatus("正在登录验证…", error: false);
            _source.Name = Name;
            _source.Host = Host;
            _source.Port = port;
            _source.CertPinSha256 = CertPin;
            if (!UsesNewCredential) _source.CredentialId = SelectedCredential.CredentialId;

            Saved = await _service.SaveSourceAsync(
                _source, UsesNewCredential ? UserName : null, UsesNewCredential ? newPassword : null);
            SetStatus("已保存。", error: false);
            SaveCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (VsphereApiException ex)
        {
            SetStatus(ex.Message, error: true);
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, error: true);
        }
        catch (ArgumentException ex)
        {
            SetStatus(ex.Message, error: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 证书指纹不匹配、网络不通等：不回显异常原文（可能含地址以外的细节），给固定指引。
            SetStatus("登录失败：无法与服务器建立安全连接，请确认地址、端口，以及证书指纹是否已核对。", error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>保存成功。对话框收到后关闭。</summary>
    public event EventHandler? SaveCompleted;

    private bool TryReadPort(out int port)
    {
        if (string.IsNullOrWhiteSpace(Port))
        {
            port = 443;
            return true;
        }

        if (int.TryParse(Port.Trim(), out port) && port is > 0 and <= 65535) return true;
        SetStatus("端口必须是 1–65535 之间的数字。", error: true);
        return false;
    }

    private void SetStatus(string text, bool error)
    {
        StatusText = text;
        IsStatusError = error;
    }
}
