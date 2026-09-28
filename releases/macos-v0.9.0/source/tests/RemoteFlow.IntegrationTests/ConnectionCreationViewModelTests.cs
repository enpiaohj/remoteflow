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
