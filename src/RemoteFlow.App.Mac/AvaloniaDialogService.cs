using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.App.Mac;

/// <summary>
/// IDialogService 的 Avalonia 占位实现。
/// <para>资产工作台 UI 接入中：先让共享 VM 可装配（本实现仅在被调到时记录"未实现"），
/// 随后逐个替换为真实 Avalonia 窗体（连接编辑 / 凭据编辑 / HostKey 确认等）。</para>
/// </summary>
public sealed class AvaloniaDialogService : IDialogService
{
    private readonly Microsoft.Extensions.Logging.ILogger<AvaloniaDialogService> _logger;

    public AvaloniaDialogService(Microsoft.Extensions.Logging.ILogger<AvaloniaDialogService> logger)
        => _logger = logger;

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", bool isDanger = false)
        => LogNotImpl<bool>($"ConfirmAsync {title} {message}");

    public Task ShowMessageAsync(string title, string message, DialogKind kind = DialogKind.Info)
        => LogNotImpl($"ShowMessageAsync {title} {message}");

    public Task<ConnectionEditorResult?> EditConnectionAsync(ConnectionProfile? existing, ProtocolType? preselectedProtocol = null)
        => LogNotImpl<ConnectionEditorResult?>($"EditConnectionAsync {existing?.Name}");

    public Task<CredentialEditorResult?> EditCredentialAsync(Credential? existing)
        => LogNotImpl<CredentialEditorResult?>($"EditCredentialAsync {existing?.Name}");

    public Task<string?> EditGroupNameAsync(GroupNamePrompt prompt)
        => LogNotImpl<string?>($"EditGroupNameAsync {prompt.Title}");

    public Task<DefaultGroupOption?> PickDefaultGroupAsync(string deletedDefaultName, IReadOnlyList<DefaultGroupOption> options)
        => LogNotImpl<DefaultGroupOption?>($"PickDefaultGroupAsync {deletedDefaultName}");

    public Task<TagEditorResult?> EditTagAsync(TagEditorPrompt prompt)
        => LogNotImpl<TagEditorResult?>($"EditTagAsync {prompt.Title}");

    public Task<IReadOnlyList<Tag>> ManageTagsAsync()
        => LogNotImpl<IReadOnlyList<Tag>>("ManageTagsAsync");

    public Task<bool> ConfirmHostKeyAsync(SshHostKeyVerificationContext context)
        => LogNotImpl<bool>($"ConfirmHostKeyAsync {context.Host}");

    public Task<string?> PromptPasswordAsync(string title, string message, bool confirm)
        => LogNotImpl<string?>($"PromptPasswordAsync {title}");

    public string? PickFileToOpen(string title, string filter) => null;

    public string? PickFileToSave(string title, string filter, string defaultFileName) => null;

    public string? PickFolder(string title) => null;

    public Task ShowAboutAsync() => LogNotImpl("ShowAboutAsync");

    public Task ShowConnectionTestAsync(ConnectionProfile profile)
        => LogNotImpl($"ShowConnectionTestAsync {profile.Name}");

    private Task<T> LogNotImpl<T>(string what)
    {
        _logger.LogWarning("IDialogService 未实现（资产工作台接入中）：{What}", what);
        return Task.FromResult<T>(default!);
    }

    private Task LogNotImpl(string what)
    {
        _logger.LogWarning("IDialogService 未实现（资产工作台接入中）：{What}", what);
        return Task.CompletedTask;
    }
}
