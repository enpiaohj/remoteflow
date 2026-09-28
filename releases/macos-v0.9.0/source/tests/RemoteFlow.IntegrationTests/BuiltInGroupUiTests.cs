using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>永久内置“我的设备”规则在双平台 UI 中不能残留可切换默认身份的入口。</summary>
public sealed class BuiltInGroupUiTests
{
    [Fact]
    public void 双平台不再提供切换默认组或解除保护入口()
    {
        var connectionsViewModel = ReadProjectFile(
            "src", "RemoteFlow.Presentation", "ViewModels", "ConnectionsPageViewModel.cs");
        var settingsViewModel = ReadProjectFile(
            "src", "RemoteFlow.Presentation", "ViewModels", "SettingsPageViewModel.cs");
        var windowsTree = ReadProjectFile(
            "src", "RemoteFlow.App", "Views", "Pages", "ConnectionResourceTree.xaml.cs");
        var macConnections = ReadProjectFile(
            "src", "RemoteFlow.App.Mac", "ConnectionListPane.cs");
        var macSettings = ReadProjectFile(
            "src", "RemoteFlow.App.Mac", "SettingsPaneView.cs");

        Assert.DoesNotContain("SetDefaultGroupAsync", connectionsViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("PickDefaultGroupAsync", connectionsViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("DefaultGroupProtected", settingsViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("OnGroupSetDefaultClick", windowsTree, StringComparison.Ordinal);
        Assert.DoesNotContain("设为默认分组", macConnections, StringComparison.Ordinal);
        Assert.DoesNotContain("DefaultGroupProtected", macSettings, StringComparison.Ordinal);
    }

    [Fact]
    public void macOS为全部分组和标签图标键提供SF_Symbols映射()
    {
        var style = ReadProjectFile("src", "RemoteFlow.App.Mac", "OrganizationStyle.cs");

        foreach (var key in RemoteFlow.Core.Models.GroupIconCatalog.All
                     .Concat(RemoteFlow.Core.Models.TagIconCatalog.All)
                     .Select(x => x.Key))
        {
            Assert.Contains($"[\"{key}\"]", style, StringComparison.Ordinal);
        }
    }

    private static string ReadProjectFile(params string[] segments)
    {
        foreach (var root in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory }.Distinct())
        {
            for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine([directory.FullName, .. segments]);
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }
            }
        }

        throw new FileNotFoundException($"未找到 {Path.Combine(segments)}。");
    }
}
