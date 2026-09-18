using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Infrastructure.Security;
using RemoteFlow.Infrastructure.Sync;
using Xunit;

namespace RemoteFlow.IntegrationTests.Cloud;

/// <summary>
/// 为「网页端云数据自查页」准备一份可复现的对照数据：固定邮箱 + 固定主口令，
/// 初始化 Vault 并推送一条连接。用于人工/脚本核对网页(JS)解密算法与客户端是否逐字节一致。
/// 需要 <c>APPSCLOUD_BASE_URL</c>。
/// </summary>
public sealed class ViewerParityFixtureTests
{
    public const string Email = "rf-viewer-parity@example.com";
    public const string Password = "Viewer-Parity-Passw0rd!";
    public const string ConnectionName = "PARITY-CHECK";

    [RequiresAppsCloudFact]
    public async Task Seed_a_known_account_and_push_one_connection()
    {
        var baseUrl = Environment.GetEnvironmentVariable("APPSCLOUD_BASE_URL")!;
        var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var keyStore = new InMemoryVaultKeyStore();
        var client = new AppsCloudClient(
            http, new CloudEndpoint { BaseUrl = baseUrl }, new InMemoryCloudTokenStore(),
            NullLogger<AppsCloudClient>.Instance);
        var vault = new VaultMasterKeyService(
            client, keyStore, new RecoveryKeyService(), NullLogger<VaultMasterKeyService>.Instance);

        // 每次重跑先清掉旧会话，重新注册（邮箱可能已被占用，注册失败则直接用其登录）。
        if (await client.RegisterAsync(Email, Password) == CloudRegisterOutcome.AlreadyExists)
        {
            await client.LoginAsync(Email, Password,
                new CloudDeviceInfo("com.appscloud.remoteflow", "parity-pc", "parity-pc", "windows"));
        }
        else
        {
            await client.LoginAsync(Email, Password,
                new CloudDeviceInfo("com.appscloud.remoteflow", "parity-pc", "parity-pc", "windows"));
        }

        var userId = await client.GetUserIdAsync();
        IVaultSession session;
        if ((await vault.TryUnlockAsync(Password)).State == VaultUnlockState.Unlocked)
        {
            session = (await vault.TryUnlockAsync(Password)).Session!;
        }
        else
        {
            var (bootstrapped, _) = await vault.BootstrapAsync(Password, requireDeviceApproval: false);
            session = bootstrapped;
        }

        using (session)
        {
            var ctx = new PayloadContext(userId, "com.appscloud.remoteflow", "connection", "parity-conn-1", 1, 1);
            var payload = session.Encrypt(ctx, System.Text.Encoding.UTF8.GetBytes(
                """{"v":1,"d":{"Id":"11111111-1111-1111-1111-111111111111","Name":"PARITY-CHECK","Host":"10.9.9.9","Port":22,"Protocol":"Ssh","Favorite":false,"Notes":"parity","CreatedAt":"2026-09-10T00:00:00+00:00","UpdatedAt":"2026-09-10T00:00:00+00:00","TagIds":[],"Rdp":{},"Ssh":{},"Vnc":{}}}"""));

            // 首跑是新建（base 0）；重跑时实体已存在 → 用服务端当前版本作为基线覆盖。
            var push = await client.PushAsync(
                [SyncPushOperation.Upsert($"parity-{Guid.NewGuid():N}", "connection", "parity-conn-1", 0, payload)]);
            var result = push.Results[0];
            if (result.Status == SyncPushStatus.Conflict && result.Server is { } server)
            {
                push = await client.PushAsync(
                    [SyncPushOperation.Upsert($"parity-{Guid.NewGuid():N}", "connection", "parity-conn-1", server.Version, payload)]);
                result = push.Results[0];
            }

            Assert.Equal(SyncPushStatus.Applied, result.Status);
        }
    }
}
