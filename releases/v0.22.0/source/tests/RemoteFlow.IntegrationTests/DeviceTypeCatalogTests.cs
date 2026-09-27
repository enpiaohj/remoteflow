using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

public sealed class DeviceTypeCatalogTests
{
    [Fact]
    public void 每种设备类型都有唯一的矢量图标资源键()
    {
        var explicitTypes = Enum.GetValues<DeviceType>()
            .Where(type => type != DeviceType.Unknown)
            .ToArray();

        Assert.Equal(12, explicitTypes.Length);
        Assert.Equal(explicitTypes.Length, DeviceTypeCatalog.All.Count);
        Assert.Equal(explicitTypes.Order(), DeviceTypeCatalog.All.Select(item => item.Type).Order());

        var keys = DeviceTypeCatalog.All.Select(item => item.IconResourceKey).ToArray();
        Assert.All(keys, key => Assert.StartsWith("DeviceIcon.", key, StringComparison.Ordinal));
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(ProtocolType.Rdp, "DeviceIcon.WindowsPc")]
    [InlineData(ProtocolType.Ssh, "DeviceIcon.Linux")]
    [InlineData(ProtocolType.Vnc, "DeviceIcon.Mac")]
    public void 未指定设备类型时按协议推断矢量图标(ProtocolType protocol, string expectedIconKey)
    {
        Assert.Equal(expectedIconKey, DeviceTypeCatalog.InferFromProtocol(protocol).IconResourceKey);

        var item = new ConnectionItemViewModel(new ConnectionProfile
        {
            Name = "自动推断",
            Host = "example.test",
            Protocol = protocol,
            DeviceType = DeviceType.Unknown
        });

        Assert.Equal(expectedIconKey, item.DeviceIconKey);
    }

    [Fact]
    public void 显式设备类型覆盖协议推断()
    {
        var item = new ConnectionItemViewModel(new ConnectionProfile
        {
            Name = "主域控",
            Host = "dc.example.test",
            Protocol = ProtocolType.Rdp,
            DeviceType = DeviceType.DomainController
        });

        Assert.Equal("DeviceIcon.DomainController", item.DeviceIconKey);
        Assert.Equal("域控", item.DeviceTypeDisplay);
    }

    [Theory]
    [InlineData(ProtocolType.Rdp, "DeviceIcon.WindowsPc")]
    [InlineData(ProtocolType.Ssh, "DeviceIcon.Linux")]
    [InlineData(ProtocolType.Vnc, "DeviceIcon.Mac")]
    public void 历史行按协议回退设备图标(ProtocolType protocol, string expectedIconKey)
    {
        var history = new HistoryItemViewModel(new ConnectionHistoryEntry
        {
            ConnectionName = "历史主机",
            Host = "history.example.test",
            Protocol = protocol,
            StartedAt = DateTimeOffset.Now,
            Result = ConnectionResult.Success
        });

        Assert.Equal(expectedIconKey, history.DeviceIconKey);
    }
}
