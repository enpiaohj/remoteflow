using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Infrastructure.Data;
using RemoteFlow.Infrastructure.Settings;
using RemoteFlow.Infrastructure.Sync;
using Xunit;

namespace RemoteFlow.IntegrationTests.Cloud;

/// <summary>
/// Vault 被重建（服务端账号清空 / 重新初始化）后，本地旧同步指针必须被清掉 ——
/// 否则客户端会永远显示「已同步」却什么都不推、不拉。
/// </summary>
public sealed class VaultSwitchDetectorTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly RemoteFlowDatabase _database;

    public VaultSwitchDetectorTests()
    {
        _database = new RemoteFlowDatabase(_workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        _database.Initialize();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    [Fact]
    public async Task Switching_to_a_rebuilt_vault_clears_the_stale_sync_pointers()
    {
        var store = new SqliteSyncStore(_database);
        await store.SetCursorAsync("com.appscloud.remoteflow", 30);
        await store.SetServerVersionAsync("connection", "c-1", 2);
        await store.EnqueueAsync("connection", "c-1", OutboxOperationType.Upsert, 2);

        var settingsStore = new JsonSettingsStore(
            Path.Combine(_workspace.Root, "settings.json"), NullLogger<JsonSettingsStore>.Instance);
        var settings = settingsStore.Load();
        settings.CloudVaultId = "old-vault";
        var detector = new VaultSwitchDetector(store, settings, settingsStore, NullLogger<VaultSwitchDetector>.Instance);

        var reset = await detector.EnsureSameVaultAsync("new-vault");

        Assert.True(reset);
        var state = await store.GetStateAsync("com.appscloud.remoteflow");
        Assert.Equal(0, state.Cursor);
        Assert.Equal(0, await store.GetServerVersionAsync("connection", "c-1"));
        Assert.Equal(0, await store.PendingCountAsync());
        Assert.Equal("new-vault", settingsStore.Load().CloudVaultId);
    }

    [Fact]
    public async Task Same_vault_does_not_reset_and_the_first_ever_sync_is_not_a_switch()
    {
        var store = new SqliteSyncStore(_database);
        var settingsStore = new JsonSettingsStore(
            Path.Combine(_workspace.Root, "settings.json"), NullLogger<JsonSettingsStore>.Instance);
        var settings = settingsStore.Load();
        var detector = new VaultSwitchDetector(store, settings, settingsStore, NullLogger<VaultSwitchDetector>.Instance);

        // 第一次同步（还没有记录过 VaultId）——记录但不算「换 Vault」。
        Assert.False(await detector.EnsureSameVaultAsync("vault-a"));
        Assert.Equal("vault-a", settingsStore.Load().CloudVaultId);

        // 同一个 Vault 再解锁——不重置。
        await store.SetCursorAsync("com.appscloud.remoteflow", 7);
        Assert.False(await detector.EnsureSameVaultAsync("vault-a"));
        Assert.Equal(7, (await store.GetStateAsync("com.appscloud.remoteflow")).Cursor);
    }
}
