using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Presentation.ViewModels;

/// <summary>
/// 新建 / 编辑凭据对话框。
/// <para>
/// <b>安全约束：编辑已有凭据时，界面不回填任何密码或私钥明文。</b>
/// 密码框留空即表示「保持原值不变」，界面上会明确说明这一点，
/// 从而既避免明文出现在 UI 上，又不会让用户误以为密码被清空了。
/// </para>
/// </summary>
public sealed partial class CredentialEditorViewModel : ObservableObject
{
    private readonly Credential _credential;

    public CredentialEditorViewModel(Credential? existing)
    {
        IsNew = existing is null;
        _credential = existing ?? new Credential();

        Title = IsNew ? "新建凭据" : "编辑凭据";

        _name = _credential.Name;
        _type = _credential.Type;
        _username = _credential.Username;
        _domain = _credential.Domain;
        _description = _credential.Description;

        HasStoredPassword = _credential.SecretReference is not null;
        HasStoredPrivateKey = _credential.KeyReference is not null;
    }

    public string Title { get; }

    public bool IsNew { get; }

    /// <summary>保险库中是否已存有密码。只显示有无，绝不显示内容。</summary>
    public bool HasStoredPassword { get; }

    /// <summary>保险库中是否已存有私钥。</summary>
    public bool HasStoredPrivateKey { get; }

    public IReadOnlyList<CredentialType> AvailableTypes { get; } =
    [
        CredentialType.WindowsDomain,
        CredentialType.LocalPassword,
        CredentialType.SshPassword,
        CredentialType.SshPrivateKey,
        CredentialType.VncPassword
    ];

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private CredentialType _type;

    [ObservableProperty]
    private string _username;

    [ObservableProperty]
    private string _domain;

    [ObservableProperty]
    private string _description;

    /// <summary>
    /// 用户新输入的密码。null 表示未修改，空字符串表示清除。
    /// <para>该值只在对话框存活期间存在，确认后立即交给 Vault 加密，不会写入任何日志或数据库。</para>
    /// </summary>
    public string? EnteredPassword { get; set; }

    /// <summary>用户新输入的私钥正文。语义同 <see cref="EnteredPassword"/>。</summary>
    [ObservableProperty]
    private string? _enteredPrivateKey;

    [ObservableProperty]
    private string _validationMessage = string.Empty;

    public bool HasValidationMessage => !string.IsNullOrEmpty(ValidationMessage);

    partial void OnValidationMessageChanged(string value) => OnPropertyChanged(nameof(HasValidationMessage));

    // 按类型决定显示哪些字段
    public bool NeedsUsername => Credential.RequiresUsername(Type);
    public bool NeedsDomain => Type == CredentialType.WindowsDomain;
    public bool NeedsPrivateKey => Credential.RequiresPrivateKey(Type);

    /// <summary>私钥登录时，密码字段承担的是 Passphrase 的角色。</summary>
    public string PasswordLabel => NeedsPrivateKey ? "私钥密码" : "密码";

    public string PasswordHint => NeedsPrivateKey
        ? HasStoredPrivateKey ? "留空表示不修改（私钥无密码可留空）" : "私钥没有加密时留空"
        : HasStoredPassword ? "留空表示不修改已保存的密码" : "密码将加密保存到本机凭据保险库";

    public string PrivateKeyHint => HasStoredPrivateKey
        ? "已保存私钥。留空表示不修改，粘贴新私钥可替换。"
        : "粘贴 OpenSSH 或 PEM 格式的私钥正文。";

    partial void OnTypeChanged(CredentialType value)
    {
        OnPropertyChanged(nameof(NeedsUsername));
        OnPropertyChanged(nameof(NeedsDomain));
        OnPropertyChanged(nameof(NeedsPrivateKey));
        OnPropertyChanged(nameof(PasswordLabel));
        OnPropertyChanged(nameof(PasswordHint));
    }

    /// <summary>校验并生成凭据元数据。校验不通过返回 null。</summary>
    public Credential? Build()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            ValidationMessage = "请填写凭据名称。";
            return null;
        }

        if (NeedsUsername && string.IsNullOrWhiteSpace(Username))
        {
            ValidationMessage = "该凭据类型必须填写用户名。";
            return null;
        }

        // 新建私钥凭据却没有提供私钥，连接时必然失败，在此提前拦截。
        if (NeedsPrivateKey && !HasStoredPrivateKey && string.IsNullOrWhiteSpace(EnteredPrivateKey))
        {
            ValidationMessage = "SSH 私钥凭据必须提供私钥正文。";
            return null;
        }

        ValidationMessage = string.Empty;

        _credential.Name = Name.Trim();
        _credential.Type = Type;
        _credential.Username = NeedsUsername ? Username.Trim() : string.Empty;
        _credential.Domain = NeedsDomain ? Domain.Trim() : string.Empty;
        _credential.Description = Description;

        return _credential;
    }
}
