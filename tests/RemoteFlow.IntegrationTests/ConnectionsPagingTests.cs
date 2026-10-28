using System.Reflection;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>连接工作台分页：Items 只放当前页，筛选 / 翻页 / 改每页条数各司其职。</summary>
public sealed class ConnectionsPagingTests
{
    private static ConnectionsPageViewModel CreateViewModel(int count)
    {
        var viewModel = new ConnectionsPageViewModel(
            null!, null!, null!, null!, null!, null!, null!, null!, null!,
            SynchronousUiDispatcher.Instance, null!);

        var all = (List<ConnectionItemViewModel>)typeof(ConnectionsPageViewModel)
            .GetField("_allItems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewModel)!;
        for (var i = 1; i <= count; i++)
        {
            all.Add(new ConnectionItemViewModel(
                new ConnectionProfile { Name = $"host-{i:000}", Host = $"h{i}.example.com", Protocol = ProtocolType.Ssh }));
        }

        Refilter(viewModel);
        return viewModel;
    }

    private static void Refilter(ConnectionsPageViewModel viewModel)
        => typeof(ConnectionsPageViewModel)
            .GetMethod("ApplyFilter", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewModel, null);

    [Fact]
    public void 默认不分页_保持所有端的原有行为()
    {
        var viewModel = CreateViewModel(45);

        Assert.Equal(45, viewModel.Items.Count);
        Assert.False(viewModel.IsPagerVisible);
        Assert.Equal(45, viewModel.TotalCount);
    }

    [Fact]
    public void 固定每页条数_Items只含当前页且总数不变()
    {
        var viewModel = CreateViewModel(45);

        viewModel.PageSize = 20;

        Assert.Equal(20, viewModel.Items.Count);
        Assert.Equal(3, viewModel.TotalPages);
        Assert.Equal(45, viewModel.TotalCount);
        Assert.True(viewModel.IsPagerVisible);
        Assert.Equal("host-001", viewModel.Items[0].Name);

        viewModel.NextPageCommand.Execute(null);
        Assert.Equal(2, viewModel.CurrentPage);
        Assert.Equal("host-021", viewModel.Items[0].Name);

        viewModel.GoToPageCommand.Execute(3);
        Assert.Equal(5, viewModel.Items.Count);
        Assert.False(viewModel.CanGoNext);
        Assert.True(viewModel.CanGoPrevious);
    }

    [Fact]
    public void 翻页越界会被限制在有效范围内()
    {
        var viewModel = CreateViewModel(45);
        viewModel.PageSize = 20;

        viewModel.CurrentPage = 99;
        Assert.Equal(3, viewModel.CurrentPage);

        viewModel.CurrentPage = -5;
        Assert.Equal(1, viewModel.CurrentPage);
    }

    [Fact]
    public void 改每页条数回到第一页_选全部则不分页()
    {
        var viewModel = CreateViewModel(45);
        viewModel.PageSize = 10;
        viewModel.CurrentPage = 3;

        viewModel.PageSize = 50;
        Assert.Equal(1, viewModel.CurrentPage);
        Assert.Equal(45, viewModel.Items.Count);

        viewModel.PageSize = 0;
        Assert.Equal(45, viewModel.Items.Count);
        Assert.False(viewModel.IsPagerVisible);
    }

    [Fact]
    public void 每页条数选项是10_20_50_100和全部()
    {
        Assert.Equal([10, 20, 50, 100, 0], PageSizeOption.Defaults.Select(o => o.Value));
    }

    [Fact]
    public void 改搜索条件回到第一页_数据刷新则停留在当前页()
    {
        var viewModel = CreateViewModel(45);
        viewModel.PageSize = 10;
        viewModel.CurrentPage = 3;

        // 数据刷新（收藏、编辑、探测结果）重跑筛选：不能把用户踢回第一页。
        Refilter(viewModel);
        Assert.Equal(3, viewModel.CurrentPage);

        viewModel.PageFilterText = "host-04";
        Assert.Equal(1, viewModel.CurrentPage);
        Assert.Equal(6, viewModel.TotalCount); // host-040 ~ host-045
        Assert.Equal(6, viewModel.Items.Count);
    }

    [Fact]
    public void 页码按钮多页时用省略号折叠()
    {
        var viewModel = CreateViewModel(300);
        viewModel.PageSize = 10; // 30 页

        viewModel.CurrentPage = 15;
        Assert.Equal(["1", "…", "14", "15", "16", "…", "30"], viewModel.PageButtons.Select(b => b.Label));
        Assert.Single(viewModel.PageButtons, b => b.IsCurrent);

        viewModel.CurrentPage = 1;
        Assert.Equal(["1", "2", "…", "30"], viewModel.PageButtons.Select(b => b.Label));

        viewModel.PageSize = 100; // 3 页：全部列出
        Assert.Equal(["1", "2", "3"], viewModel.PageButtons.Select(b => b.Label));
    }

    [Fact]
    public void 数据删到当前页之外时回退到最后一页()
    {
        var viewModel = CreateViewModel(45);
        viewModel.PageSize = 20;
        viewModel.CurrentPage = 3;

        var all = (List<ConnectionItemViewModel>)typeof(ConnectionsPageViewModel)
            .GetField("_allItems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewModel)!;
        all.RemoveRange(40, 5);
        Refilter(viewModel);

        Assert.Equal(2, viewModel.CurrentPage);
        Assert.Equal(20, viewModel.Items.Count);
    }
}
