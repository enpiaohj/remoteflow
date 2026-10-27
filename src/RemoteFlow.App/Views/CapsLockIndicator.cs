using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RemoteFlow.App.Views;

/// <summary>
/// 密码框的「大写锁定已开启」提示。WPF 的 <see cref="PasswordBox"/> 不像系统原生密码框那样
/// 自带气泡，Caps Lock 开着时用户只会看到一串点，输错了也无从察觉。
/// <para>
/// 用法：在样式里设置 <c>views:CapsLockIndicator.IsMonitored="True"</c>，模板按
/// <see cref="IsActiveProperty"/> 显示提示。只在该控件拥有键盘焦点且 Caps Lock 为开时为 true，
/// 避免同一页多个密码框同时亮提示。
/// </para>
/// </summary>
public static class CapsLockIndicator
{
    /// <summary>读取 Caps Lock 状态。测试可替换，避免依赖真实键盘状态。</summary>
    internal static Func<bool> CapsLockProbe { get; set; } = () => Keyboard.IsKeyToggled(Key.CapsLock);

    public static readonly DependencyProperty IsMonitoredProperty =
        DependencyProperty.RegisterAttached(
            "IsMonitored", typeof(bool), typeof(CapsLockIndicator),
            new PropertyMetadata(false, OnIsMonitoredChanged));

    private static readonly DependencyPropertyKey IsActivePropertyKey =
        DependencyProperty.RegisterAttachedReadOnly(
            "IsActive", typeof(bool), typeof(CapsLockIndicator), new PropertyMetadata(false));

    /// <summary>该控件当前是否应显示大写锁定提示（有键盘焦点且 Caps Lock 开启）。</summary>
    public static readonly DependencyProperty IsActiveProperty = IsActivePropertyKey.DependencyProperty;

    public static bool GetIsMonitored(DependencyObject element) => (bool)element.GetValue(IsMonitoredProperty);

    public static void SetIsMonitored(DependencyObject element, bool value) => element.SetValue(IsMonitoredProperty, value);

    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActivePropertyKey.DependencyProperty);

    /// <summary>按给定的焦点状态与当前 Caps Lock 重新计算。事件处理与测试共用。</summary>
    public static void Refresh(DependencyObject element, bool hasKeyboardFocus)
        => element.SetValue(IsActivePropertyKey, hasKeyboardFocus && CapsLockProbe());

    private static void OnIsMonitoredChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.GotKeyboardFocus -= OnFocusChanged;
        element.LostKeyboardFocus -= OnFocusChanged;
        element.PreviewKeyDown -= OnKeyChanged;
        element.PreviewKeyUp -= OnKeyChanged;

        if (e.NewValue is true)
        {
            element.GotKeyboardFocus += OnFocusChanged;
            element.LostKeyboardFocus += OnFocusChanged;
            // Caps Lock 在输入过程中被切换，也要即时更新。
            element.PreviewKeyDown += OnKeyChanged;
            element.PreviewKeyUp += OnKeyChanged;
        }
        else
        {
            element.ClearValue(IsActivePropertyKey);
        }
    }

    private static void OnFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is UIElement element)
        {
            // Lost 事件里 IsKeyboardFocused 此刻仍可能为 true，直接以事件类型判断。
            Refresh(element, e.RoutedEvent == Keyboard.GotKeyboardFocusEvent);
        }
    }

    private static void OnKeyChanged(object sender, KeyEventArgs e)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        // 按下 / 抬起 Caps Lock 本身时，系统切换状态发生在按键处理之后：延后到输入处理完成再读取。
        if (e.Key == Key.CapsLock || e.SystemKey == Key.CapsLock)
        {
            element.Dispatcher.BeginInvoke(
                new Action(() => Refresh(element, element.IsKeyboardFocusWithin)),
                System.Windows.Threading.DispatcherPriority.Input);
            return;
        }

        Refresh(element, element.IsKeyboardFocusWithin);
    }
}
