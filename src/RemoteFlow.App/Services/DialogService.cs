using System.Windows;
using Microsoft.Win32;
using RemoteFlow.App.ViewModels;
using RemoteFlow.App.Views.Dialogs;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.App.Services;

/// <summary>
/// 对话框服务实现。所有交互统一切回 UI 线程执行，
/// 因此协议层可以在自己的后台线程上安全地请求用户确认。
/// </summary>
public sealed class DialogService(
    ConnectionService connections,
    GroupService groupService,
    ICredentialRepository credentials,
    AppSettings settings) : IDialogService
{
    /// <summary>取当前活动窗口作为对话框宿主，保证居中显示且正确模态。</summary>
    private static Window? Owner =>
        System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? System.Windows.Application.Current?.MainWindow;

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", bool isDanger = false)
        => InvokeOnUiAsync(() => MessageDialog.ShowConfirm(Owner, title, message, confirmText, isDanger));

    public Task ShowMessageAsync(string title, string message, DialogKind kind = DialogKind.Info)
        => InvokeOnUiAsync(() =>
        {
            MessageDialog.ShowMessage(Owner, title, message, kind);
            return true;
        });

    public async Task<ConnectionEditorResult?> EditConnectionAsync(ConnectionProfile? existing)
    {
        // 编辑器需要凭据、分组、标签作为下拉数据源，先在后台取齐再开对话框。
        var credentialList = await credentials.GetAllAsync();
        var groups = await connections.GetGroupsAsync();
        var tags = await connections.GetTagsAsync();
        // 新建连接默认落在「我的设备」（或既有库里的第一个用户分组）。
        var defaultGroupId = existing is null ? await groupService.EnsureSeedAsync() : (Guid?)null;

        return await InvokeOnUiAsync<ConnectionEditorResult?>(() =>
        {
            var viewModel = new ConnectionEditorViewModel(existing, credentialList, groups, tags, settings, defaultGroupId);
            var dialog = new ConnectionEditorDialog(viewModel, ManageTagsAsync) { Owner = Owner };

            return dialog.ShowDialog() == true && dialog.Result is { } profile
                ? new ConnectionEditorResult(profile, dialog.ConnectImmediately)
                : null;
        });
    }

    public Task<CredentialEditorResult?> EditCredentialAsync(Credential? existing)
        => InvokeOnUiAsync<CredentialEditorResult?>(() =>
        {
            var viewModel = new CredentialEditorViewModel(existing);
            var dialog = new CredentialEditorDialog(viewModel) { Owner = Owner };

            return dialog.ShowDialog() == true && dialog.Result is { } credential
                ? new CredentialEditorResult(credential, dialog.Password, dialog.PrivateKey)
                : null;
        });

    public Task<bool> ConfirmHostKeyAsync(SshHostKeyVerificationContext context)
        => InvokeOnUiAsync(() => HostKeyDialog.Show(Owner, context));

    public Task<string?> EditGroupNameAsync(GroupNamePrompt prompt)
        => InvokeOnUiAsync(() => GroupNameDialog.Prompt(Owner, prompt));

    public Task<TagEditorResult?> EditTagAsync(TagEditorPrompt prompt)
        => InvokeOnUiAsync(() => TagEditorDialog.Prompt(Owner, prompt));

    public async Task<IReadOnlyList<Tag>> ManageTagsAsync()
    {
        var initialTags = await connections.GetTagsAsync();
        var viewModel = new TagManagerViewModel(connections, this, initialTags);

        await InvokeOnUiAsync(() =>
        {
            var dialog = new TagManagerDialog(viewModel) { Owner = Owner };
            dialog.ShowDialog();
            return true;
        });

        // 对话框内部每次增删改都已经落库，这里重新拉一遍作为唯一真相返回给调用方，
        // 不直接复用 viewModel.Tags——避免把展示层模型泄漏到调用方。
        return await connections.GetTagsAsync();
    }

    public Task<string?> PromptPasswordAsync(string title, string message, bool confirm)
        => InvokeOnUiAsync(() => PasswordPromptDialog.Prompt(Owner, title, message, confirm));

    public string? PickFileToOpen(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true
        };

        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? PickFileToSave(string title, string filter, string defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = defaultFileName,
            OverwritePrompt = true
        };

        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    /// <summary>
    /// 在 UI 线程执行并返回结果。
    /// 已在 UI 线程时直接执行，避免为一次弹窗多绕一圈调度。
    /// </summary>
    private static Task<T> InvokeOnUiAsync<T>(Func<T> action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return Task.FromResult(action());
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}
