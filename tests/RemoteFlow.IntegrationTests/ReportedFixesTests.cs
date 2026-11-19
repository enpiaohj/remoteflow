using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>用户反馈的三个问题：状态筛选下刷新后列表清空、标签拖动排序。</summary>
public sealed class ReportedFixesTests
{
    private static ConnectionsPageViewModel CreatePage(PresenceProbeService probe, params ConnectionProfile[] profiles)
    {
        var vm = new ConnectionsPageViewModel(null!, null!, null!, new ConnectionSearchService(),
            null!, null!, null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<ConnectionsPageViewModel>.Instance,
            SynchronousUiDispatcher.Instance, null!, probe);
        var items = (List<ConnectionItemViewModel>)typeof(ConnectionsPageViewModel)
            .GetField("_allItems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        items.AddRange(profiles.Select(p => new ConnectionItemViewModel(p)));
        typeof(ConnectionsPageViewModel).GetProperty(nameof(ConnectionsPageViewModel.PresenceProbeEnabled))!
            .SetValue(vm, true);
        vm.PageSize = 20;
        vm.ClearFiltersCommand.Execute(null);
        return vm;
    }

    [Fact]
    public async Task 在线筛选下探测覆盖全部候选并在完成后显示在线项()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = listener.AcceptTcpClientAsync();
        var onlinePort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var offlinePort = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        try
        {
            var vm = CreatePage(new PresenceProbeService(),
                new ConnectionProfile { Name = "up", Host = "127.0.0.1", Port = onlinePort },
                new ConnectionProfile { Name = "down", Host = "127.0.0.1", Port = offlinePort });

            // 刷新后所有行都是「未知」：在线筛选下列表为空，探测仍必须覆盖这两台。
            vm.PresenceFilter = PresenceFilterOption.Online;
            Assert.Empty(vm.Items);

            await vm.ProbePresenceCommand.ExecuteAsync(null);

            Assert.Equal(["up"], vm.Items.Select(i => i.Name).ToArray());

            vm.PresenceFilter = PresenceFilterOption.Offline;
            Assert.Equal(["down"], vm.Items.Select(i => i.Name).ToArray());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void 标签只能在非页面标签之间换位()
    {
        var main = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        var tabs = new ObservableCollection<WorkspaceTabViewModel>();
        typeof(MainViewModel).GetField("<Tabs>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(main, tabs);

        var page = new PageTabViewModel();
        using var a = NewSessionTab();
        using var b = NewSessionTab();
        using var c = NewSessionTab();
        foreach (var tab in new WorkspaceTabViewModel[] { page, a, b, c })
        {
            tabs.Add(tab);
        }

        Assert.True(main.MoveTab(c, 1));
        Assert.Equal(new WorkspaceTabViewModel[] { page, c, a, b }, tabs.ToArray());

        Assert.False(main.MoveTab(c, 0));   // 不能挤到页面标签前面
        Assert.False(main.MoveTab(page, 2)); // 页面标签固定
        Assert.False(main.MoveTab(a, 2));    // 原位
        Assert.False(main.MoveTab(a, 9));    // 越界
        Assert.Equal(new WorkspaceTabViewModel[] { page, c, a, b }, tabs.ToArray());
    }

    private static SessionTabViewModel NewSessionTab() => new(
        new FakeSession(), _ => Task.CompletedTask, _ => Task.CompletedTask,
        SynchronousUiDispatcher.Instance, new ManualTimerFactory());
}
