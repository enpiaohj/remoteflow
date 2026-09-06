using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 多选集合的纯逻辑：不碰数据库 / 不开对话框，直接构造 VM 并操作 Items 与选择集合。
/// 构造参数传 null——被测路径（Items / SelectedConnections / 各命令）不触碰它们才成立。
/// </summary>
public sealed class ConnectionSelectionViewModelTests
{
    private static ConnectionsPageViewModel CreateVm()
        => new(null!, null!, null!, null!, null!, null!, null!, null!, null!, SynchronousUiDispatcher.Instance, null!);

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

    [Fact]
    public void GroupToggle_SelectsAndClearsWholeSubtree()
    {
        var vm = CreateVm();
        var child = new ConnectionGroupNodeViewModel { Name = "子分组" };
        var c1 = Item("c1");
        var c2 = Item("c2");
        child.Connections.Add(c1);
        child.Connections.Add(c2);

        var root = new ConnectionGroupNodeViewModel { Name = "父分组" };
        var r = Item("r");
        root.ChildGroups.Add(child);
        root.Connections.Add(r);
        vm.GroupNodes.Add(root);

        vm.ToggleGroupSelection(root);
        Assert.Equal(3, vm.SelectedConnections.Count);
        Assert.All(new[] { r, c1, c2 }, i => Assert.True(i.IsSelected));

        vm.ToggleGroupSelection(root);
        Assert.Empty(vm.SelectedConnections);
        Assert.All(new[] { r, c1, c2 }, i => Assert.False(i.IsSelected));
    }

    [Fact]
    public void GroupState_ReflectsNonePartialFull()
    {
        var vm = CreateVm();
        var child = new ConnectionGroupNodeViewModel { Name = "子分组" };
        var c1 = Item("c1");
        var c2 = Item("c2");
        child.Connections.Add(c1);
        child.Connections.Add(c2);

        var root = new ConnectionGroupNodeViewModel { Name = "父分组" };
        var r = Item("r");
        root.ChildGroups.Add(child);
        root.Connections.Add(r);
        vm.GroupNodes.Add(root);

        vm.RefreshGroupSelectionStates();
        Assert.False(root.SelectionState);
        Assert.False(child.SelectionState);

        // 只选父分组直属一台 → 父半选(null)、子分组未选(false)
        vm.ToggleSelect(r);
        vm.RefreshGroupSelectionStates();
        Assert.Null(root.SelectionState);
        Assert.False(child.SelectionState);

        // 再把两台子机也选上 → 全选(true)
        vm.ToggleSelect(c1);
        vm.ToggleSelect(c2);
        vm.RefreshGroupSelectionStates();
        Assert.True(root.SelectionState);
        Assert.True(child.SelectionState);
    }
}
