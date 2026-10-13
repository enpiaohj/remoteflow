using System.IO;
using RemoteFlow.App.Services;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>应用内更新改了文件名之后的善后：快捷方式改指向、旧文件清理。全部在临时目录里进行。</summary>
public sealed class UpdateFileHousekeepingTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "rf-update-housekeeping-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响结论。
        }
    }

    [Fact]
    public void 只把指向旧文件的快捷方式改指向新文件()
    {
        var oldExe = Touch("RemoteFlow-v0.25.1-win-x64.exe");
        var newExe = Touch("RemoteFlow-v0.25.2-win-x64.exe");
        var other = Touch("other.exe");
        var mine = CreateShortcut("mine.lnk", oldExe);
        var unrelated = CreateShortcut("unrelated.lnk", other);

        var updated = RunSta(() => ShortcutRetargeter.Retarget(oldExe, newExe, [mine, unrelated, Path.Combine(_dir, "missing.lnk")]));

        Assert.Equal(1, updated);
        Assert.Equal(newExe, ReadTarget(mine), ignoreCase: true);
        Assert.Equal(other, ReadTarget(unrelated), ignoreCase: true);
    }

    [Fact]
    public void 启动时按标记删除旧版本文件并清掉标记()
    {
        var oldExe = Touch("RemoteFlow-v0.25.1-win-x64.exe");
        var newExe = Touch("RemoteFlow-v0.25.2-win-x64.exe");
        File.WriteAllText(newExe + ".replaces", "RemoteFlow-v0.25.1-win-x64.exe");

        WpfUpdateInstaller.CleanupPreviousVersion(newExe);

        Assert.False(File.Exists(oldExe));
        Assert.False(File.Exists(newExe + ".replaces"));
        Assert.True(File.Exists(newExe));
    }

    [Theory]
    [InlineData(@"..\\important.exe")]
    [InlineData("notes.txt")]
    [InlineData("RemoteFlow-v0.25.2-win-x64.exe")]
    [InlineData("")]
    public void 标记内容不是同目录的旧发布产物时什么都不删(string markerContent)
    {
        var newExe = Touch("RemoteFlow-v0.25.2-win-x64.exe");
        var bystander = Touch("notes.txt");
        var outside = Path.Combine(Path.GetDirectoryName(_dir)!, "important.exe");
        File.WriteAllText(newExe + ".replaces", markerContent);

        WpfUpdateInstaller.CleanupPreviousVersion(newExe);

        Assert.True(File.Exists(newExe));
        Assert.True(File.Exists(bystander));
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public void 原地替换留下的old文件也会清理()
    {
        var exe = Touch("RemoteFlow.exe");
        var backup = Touch("RemoteFlow.exe.old");

        WpfUpdateInstaller.CleanupPreviousVersion(exe);

        Assert.False(File.Exists(backup));
        Assert.True(File.Exists(exe));
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private string CreateShortcut(string name, string target)
    {
        var path = Path.Combine(_dir, name);
        RunSta(() =>
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var shortcut = shell.CreateShortcut(path);
            shortcut.TargetPath = target;
            shortcut.Save();
            return 0;
        });
        return path;
    }

    private static string ReadTarget(string link)
        => RunSta(() =>
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            return (string)shell.CreateShortcut(link).TargetPath;
        });

    private static T RunSta<T>(Func<T> func)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw new InvalidOperationException("STA 线程执行失败", error);
        }

        return result;
    }
}
