using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Infrastructure.Security;
using Xunit;

namespace RemoteFlow.IntegrationTests.Cloud;

public sealed class VaultMasterKeyServiceTests
{
    private const string Password = "master-passw0rd-1234";

    [Fact]
    public async Task Cached_vmk_is_reused_when_it_belongs_to_the_current_vault()
    {
        var vaultId = Guid.NewGuid();
        var vmk = VaultCryptography.NewMasterKey();
        var store = new InMemoryVaultKeyStore();
        await store.SetCachedMasterKeyAsync(vmk, vaultId.ToString());
        var client = new FakeVaultClient { Status = Status(vaultId) };
        var service = NewService(client, store);

        var result = await service.TryUnlockAsync(password: null);

        Assert.Equal(VaultUnlockState.Unlocked, result.State);
        Assert.Equal(0, client.EnvelopeFetches);   // 命中缓存，无需取信封
        Assert.Equal(0, store.ClearCalls);
    }

    [Fact]
    public async Task Stale_cached_vmk_is_discarded_when_the_vault_was_rebuilt()
    {
        var oldVaultId = Guid.NewGuid();
        var newVaultId = Guid.NewGuid();
        var store = new InMemoryVaultKeyStore();
        await store.SetCachedMasterKeyAsync(VaultCryptography.NewMasterKey(), oldVaultId.ToString());

        var currentVmk = VaultCryptography.NewMasterKey();
        var client = new FakeVaultClient
        {
            Status = Status(newVaultId),
            PasswordEnvelope = VaultCryptography.WrapWithSecret(VaultEnvelopeKinds.Password, Password, currentVmk),
        };
        var service = NewService(client, store);

        // 无口令：缓存陈旧被清除，落到「需要口令」。
        Assert.Equal(VaultUnlockState.NeedsPassword, (await service.TryUnlockAsync(null)).State);
        Assert.Equal(1, store.ClearCalls);

        // 补口令：用当前 Vault 的信封解锁，拿到的是当前 VMK。
        var unlocked = await service.TryUnlockAsync(Password);
        Assert.Equal(VaultUnlockState.Unlocked, unlocked.State);
        using var session = unlocked.Session!;
        var ctx = new PayloadContext(Guid.NewGuid(), "app", "connection", "c1", 1, 1);
        var round = session.Decrypt(ctx, session.Encrypt(ctx, "hi"u8.ToArray()));
        Assert.Equal("hi"u8.ToArray(), round);
    }

    [Fact]
    public async Task Offline_falls_back_to_the_cached_vmk()
    {
        var vaultId = Guid.NewGuid();
        var store = new InMemoryVaultKeyStore();
        await store.SetCachedMasterKeyAsync(VaultCryptography.NewMasterKey(), vaultId.ToString());
        var client = new FakeVaultClient { StatusThrows = new HttpRequestException("offline") };
        var service = NewService(client, store);

        var result = await service.TryUnlockAsync(null);

        Assert.Equal(VaultUnlockState.Unlocked, result.State);
        Assert.Equal(0, store.ClearCalls);
    }

    [Fact]
    public async Task Password_unlock_caches_the_vmk_tagged_with_the_current_vault()
    {
        var vaultId = Guid.NewGuid();
        var vmk = VaultCryptography.NewMasterKey();
        var store = new InMemoryVaultKeyStore();
        var client = new FakeVaultClient
        {
            Status = Status(vaultId),
            PasswordEnvelope = VaultCryptography.WrapWithSecret(VaultEnvelopeKinds.Password, Password, vmk),
        };
        var service = NewService(client, store);

        Assert.Equal(VaultUnlockState.Unlocked, (await service.TryUnlockAsync(Password)).State);

        var cached = await store.GetCachedMasterKeyAsync();
        Assert.NotNull(cached);
        Assert.Equal(vaultId.ToString(), cached!.VaultTag);
    }

    private static VaultMasterKeyService NewService(FakeVaultClient client, InMemoryVaultKeyStore store) =>
        new(client, store, new RecoveryKeyService(), NullLogger<VaultMasterKeyService>.Instance);

    private static CloudVaultStatus Status(Guid vaultId) =>
        new(Exists: true, CurrentKeyVersion: 1, HasPasswordEnvelope: true, HasRecoveryEnvelope: true,
            RequireDeviceApproval: false, ThisDeviceApproved: true, VaultId: vaultId);

    /// <summary>只回答 vault 相关方法的 <see cref="ICloudClient"/>；其余抛未实现。</summary>
    private sealed class FakeVaultClient : ICloudClient
    {
        public CloudVaultStatus Status { get; set; } = default!;
        public Exception? StatusThrows { get; set; }
        public VaultKeyEnvelope? PasswordEnvelope { get; set; }
        public int EnvelopeFetches { get; private set; }

        public Task<CloudVaultStatus> GetVaultStatusAsync(CancellationToken ct = default) =>
            StatusThrows is not null ? Task.FromException<CloudVaultStatus>(StatusThrows) : Task.FromResult(Status);

        public Task<VaultKeyEnvelope?> GetVaultEnvelopeAsync(string kind, CancellationToken ct = default)
        {
            EnvelopeFetches++;
            return Task.FromResult(kind == VaultEnvelopeKinds.Password ? PasswordEnvelope : null);
        }

        public Task<bool> HasSessionAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<Guid> GetUserIdAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task PutSystemInfoAsync(RemoteFlow.Core.Diagnostics.SystemInfo i, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CloudRegisterOutcome> RegisterAsync(string e, string p, CancellationToken ct = default) => throw new NotImplementedException();
        public Task LoginAsync(string e, string p, CloudDeviceInfo d, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ChangePasswordAsync(string e, string c, string n, CancellationToken ct = default) => throw new NotImplementedException();
        public Task LogoutAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> BootstrapVaultAsync(VaultKeyEnvelope p, VaultKeyEnvelope r, bool a, CancellationToken ct = default) => throw new NotImplementedException();
        public Task PutVaultEnvelopeAsync(VaultKeyEnvelope e, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetRequireApprovalAsync(bool enabled, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CloudPendingDevice>> GetPendingDevicesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task ApproveDeviceAsync(Guid targetDeviceId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SyncPushResponse> PushAsync(IReadOnlyList<SyncPushOperation> o, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SyncPullPage> PullAsync(long cursor, int limit, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
