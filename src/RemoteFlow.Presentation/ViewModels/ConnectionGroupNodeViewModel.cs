using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RemoteFlow.Presentation.ViewModels;

/// <summary>
/// 「我的连接」分组树的一个节点。子分组和直属连接分开存放，
/// UI 先渲染子分组、再渲染连接行。
/// </summary>
public sealed partial class ConnectionGroupNodeViewModel : ObservableObject
{
    /// <summary>分组 Id。<c>null</c> 表示系统「未分组」节点。</summary>
    public Guid? GroupId { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>嵌套层级，根级为 0。用于缩进。</summary>
    public int Depth { get; init; }

    /// <summary>是否系统「未分组」节点——不显示右键的重命名 / 删除 / 新建子分组。</summary>
    public bool IsUngrouped { get; init; }

    /// <summary>是否默认新建连接分组（决定菜单是否出现「设为默认分组」等）。</summary>
    public bool IsDefault { get; init; }

    /// <summary>该分组是否受保护 / 锁定（重命名 / 删除 / 移动层级被禁）。</summary>
    public bool IsProtected { get; init; }

    public ObservableCollection<ConnectionGroupNodeViewModel> ChildGroups { get; } = [];

    public ObservableCollection<ConnectionItemViewModel> Connections { get; } = [];

    /// <summary>该分组（含所有子孙分组）的连接总数，显示在分组标题右侧。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyGroup))]
    private int _totalCount;

    /// <summary>整个子树一条连接都没有——展开时显示「暂无连接」提示。</summary>
    public bool IsEmptyGroup => TotalCount == 0;

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>搜索过滤后是否可见（自身或某个子孙命中）。</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>该分组子树在多选模式下的勾选态：true=全选、false=未选、null=部分。</summary>
    [ObservableProperty]
    private bool? _selectionState;

    public bool HasChildGroups => ChildGroups.Count > 0;

    public bool HasConnections => Connections.Count > 0;

    /// <summary>子树（含子孙分组）存在连接时才在组头显示可勾选。</summary>
    public bool HasAnyConnections => TotalCount > 0;

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    /// <summary>ListBoxItem 的 UIA Name 取自 ToString，屏幕阅读器应读到分组名而非类型转储。</summary>
    public override string ToString() => Name;
}
