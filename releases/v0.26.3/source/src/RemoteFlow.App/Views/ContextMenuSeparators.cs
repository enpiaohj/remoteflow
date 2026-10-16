using System.Windows;
using System.Windows.Controls;

namespace RemoteFlow.App.Views;

/// <summary>
/// 右键菜单按上下文隐藏菜单项后，整理分隔线：分隔线只在两侧都有可见菜单项时出现，
/// 且连续多条只保留一条——不出现首尾悬空、底部成对或相邻重复的分隔线。
/// 每次菜单打开、完成菜单项显隐后调用；菜单实例复用时被折叠的分隔线也会按需恢复。
/// </summary>
public static class ContextMenuSeparators
{
    public static void Normalize(ItemsControl menu)
    {
        var items = menu.Items.Cast<object>().ToList();
        var n = items.Count;

        var visibleAfter = new bool[n];
        var hasAfter = false;
        for (var i = n - 1; i >= 0; i--)
        {
            visibleAfter[i] = hasAfter;
            if (IsVisibleItem(items[i]))
            {
                hasAfter = true;
            }
        }

        var hasItemBefore = false;
        var lastVisibleIsSeparator = false;
        for (var i = 0; i < n; i++)
        {
            if (items[i] is Separator separator)
            {
                var show = hasItemBefore && visibleAfter[i] && !lastVisibleIsSeparator;
                separator.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (show)
                {
                    lastVisibleIsSeparator = true;
                }
            }
            else if (IsVisibleItem(items[i]))
            {
                hasItemBefore = true;
                lastVisibleIsSeparator = false;
            }
        }
    }

    private static bool IsVisibleItem(object item)
        => item is UIElement { Visibility: Visibility.Visible } and not Separator;
}
