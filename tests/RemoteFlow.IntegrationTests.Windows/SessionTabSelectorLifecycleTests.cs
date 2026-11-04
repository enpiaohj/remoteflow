using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RemoteFlow.App.Views.Sessions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 会话选择器的列表镜像与数量角标必须在「卸载 → 重新加载」后继续与 <c>MainViewModel.Tabs</c> 同步。
/// WPF 在窗口句柄重建（进出全屏、窗口样式 / 材质切换）与主题切换时都会先 Unloaded 再 Loaded，
/// 而此时 DataContext 没有变，不会再触发 DataContextChanged。
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class SessionTabSelectorLifecycleTests
{
    [Fact]
    public void ReloadedSelector_ResyncsTabsChangedWhileUnloaded()
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            Window? window = null;
            try
            {
                var main = CreateMainViewModel();
                var selector = new SessionTabSelector { DataContext = main };
                var list = (ListBox)selector.FindName("SessionsList");
                var badge = (Border)selector.FindName("HeaderCountBadge");
                var badgeText = (TextBlock)selector.FindName("HeaderCountText");

                window = new Window
                {
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000,
                    Top = -10000,
                    Width = 400,
                    Height = 80,
                    Content = selector,
                };
                window.Show();
                Flush();
                Assert.Empty(list.Items);

                main.Tabs.Add(NewSessionTab("甲"));
                main.Tabs.Add(NewSessionTab("乙"));
                Assert.Equal(2, list.Items.Count);
                Assert.Equal(Visibility.Visible, badge.Visibility);
                Assert.Equal("2", badgeText.Text);

                // 卸载期间开了第三个会话。
                window.Content = null;
                Flush();
                main.Tabs.Add(NewSessionTab("丙"));

                window.Content = selector;
                Flush();
                Assert.Equal(3, list.Items.Count);
                Assert.Equal("3", badgeText.Text);

                // 重新加载后的后续增删也要继续同步（且不重复订阅：一次变化只重建一次，数量不翻倍）。
                main.Tabs.RemoveAt(0);
                Assert.Equal(2, list.Items.Count);
                Assert.Equal("2", badgeText.Text);

                // 再经历一轮卸载期间的关闭。
                window.Content = null;
                Flush();
                main.Tabs.RemoveAt(0);
                window.Content = selector;
                Flush();
                Assert.Single(list.Items);
                Assert.Equal(Visibility.Collapsed, badge.Visibility);
            }
            finally
            {
                window?.Close();
                UiTestHost.RestoreAppResources();
            }
        });
    }

    [Fact]
    public void PageHeader_ReturnsToRememberedSession_ArrowOnlyOpensList_AndShowsAccount()
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            Window? window = null;
            using var tab = NewSessionTab("演示会话", @"EXAMPLE\operator");
            try
            {
                var main = CreateMainViewModel();
                main.Tabs.Add(tab);
                var page = new PageTabViewModel();
                main.Tabs.Insert(0, page);
                typeof(MainViewModel).GetField("_selectedTab", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(main, page);
                main.ReturnTab = tab;
                var selector = new SessionTabSelector { DataContext = main };
                window = new Window
                {
                    ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000,
                    Width = 400, Height = 80, Content = selector
                };
                window.Show();
                Flush();
                var toggle = (System.Windows.Controls.Primitives.ToggleButton)selector.FindName("SelectorToggle");
                var popup = (System.Windows.Controls.Primitives.Popup)selector.FindName("SelectorPopup");
                Assert.Equal($"返回 {tab.Title}", ((TextBlock)selector.FindName("HeaderTitle")).Text);
                Assert.Equal("1", ((TextBlock)selector.FindName("HeaderCountText")).Text);
                Assert.Equal(Visibility.Visible, ((Border)selector.FindName("HeaderCountBadge")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((Button)selector.FindName("HeaderClose")).Visibility);
                Assert.Equal(tab.TabToolTip, toggle.ToolTip);
                Assert.Contains(@"账号：EXAMPLE\operator", (string)toggle.ToolTip, StringComparison.Ordinal);

                ((Button)selector.FindName("SelectorArrow")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.True(popup.IsOpen, $"Popup={popup.IsOpen}; Checked={toggle.IsChecked}; Selected={main.SelectedTab?.GetType().Name}");
                Assert.Same(page, main.SelectedTab);
                popup.IsOpen = false;
                toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Same(tab, main.SelectedTab);
                Assert.False(popup.IsOpen);
                Assert.Equal(tab.Title, ((TextBlock)selector.FindName("HeaderTitle")).Text);
                Assert.Equal(Visibility.Visible, ((Button)selector.FindName("HeaderClose")).Visibility);
                Assert.Equal(tab.TabToolTip, toggle.ToolTip);
            }
            finally
            {
                window?.Close();
                UiTestHost.RestoreAppResources();
            }
        });
    }

    private static void Flush() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    /// <summary>
    /// 选择器只读取 Tabs / SelectedTab / PropertyChanged；不构造完整的 MainViewModel 依赖树，
    /// 只补上 Tabs 集合（自动属性的支持字段）。
    /// </summary>
    private static MainViewModel CreateMainViewModel()
    {
        var main = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        typeof(MainViewModel)
            .GetField("<Tabs>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(main, new ObservableCollection<WorkspaceTabViewModel>());
        return main;
    }

    private static SessionTabViewModel NewSessionTab(string name, string? account = null) => new(
        new FakeSession(new ConnectionProfile { Name = name, Host = name, Protocol = ProtocolType.Ssh }),
        _ => Task.CompletedTask,
        _ => Task.CompletedTask,
        SynchronousUiDispatcher.Instance,
        new NoOpTimerFactory(), account: account);

    private sealed class FakeSession(ConnectionProfile profile) : RemoteSessionBase
    {
        public override ProtocolType Protocol => profile.Protocol;

        public override ConnectionProfile Profile => profile;

        public override Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        protected override ValueTask PerformTeardownAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoOpTimerFactory : IUiTimerFactory
    {
        public IUiTimer Create(TimeSpan interval) => new NoOpTimer { Interval = interval };
    }

    private sealed class NoOpTimer : IUiTimer
    {
        public TimeSpan Interval { get; set; }

        public event EventHandler? Tick
        {
            add { }
            remove { }
        }

        public void Start() { }

        public void Stop() { }

        public void Dispose() { }
    }
}
