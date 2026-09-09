using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Infrastructure.Security;
using RemoteFlow.Infrastructure.Sync;
using Xunit;

namespace RemoteFlow.IntegrationTests.Cloud;

/// <summary>
/// 端到端 E2EE 契约验证：对着真实 AppsCloud（G3 Vault + G4 Sync）跑
/// 注册 → 登录 → bootstrap Vault → 加密推送 → 拉回解密，并覆盖第二台设备的
/// Recovery Key 与设备批准两条恢复路径。
/// <para>需要环境变量 <c>APPSCLOUD_BASE_URL</c>（如 <c>http://192.0.2.20:18081/appscloud/</c>）；未设置则跳过。</para>
/// </summary>
/// <summary>需要环境变量 <c>APPSCLOUD_BASE_URL</c> 指向一个可达的 AppsCloud（含 G3/G4）；未设置则跳过。</summary>
public sealed class RequiresAppsCloudFactAttribute : FactAttribute
{
    public RequiresAppsCloudFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPSCLOUD_BASE_URL")))
        {
            Skip = "未设置 APPSCLOUD_BASE_URL —— 跳过对真实 AppsCloud 的端到端验证。";
        }
    }
}

public sealed class CloudRoundTripTests
{
    private static readonly string BaseUrl = Environment.GetEnvironmentVariable("APPSCLOUD_BASE_URL") ?? "";
    private const string AppId = "com.appscloud.remoteflow";
    private const string Password = "E2e-Passw0rd!";

    [RequiresAppsCloudFact]
    public async Task End_to_end_e2ee_round_trip_across_bootstrap_recovery_and_approval()
    {
        var email = $"rf-e2e-{Guid.NewGuid():N}@example.com";
        var plaintext = Encoding.UTF8.GetBytes("""{"protocol":"rdp","host":"10.20.30.40","user":"CORP\\admin"}""");
        const string entityId = "conn-e2e-1";

        // ── 设备 A：注册 + 登录 + bootstrap ──────────────────────
        var deviceA = NewDevice();
        Assert.Equal(CloudRegisterOutcome.Created, await deviceA.Client.RegisterAsync(email, Password));
        await deviceA.LoginAsync(email, "pc-a");
        var userId = await deviceA.Client.GetUserIdAsync();

        Assert.Equal(VaultUnlockState.NeedsBootstrap, (await deviceA.Vault.TryUnlockAsync()).State);
        var (sessionA, recoveryKey) = await deviceA.Vault.BootstrapAsync();

        // ── 设备 A：加密并推送一条连接，自己拉回解密 ────────────
        var context = new PayloadContext(userId, AppId, "connection", entityId, SchemaVersion: 1, KeyVersion: 1);
        var payload = sessionA.Encrypt(context, plaintext);
        var push = await deviceA.Client.PushAsync(
            [SyncPushOperation.Upsert("op-1", "connection", entityId, 0, payload)]);
        Assert.Equal(SyncPushStatus.Applied, push.Results[0].Status);
        Assert.Equal(1, push.Results[0].Version);

        var pullA = await deviceA.Client.PullAsync(0, 100);
        Assert.Equal(plaintext, sessionA.Decrypt(context, ToPayload(Assert.Single(pullA.Changes))));

        // ── 设备 B：Recovery Key 路径 ───────────────────────────
        var deviceB = NewDevice();
        await deviceB.LoginAsync(email, "pc-b");
        Assert.Equal(VaultUnlockState.NeedsApproval, (await deviceB.Vault.TryUnlockAsync()).State);
        using var sessionB = await deviceB.Vault.RecoverAsync(recoveryKey.ToDisplayString());

        var pullB = await deviceB.Client.PullAsync(0, 100);
        Assert.Equal(plaintext, sessionB.Decrypt(context, ToPayload(Assert.Single(pullB.Changes))));

        // ── 设备 C：设备批准路径 ────────────────────────────────
        var deviceC = NewDevice();
        await deviceC.LoginAsync(email, "pc-c");
        Assert.Equal(VaultUnlockState.NeedsApproval, (await deviceC.Vault.TryUnlockAsync()).State);

        var pending = await deviceA.Client.GetPendingDevicesAsync();
        var pendingC = Assert.Single(pending, p => p.ClientDeviceId == "pc-c");
        await deviceA.Vault.ApprovePendingDeviceAsync(sessionA, pendingC);

        var unlockC = await deviceC.Vault.TryUnlockAsync();
        Assert.Equal(VaultUnlockState.Unlocked, unlockC.State);
        using var sessionC = unlockC.Session!;
        var pullC = await deviceC.Client.PullAsync(0, 100);
        Assert.Equal(plaintext, sessionC.Decrypt(context, ToPayload(Assert.Single(pullC.Changes))));

        sessionA.Dispose();
    }

    private static Device NewDevice()
    {
        var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var keyStore = new InMemoryVaultKeyStore();
        var client = new AppsCloudClient(
            http, new CloudEndpoint { BaseUrl = BaseUrl }, new InMemoryCloudTokenStore(),
            NullLogger<AppsCloudClient>.Instance);
        var deviceKeys = new DeviceKeyService(keyStore);
        var vault = new VaultMasterKeyService(
            client, keyStore, deviceKeys, new RecoveryKeyService(), NullLogger<VaultMasterKeyService>.Instance);
        return new Device(client, deviceKeys, vault);
    }

    private sealed record Device(AppsCloudClient Client, DeviceKeyService DeviceKeys, VaultMasterKeyService Vault)
    {
        public async Task LoginAsync(string email, string clientDeviceId)
        {
            var publicKey = Convert.ToBase64String(await DeviceKeys.EnsureDeviceKeyAsync());
            await Client.LoginAsync(email, Password,
                new CloudDeviceInfo(AppId, clientDeviceId, clientDeviceId, "windows", publicKey));
        }
    }

    private static EncryptedPayload ToPayload(SyncPulledChange change) =>
        new(change.Ciphertext!, change.Nonce!, change.KeyVersion, change.SchemaVersion);
}
