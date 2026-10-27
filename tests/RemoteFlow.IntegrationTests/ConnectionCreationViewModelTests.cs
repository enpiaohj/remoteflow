using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

public sealed class ConnectionCreationViewModelTests
{
    [Fact]
    public async Task CreateConnectionAsync_PassesExplicitGroupToDialogCapability()
    {
        var dialogs = new CapturingDialogService();
        var viewModel = new ConnectionsPageViewModel(
            null!, null!, null!, null!, null!, dialogs, null!, null!, null!,
            SynchronousUiDispatcher.Instance, null!);
        var groupId = Guid.NewGuid();

        await viewModel.CreateConnectionAsync(defaultGroupId: groupId);

        Assert.Equal(groupId, dialogs.DefaultGroupId);
    }

    /// <summary>
    /// 「保存并连接」时，新保存的连接不一定在当前列表里（如处于「最近连接」「收藏」筛选视图，
    /// 它还没连过 / 没收藏），此时选中项为空。原先「选中项为空就不连」会让保存并连接静默什么都不做。
    /// </summary>
    [Fact]
    public async Task SaveAndConnect_ConnectsEvenWhenSavedItemIsNotInCurrentList()
    {
        var viewModel = new ConnectionsPageViewModel(
            null!, null!, null!, null!, null!, new CapturingDialogService(), null!, null!, null!,
            SynchronousUiDispatcher.Instance, null!);
        var saved = new ConnectionProfile { Name = "刚保存", Host = "h", Protocol = ProtocolType.Ssh };

        ConnectionProfile? requested = null;
        viewModel.OpenConnectionRequested += (_, profile) => requested = profile;

        var connect = typeof(ConnectionsPageViewModel).GetMethod(
            "ConnectSavedAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)connect.Invoke(viewModel, [saved.Id, saved])!;

        Assert.Same(saved, requested);
    }

    private sealed class CapturingDialogService : IDialogService, IConnectionEditorDialogService
    {
        public Guid? DefaultGroupId { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", bool isDanger = false)
            => Task.FromResult(false);

        public Task ShowMessageAsync(string title, string message, DialogKind kind = DialogKind.Info)
            => Task.CompletedTask;

        public Task CopyToClipboardAsync(string text) => Task.CompletedTask;

        public Task<ConnectionEditorResult?> EditConnectionAsync(
            ConnectionProfile? existing, ProtocolType? preselectedProtocol = null)
            => Task.FromResult<ConnectionEditorResult?>(null);

        public Task<ConnectionEditorResult?> EditConnectionAsync(
            ConnectionProfile? existing, ProtocolType? preselectedProtocol, Guid defaultGroupId)
        {
            DefaultGroupId = defaultGroupId;
            return Task.FromResult<ConnectionEditorResult?>(null);
        }

        public Task<CredentialEditorResult?> EditCredentialAsync(Credential? existing)
            => Task.FromResult<CredentialEditorResult?>(null);

        public Task<string?> EditGroupNameAsync(GroupNamePrompt prompt)
            => Task.FromResult<string?>(null);

        public Task<DefaultGroupOption?> PickDefaultGroupAsync(
            string deletedDefaultName, IReadOnlyList<DefaultGroupOption> options)
            => Task.FromResult<DefaultGroupOption?>(null);

        public Task<TagEditorResult?> EditTagAsync(TagEditorPrompt prompt)
            => Task.FromResult<TagEditorResult?>(null);

        public Task<IReadOnlyList<Tag>> ManageTagsAsync()
            => Task.FromResult<IReadOnlyList<Tag>>([]);

        public Task<bool> ConfirmHostKeyAsync(SshHostKeyVerificationContext context)
            => Task.FromResult(false);

        public Task<string?> PromptPasswordAsync(string title, string message, bool confirm)
            => Task.FromResult<string?>(null);

        public string? PickFileToOpen(string title, string filter) => null;

        public string? PickFileToSave(string title, string filter, string defaultFileName) => null;

        public string? PickFolder(string title) => null;

        public Task ShowAboutAsync() => Task.CompletedTask;

        public Task ShowConnectionTestAsync(ConnectionProfile profile) => Task.CompletedTask;
    }
}
