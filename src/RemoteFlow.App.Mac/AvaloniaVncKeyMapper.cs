using Avalonia.Input;
using MarcusW.VncClient;

namespace RemoteFlow.App.Mac;

/// <summary>
/// Avalonia <see cref="Key"/> → VNC X11 keysym 映射。命名与 WPF 侧
/// <c>RemoteFlow.App.Views.Sessions.VncKeyMapper</c> 对齐（同一 X11 keysym 体系）。
/// <para>
/// RFB 用 X11 keysym 表达按键。功能键查表；字母 / 数字按键按逻辑键 + Shift
/// 修饰推断 keysym（ASCII 段 keysym 与码点一致）。
/// </para>
/// <para>
/// 局限（骨架阶段）：未做完整键盘布局 / IME / 复杂符号键（OEM）映射；
/// 满足基本运维交互。精确映射留待后续（可加 TextInput 通道按字符发键）。
/// </para>
/// </summary>
public static class AvaloniaVncKeyMapper
{
    private static readonly Dictionary<Key, KeySymbol> SpecialKeys = new()
    {
        [Key.Back] = KeySymbol.BackSpace,
        [Key.Tab] = KeySymbol.Tab,
        [Key.Enter] = KeySymbol.Return,
        [Key.Escape] = KeySymbol.Escape,
        [Key.Space] = KeySymbol.space,
        [Key.Delete] = KeySymbol.Delete,
        [Key.Insert] = KeySymbol.Insert,
        [Key.Home] = KeySymbol.Home,
        [Key.End] = KeySymbol.End,
        [Key.PageUp] = KeySymbol.Page_Up,
        [Key.PageDown] = KeySymbol.Page_Down,
        [Key.Left] = KeySymbol.Left,
        [Key.Right] = KeySymbol.Right,
        [Key.Up] = KeySymbol.Up,
        [Key.Down] = KeySymbol.Down,
        [Key.CapsLock] = KeySymbol.Caps_Lock,
        [Key.LeftShift] = KeySymbol.Shift_L,
        [Key.RightShift] = KeySymbol.Shift_R,
        [Key.LeftCtrl] = KeySymbol.Control_L,
        [Key.RightCtrl] = KeySymbol.Control_R,
        [Key.LeftAlt] = KeySymbol.Alt_L,
        [Key.RightAlt] = KeySymbol.Alt_R,
        [Key.LWin] = KeySymbol.Super_L,
        [Key.RWin] = KeySymbol.Super_R,
        [Key.F1] = KeySymbol.F1,
        [Key.F2] = KeySymbol.F2,
        [Key.F3] = KeySymbol.F3,
        [Key.F4] = KeySymbol.F4,
        [Key.F5] = KeySymbol.F5,
        [Key.F6] = KeySymbol.F6,
        [Key.F7] = KeySymbol.F7,
        [Key.F8] = KeySymbol.F8,
        [Key.F9] = KeySymbol.F9,
        [Key.F10] = KeySymbol.F10,
        [Key.F11] = KeySymbol.F11,
        [Key.F12] = KeySymbol.F12,
        // 小键盘独立键位（远端可能依赖 Num Lock 状态下的方向键行为）。
        [Key.NumPad0] = KeySymbol.KP_0,
        [Key.NumPad1] = KeySymbol.KP_1,
        [Key.NumPad2] = KeySymbol.KP_2,
        [Key.NumPad3] = KeySymbol.KP_3,
        [Key.NumPad4] = KeySymbol.KP_4,
        [Key.NumPad5] = KeySymbol.KP_5,
        [Key.NumPad6] = KeySymbol.KP_6,
        [Key.NumPad7] = KeySymbol.KP_7,
        [Key.NumPad8] = KeySymbol.KP_8,
        [Key.NumPad9] = KeySymbol.KP_9,
        [Key.Add] = KeySymbol.KP_Add,
        [Key.Subtract] = KeySymbol.KP_Subtract,
        [Key.Multiply] = KeySymbol.KP_Multiply,
        [Key.Divide] = KeySymbol.KP_Divide,
        [Key.Decimal] = KeySymbol.KP_Decimal,
    };

    /// <summary>数字行在 Shift 下的符号，下标 = 键数字。</summary>
    private static readonly int[] ShiftedDigits = { ')', '!', '@', '#', '$', '%', '^', '&', '*', '(' };

    private static readonly Dictionary<Key, KeySymbol> ModifierKeys = new()
    {
        [Key.LeftShift] = KeySymbol.Shift_L,
        [Key.RightShift] = KeySymbol.Shift_R,
        [Key.LeftCtrl] = KeySymbol.Control_L,
        [Key.RightCtrl] = KeySymbol.Control_R,
        [Key.LeftAlt] = KeySymbol.Alt_L,
        [Key.RightAlt] = KeySymbol.Alt_R,
        [Key.LWin] = KeySymbol.Super_L,
        [Key.RWin] = KeySymbol.Super_R,
    };

    /// <summary>键是否为修饰键。</summary>
    public static bool IsModifier(Key key) => ModifierKeys.ContainsKey(key);

    /// <summary>修饰键 keysym。</summary>
    public static KeySymbol MapModifier(Key key) => ModifierKeys[key];

    /// <summary>尝试把逻辑键映射为 keysym。无法可靠映射（复杂符号 / 布局相关）返回 null。</summary>
    public static KeySymbol? TryMap(Key key, bool shifted)
    {
        if (SpecialKeys.TryGetValue(key, out var special))
        {
            return special;
        }

        if (key is >= Key.A and <= Key.Z)
        {
            var baseCode = 'a' + (key - Key.A);
            return (KeySymbol)(shifted ? baseCode - 32 : baseCode);
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            var digit = key - Key.D0;
            return (KeySymbol)(shifted ? ShiftedDigits[digit] : '0' + digit);
        }

        return null;
    }
}
