using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>连接编辑器里的 RDP「颜色深度」选项：默认、回填、写回。</summary>
public class ConnectionEditorColorDepthTests
{
    [Fact]
    public void 新建连接默认32位()
    {
        var vm = NewEditor(existing: null);

        Assert.Equal(32, vm.SelectedColorDepth!.Value);
        Assert.Equal(new[] { 32, 24, 16, 15 }, vm.ColorDepthOptions.Select(o => o.Value));
    }

    [Fact]
    public void 已保存的颜色深度回填到下拉()
    {
        var vm = NewEditor(new ConnectionProfile { Name = "srv", Host = "h", Protocol = ProtocolType.Rdp, Rdp = { ColorDepth = 16 } });

        Assert.Equal(16, vm.SelectedColorDepth!.Value);
    }

    [Fact]
    public void 选择后写入保存结果且不改动原连接()
    {
        var existing = new ConnectionProfile { Name = "srv", Host = "h", Port = 3389, Protocol = ProtocolType.Rdp };
        var vm = NewEditor(existing);

        vm.SelectedColorDepth = vm.ColorDepthOptions.Single(o => o.Value == 24);
        var saved = vm.Build();

        Assert.NotNull(saved);
        Assert.Equal(24, saved.Rdp.ColorDepth);
        Assert.Equal(32, existing.Rdp.ColorDepth);
    }

    [Fact]
    public void 未知取值回落到32位选项而不抛异常()
    {
        var vm = NewEditor(new ConnectionProfile { Name = "srv", Host = "h", Protocol = ProtocolType.Rdp, Rdp = { ColorDepth = 8 } });

        Assert.Equal(32, vm.SelectedColorDepth!.Value);
    }

    private static ConnectionEditorViewModel NewEditor(ConnectionProfile? existing)
        => new(existing, credentials: [], groups: [], tags: [], defaults: new AppSettings(), defaultGroupId: null, preselectedProtocol: ProtocolType.Rdp);
}
