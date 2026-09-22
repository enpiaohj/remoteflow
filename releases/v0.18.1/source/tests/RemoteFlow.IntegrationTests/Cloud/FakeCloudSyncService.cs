using RemoteFlow.Core.Cloud;

namespace RemoteFlow.IntegrationTests.Cloud;

/// <summary>脚本化的 <see cref="ICloudSyncService"/>，供 CloudSyncViewModel 单元测试。</summary>
public sealed class FakeCloudSyncService : ICloudSyncService
{
    public CloudUnlockState? ResumeResult { get; set; }
    public CloudUnlockState SignInResult { get; set; } = CloudUnlockState.Ready;
    public CloudUnlockState RegisterResult { get; set; } = CloudUnlockState.NeedsBootstrap;
    public CloudUnlockState UnlockResult { get; set; } = CloudUnlockState.Ready;
    public CloudUnlockState RetryResult { get; set; } = CloudUnlockState.Ready;
    public string RecoveryKeyToReturn { get; set; } = "AAAA BBBB CCCC";
    public SyncRunResult NextRun { get; set; } = new(0, 0, 0, 0, 0, SyncStatus.Synced);
    public SyncStateSnapshot StateSnapshot { get; set; } = new(0, null, null, SyncStatus.Synced);
    public int PendingOutbox { get; set; }
    public bool RequireApproval { get; set; }
    public List<CloudPendingDevice> Pending { get; } = [];
    public List<SyncConflictRecord> ConflictList { get; } = [];

    public int SyncNowCalls { get; private set; }
    public Guid? ApprovedDeviceId { get; private set; }
    public bool? RequireApprovalSetTo { get; private set; }
    public (string Current, string New)? ChangedPassword { get; private set; }
    public int ResetRecoveryKeyCalls { get; private set; }
    public bool BootstrapRequireApproval { get; private set; }
    public (Guid Id, ConflictResolution Resolution)? ResolvedConflict { get; private set; }
    public bool? SignedOutWipe { get; private set; }
    public bool RestoredFromCloud { get; private set; }
    public Exception? ThrowOnSignIn { get; set; }

    public bool IsSignedIn { get; private set; }
    public bool IsVaultUnlocked { get; private set; }

    public Task<CloudUnlockState> SignInAsync(string baseUrl, string email, string password, CancellationToken ct = default)
    {
        if (ThrowOnSignIn is not null)
        {
            throw ThrowOnSignIn;
        }

        IsSignedIn = true;
        Apply(SignInResult);
        return Task.FromResult(SignInResult);
    }

    public Task<CloudUnlockState> RegisterAsync(string baseUrl, string email, string password, CancellationToken ct = default)
    {
        if (ThrowOnSignIn is not null)
        {
            throw ThrowOnSignIn;
        }

        IsSignedIn = true;
        Apply(RegisterResult);
        return Task.FromResult(RegisterResult);
    }

    public Task<CloudUnlockState?> TryResumeAsync(CancellationToken ct = default)
    {
        if (ResumeResult is { } r)
        {
            IsSignedIn = true;
            Apply(r);
        }

        return Task.FromResult(ResumeResult);
    }

    public Task<CloudUnlockState> UnlockWithPasswordAsync(string password, CancellationToken ct = default)
    {
        Apply(UnlockResult);
        return Task.FromResult(UnlockResult);
    }

    public Task<string> BootstrapVaultAsync(bool requireDeviceApproval = false, CancellationToken ct = default)
    {
        BootstrapRequireApproval = requireDeviceApproval;
        RequireApproval = requireDeviceApproval;
        Apply(CloudUnlockState.Ready);
        return Task.FromResult(RecoveryKeyToReturn);
    }

    public Task<CloudUnlockState> RetryUnlockAsync(CancellationToken ct = default)
    {
        Apply(RetryResult);
        return Task.FromResult(RetryResult);
    }

    public Task<bool> GetRequireApprovalAsync(CancellationToken ct = default) => Task.FromResult(RequireApproval);

    public Task SetRequireApprovalAsync(bool enabled, CancellationToken ct = default)
    {
        RequireApprovalSetTo = enabled;
        RequireApproval = enabled;
        return Task.CompletedTask;
    }

    public Task RecoverVaultAsync(string recoveryKey, CancellationToken ct = default)
    {
        Apply(CloudUnlockState.Ready);
        return Task.CompletedTask;
    }

    public Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default)
    {
        ChangedPassword = (currentPassword, newPassword);
        return Task.CompletedTask;
    }

    public Task<string> ResetRecoveryKeyAsync(CancellationToken ct = default)
    {
        ResetRecoveryKeyCalls++;
        return Task.FromResult(RecoveryKeyToReturn);
    }

    public Task<SyncRunResult> SyncNowAsync(CancellationToken ct = default)
    {
        SyncNowCalls++;
        return Task.FromResult(NextRun);
    }

    public Task<SyncStateSnapshot> GetStateAsync(CancellationToken ct = default) => Task.FromResult(StateSnapshot);

    public Task<int> GetPendingOutboxCountAsync(CancellationToken ct = default) => Task.FromResult(PendingOutbox);

    public Task<IReadOnlyList<CloudPendingDevice>> GetPendingDevicesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CloudPendingDevice>>(Pending);

    public Task ApproveDeviceAsync(Guid deviceId, CancellationToken ct = default)
    {
        ApprovedDeviceId = deviceId;
        Pending.RemoveAll(p => p.DeviceId == deviceId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CloudConflictInfo>> GetConflictsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CloudConflictInfo>>(
            [.. ConflictList.Select(c => new CloudConflictInfo(
                c.Id, c.EntityType, c.EntityId, $"{c.EntityType} 标签", "连接", c.DetectedAt))]);

    public IReadOnlyList<CloudSyncedCount> SyncedCounts { get; set; } = [];

    public Task<IReadOnlyList<CloudSyncedCount>> GetSyncedCountsAsync(CancellationToken ct = default) =>
        Task.FromResult(SyncedCounts);

    public Task ResolveConflictAsync(Guid conflictId, ConflictResolution resolution, CancellationToken ct = default)
    {
        ResolvedConflict = (conflictId, resolution);
        ConflictList.RemoveAll(c => c.Id == conflictId);
        return Task.CompletedTask;
    }

    public Task SignOutAsync(bool wipeLocalCloudData, CancellationToken ct = default)
    {
        SignedOutWipe = wipeLocalCloudData;
        IsSignedIn = false;
        IsVaultUnlocked = false;
        return Task.CompletedTask;
    }

    public Task RestoreFromCloudAsync(CancellationToken ct = default)
    {
        RestoredFromCloud = true;
        return Task.CompletedTask;
    }

    private void Apply(CloudUnlockState state) => IsVaultUnlocked = state == CloudUnlockState.Ready;
}
