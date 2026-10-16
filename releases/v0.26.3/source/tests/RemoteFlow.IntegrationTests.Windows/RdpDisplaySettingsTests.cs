using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Protocol.Rdp;
using RemoteFlow.Protocol.Rdp.Interop;
using Xunit;

namespace RemoteFlow.IntegrationTests;

[Collection(WpfApplicationCollection.Name)]
public sealed class RdpDisplaySettingsTests
{
    [Theory]
    [InlineData(1.0, 100)]
    [InlineData(1.25, 125)]
    [InlineData(1.5, 150)]
    [InlineData(1.75, 175)]
    [InlineData(2.0, 200)]
    public void HighDpi_PreservesPhysicalPixelsAndRequestsMatchingDesktopScale(double dpiScale, uint expectedScale)
    {
        var settings = RdpDisplaySettings.Create(new RdpOptions(), 3000, 1800, dpiScale);

        Assert.Equal(3000, settings.Width);
        Assert.Equal(1800, settings.Height);
        Assert.Equal(expectedScale, settings.DesktopScaleFactor);
        Assert.Contains(settings.DeviceScaleFactor, new uint[] { 100, 140, 180 });
    }

    [Fact]
    public void PhysicalSize_IsInMillimetersRatherThanPixels()
    {
        var settings = RdpDisplaySettings.Create(new RdpOptions(), 3840, 2160, 2.0);

        Assert.Equal(508u, settings.PhysicalWidth);
        Assert.Equal(286u, settings.PhysicalHeight);
    }

    [Fact]
    public void FixedResolution_IsPreservedOnHighDpiDisplay()
    {
        var options = new RdpOptions
        {
            DisplayMode = RdpDisplayMode.FixedResolution,
            DesktopWidth = 1600,
            DesktopHeight = 900
        };
        var settings = RdpDisplaySettings.Create(options, 3840, 2160, 1.5);

        Assert.Equal(1600, settings.Width);
        Assert.Equal(900, settings.Height);
        Assert.Equal(150u, settings.DesktopScaleFactor);
    }

    [Fact]
    public void FitToWindow_DoesNotEnlargeSmallViewportToStartupMinimum()
    {
        var settings = RdpDisplaySettings.Create(new RdpOptions(), 800, 600, 1.0);

        Assert.Equal(800, settings.Width);
        Assert.Equal(600, settings.Height);
    }

    [Theory]
    [InlineData(0, 0, 1024, 768)]
    [InlineData(1, 1, 200, 200)]
    [InlineData(1365, 767, 1364, 767)]
    [InlineData(16000, 12000, 8192, 8192)]
    public void FitToWindow_UsesProtocolLimits(int width, int height, int expectedWidth, int expectedHeight)
    {
        var settings = RdpDisplaySettings.Create(new RdpOptions(), width, height, 1);

        Assert.Equal(expectedWidth, settings.Width);
        Assert.Equal(expectedHeight, settings.Height);
    }

    [Fact]
    public void InitialHandshake_SetsDpiOnRealActiveXBeforeConnecting()
    {
        UiTestHost.RunOnSta(() =>
        {
            var located = RdpControlLocator.Locate();
            Assert.NotNull(located);
            var clsid = located.Value.Clsid;
            var client = Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, throwOnError: true)!)!;
            var session = new RdpSession(new SessionRequest
            {
                Profile = new ConnectionProfile { Name = "DPI regression", Host = "rdp.invalid" }
            }, clsid.ToString(), NullLogger.Instance);
            try
            {
                // 连接开始前的最新 viewport，不能被 HostControl 的默认尺寸覆盖。
                session.UpdateDisplaySize(3000, 1800, 1.5);
                typeof(RdpSession).GetMethod("Configure", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(session, [client]);

                var extended = (IMsRdpExtendedSettings)client;
                Assert.Equal(150u, (uint)extended.get_Property("DesktopScaleFactor"));
                Assert.Equal(140u, (uint)extended.get_Property("DeviceScaleFactor"));
                dynamic rdp = client;
                Assert.Equal(3000, (int)rdp.DesktopWidth);
                Assert.Equal(1800, (int)rdp.DesktopHeight);
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
