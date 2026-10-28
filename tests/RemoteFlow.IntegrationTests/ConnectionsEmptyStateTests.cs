using System.Reflection;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

public sealed class ConnectionsEmptyStateTests
{
    private static ConnectionsPageViewModel Create(params ConnectionProfile[] profiles)
    {
        var vm = new ConnectionsPageViewModel(null!, null!, null!, new ConnectionSearchService(),
            null!, null!, null!, null!, null!, SynchronousUiDispatcher.Instance, null!);
        var items = (List<ConnectionItemViewModel>)typeof(ConnectionsPageViewModel)
            .GetField("_allItems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        items.AddRange(profiles.Select(p => new ConnectionItemViewModel(p)));
        vm.PageSize = 20;
        vm.ProtocolFilter = ProtocolFilterOption.Ssh;
        vm.ClearFiltersCommand.Execute(null);
        return vm;
    }

    [Fact]
    public void EmptyLibrary_TakesPrecedenceOverViewsAndFilters()
    {
        var vm = Create();
        vm.Filter = ConnectionFilter.Favorites;
        vm.PageFilterText = "missing";
        Assert.Equal("Library", vm.EmptyStateKind);
        Assert.True(vm.ShowImportHint);
        Assert.True(vm.ShowEmptyState);
        Assert.False(vm.HasConnections);
        vm.IsLoading = true;
        Assert.False(vm.ShowEmptyState);
        Assert.True(vm.ShowLoadingState);
    }

    [Fact]
    public void Favorites_DistinguishesEmptyCollectionFromNoMatches()
    {
        var vm = Create(new ConnectionProfile { Name = "server", Favorite = true });
        vm.Filter = ConnectionFilter.Favorites;
        vm.PageFilterText = "missing";
        Assert.Equal("NoMatches", vm.EmptyStateKind);
        vm.ResetEmptyFiltersCommand.Execute(null);
        Assert.Equal(ConnectionFilter.Favorites, vm.Filter);
        Assert.Single(vm.Items);

        var empty = Create(new ConnectionProfile { Name = "server" });
        empty.Filter = ConnectionFilter.Favorites;
        Assert.Equal("Favorites", empty.EmptyStateKind);
        empty.BrowseAllConnectionsCommand.Execute(null);
        Assert.Equal(ConnectionFilter.All, empty.Filter);
        Assert.Single(empty.Items);
    }

    [Fact]
    public void Reset_RetainsGroupAndSortWhileClearingBothSearchesAndAllFilters()
    {
        var group = new ConnectionGroupNodeViewModel { GroupId = Guid.NewGuid(), Name = "测试组", TotalCount = 1 };
        var vm = Create(new ConnectionProfile { Name = "server", GroupId = group.GroupId });
        vm.SelectedGroupNode = group;
        vm.SearchText = "missing";
        vm.PageFilterText = "missing";
        vm.ProtocolFilter = ProtocolFilterOption.Vnc;
        vm.TagFilter = new(Guid.NewGuid(), "tag", "");
        vm.PresenceFilter = PresenceFilterOption.Online;
        vm.SortMode = ConnectionSortMode.Protocol;
        Assert.Equal("NoMatches", vm.EmptyStateKind);
        vm.ResetEmptyFiltersCommand.Execute(null);
        Assert.Same(group, vm.SelectedGroupNode);
        Assert.Equal(ConnectionSortMode.Protocol, vm.SortMode);
        Assert.Empty(vm.SearchText);
        Assert.Empty(vm.PageFilterText);
        Assert.False(vm.HasActiveFilters);
        Assert.Single(vm.Items);
        group.TotalCount = 0;
        vm.PageFilterText = "missing";
        Assert.Equal("Group", vm.EmptyStateKind);
    }

    [Fact]
    public void Recent_ResetExpandsTimeRangeAndSearchWorksInVisibleSearchBox()
    {
        var vm = Create(new ConnectionProfile { Name = "server", LastConnectedAt = DateTimeOffset.Now.AddDays(-20), Notes = "maintenance" });
        vm.Filter = ConnectionFilter.Recent;
        Assert.True(vm.IsEmpty);
        Assert.Equal("NoMatches", vm.EmptyStateKind);
        vm.ResetEmptyFiltersCommand.Execute(null);
        Assert.Equal(RecentRange.All, vm.RecentRange);
        Assert.Equal(ConnectionFilter.Recent, vm.Filter);
        Assert.Single(vm.Items);
        vm.PageFilterText = "missing";
        Assert.Empty(vm.Items);
        vm.PageFilterText = "maintenance";
        Assert.Single(vm.Items);

        var unused = Create(new ConnectionProfile { Name = "server" });
        unused.Filter = ConnectionFilter.Recent;
        Assert.Equal("Recent", unused.EmptyStateKind);
    }

    [Fact]
    public void ImportEntry_RequestsExistingSettingsPage()
    {
        var vm = Create();
        NavigationPage? requested = null;
        vm.NavigationRequested += (_, page) => requested = page;
        vm.OpenImportSettingsCommand.Execute(null);
        Assert.Equal(NavigationPage.Settings, requested);
    }
}
