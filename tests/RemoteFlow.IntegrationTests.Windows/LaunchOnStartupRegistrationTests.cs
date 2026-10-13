using RemoteFlow.App.Services;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>开机启动项的命令行形式：带启动参数，且只升级指向当前可执行文件的旧启动项。</summary>
public sealed class LaunchOnStartupRegistrationTests
{
    private const string Exe = @"D:\Apps\RemoteFlow\RemoteFlow.exe";

    [Fact]
    public void 启动项带引号路径与启动参数()
    {
        Assert.Equal("\"" + Exe + "\" --startup", WpfLaunchOnStartupService.BuildCommand(Exe));
    }

    [Theory]
    [InlineData(@"""D:\Apps\RemoteFlow\RemoteFlow.exe""", true)]
    [InlineData(@"""d:\apps\remoteflow\REMOTEFLOW.EXE""", true)]
    [InlineData(@"D:\Apps\RemoteFlow\RemoteFlow.exe", true)]
    [InlineData(@"""D:\Apps\RemoteFlow\RemoteFlow.exe"" --startup", false)]
    [InlineData(@"""D:\Temp\trial\RemoteFlow.exe""", false)]
    [InlineData(@"""D:\Temp\trial\RemoteFlow.exe"" --startup", false)]
    public void 只升级指向当前程序且缺少参数的旧启动项(string registered, bool expected)
    {
        Assert.Equal(expected, WpfLaunchOnStartupService.NeedsUpgrade(registered, Exe));
    }
}
