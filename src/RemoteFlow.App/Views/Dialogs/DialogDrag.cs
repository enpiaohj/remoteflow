using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace RemoteFlow.App.Views.Dialogs;

/// <summary>
/// 无边框对话框的拖动：在窗口任意非交互区域按住即可移动，和其它对话框一致。
/// <para>
/// 凭据 / 连接编辑器原先只挂在标题栏上，窗口外缘、表单空白、底部按钮栏都拖不动，
/// 而别的对话框整窗都能拖——表现为「有的对话框无法移动」。
/// </para>
/// <para>
/// 必须在预览（隧道）阶段处理：表单放在 <see cref="ScrollViewer"/> 里，它收到左键按下会去抢焦点
/// 并把事件标为已处理，冒泡到窗口的处理器永远收不到。预览阶段先判断按下的位置：
/// 输入框、按钮、下拉、列表、滚动条等可交互控件原样放行，其余区域才拖动。
/// </para>
/// </summary>
internal static class DialogDrag
{
    public static void EnableWholeWindow(Window window)
        => window.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed
                || e.OriginalSource is not DependencyObject source
                || IsInteractive(source))
            {
                return;
            }

            try
            {
                window.DragMove();
                e.Handled = true;
            }
            catch (InvalidOperationException)
            {
                // DragMove 要求调用时左键确实按住；时序错位（事件报告 Pressed 但实际已释放）会抛出，
                // 忽略即可——不拖动，也不能让异常打断模态对话框。
            }
        };

    /// <summary>按下位置自身或其祖先是否是可交互控件（这些控件要自己处理点击 / 选择 / 滚动）。</summary>
    internal static bool IsInteractive(DependencyObject source)
    {
        for (var current = source; current is not null; current = Parent(current))
        {
            if (current is ButtonBase
                or TextBoxBase
                or PasswordBox
                or Selector
                or ScrollBar
                or Thumb
                or Slider
                or Expander
                or MenuItem
                or Popup)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 先走可视树；走不通时回退逻辑树——文本里的 <see cref="System.Windows.Documents.Run"/> 等内容元素
    /// 不在可视树里，尚未应用模板的控件内容也只有逻辑父级。
    /// </summary>
    private static DependencyObject? Parent(DependencyObject current)
        => (current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : null)
           ?? LogicalTreeHelper.GetParent(current);
}
