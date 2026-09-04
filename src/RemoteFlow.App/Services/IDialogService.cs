using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.App.Services;

/// <summary>对话框类型，决定图标与强调色。</summary>
public enum DialogKind
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// 对话框服务。让 ViewModel 无需引用任何 View 类型即可与用户交互。
/// </summary>
public interface IDialogService
{
    /// <summary>确认对话框。<paramref name="isDanger"/> 为 true 时确认按钮使用警示色。</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", bool isDanger = false);

    /// <summary>信息提示。</summary>
    Task ShowMessageAsync(string title, string message, DialogKind kind = DialogKind.Info);

    /// <summary>
    /// 新建或编辑连接。<paramref name="existing"/> 为 null 表示新建。
    /// 返回 null 表示用户取消。
    /// </summary>
    Task<ConnectionEditorResult?> EditConnectionAsync(ConnectionProfile? existing);

    /// <summary>新建或编辑凭据。返回 null 表示用户取消。</summary>
    Task<CredentialEditorResult?> EditCredentialAsync(Credential? existing);

    /// <summary>
    /// SSH 主机密钥确认。首次连接询问是否信任；指纹变化时必须以强警告呈现。
    /// </summary>
    Task<bool> ConfirmHostKeyAsync(SshHostKeyVerificationContext context);

    /// <summary>
    /// 弹出口令输入框。<paramref name="confirm"/> 为 true 时要求两次输入一致
    /// （用于设置新口令）。返回 null 表示用户取消。
    /// </summary>
    Task<string?> PromptPasswordAsync(string title, string message, bool confirm);

    /// <summary>选择要打开的文件，取消返回 null。</summary>
    string? PickFileToOpen(string title, string filter);

    /// <summary>选择保存路径，取消返回 null。</summary>
    string? PickFileToSave(string title, string filter, string defaultFileName);

    /// <summary>选择一个目录，取消返回 null。</summary>
    string? PickFolder(string title);
}

/// <summary>
/// 连接编辑结果。
/// </summary>
public sealed record ConnectionEditorResult(ConnectionProfile Profile, bool ConnectImmediately);

/// <summary>
/// 凭据编辑结果。
/// <para>
/// <b>安全约束：</b><see cref="Password"/> / <see cref="PrivateKey"/> 为 null 表示
/// 「保持原值不变」，为空字符串表示「清除」。这样密码框留空即可安全地表示不修改，
/// 编辑已有凭据时无需把明文密码回填到界面上。
/// </para>
/// </summary>
public sealed record CredentialEditorResult(Credential Credential, string? Password, string? PrivateKey);
