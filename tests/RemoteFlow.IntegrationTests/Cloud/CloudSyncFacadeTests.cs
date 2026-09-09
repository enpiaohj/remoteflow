using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Data;
using RemoteFlow.Infrastructure.Security;
using RemoteFlow.Infrastructure.Settings;
using RemoteFlow.Infrastructure.Sync;
using RemoteFlow.Infrastructure.Sync.Sources;
using Xunit;

namespace RemoteFlow.IntegrationTests.Cloud;

/// <summary>
/// 「完成定义」验证（安全设计 §15）：一台干净 RemoteFlow 通过 <see cref="ICloudSyncService"/>
/// 恢复到完整连接 + 凭据 + Secret，并能解析出可连接的凭据；对真实 AppsCloud（G3/G4）执行。
/// </summary>
public sealed class CloudSyncFacadeTests : IDisposable
{
    private readonly List<TempWorkspace> _workspaces = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var w in _workspaces)
        {
            w.Dispose();
        }
    }

    [RequiresAppsCloudFact]
    public async Task Sign_in_with_an_unregistered_email_creates_the_account_and_proceeds_to_bootstrap()
    {
        var baseUrl = Environment.GetEnvironmentVariable("APPSCLOUD_BASE_URL")!;
        var email = $"rf-signup-{Guid.NewGuid():N}@example.com";
        const string password = "Signup-Passw0rd!";

        var device = NewDevice(baseUrl);

        // 未在别处开户，直接 SignInAsync：应自动注册后登录，落到「需初始化 Vault」。
        Assert.Equal(CloudUnlockState.NeedsBootstrap, await device.Sync.SignInAsync(baseUrl, email, password));

        // 同一邮箱、错误密码：账号已存在，按凭据错误处理，不得静默改写。
        var bad = NewDevice(baseUrl);
        var err = await Assert.ThrowsAsync<CloudSignInException>(
            () => bad.Sync.SignInAsync(baseUrl, email, "Wrong-Passw0rd!"));
        Assert.Equal("邮箱或密码不正确。", err.Message);

        await device.Sync.SignOutAsync(wipeLocalCloudData: true);
    }

    [RequiresAppsCloudFact]
    public async Task A_fresh_device_recovers_connections_credentials_and_secrets_via_the_facade()
    {
        var baseUrl = Environment.GetEnvironmentVariable("APPSCLOUD_BASE_URL")!;
        var email = $"rf-facade-{Guid.NewGuid():N}@example.com";
        const string password = "Facade-Passw0rd!";
        const string rdpPassword = "S3cr3t-RDP!";

        // ── 设备 A：登录 + bootstrap + 造数据 + 同步 ─────────────
        var alice = NewDevice(baseUrl);
        await alice.Client.RegisterAsync(email, password);
        Assert.Equal(CloudUnlockState.NeedsBootstrap, await alice.Sync.SignInAsync(baseUrl, email, password));
        var recoveryKey = await alice.Sync.BootstrapVaultAsync();

        var credential = await alice.Credentials.CreateAsync(
            new Credential { Name = "DomainAdmin", Type = CredentialType.WindowsDomain, Username = "admin", Domain = "CORP" },
            rdpPassword, privateKey: null);
        var connection = await alice.Connections.CreateAsync(new ConnectionProfile
        {
            Name = "DC01", Host = "10.20.30.40", Port = 3389, Protocol = ProtocolType.Rdp,
            CredentialId = credential.Id,
        });

        var pushResult = await alice.Sync.SyncNowAsync();
        Assert.Equal(SyncStatus.Synced, pushResult.Status);
        Assert.True(pushResult.Pushed >= 3); // connection + credential + credential-secret

        // ── 设备 B：干净 → 登录 → Recovery Key → 同步 ───────────
        var bob = NewDevice(baseUrl);
        Assert.Equal(CloudUnlockState.NeedsApproval, await bob.Sync.SignInAsync(baseUrl, email, password));
        await bob.Sync.RecoverVaultAsync(recoveryKey);

        var pullResult = await bob.Sync.SyncNowAsync();
        Assert.Equal(SyncStatus.Synced, pullResult.Status);
        Assert.True(pullResult.Pulled >= 3);

        // 连接 + 凭据元数据 落地
        var restoredConnection = await bob.Connections.GetByIdAsync(connection.Id);
        Assert.NotNull(restoredConnection);
        Assert.Equal("DC01", restoredConnection!.Name);
        Assert.Equal(credential.Id, restoredConnection.CredentialId);

        var restoredCredential = await bob.Credentials.GetByIdAsync(credential.Id);
        Assert.NotNull(restoredCredential);
        Assert.Equal("admin", restoredCredential!.Username);

        // Secret 落地：干净设备能解析出可用于连接的明文密码
        using var resolved = await bob.Credentials.ResolveAsync(credential.Id);
        Assert.NotNull(resolved);
        Assert.Equal(rdpPassword, resolved!.Password);

        await alice.Sync.SignOutAsync(wipeLocalCloudData: false);
        await bob.Sync.SignOutAsync(wipeLocalCloudData: true);
    }

    private Device NewDevice(string baseUrl)
    {
        var workspace = new TempWorkspace();
        _workspaces.Add(workspace);

        var vault = new InMemoryCredentialVault();
        var keyStore = new CredentialVaultKeyStore(vault);
        var tokenStore = new CredentialVaultTokenStore(vault);
        var endpoint = new CloudEndpoint { BaseUrl = baseUrl };
        var gate = new CloudSyncGate();
        var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var client = new AppsCloudClient(http, endpoint, tokenStore, NullLogger<AppsCloudClient>.Instance);

        var deviceKeys = new DeviceKeyService(keyStore);
        var vaultKeys = new VaultMasterKeyService(
            client, keyStore, deviceKeys, new RecoveryKeyService(), NullLogger<VaultMasterKeyService>.Instance);

        var database = new RemoteFlowDatabase(workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        database.Initialize();
        var connRepo = new SqliteConnectionRepository(database);
        var credRepo = new SqliteCredentialRepository(database);
        var groupRepo = new SqliteGroupRepository(database);
        var tagRepo = new SqliteTagRepository(database);
        var store = new SqliteSyncStore(database);

        ISyncEntitySource[] sources =
        [
            new ConnectionSyncSource(connRepo),
            new CredentialSyncSource(credRepo),
            new CredentialSecretSyncSource(credRepo, vault),
            new GroupSyncSource(groupRepo),
            new TagSyncSource(tagRepo),
        ];
        var conflicts = new ConflictService(store, sources, NullLogger<ConflictService>.Instance);
        var coordinator = new SyncCoordinator(
            client, store, sources, conflicts, NullLogger<SyncCoordinator>.Instance);

        var settingsStore = new JsonSettingsStore(
            Path.Combine(workspace.Root, "settings.json"), NullLogger<JsonSettingsStore>.Instance);
        var settings = settingsStore.Load();

        var sync = new CloudSyncService(
            client, tokenStore, endpoint, gate, vaultKeys, deviceKeys, coordinator, conflicts,
            store, settings, settingsStore, NullLogger<CloudSyncService>.Instance);

        var tracker = new OutboxSyncChangeTracker(store, gate, NullLogger<OutboxSyncChangeTracker>.Instance);
        return new Device(
            client, sync,
            new ConnectionService(connRepo, groupRepo, tagRepo, tracker),
            new CredentialService(credRepo, vault, NullLogger<CredentialService>.Instance, tracker));
    }

    private sealed record Device(
        AppsCloudClient Client,
        CloudSyncService Sync,
        ConnectionService Connections,
        CredentialService Credentials);
}
