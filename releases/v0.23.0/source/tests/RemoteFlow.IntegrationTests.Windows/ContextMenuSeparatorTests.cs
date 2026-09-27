using System.IO;
using System.Windows;
using System.Windows.Controls;
using RemoteFlow.App.Views;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>右键菜单隐藏部分菜单项后，分隔线不得悬空、成对或出现在首尾。</summary>
public sealed class ContextMenuSeparatorTests
{
    [Fact]
    public void 内置分组菜单隐藏编辑和删除后底部不残留分隔线()
    {
        RunSta(() =>
        {
            // 与分组右键菜单同构：新建连接 / 新建子分组 | 编辑分组 | 删除分组
            var menu = Menu(Item(true), Item(true), Sep(), Item(false), Sep(), Item(false));

            ContextMenuSeparators.Normalize(menu);

            Assert.Equal("II", Shape(menu));
        });
    }

    [Fact]
    public void 中间整组隐藏时相邻分隔线只保留一条()
    {
        RunSta(() =>
        {
            var menu = Menu(Item(true), Sep(), Item(false), Sep(), Item(true), Sep(), Item(false));

            ContextMenuSeparators.Normalize(menu);

            Assert.Equal("I-I", Shape(menu));
        });
    }

    [Fact]
    public void 全部可见时分隔线原样保留且可再次恢复()
    {
        RunSta(() =>
        {
            var hidden = Item(false);
            var menu = Menu(Item(true), Sep(), hidden, Sep(), Item(true));
            ContextMenuSeparators.Normalize(menu);
            Assert.Equal("I-I", Shape(menu));

            // 同一菜单实例再次打开、菜单项恢复可见时，被折叠的分隔线要重新出现。
            hidden.Visibility = Visibility.Visible;
            ContextMenuSeparators.Normalize(menu);
            Assert.Equal("I-I-I", Shape(menu));
        });
    }

    [Fact]
    public void 所有动态隐藏菜单项的右键菜单都调用统一分隔线整理()
    {
        foreach (var segments in new[]
                 {
                     new[] { "src", "RemoteFlow.App", "Views", "Pages", "ConnectionResourceTree.xaml.cs" },
                     new[] { "src", "RemoteFlow.App", "Views", "Pages", "ConnectionsPage.xaml.cs" },
                     new[] { "src", "RemoteFlow.App", "Views", "Pages", "HomePage.xaml.cs" },
                 })
        {
            var code = File.ReadAllText(FindProjectFile(segments));
            Assert.Contains("ContextMenuSeparators.Normalize(", code, StringComparison.Ordinal);
        }
    }

    private static ContextMenu Menu(params Control[] items)
    {
        var menu = new ContextMenu();
        foreach (var item in items)
        {
            menu.Items.Add(item);
        }

        return menu;
    }

    private static MenuItem Item(bool visible) => new()
    {
        Header = "x",
        Visibility = visible ? Visibility.Visible : Visibility.Collapsed
    };

    private static Separator Sep() => new();

    /// <summary>可见结构：I = 菜单项，- = 分隔线。</summary>
    private static string Shape(ContextMenu menu) => string.Concat(menu.Items.Cast<UIElement>()
        .Where(x => x.Visibility == Visibility.Visible)
        .Select(x => x is Separator ? "-" : "I"));

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    private static string FindProjectFile(params string[] segments)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"未找到 {Path.Combine(segments)}。");
    }
}
