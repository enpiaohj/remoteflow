using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RemoteFlow.App.Views;

/// <summary>
/// 菜单项的统一外观约定（应用内右键菜单与托盘菜单共用）：
/// <list type="bullet">
///   <item><see cref="IconKeyProperty"/>：左侧图标，取 <c>Ui.*</c> 线性图标资源键，颜色跟随文字。</item>
///   <item><see cref="IsDangerProperty"/>：不可撤销 / 破坏性操作（删除、断开全部等）用警示色，并按约定放在菜单最后一组。</item>
/// </list>
/// 图标列在模板里固定占位，带图标与不带图标的菜单项文字始终对齐。
/// </summary>
public static class MenuItemAssist
{
    public static readonly DependencyProperty IconKeyProperty = DependencyProperty.RegisterAttached(
        "IconKey", typeof(string), typeof(MenuItemAssist), new PropertyMetadata(null, OnIconKeyChanged));

    /// <summary>由 <see cref="IconKeyProperty"/> 解析出的几何，供菜单项模板绑定；不要直接设置。</summary>
    public static readonly DependencyProperty IconGeometryProperty = DependencyProperty.RegisterAttached(
        "IconGeometry", typeof(Geometry), typeof(MenuItemAssist), new PropertyMetadata(null));

    public static readonly DependencyProperty IsDangerProperty = DependencyProperty.RegisterAttached(
        "IsDanger", typeof(bool), typeof(MenuItemAssist), new PropertyMetadata(false));

    public static string? GetIconKey(DependencyObject element) => (string?)element.GetValue(IconKeyProperty);

    public static void SetIconKey(DependencyObject element, string? value) => element.SetValue(IconKeyProperty, value);

    public static Geometry? GetIconGeometry(DependencyObject element) => (Geometry?)element.GetValue(IconGeometryProperty);

    public static void SetIconGeometry(DependencyObject element, Geometry? value) => element.SetValue(IconGeometryProperty, value);

    public static bool GetIsDanger(DependencyObject element) => (bool)element.GetValue(IsDangerProperty);

    public static void SetIsDanger(DependencyObject element, bool value) => element.SetValue(IsDangerProperty, value);

    private static void OnIconKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var geometry = e.NewValue is string { Length: > 0 } key
            ? System.Windows.Application.Current?.TryFindResource(key) as Geometry
            : null;
        d.SetValue(IconGeometryProperty, geometry);
    }

    /// <summary>代码里构造菜单项时的链式写法：<c>new MenuItem { Header = "编辑连接" }.WithIcon("Ui.Edit")</c>。</summary>
    public static MenuItem WithIcon(this MenuItem item, string iconKey)
    {
        SetIconKey(item, iconKey);
        return item;
    }

    /// <summary>标记为破坏性操作（警示色）。</summary>
    public static MenuItem AsDanger(this MenuItem item)
    {
        SetIsDanger(item, true);
        return item;
    }
}
