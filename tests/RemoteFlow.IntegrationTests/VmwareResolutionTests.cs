using RemoteFlow.Core.Vmware;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>分辨率按虚拟机显存收敛：超过上限客户机会忽略请求，控制台变大而画面不动、留出黑边。</summary>
public sealed class VmwareResolutionTests
{
    [Fact]
    public void 默认8MB显存下最大化窗口的请求被收敛到可用范围()
    {
        // 实机：窗口最大化请求 2552x1318，8MB 显存撑不住，客户机整个忽略，留下大片黑边。
        var (w, h) = VmwareResolution.Fit(2552, 1318, 8192);

        Assert.True((long)w * h * 4 <= 8192L * 1024, $"{w}x{h} 超出显存");
        Assert.Equal(0, w % 8);
        Assert.Equal(0, h % 2);
        // 保持宽高比（允许取整误差）：不能因为收敛把画面拉变形。
        Assert.InRange((double)w / h, 2552.0 / 1318 - 0.05, 2552.0 / 1318 + 0.05);
        // 收敛后应尽量大，不能缩得过头。
        Assert.True((long)w * h * 4 > 8192L * 1024 * 85 / 100);
    }

    [Theory]
    [InlineData(1552, 866)]
    [InlineData(1920, 1080)]
    [InlineData(1024, 768)]
    public void 显存够用时保持期望分辨率不变(int width, int height)
    {
        var (w, h) = VmwareResolution.Fit(width, height, 8192);

        Assert.Equal((width / 8 * 8, height / 2 * 2), (w, h));
    }

    [Fact]
    public void 显存更大时允许更大的分辨率()
    {
        var (w, h) = VmwareResolution.Fit(2552, 1318, 16384);

        Assert.Equal((2552 / 8 * 8, 1318 / 2 * 2), (w, h));
    }

    [Fact]
    public void 读不到显存时按默认8MB保守估计()
    {
        Assert.Equal(VmwareResolution.Fit(3840, 2160, 8192), VmwareResolution.Fit(3840, 2160, null));
        Assert.Equal(VmwareResolution.Fit(3840, 2160, 8192), VmwareResolution.Fit(3840, 2160, 0));
    }

    [Theory]
    [InlineData(10000, 100)]
    [InlineData(100, 10000)]
    [InlineData(7680, 4320)]
    public void 极端宽高比也不会超出显存或低于下限(int width, int height)
    {
        var (w, h) = VmwareResolution.Fit(width, height, 8192);

        Assert.True(w >= VmwareResolution.MinWidth);
        Assert.True(h >= VmwareResolution.MinHeight);
        // 极端比例钳到下限时可能略超上限，但绝不能比期望更大。
        Assert.True(w <= Math.Max(width, VmwareResolution.MinWidth));
        Assert.True(h <= Math.Max(height, VmwareResolution.MinHeight));
    }

    [Fact]
    public void 明显被压缩时才判定为受显存限制()
    {
        Assert.True(VmwareResolution.IsConstrained(2552, 1318, 2016, 1040));
        Assert.False(VmwareResolution.IsConstrained(1552, 866, 1552, 866));
        Assert.False(VmwareResolution.IsConstrained(1920, 1080, 1912, 1080)); // 仅取整，不算受限
    }
}

/// <summary>分辨率档位：循环顺序、预设尺寸与工具条行为。</summary>
public sealed class VmwareResolutionModeTests
{
    [Fact]
    public void 档位按固定顺序循环并回到自适应()
    {
        var mode = VmwareResolutionMode.Auto;
        var seen = new List<VmwareResolutionMode>();
        for (var i = 0; i < 5; i++)
        {
            mode = VmwareResolution.Next(mode);
            seen.Add(mode);
        }

        Assert.Equal(
            [VmwareResolutionMode.Hd720, VmwareResolutionMode.Xga, VmwareResolutionMode.Svga,
             VmwareResolutionMode.Keep, VmwareResolutionMode.Auto], seen);
    }

    [Theory]
    [InlineData(VmwareResolutionMode.Hd720, 1280, 720)]
    [InlineData(VmwareResolutionMode.Xga, 1024, 768)]
    [InlineData(VmwareResolutionMode.Svga, 800, 600)]
    public void 固定档有明确尺寸(VmwareResolutionMode mode, int width, int height)
        => Assert.Equal((width, height), VmwareResolution.PresetSize(mode));

    [Theory]
    [InlineData(VmwareResolutionMode.Auto)]
    [InlineData(VmwareResolutionMode.Keep)]
    public void 自适应与保持不变没有固定尺寸(VmwareResolutionMode mode)
        => Assert.Null(VmwareResolution.PresetSize(mode));

    [Fact]
    public void 所有固定档都在默认显存范围内_无需被收敛()
    {
        foreach (var mode in new[] { VmwareResolutionMode.Hd720, VmwareResolutionMode.Xga, VmwareResolutionMode.Svga })
        {
            var (w, h) = VmwareResolution.PresetSize(mode)!.Value;
            Assert.Equal((w, h), VmwareResolution.Fit(w, h, VmwareResolution.DefaultVideoRamKb));
        }
    }

    [Fact]
    public void 每个档位都有提示文案且互不相同()
    {
        var labels = Enum.GetValues<VmwareResolutionMode>().Select(VmwareResolution.Label).ToArray();

        Assert.All(labels, l => Assert.False(string.IsNullOrWhiteSpace(l)));
        Assert.Equal(labels.Length, labels.Distinct().Count());
    }

    [Fact]
    public void 会话标签切换档位会更新提示并通知视图()
    {
        var fake = new FakeSession();
        fake.Profile.Vmware.Enabled = true;
        using var vm = new RemoteFlow.Presentation.ViewModels.SessionTabViewModel(
            fake, _ => Task.CompletedTask, _ => Task.CompletedTask,
            RemoteFlow.Presentation.Host.SynchronousUiDispatcher.Instance, new ManualTimerFactory());
        var actions = new List<RemoteFlow.Presentation.ViewModels.SessionAction>();
        vm.ActionRequested += (_, a) => actions.Add(a);

        Assert.True(vm.IsVmwareConsole);
        Assert.Contains("自适应", vm.VmwareResolutionLabel);

        vm.CycleVmwareResolutionCommand.Execute(null);

        Assert.Equal(VmwareResolutionMode.Hd720, vm.VmwareResolution);
        Assert.Contains("1280×720", vm.VmwareResolutionLabel);
        Assert.Equal([RemoteFlow.Presentation.ViewModels.SessionAction.CycleVmwareResolution], actions);
    }

    [Fact]
    public void 普通连接不显示分辨率按钮()
    {
        using var vm = new RemoteFlow.Presentation.ViewModels.SessionTabViewModel(
            new FakeSession(), _ => Task.CompletedTask, _ => Task.CompletedTask,
            RemoteFlow.Presentation.Host.SynchronousUiDispatcher.Instance, new ManualTimerFactory());

        Assert.False(vm.IsVmwareConsole);
    }
}
