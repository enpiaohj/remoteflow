using System.Windows.Input;
using MarcusW.VncClient;

namespace RemoteFlow.App.Views.Sessions;

/// <remarks>
/// 本类做的是「WPF <c>Key</c> 枚举 → VNC keysym」的映射，属 UI 层职责：
/// 协议层不应认识任何 UI 框架的输入枚举。因此它随 macOS 移植从
/// RemoteFlow.Protocol.Vnc 移入本工程；Avalonia 侧会有对应的自有映射。
/// </remarks>
/// <summary>
/// WPF 按键 → X11 KeySymbol 映射。RFB 协议使用 X11 keysym 表达按键，
/// 与 Windows 的虚拟键码体系不同，必须显式转换。
/// </summary>
public static class VncKeyMapper
{
    /// <summary>
    /// 非可打印键的映射表。可打印字符不走这里，
    /// 而是由文本输入事件按 Unicode 规则直接转换，这样才能正确处理输入法与各国键盘布局。
    /// </summary>
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

        [Key.LeftShift] = KeySymbol.Shift_L,
        [Key.RightShift] = KeySymbol.Shift_R,
        [Key.LeftCtrl] = KeySymbol.Control_L,
        [Key.RightCtrl] = KeySymbol.Control_R,
        [Key.LeftAlt] = KeySymbol.Alt_L,
        [Key.RightAlt] = KeySymbol.Alt_R,
        [Key.LWin] = KeySymbol.Super_L,
        [Key.RWin] = KeySymbol.Super_R,
        [Key.Apps] = KeySymbol.Menu,

        [Key.CapsLock] = KeySymbol.Caps_Lock,
        [Key.NumLock] = KeySymbol.Num_Lock,
        [Key.Scroll] = KeySymbol.Scroll_Lock,
        [Key.PrintScreen] = KeySymbol.Print,
        [Key.Pause] = KeySymbol.Pause,

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

        // 小键盘。远端可能依赖其独立键位（如 Num Lock 状态下的方向键行为）。
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
        [Key.Decimal] = KeySymbol.KP_Decimal
    };

    /// <summary>尝试把 WPF 的特殊按键映射为 KeySymbol。</summary>
    public static bool TryMapSpecialKey(Key key, out KeySymbol keySymbol)
        => SpecialKeys.TryGetValue(key, out keySymbol);

    /// <summary>
    /// 把一个 Unicode 字符映射为 KeySymbol。
    /// <para>
    /// X11 约定：ASCII 可打印区间的 keysym 值等于字符本身；
    /// 其余 Unicode 字符使用 <c>0x01000000 + 码点</c> 的扩展编码。
    /// 这条规则让中文等非 ASCII 字符也能直接送达远端。
    /// </para>
    /// </summary>
    public static KeySymbol MapCharacter(int codePoint)
    {
        if (codePoint is >= 0x20 and <= 0x7E)
        {
            return (KeySymbol)codePoint;
        }

        return (KeySymbol)(0x01000000 + codePoint);
    }
}
