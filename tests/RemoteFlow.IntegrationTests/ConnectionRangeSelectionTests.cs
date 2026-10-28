using System.Reflection;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>连接工作台多选：Ctrl 切换、Shift 连选（可跨页），翻页不丢已勾选项。</summary>
public sealed class ConnectionRangeSelectionTests
{
    private static ConnectionsPageViewModel CreateViewModel(int count, int pageSize = 0)
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
        viewModel.PageSize = pageSize;
        return viewModel;
    }

    private static void Refilter(ConnectionsPageViewModel viewModel)
        => typeof(ConnectionsPageViewModel)
            .GetMethod("ApplyFilter", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewModel, null);

    private static ConnectionItemViewModel Row(ConnectionsPageViewModel vm, int number)
        => vm.Items.Concat(AllFiltered(vm)).First(i => i.Name == $"host-{number:000}");

    private static IEnumerable<ConnectionItemViewModel> AllFiltered(ConnectionsPageViewModel vm)
        => ((List<ConnectionItemViewModel>)typeof(ConnectionsPageViewModel)
            .GetField("_filtered", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!);

    private static string[] SelectedNames(ConnectionsPageViewModel vm)
        => vm.SelectedConnections.Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public void 翻页后已勾选项保留_翻回来还在()
    {
        var vm = CreateViewModel(45, pageSize: 10);
        var first = Row(vm, 3);
        vm.ToggleSelect(first);
        vm.ToggleSelect(Row(vm, 5));

        vm.GoToPageCommand.Execute(2);
        Assert.Equal(["host-003", "host-005"], SelectedNames(vm));
        Assert.True(first.IsSelected);

        vm.GoToPageCommand.Execute(1);
        Assert.Equal(["host-003", "host-005"], SelectedNames(vm));
        Assert.True(vm.Items.Single(i => i.Name == "host-003").IsSelected);
    }

    [Fact]
    public void 筛选掉的已勾选项会移出选择_留在结果里的保留()
    {
        var vm = CreateViewModel(45, pageSize: 10);
        vm.ToggleSelect(Row(vm, 3));
        vm.ToggleSelect(Row(vm, 40));

        vm.PageFilterText = "host-04"; // 只剩 host-040 ~ host-045

        Assert.Equal(["host-040"], SelectedNames(vm));
        Assert.DoesNotContain(AllFiltered(vm), i => i.Name == "host-003");
    }

    [Fact]
    public void Ctrl单击切换勾选并成为连选起点()
    {
        var vm = CreateViewModel(20);

        vm.SelectWithModifiers(Row(vm, 2), ctrl: true, shift: false);
        vm.SelectWithModifiers(Row(vm, 4), ctrl: true, shift: false);
        Assert.Equal(["host-002", "host-004"], SelectedNames(vm));

        vm.SelectWithModifiers(Row(vm, 2), ctrl: true, shift: false);
        Assert.Equal(["host-004"], SelectedNames(vm));
        Assert.Equal("host-002", vm.SelectedItem!.Name);
    }

    [Fact]
    public void Shift单击从起点连选到目标_替换原有勾选()
    {
        var vm = CreateViewModel(20);
        vm.ToggleSelect(Row(vm, 15)); // 旧勾选，Shift 连选应当被替换

        vm.NoteSelectionAnchor(Row(vm, 3));
        vm.SelectWithModifiers(Row(vm, 6), ctrl: false, shift: true);

        Assert.Equal(["host-003", "host-004", "host-005", "host-006"], SelectedNames(vm));
    }

    [Fact]
    public void Shift反向连选同样包含两端()
    {
        var vm = CreateViewModel(20);
        vm.NoteSelectionAnchor(Row(vm, 8));

        vm.SelectWithModifiers(Row(vm, 6), ctrl: false, shift: true);

        Assert.Equal(["host-006", "host-007", "host-008"], SelectedNames(vm));
    }

    [Fact]
    public void CtrlShift连选追加到现有勾选()
    {
        var vm = CreateViewModel(20);
        vm.ToggleSelect(Row(vm, 15));
        vm.NoteSelectionAnchor(Row(vm, 2));

        vm.SelectWithModifiers(Row(vm, 4), ctrl: true, shift: true);

        Assert.Equal(["host-002", "host-003", "host-004", "host-015"], SelectedNames(vm));
    }

    [Fact]
    public void 没有起点时Shift以当前选中行为起点()
    {
        var vm = CreateViewModel(20);
        vm.SelectedItem = Row(vm, 5);

        vm.SelectWithModifiers(Row(vm, 7), ctrl: false, shift: true);

        Assert.Equal(["host-005", "host-006", "host-007"], SelectedNames(vm));
    }

    [Fact]
    public void Shift连选可以跨页()
    {
        var vm = CreateViewModel(45, pageSize: 10);
        vm.NoteSelectionAnchor(Row(vm, 8));
        vm.GoToPageCommand.Execute(2);

        vm.SelectRange(Row(vm, 13), additive: false);

        Assert.Equal(
            ["host-008", "host-009", "host-010", "host-011", "host-012", "host-013"],
            SelectedNames(vm));
    }

    [Fact]
    public void 全选覆盖所有页_清除也复位其它页的勾选标记()
    {
        var vm = CreateViewModel(45, pageSize: 10);

        vm.SelectAllVisibleCommand.Execute(null);
        Assert.Equal(45, vm.SelectedConnections.Count);
        Assert.True(vm.IsAllSelected);

        var offPage = vm.SelectedConnections.First(i => !vm.Items.Contains(i));
        vm.ClearSelectionCommand.Execute(null);

        Assert.Empty(vm.SelectedConnections);
        Assert.False(offPage.IsSelected);
        Assert.False(vm.IsAllSelected);
    }
}
