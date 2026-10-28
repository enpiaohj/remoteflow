using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure;
using RemoteFlow.Infrastructure.Data;
using RemoteFlow.Infrastructure.Settings;
using RemoteFlow.Infrastructure.Security;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 一次性演示数据生成器（临时探针，不入库）：只有设置了环境变量 REMOTEFLOW_DEMO_DIR 才会运行，
/// 在该目录里生成一份全是演示数据的数据目录，供 README 截图使用（配合 REMOTEFLOW_DATA_DIR 启动应用）。
/// 主机名一律用 example.com，不含任何真实内网信息。
/// </summary>
public sealed class ZzDemoSeed
{
    [Fact]
    public async Task 生成演示数据()
    {
        var dir = Environment.GetEnvironmentVariable("REMOTEFLOW_DEMO_DIR");
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        var theme = Environment.GetEnvironmentVariable("REMOTEFLOW_DEMO_THEME") ?? "Light";

        var paths = new AppPaths(dir);
        var database = new RemoteFlowDatabase(paths.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        database.Initialize();

        var connectionRepo = new SqliteConnectionRepository(database);
        var groupRepo = new SqliteGroupRepository(database);
        var tagRepo = new SqliteTagRepository(database);
        var credentialRepo = new SqliteCredentialRepository(database);
        var historyRepo = new SqliteHistoryRepository(database);
        var vault = new DpapiCredentialVault(paths.VaultPath, NullLogger<DpapiCredentialVault>.Instance);

        var connections = new ConnectionService(connectionRepo, groupRepo, tagRepo);
        var groups = new GroupService(groupRepo, connectionRepo);
        var credentials = new CredentialService(credentialRepo, vault, NullLogger<CredentialService>.Instance);

        if ((await connections.GetAllAsync()).Count > 0)
        {
            // 已经生成过：只刷新设置（主题），不重复造数据。
            await SaveSettingsAsync(paths, theme);
            return;
        }

        // ── 分组 ─────────────────────────────────────────────────
        var prod = await groups.CreateAsync("生产环境", null);
        var web = await groups.CreateAsync("Web 集群", prod.Id);
        var db = await groups.CreateAsync("数据库", prod.Id);
        var test = await groups.CreateAsync("测试环境", null);
        var office = await groups.CreateAsync("办公网络", null);

        // ── 标签 ─────────────────────────────────────────────────
        var tagProd = await connections.CreateTagAsync("生产", "#E5484D", "");
        var tagDev = await connections.CreateTagAsync("开发", "#2E9E5B", "");
        var tagTest = await connections.CreateTagAsync("测试", "#E58A00", "");

        // ── 凭据（Secret 全是占位串，只存进本演示目录的保险库）──────
        var ops = await credentials.CreateAsync(
            new Credential { Name = "运维账号", Type = CredentialType.SshPassword, Username = "ops" }, "demo-only", null);
        var deploy = await credentials.CreateAsync(
            new Credential { Name = "部署账号", Type = CredentialType.SshPassword, Username = "deploy" }, "demo-only", null);
        var domainAdmin = await credentials.CreateAsync(
            new Credential { Name = "域管理员", Type = CredentialType.WindowsDomain, Domain = "EXAMPLE", Username = "admin" }, "demo-only", null);
        var localAdmin = await credentials.CreateAsync(
            new Credential { Name = "本地管理员", Type = CredentialType.LocalPassword, Username = "Administrator" }, "demo-only", null);
        var vnc = await credentials.CreateAsync(
            new Credential { Name = "设计部 VNC", Type = CredentialType.VncPassword }, "demo-only", null);

        // ── 连接（13 台）──────────────────────────────────────────
        var now = DateTimeOffset.Now;
        var specs = new (string Name, string Host, ProtocolType Protocol, DeviceType Device, Guid Group, Guid Tag, Guid Cred, bool Fav, TimeSpan? Ago)[]
        {
            ("核心交换机", "core-sw.example.com", ProtocolType.Ssh, DeviceType.NetworkDevice, prod.Id, tagProd.Id, ops.Id, true, TimeSpan.FromHours(3)),
            ("前台 PC", "frontdesk.example.com", ProtocolType.Rdp, DeviceType.WindowsPc, office.Id, tagDev.Id, localAdmin.Id, false, null),
            ("设计部 iMac", "imac-design.example.com", ProtocolType.Vnc, DeviceType.Mac, office.Id, tagDev.Id, vnc.Id, false, TimeSpan.FromDays(3)),
            ("build-agent", "build.example.com", ProtocolType.Ssh, DeviceType.Container, test.Id, tagDev.Id, deploy.Id, false, TimeSpan.FromDays(1)),
            ("db-primary", "db-primary.example.com", ProtocolType.Ssh, DeviceType.Database, db.Id, tagProd.Id, ops.Id, true, TimeSpan.FromHours(1)),
            ("db-replica", "db-replica.example.com", ProtocolType.Ssh, DeviceType.Database, db.Id, tagProd.Id, ops.Id, false, null),
            ("dc-01", "dc-01.example.com", ProtocolType.Rdp, DeviceType.DomainController, prod.Id, tagProd.Id, domainAdmin.Id, true, TimeSpan.FromDays(2)),
            ("files-01", "files-01.example.com", ProtocolType.Rdp, DeviceType.FileServer, prod.Id, tagProd.Id, domainAdmin.Id, false, TimeSpan.FromDays(4)),
            ("test-app", "test-app.example.com", ProtocolType.Ssh, DeviceType.Linux, test.Id, tagTest.Id, deploy.Id, false, TimeSpan.FromHours(20)),
            ("test-win", "test-win.example.com", ProtocolType.Rdp, DeviceType.WindowsServer, test.Id, tagTest.Id, localAdmin.Id, false, null),
            ("web-01", "web-01.example.com", ProtocolType.Ssh, DeviceType.WebHost, web.Id, tagProd.Id, deploy.Id, true, TimeSpan.FromMinutes(40)),
            ("web-02", "web-02.example.com", ProtocolType.Ssh, DeviceType.WebHost, web.Id, tagProd.Id, deploy.Id, false, TimeSpan.FromHours(6)),
            ("web-03", "web-03.example.com", ProtocolType.Ssh, DeviceType.WebHost, web.Id, tagProd.Id, deploy.Id, false, null),
        };

        var created = new List<ConnectionProfile>();
        foreach (var s in specs)
        {
            var profile = new ConnectionProfile
            {
                Name = s.Name,
                Host = s.Host,
                Port = ConnectionProfile.GetDefaultPort(s.Protocol),
                Protocol = s.Protocol,
                DeviceType = s.Device,
                GroupId = s.Group,
                CredentialId = s.Cred,
                TagIds = [s.Tag],
                Favorite = s.Fav,
                Notes = "演示数据",
                LastConnectedAt = s.Ago is { } ago ? now - ago : null,
            };
            created.Add(await connections.CreateAsync(profile));
        }

        // ── 连接历史：有成功有失败，详情栏的柱状图与摘要才有内容 ──────
        var rng = new Random(20261028);
        foreach (var c in created.Where(c => c.LastConnectedAt is not null))
        {
            var count = rng.Next(5, 11);
            for (var i = 0; i < count; i++)
            {
                var started = c.LastConnectedAt!.Value - TimeSpan.FromHours(i * rng.Next(5, 30));
                var failed = rng.Next(0, 7) == 0;
                var entry = new ConnectionHistoryEntry
                {
                    ConnectionId = c.Id,
                    ConnectionName = c.Name,
                    Host = c.Host,
                    Protocol = c.Protocol,
                    StartedAt = started,
                    EndedAt = started + TimeSpan.FromMinutes(failed ? rng.Next(1, 4) : rng.Next(4, 240)),
                    Result = failed ? ConnectionResult.Failed : ConnectionResult.Success,
                    ErrorCode = failed ? ConnectionErrorCode.RemoteClosed : ConnectionErrorCode.None,
                };
                await historyRepo.AddAsync(entry);
            }
        }

        await SaveSettingsAsync(paths, theme);
        SqliteConnection.ClearAllPools();
    }

    private static async Task SaveSettingsAsync(AppPaths paths, string theme)
    {
        var store = new JsonSettingsStore(paths.SettingsPath, NullLogger<JsonSettingsStore>.Instance);
        var settings = store.Load();
        settings.Theme = Enum.Parse<AppTheme>(theme);
        // 演示主机名都是 example.com，探测只会得到一排「离线」，截图里关掉探测更干净。
        settings.PresenceProbeEnabled = false;
        settings.HomeSecurityTipDismissed = true;
        await store.SaveAsync(settings);
    }
}
