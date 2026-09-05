using RemoteFlow.App.ViewModels;
using RemoteFlow.Core.Models;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 多选集合的纯逻辑：不碰数据库 / 不开对话框，直接构造 VM 并操作 Items 与选择集合。
/// 构造参数传 null——被测路径（Items / SelectedConnections / 各命令）不触碰它们才成立。
/// </summary>
public sealed class ConnectionSelectionViewModelTests
{
    private static ConnectionsPageViewModel CreateVm()
        => new(null!, null!, null!, null!, null!, null!, null!, null!);

    private static ConnectionItemViewModel Item(string name)
        => new(new ConnectionProfile { Name = name, Protocol = ProtocolType.Ssh });

    [Fact]
    public void ToggleSelect_AddsThenRemovesSelection()
    {
        var vm = CreateVm();
        var a = Item("A");
        var b = Item("B");
        vm.Items.Add(a);
        vm.Items.Add(b);

        vm.ToggleSelect(a);
        Assert.True(a.IsSelected);
        Assert.True(vm.HasSelection);
        Assert.Single(vm.SelectedConnections);
        Assert.Equal("已选择 1 项", vm.SelectionSummary);

        vm.ToggleSelect(a);
        Assert.False(a.IsSelected);
        Assert.False(vm.HasSelection);
        Assert.Empty(vm.SelectedConnections);
    }

    [Fact]
    public void SelectAllVisible_MarksEveryVisibleItem()
    {
        var vm = CreateVm();
        var a = Item("A");
        var b = Item("B");
        var c = Item("C");
        vm.Items.Add(a);
        vm.Items.Add(b);
        vm.Items.Add(c);

        vm.SelectAllVisibleCommand.Execute(null);

        Assert.Equal(3, vm.SelectedConnections.Count);
        Assert.All(vm.Items, i => Assert.True(i.IsSelected));
        Assert.True(vm.IsAllSelected);
    }

    [Fact]
    public void ClearSelection_ResetsFlagsAndSummary()
    {
        var vm = CreateVm();
        var a = Item("A");
        vm.Items.Add(a);
        vm.ToggleSelect(a);
        Assert.True(vm.HasSelection);

        vm.ClearSelectionCommand.Execute(null);

        Assert.Empty(vm.SelectedConnections);
        Assert.False(a.IsSelected);
        Assert.False(vm.HasSelection);
        Assert.False(vm.IsAllSelected);
        Assert.Equal("已选择 0 项", vm.SelectionSummary);
    }
}
