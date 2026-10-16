using RemoteFlow.App.Services;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>应用内更新后新版本落在哪个文件：文件名是发布产物命名时跟随新版本，否则保持原名。</summary>
public sealed class UpdateInstallerPathTests
{
    private const string Package = @"C:\Temp\RemoteFlow-update\RemoteFlow-v0.25.2-win-x64.exe.download";

    [Theory]
    [InlineData(@"D:\Apps\RemoteFlow-v0.25.1-win-x64.exe", @"D:\Apps\RemoteFlow-v0.25.2-win-x64.exe")]
    [InlineData(@"D:\Apps\remoteflow-V0.23.0-WIN-X64.EXE", @"D:\Apps\RemoteFlow-v0.25.2-win-x64.exe")]
    [InlineData(@"D:\Apps\RemoteFlow.exe", @"D:\Apps\RemoteFlow.exe")]
    [InlineData(@"D:\Apps\RemoteFlow-v0.25.1-win-x64 (2).exe", @"D:\Apps\RemoteFlow-v0.25.1-win-x64 (2).exe")]
    [InlineData(@"D:\Apps\我的远程.exe", @"D:\Apps\我的远程.exe")]
    public void 发布产物命名的文件跟随新版本_自定义名字保持不变(string current, string expected)
    {
        Assert.Equal(expected, WpfUpdateInstaller.ResolveNewPath(current, Package));
    }

    [Fact]
    public void 更新包名字不是发布产物命名时保持原路径()
    {
        const string current = @"D:\Apps\RemoteFlow-v0.25.1-win-x64.exe";

        Assert.Equal(current, WpfUpdateInstaller.ResolveNewPath(current, @"C:\Temp\whatever.bin"));
    }

    [Theory]
    [InlineData(@"""D:\Apps\RemoteFlow-v0.25.1-win-x64.exe"" --startup", true)]
    [InlineData(@"""D:\Apps\RemoteFlow-v0.25.1-win-x64.exe""", true)]
    [InlineData(@"""D:\Other\RemoteFlow-v0.25.1-win-x64.exe"" --startup", false)]
    public void 启动项只在指向旧文件时才改指向(string registered, bool expected)
    {
        Assert.Equal(expected, WpfLaunchOnStartupService.PointsTo(registered, @"D:\Apps\RemoteFlow-v0.25.1-win-x64.exe"));
    }
}
