using AppKit;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.App.Mac.Host;

/// <summary>
/// <see cref="IDialogService"/> 的 AppKit 实现（8.B 起步版）。
/// <para>
/// 确认 / 提示 / Host Key 已用 <see cref="NSAlert"/> 落地；
/// 编辑类 sheet（连接 / 凭据 / 分组 / 标签）与文件选择器在 8.C 逐个补齐——
/// 未实现的先返回取消并记录，不抛异常，使共享 VM 可完整装配。
/// </para>
/// </summary>
public sealed class AppKitDialogService : IDialogService
{
    private readonly Microsoft.Extensions.Logging.ILogger<AppKitDialogService> _logger;

    public AppKitDialogService(Microsoft.Extensions.Logging.ILogger<AppKitDialogService> logger)
        => _logger = logger;

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", bool isDanger = false)
    {
        var tcs = new TaskCompletionSource<bool>();
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            var alert = new NSAlert
            {
                MessageText = title,
                InformativeText = message,
                AlertStyle = isDanger ? NSAlertStyle.Critical : NSAlertStyle.Informational,
            };
            alert.AddButton(confirmText);
            alert.AddButton("取消");
            tcs.SetResult(alert.RunModal() == (nint)NSAlertButtonReturn.First);
        });
        return tcs.Task;
    }

    public Task ShowMessageAsync(string title, string message, DialogKind kind = DialogKind.Info)
    {
        var tcs = new TaskCompletionSource();
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            new NSAlert
            {
                MessageText = title,
                InformativeText = message,
                AlertStyle = kind switch
                {
                    DialogKind.Error => NSAlertStyle.Critical,
                    DialogKind.Warning => NSAlertStyle.Warning,
                    _ => NSAlertStyle.Informational,
                },
            }.RunModal();
            tcs.SetResult();
        });
        return tcs.Task;
    }

    public Task<bool> ConfirmHostKeyAsync(SshHostKeyVerificationContext context)
    {
        var changed = context.IsMismatch;
        var title = changed ? "⚠️ 主机密钥已变化" : "首次连接：确认主机密钥";
        var body = changed
            ? $"{context.Host}:{context.Port} 的密钥指纹与此前记录不一致，可能存在中间人攻击。\n\n" +
              $"此前：{context.KnownFingerprint}\n本次：{context.Fingerprint}\n\n仍要信任并连接吗？"
            : $"{context.Host}:{context.Port}\n算法 {context.KeyAlgorithm}\n指纹 {context.Fingerprint}\n\n信任该主机并记录指纹？";
        return ConfirmAsync(title, body, changed ? "仍然信任" : "信任", isDanger: changed);
    }

    // ── 8.C 逐个补齐 ──────────────────────────────────────────────

    public Task<ConnectionEditorResult?> EditConnectionAsync(ConnectionProfile? existing, ProtocolType? preselectedProtocol = null)
        => NotYet<ConnectionEditorResult?>("EditConnectionAsync");

    public Task<CredentialEditorResult?> EditCredentialAsync(Credential? existing)
        => NotYet<CredentialEditorResult?>("EditCredentialAsync");

    public Task<string?> EditGroupNameAsync(GroupNamePrompt prompt) => NotYet<string?>("EditGroupNameAsync");

    public Task<DefaultGroupOption?> PickDefaultGroupAsync(string deletedDefaultName, IReadOnlyList<DefaultGroupOption> options)
        => NotYet<DefaultGroupOption?>("PickDefaultGroupAsync");

    public Task<TagEditorResult?> EditTagAsync(TagEditorPrompt prompt) => NotYet<TagEditorResult?>("EditTagAsync");

    public Task<IReadOnlyList<Tag>> ManageTagsAsync() => NotYet<IReadOnlyList<Tag>>("ManageTagsAsync");

    public Task<string?> PromptPasswordAsync(string title, string message, bool confirm)
        => NotYet<string?>("PromptPasswordAsync");

    public string? PickFileToOpen(string title, string filter) => null;

    public string? PickFileToSave(string title, string filter, string defaultFileName) => null;

    public string? PickFolder(string title) => null;

    public Task ShowAboutAsync() => ShowMessageAsync("RemoteFlow", "统一远程连接工作台（macOS）");

    public Task ShowConnectionTestAsync(ConnectionProfile profile) => NotYet<object?>("ShowConnectionTestAsync");

    private Task<T> NotYet<T>(string what)
    {
        _logger.LogWarning("IDialogService.{What} 尚未实现（8.C）", what);
        return Task.FromResult<T>(default!);
    }
}
