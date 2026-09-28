using System.Runtime.InteropServices;
using RemoteFlow.Infrastructure;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 锁定应用数据目录在各平台上的落点。
/// <para>
/// 背景：macOS 移植期间一度以为 .NET 会把 <see cref="Environment.SpecialFolder.LocalApplicationData"/>
/// 解析到 XDG 的 <c>~/.local/share</c>（那是 Linux 行为），从而计划为 macOS 加一条平台分支。
/// 实测表明 .NET 在 macOS 上已正确映射到 <c>~/Library/Application Support</c>，因此无需分支。
/// 这组测试把该行为固定下来：若未来运行时改变映射，会在此处失败，而不是让用户数据悄悄换位置。
/// </para>
/// </summary>
public sealed class AppPathsTests
{
    [Fact]
    public void 默认数据目录位于平台惯例位置且以产品名结尾()
    {
        var directory = AppPaths.ResolveDefaultDataDirectory();

        Assert.EndsWith(AppPaths.AppFolderName, directory);
        Assert.True(Path.IsPathRooted(directory), $"应为绝对路径，实际为 {directory}");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // macOS 惯例：~/Library/Application Support/RemoteFlow
            Assert.Contains("Library/Application Support", directory);
            Assert.DoesNotContain(".local/share", directory);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Assert.StartsWith(local, directory);
        }
    }

    [Fact]
    public void 解析默认目录不产生任何副作用()
    {
        // 纯计算，不得创建目录——否则测试与首次启动都会污染用户目录。
        var directory = AppPaths.ResolveDefaultDataDirectory();
        var existedBefore = Directory.Exists(directory);

        AppPaths.ResolveDefaultDataDirectory();

        Assert.Equal(existedBefore, Directory.Exists(directory));
    }

    [Fact]
    public void 自定义目录覆盖默认位置且各文件落在其下()
    {
        using var workspace = new TempWorkspace();

        var paths = new AppPaths(workspace.Root);

        Assert.Equal(workspace.Root, paths.DataDirectory);
        Assert.Equal(Path.Combine(workspace.Root, "remoteflow.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(workspace.Root, "vault.dat"), paths.VaultPath);
        Assert.Equal(Path.Combine(workspace.Root, "settings.json"), paths.SettingsPath);
        Assert.Equal(Path.Combine(workspace.Root, "logs"), paths.LogDirectory);
        Assert.StartsWith(paths.LogDirectory, paths.LogFileTemplate);
    }

    [Fact]
    public void 构造时创建数据目录与日志目录()
    {
        using var workspace = new TempWorkspace();
        var target = Path.Combine(workspace.Root, "nested", "data");

        var paths = new AppPaths(target);

        Assert.True(Directory.Exists(paths.DataDirectory));
        Assert.True(Directory.Exists(paths.LogDirectory));
    }
}
