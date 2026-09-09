using RemoteFlow.Core.Cloud;

namespace RemoteFlow.IntegrationTests.Cloud;

/// <summary>脚本化的 <see cref="ICloudSyncService"/>，供 CloudSyncViewModel 单元测试。</summary>
public sealed class FakeCloudSyncService : ICloudSyncService
{
    public CloudUnlockState? ResumeResult { get; set; }
    public CloudUnlockState SignInResult { get; set; } = CloudUnlockState.Ready;
    public string RecoveryKeyToReturn { get; set; } = "AAAA BBBB CCCC";
    public SyncRunResult NextRun { get; set; } = new(0, 0, 0, 0, 0, SyncStatus.Synced);
    public SyncStateSnapshot StateSnapshot { get; set; } = new(0, null, null, SyncStatus.Synced);
    public int PendingOutbox { get; set; }
    public List<CloudPendingDevice> Pending { get; } = [];
    public List<SyncConflictRecord> ConflictList { get; } = [];

    public int SyncNowCalls { get; private set; }
    public Guid? ApprovedDeviceId { get; private set; }
    public (Guid Id, ConflictResolution Resolution)? ResolvedConflict { get; private set; }
    public bool? SignedOutWipe { get; private set; }
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

    public Task<CloudUnlockState?> TryResumeAsync(CancellationToken ct = default)
    {
        if (ResumeResult is { } r)
        {
            IsSignedIn = true;
            Apply(r);
        }

        return Task.FromResult(ResumeResult);
    }

    public Task<string> BootstrapVaultAsync(CancellationToken ct = default)
    {
        Apply(CloudUnlockState.Ready);
        return Task.FromResult(RecoveryKeyToReturn);
    }

    public Task RecoverVaultAsync(string recoveryKey, CancellationToken ct = default)
    {
        Apply(CloudUnlockState.Ready);
        return Task.CompletedTask;
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

    public Task<IReadOnlyList<SyncConflictRecord>> GetConflictsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SyncConflictRecord>>(ConflictList);

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

    private void Apply(CloudUnlockState state) => IsVaultUnlocked = state == CloudUnlockState.Ready;
}
