using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RemoteFlow.App.Views.Pages;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 刷新会重建分组树。树控件的 SelectedItem 是双向绑定，列表一清空就把「未选中」写回视图模型，
/// 因此必须在真实控件上验证刷新后仍停在原分组（单元测试没有控件，测不到）。
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class GroupSelectionRefreshTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 重建分组树后仍停留在原分组(bool ungrouped)
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            Window? window = null;
            try
            {
                var group = new ConnectionGroup { Id = Guid.NewGuid(), Name = "生产" };
                var vm = new ConnectionsPageViewModel(null!, null!, null!, new ConnectionSearchService(),
                    null!, null!, new AppSettings(), null!, null!, SynchronousUiDispatcher.Instance, null!);
                Set(vm, "_groups", new List<ConnectionGroup> { group });
                var items = new List<ConnectionItemViewModel>
                {
                    new(new ConnectionProfile { Name = "组内", GroupId = group.Id }),
                    new(new ConnectionProfile { Name = "未分组主机" }),
                };
                ((List<ConnectionItemViewModel>)Get(vm, "_allItems")!).AddRange(items);
                vm.PageSize = 20;
                Rebuild(vm, items);

                var tree = new ConnectionResourceTree { DataContext = vm };
                window = new Window
                {
                    ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000,
                    Width = 320, Height = 600, Content = tree
                };
                window.Show();
                Flush();

                var list = (ListBox)tree.FindName("GroupTreeList");
                list.SelectedItem = vm.FlatTreeNodes.Single(n => n.IsUngrouped == ungrouped);
                Flush();
                var selectedId = vm.SelectedGroupNode!.GroupId;
                var title = vm.ViewTitle;

                Rebuild(vm, items); // 刷新按钮走的重建路径
                Flush();

                Assert.NotNull(vm.SelectedGroupNode);
                Assert.Equal(selectedId, vm.SelectedGroupNode!.GroupId);
                Assert.Equal(ungrouped, vm.SelectedGroupNode.IsUngrouped);
                Assert.Same(vm.SelectedGroupNode, list.SelectedItem); // 树上高亮的是新节点
                Assert.Equal(title, vm.ViewTitle);
                Assert.Null(vm.SelectedSmartView);                    // 没有跳回「所有设备」
            }
            finally
            {
                window?.Close();
                UiTestHost.RestoreAppResources();
            }
        });
    }

    private static void Rebuild(ConnectionsPageViewModel vm, IReadOnlyList<ConnectionItemViewModel> items)
        => typeof(ConnectionsPageViewModel).GetMethod("BuildGroupTree", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [items]);

    private static void Set(object target, string field, object value)
        => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object? Get(object target, string field)
        => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void Flush() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
}
