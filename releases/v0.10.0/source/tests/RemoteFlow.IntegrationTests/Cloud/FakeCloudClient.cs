using RemoteFlow.Core.Cloud;

namespace RemoteFlow.IntegrationTests.Cloud;

/// <summary>只实现 Sync 相关方法的 <see cref="ICloudClient"/>，其余抛未实现。可注入瞬时故障。</summary>
public sealed class FakeCloudClient(InMemoryCloudServer server) : ICloudClient
{
    /// <summary>大于 0 时，接下来的 N 次 Push 抛 <see cref="HttpRequestException"/>。</summary>
    public int FailNextPushes { get; set; }

    public int PushCalls { get; private set; }
    public int PullCalls { get; private set; }

    public Task<SyncPushResponse> PushAsync(
        IReadOnlyList<SyncPushOperation> operations, CancellationToken ct = default)
    {
        PushCalls++;
        if (FailNextPushes > 0)
        {
            FailNextPushes--;
            throw new HttpRequestException("simulated network failure");
        }

        return Task.FromResult(server.Push(operations));
    }

    public Task<SyncPullPage> PullAsync(long cursor, int limit, CancellationToken ct = default)
    {
        PullCalls++;
        return Task.FromResult(server.Pull(cursor, limit));
    }

    // ── 其余端点：Sync 单元测试不涉及 ──────────────────────────

    public Task<bool> HasSessionAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<Guid> GetUserIdAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Guid> GetCurrentDeviceIdAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<CloudRegisterOutcome> RegisterAsync(string e, string p, CancellationToken ct = default) => throw new NotImplementedException();
    public Task LoginAsync(string e, string p, CloudDeviceInfo d, CancellationToken ct = default) => throw new NotImplementedException();
    public Task LogoutAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<CloudVaultStatus> GetVaultStatusAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> BootstrapVaultAsync(DeviceKeyEnvelope d, RecoveryKeyEnvelope r, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<DeviceKeyEnvelope?> GetDeviceEnvelopeAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<CloudPendingDevice>> GetPendingDevicesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task AddDeviceEnvelopeAsync(Guid t, DeviceKeyEnvelope e, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<RecoveryKeyEnvelope?> GetRecoveryEnvelopeAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task PutRecoveryEnvelopeAsync(RecoveryKeyEnvelope e, CancellationToken ct = default) => throw new NotImplementedException();
}
