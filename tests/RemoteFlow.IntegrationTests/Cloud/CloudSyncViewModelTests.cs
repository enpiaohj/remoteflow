using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests.Cloud;

public sealed class CloudSyncViewModelTests
{
    private readonly FakeCloudSyncService _sync = new();
    private readonly StubDialogService _dialogs = new();
    private readonly AppSettings _settings = new() { CloudBaseUrl = "https://host/appscloud/" };

    private CloudSyncViewModel NewViewModel() =>
        new(_sync, _dialogs, _settings, NullLogger<CloudSyncViewModel>.Instance);

    [Fact]
    public void A_configured_server_url_prefills_and_locks_the_field()
    {
        var vm = NewViewModel();

        Assert.Equal("https://host/appscloud/", vm.ServerUrl);
        Assert.False(vm.ServerUrlEditable);
        Assert.True(vm.HasConfiguredServerUrl);
    }

    [Fact]
    public void Triple_click_unlocks_the_server_url_field()
    {
        var vm = NewViewModel();

        vm.UnlockServerUrlField();

        Assert.True(vm.ServerUrlEditable);
    }

    [Fact]
    public void Without_a_configured_url_the_field_is_editable()
    {
        var vm = new CloudSyncViewModel(
            _sync, _dialogs, new AppSettings { CloudBaseUrl = string.Empty }, NullLogger<CloudSyncViewModel>.Instance);

        Assert.True(vm.ServerUrlEditable);
        Assert.False(vm.HasConfiguredServerUrl);
    }

    [Fact]
    public void Toggling_auth_mode_switches_the_submit_label()
    {
        var vm = NewViewModel();
        Assert.False(vm.IsRegisterMode);

        vm.ToggleAuthModeCommand.Execute(null);

        Assert.True(vm.IsRegisterMode);
        Assert.Equal("创建账号并启用同步", vm.SubmitLabel);
    }

    [Fact]
    public async Task Initialize_resuming_a_ready_session_lands_in_ready_and_refreshes()
    {
        _sync.ResumeResult = CloudUnlockState.Ready;
        _sync.PendingOutbox = 4;
        var vm = NewViewModel();

        await vm.InitializeAsync();

        Assert.Equal(CloudSyncUiState.Ready, vm.State);
        Assert.Equal(4, vm.PendingChangeCount);
        Assert.False(vm.CanEditConnectionFields);
    }

    [Fact]
    public async Task Connect_that_needs_bootstrap_moves_to_vault_setup_and_clears_password()
    {
        _sync.SignInResult = CloudUnlockState.NeedsBootstrap;
        var vm = NewViewModel();
        vm.Email = "me@example.com";
        vm.Password = "cloud-passw0rd";

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(CloudSyncUiState.NeedsVaultSetup, vm.State);
        Assert.Equal(string.Empty, vm.Password);
    }

    [Fact]
    public async Task Register_requires_a_matching_twelve_char_password()
    {
        var vm = NewViewModel();
        vm.ToggleAuthModeCommand.Execute(null);
        vm.Email = "me@example.com";
        vm.Password = "short";
        vm.PasswordConfirm = "short";

        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(CloudSyncUiState.SignedOut, vm.State);
        Assert.NotNull(vm.ErrorMessage);

        vm.Password = "long-enough-passw0rd";
        vm.PasswordConfirm = "different-passw0rd!!";
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(CloudSyncUiState.SignedOut, vm.State);

        vm.Password = "long-enough-passw0rd";
        vm.PasswordConfirm = "long-enough-passw0rd";
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(CloudSyncUiState.NeedsVaultSetup, vm.State);
    }

    [Fact]
    public async Task Create_vault_surfaces_the_recovery_key_until_saved_and_acknowledged()
    {
        _sync.RecoveryKeyToReturn = "WORD WORD WORD";
        var vm = NewViewModel();
        vm.Email = "me@example.com";
        vm.Password = "cloud-passw0rd";
        await vm.ConnectCommand.ExecuteAsync(null);

        await vm.CreateVaultCommand.ExecuteAsync(null);
        Assert.Equal("WORD WORD WORD", vm.NewRecoveryKey);
        Assert.True(vm.HasNewRecoveryKey);
        Assert.Equal(CloudSyncUiState.Ready, vm.State);
        Assert.False(vm.AcknowledgeRecoveryKeyCommand.CanExecute(null));

        await vm.CopyRecoveryKeyCommand.ExecuteAsync(null);
        Assert.Equal("WORD WORD WORD", _dialogs.LastClipboardText);
        Assert.True(vm.AcknowledgeRecoveryKeyCommand.CanExecute(null));

        vm.AcknowledgeRecoveryKeyCommand.Execute(null);
        Assert.Null(vm.NewRecoveryKey);
    }

    [Fact]
    public async Task Sync_now_reports_a_count_summary_and_is_only_available_when_ready()
    {
        var vm = NewViewModel();
        Assert.False(vm.SyncNowCommand.CanExecute(null));

        _sync.SignInResult = CloudUnlockState.Ready;
        _sync.NextRun = new SyncRunResult(3, 0, 5, 0, 10, SyncStatus.Synced);
        vm.Email = "me@example.com";
        vm.Password = "cloud-passw0rd";
        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.True(vm.SyncNowCommand.CanExecute(null));
        await vm.SyncNowCommand.ExecuteAsync(null);
        Assert.Equal(1, _sync.SyncNowCalls);
        Assert.Equal("上传 3 · 下载 5", vm.LastSyncSummary);
    }

    [Fact]
    public async Task Changing_the_require_approval_toggle_calls_through()
    {
        _sync.SignInResult = CloudUnlockState.Ready;
        var vm = NewViewModel();
        vm.Email = "me@example.com";
        vm.Password = "cloud-passw0rd";
        await vm.ConnectCommand.ExecuteAsync(null);

        vm.RequireApproval = true;

        Assert.Equal(true, _sync.RequireApprovalSetTo);
    }

    [Fact]
    public async Task Changing_the_master_password_prompts_twice_and_calls_through()
    {
        _sync.SignInResult = CloudUnlockState.Ready;
        _dialogs.PasswordResponses.Enqueue("current-passw0rd");
        _dialogs.PasswordResponses.Enqueue("brand-new-passw0rd");
        var vm = NewViewModel();
        vm.Email = "me@example.com";
        vm.Password = "cloud-passw0rd";
        await vm.ConnectCommand.ExecuteAsync(null);

        await vm.ChangePasswordCommand.ExecuteAsync(null);

        Assert.Equal(("current-passw0rd", "brand-new-passw0rd"), _sync.ChangedPassword);
    }

    [Fact]
    public async Task Approving_a_pending_device_calls_through_and_refreshes_the_list()
    {
        _sync.SignInResult = CloudUnlockState.Ready;
        _sync.RequireApproval = true;
        var deviceId = Guid.NewGuid();
        _sync.Pending.Add(new CloudPendingDevice(deviceId, "pc-b", "Bob PC", "windows", DateTimeOffset.UtcNow));
        var vm = NewViewModel();
        vm.Email = "me@example.com";
        vm.Password = "cloud-passw0rd";
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Single(vm.PendingDevices);

        await vm.ApproveDeviceCommand.ExecuteAsync(vm.PendingDevices[0]);

        Assert.Equal(deviceId, _sync.ApprovedDeviceId);
        Assert.Empty(vm.PendingDevices);
    }

    [Fact]
    public async Task Resolving_a_conflict_keep_local_calls_through()
    {
        _sync.SignInResult = CloudUnlockState.Ready;
        var id = Guid.NewGuid();
        _sync.ConflictList.Add(new SyncConflictRecord(
            id, "connection", "c-1", null,
            new SyncServerEntity("connection", "c-1", 2, 0, 1, 1, false, null, null),
            DateTimeOffset.UtcNow, ConflictResolution.Unresolved));
        var vm = NewViewModel();
        vm.Email = "me@example.com";
        vm.Password = "cloud-passw0rd";
        await vm.ConnectCommand.ExecuteAsync(null);

        await vm.KeepLocalCommand.ExecuteAsync(vm.Conflicts[0]);

        Assert.Equal((id, ConflictResolution.KeepLocal), _sync.ResolvedConflict);
    }

    [Fact]
    public async Task Disconnect_and_wipe_needs_a_danger_confirmation()
    {
        _sync.SignInResult = CloudUnlockState.Ready;
        var vm = NewViewModel();
        vm.Email = "me@example.com";
        vm.Password = "cloud-passw0rd";
        await vm.ConnectCommand.ExecuteAsync(null);

        _dialogs.ConfirmResult = false;
        await vm.DisconnectAndWipeCommand.ExecuteAsync(null);
        Assert.Null(_sync.SignedOutWipe);

        _dialogs.ConfirmResult = true;
        await vm.DisconnectAndWipeCommand.ExecuteAsync(null);
        Assert.True(_sync.SignedOutWipe);
        Assert.Equal(CloudSyncUiState.SignedOut, vm.State);
    }

    [Fact]
    public async Task Restore_from_cloud_needs_a_danger_confirm_and_calls_through()
    {
        _sync.SignInResult = CloudUnlockState.Ready;
        var vm = NewViewModel();
        vm.Email = "me@example.com";
        vm.Password = "cloud-passw0rd";
        await vm.ConnectCommand.ExecuteAsync(null);

        // 危险操作需先手动输入「清除」才启用（防误点）。
        Assert.False(vm.RestoreFromCloudCommand.CanExecute(null));
        vm.DangerPhrase = "清除";
        Assert.True(vm.RestoreFromCloudCommand.CanExecute(null));

        _dialogs.ConfirmResult = false;
        await vm.RestoreFromCloudCommand.ExecuteAsync(null);
        Assert.False(_sync.RestoredFromCloud);

        _dialogs.ConfirmResult = true;
        await vm.RestoreFromCloudCommand.ExecuteAsync(null);
        Assert.True(_sync.RestoredFromCloud);
    }

    [Fact]
    public async Task Wipe_commands_stay_disabled_until_the_danger_phrase_is_typed()
    {
        _sync.SignInResult = CloudUnlockState.Ready;
        var vm = NewViewModel();
        vm.Email = "me@example.com";
        vm.Password = "cloud-passw0rd";
        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.False(vm.DisconnectAndWipeCommand.CanExecute(null));
        Assert.False(vm.DangerArmed);

        vm.DangerPhrase = "清";
        Assert.False(vm.DisconnectAndWipeCommand.CanExecute(null));

        vm.DangerPhrase = "清除";
        Assert.True(vm.DangerArmed);
        Assert.True(vm.DisconnectAndWipeCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_failed_connect_surfaces_the_error_message()
    {
        _sync.ThrowOnSignIn = new CloudSignInException("邮箱或密码错误");
        var vm = NewViewModel();
        vm.Email = "me@example.com";
        vm.Password = "irrelevant";

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.Equal("邮箱或密码错误", vm.ErrorMessage);
        Assert.Equal(CloudSyncUiState.SignedOut, vm.State);
    }

    private sealed class StubDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public string? LastClipboardText { get; private set; }
        public Queue<string?> PasswordResponses { get; } = new();

        public Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", bool isDanger = false) =>
            Task.FromResult(ConfirmResult);

        public Task ShowMessageAsync(string title, string message, DialogKind kind = DialogKind.Info) =>
            Task.CompletedTask;

        public Task CopyToClipboardAsync(string text)
        {
            LastClipboardText = text;
            return Task.CompletedTask;
        }

        public Task<ConnectionEditorResult?> EditConnectionAsync(ConnectionProfile? e, ProtocolType? p = null) => Task.FromResult<ConnectionEditorResult?>(null);
        public Task<CredentialEditorResult?> EditCredentialAsync(Credential? e) => Task.FromResult<CredentialEditorResult?>(null);
        public Task<string?> EditGroupNameAsync(GroupNamePrompt prompt) => Task.FromResult<string?>(null);
        public Task<DefaultGroupOption?> PickDefaultGroupAsync(string n, IReadOnlyList<DefaultGroupOption> o) => Task.FromResult<DefaultGroupOption?>(null);
        public Task<TagEditorResult?> EditTagAsync(TagEditorPrompt prompt) => Task.FromResult<TagEditorResult?>(null);
        public Task<IReadOnlyList<Tag>> ManageTagsAsync() => Task.FromResult<IReadOnlyList<Tag>>([]);
        public Task<bool> ConfirmHostKeyAsync(SshHostKeyVerificationContext context) => Task.FromResult(false);
        public Task<string?> PromptPasswordAsync(string title, string message, bool confirm) =>
            Task.FromResult(PasswordResponses.Count > 0 ? PasswordResponses.Dequeue() : null);
        public string? PickFileToOpen(string title, string filter) => null;
        public string? PickFileToSave(string title, string filter, string defaultFileName) => null;
        public string? PickFolder(string title) => null;
        public Task ShowAboutAsync() => Task.CompletedTask;
        public Task ShowConnectionTestAsync(ConnectionProfile profile) => Task.CompletedTask;
    }
}
