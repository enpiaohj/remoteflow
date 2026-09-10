using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Core.Diagnostics;
using RemoteFlow.Infrastructure.Diagnostics;
using RemoteFlow.Infrastructure.Sync;
using Xunit;

namespace RemoteFlow.IntegrationTests.Cloud;

/// <summary>
/// 系统信息上报端到端：登录真实 AppsCloud 后 PUT /api/v1/devices/system-info。
/// 需要 <c>APPSCLOUD_BASE_URL</c>。
/// </summary>
public sealed class SystemInfoUploadTests
{
    [RequiresAppsCloudFact]
    public async Task Uploads_basic_system_info_for_the_current_device()
    {
        var baseUrl = Environment.GetEnvironmentVariable("APPSCLOUD_BASE_URL")!;
        var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var client = new AppsCloudClient(
            http, new CloudEndpoint { BaseUrl = baseUrl }, new InMemoryCloudTokenStore(),
            NullLogger<AppsCloudClient>.Instance);

        await client.LoginAsync(
            ViewerParityFixtureTests.Email, ViewerParityFixtureTests.Password,
            new CloudDeviceInfo("com.appscloud.remoteflow", "parity-pc", "parity-pc", "windows"));

        var collected = new SystemInfoCollector().Collect();
        Assert.False(string.IsNullOrWhiteSpace(collected.HostName));
        Assert.False(string.IsNullOrWhiteSpace(collected.OsName));

        await client.PutSystemInfoAsync(collected);

        // 采集器本身在 Windows 上应能取到 CPU / 内存 / 磁盘。
        if (OperatingSystem.IsWindows())
        {
            Assert.False(string.IsNullOrWhiteSpace(collected.CpuModel));
            Assert.True(collected.MemoryTotalBytes > 0);
            Assert.NotEmpty(collected.Disks);
        }
    }
}
