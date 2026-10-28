using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Protocol.Rdp;
using RemoteFlow.Protocol.Rdp.Interop;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>RDP 连接超时：默认与 mstsc 一致（120 秒），可在设置里调整，并真正写进 RDP 控件。</summary>
public sealed class RdpConnectionTimeoutTests
{
    [Fact]
    public void 设置默认值与mstsc一致()
    {
        var settings = new AppSettings();

        Assert.Equal(120, settings.RdpConnectionTimeoutSeconds);
        Assert.Equal(120, AppSettings.RdpConnectionTimeoutDefault);
    }

    [Theory]
    [InlineData(120, 120)]
    [InlineData(45, 45)]
    [InlineData(1, AppSettings.RdpConnectionTimeoutMin)]
    [InlineData(0, AppSettings.RdpConnectionTimeoutMin)]
    [InlineData(99999, AppSettings.RdpConnectionTimeoutMax)]
    public void 超时写入真实控件_越界值被纠正到合法范围(int configured, int expected)
    {
        UiTestHost.RunOnSta(() =>
        {
            var located = RdpControlLocator.Locate();
            Assert.NotNull(located);
            var clsid = located.Value.Clsid;
            var client = Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, throwOnError: true)!)!;
            var session = new RdpSession(new SessionRequest
            {
                Profile = new ConnectionProfile { Name = "timeout", Host = "rdp.invalid" }
            }, clsid.ToString(), NullLogger.Instance, configured);
            try
            {
                typeof(RdpSession).GetMethod("Configure", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(session, [client]);

                dynamic rdp = client;
                Assert.Equal(expected, (int)rdp.AdvancedSettings9.singleConnectionTimeout);
                Assert.Equal(expected, (int)rdp.AdvancedSettings9.overallConnectionTimeout);
                Assert.Equal(0, (int)rdp.Connected); // 只测本机 COM 配置，不发起网络连接。
            }
            finally
            {
                session.DisposeAsync().GetAwaiter().GetResult();
                Marshal.FinalReleaseComObject(client);
            }
        });
    }
}
