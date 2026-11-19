using RemoteFlow.App.Views;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>标签拖动：换位序号规则（含拖到第一个位置）。</summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class DragInteractionTests
{
    // 布局：页面标签 40，三个会话标签 120 / 80 / 100。
    private static readonly double[] Widths = [40, 120, 80, 100];

    [Fact]
    public void 比第一个标签更宽的标签拖到最左也能换到第一个位置()
    {
        // 第三个会话（宽 100）比第一个（宽 120）窄；换成更宽的被拖者：第二个会话宽 80、第一个宽 60。
        double[] widths = [40, 60, 150, 50];
        // 被拖的是序号 2（宽 150），被限制在页面标签右侧，左边缘最小为 40。
        Assert.Equal(1, MainWindow.ComputeDropIndex(widths, firstMovable: 1, oldIndex: 2, desiredLeft: 40));
    }

    [Fact]
    public void 拖到最右端换到最后一个位置()
    {
        // 序号 1（宽 120）被限制在右边界：左边缘最大为 总宽 - 自身宽。
        var total = Widths.Sum();
        Assert.Equal(3, MainWindow.ComputeDropIndex(Widths, 1, 1, total - Widths[1]));
    }

    [Fact]
    public void 没越过邻居中点时不换位()
    {
        // 序号 2（宽 80）原位左边缘 160；邻居中点分别为 100 与 290。
        Assert.Equal(2, MainWindow.ComputeDropIndex(Widths, 1, 2, 160));
        Assert.Equal(2, MainWindow.ComputeDropIndex(Widths, 1, 2, 150));
    }
    [Fact]
    public void 页面标签不参与排序()
    {
        // 即便左边缘被拖到 0（页面标签位置），也只会落到第一个可移动位置。
        Assert.Equal(1, MainWindow.ComputeDropIndex(Widths, firstMovable: 1, oldIndex: 3, desiredLeft: 0));
    }

    [Fact]
    public void 换位后不会立刻反向换回()
    {
        // 序号 2（宽 80）左边缘向左越过序号 1 的中点（40 + 60 = 100）→ 换到 1。
        Assert.Equal(1, MainWindow.ComputeDropIndex(Widths, 1, 2, 99));
        // 换位后新布局：页面 40，被拖 80（序号 1），原第一个 120（序号 2），最后 100。
        double[] after = [40, 80, 120, 100];
        // 被拖标签左边缘仍是 99：不应再被判定要往右换回。
        Assert.Equal(1, MainWindow.ComputeDropIndex(after, 1, 1, 99));
    }
}
