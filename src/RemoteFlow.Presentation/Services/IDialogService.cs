using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.Presentation.Services;

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

    /// <summary>把文本写入系统剪贴板。</summary>
    Task CopyToClipboardAsync(string text);

    /// <summary>
    /// 新建或编辑连接。<paramref name="existing"/> 为 null 表示新建。
    /// <paramref name="preselectedProtocol"/> 在新建时预选协议（托盘「新建连接 → 协议」入口用），
    /// null 沿用默认 RDP。返回 null 表示用户取消。
    /// </summary>
    Task<ConnectionEditorResult?> EditConnectionAsync(ConnectionProfile? existing, ProtocolType? preselectedProtocol = null);

    /// <summary>新建或编辑凭据。返回 null 表示用户取消。</summary>
    Task<CredentialEditorResult?> EditCredentialAsync(Credential? existing);

    /// <summary>
    /// 新建 / 重命名分组的小对话框。返回用户填写的分组名（已 Trim），取消返回 null。
    /// </summary>
    Task<string?> EditGroupNameAsync(GroupNamePrompt prompt);

    /// <summary>从候选中单选一个新默认分组。返回 null 表示用户取消。</summary>
    Task<DefaultGroupOption?> PickDefaultGroupAsync(string deletedDefaultName, IReadOnlyList<DefaultGroupOption> options);

    /// <summary>新建 / 编辑单个标签（名称 / 颜色 / 描述）的小对话框。返回 null 表示取消。</summary>
    Task<TagEditorResult?> EditTagAsync(TagEditorPrompt prompt);

    /// <summary>
    /// 标签管理：列表形式新建 / 编辑 / 删除标签。对话框关闭后返回数据库里最新的标签列表，
    /// 供调用方刷新自己持有的标签选择状态。
    /// </summary>
    Task<IReadOnlyList<Tag>> ManageTagsAsync();

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

    /// <summary>「关于 RemoteFlow」+ 快捷键速查对话框。</summary>
    Task ShowAboutAsync();

    /// <summary>
    /// 打开统一「测试连接」对话框：对 <paramref name="profile"/> 自动执行
    /// DNS → Ping → TCP 诊断，对话框关闭后返回。
    /// </summary>
    Task ShowConnectionTestAsync(ConnectionProfile profile);
}

/// <summary>
/// 连接编辑结果。
/// </summary>
public sealed record ConnectionEditorResult(ConnectionProfile Profile, bool ConnectImmediately);

/// <summary>分组名输入对话框的上下文。</summary>
/// <param name="Title">对话框标题，如「新建分组」/「重命名分组」。</param>
/// <param name="InitialName">重命名时的原名；新建时为空。</param>
/// <param name="ParentName">上级分组名，用于副标题提示；根级时为 null。</param>
public sealed record GroupNamePrompt(string Title, string InitialName = "", string? ParentName = null);

/// <summary>可选作默认分组的候选项。</summary>
public sealed record DefaultGroupOption(Guid Id, string Name);

/// <summary>标签编辑对话框的上下文。</summary>
/// <param name="Title">对话框标题，如「新建标签」/「编辑标签」。</param>
/// <param name="InitialName">编辑时的原名称；新建时为空。</param>
/// <param name="InitialColor">初始颜色（#RRGGBB）；新建时给一个默认色。</param>
/// <param name="InitialDescription">初始描述；新建时为空。</param>
public sealed record TagEditorPrompt(
    string Title, string InitialName = "", string InitialColor = "#0F6CBD", string InitialDescription = "");

/// <summary>标签编辑结果，均已 Trim。</summary>
public sealed record TagEditorResult(string Name, string Color, string Description);

/// <summary>
/// 凭据编辑结果。
/// <para>
/// <b>安全约束：</b><see cref="Password"/> / <see cref="PrivateKey"/> 为 null 表示
/// 「保持原值不变」，为空字符串表示「清除」。这样密码框留空即可安全地表示不修改，
/// 编辑已有凭据时无需把明文密码回填到界面上。
/// </para>
/// </summary>
public sealed record CredentialEditorResult(Credential Credential, string? Password, string? PrivateKey);
