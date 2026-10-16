using AppKit;
using MarcusW.VncClient;

namespace RemoteFlow.App.Mac;

/// <summary>
/// AppKit <see cref="NSEvent"/> → VNC X11 keysym 映射。与 WPF / 旧 Avalonia 侧同一 X11 keysym 体系。
/// <para>
/// 功能键按 macOS 硬件虚拟键码查表；可打印键按 <c>CharactersIgnoringModifiers</c> 的
/// ASCII 码点直取（ASCII 段 keysym 与码点一致）。
/// </para>
/// <para>局限：未做完整键盘布局 / IME / OEM 符号键映射，满足基本运维交互。</para>
/// </summary>
public static class AppKitVncKeyMapper
{
    // macOS 虚拟键码（kVK_*）→ keysym。
    private static readonly Dictionary<ushort, KeySymbol> Special = new()
    {
        [0x24] = KeySymbol.Return,      // Return
        [0x4C] = KeySymbol.KP_Enter,    // Keypad Enter
        [0x30] = KeySymbol.Tab,
        [0x31] = KeySymbol.space,
        [0x33] = KeySymbol.BackSpace,
        [0x35] = KeySymbol.Escape,
        [0x75] = KeySymbol.Delete,      // Forward Delete
        [0x72] = KeySymbol.Insert,      // Help/Insert
        [0x73] = KeySymbol.Home,
        [0x77] = KeySymbol.End,
        [0x74] = KeySymbol.Page_Up,
        [0x79] = KeySymbol.Page_Down,
        [0x7B] = KeySymbol.Left,
        [0x7C] = KeySymbol.Right,
        [0x7D] = KeySymbol.Down,
        [0x7E] = KeySymbol.Up,
        [0x39] = KeySymbol.Caps_Lock,
        [0x7A] = KeySymbol.F1,
        [0x78] = KeySymbol.F2,
        [0x63] = KeySymbol.F3,
        [0x76] = KeySymbol.F4,
        [0x60] = KeySymbol.F5,
        [0x61] = KeySymbol.F6,
        [0x62] = KeySymbol.F7,
        [0x64] = KeySymbol.F8,
        [0x65] = KeySymbol.F9,
        [0x6D] = KeySymbol.F10,
        [0x67] = KeySymbol.F11,
        [0x6F] = KeySymbol.F12,
    };

    // 修饰键虚拟键码 → keysym（FlagsChanged 用）。
    private static readonly Dictionary<ushort, KeySymbol> Modifiers = new()
    {
        [0x38] = KeySymbol.Shift_L,
        [0x3C] = KeySymbol.Shift_R,
        [0x3B] = KeySymbol.Control_L,
        [0x3E] = KeySymbol.Control_R,
        [0x3A] = KeySymbol.Alt_L,       // Option
        [0x3D] = KeySymbol.Alt_R,
        [0x37] = KeySymbol.Super_L,     // Command
        [0x36] = KeySymbol.Super_R,
    };

    public static bool IsModifier(ushort keyCode) => Modifiers.ContainsKey(keyCode);

    public static KeySymbol? MapModifier(ushort keyCode)
        => Modifiers.TryGetValue(keyCode, out var s) ? s : null;

    /// <summary>把普通按键事件映射为 keysym。无法可靠映射返回 null。</summary>
    public static KeySymbol? Map(NSEvent e)
    {
        if (Special.TryGetValue(e.KeyCode, out var special))
        {
            return special;
        }

        var chars = e.CharactersIgnoringModifiers;
        if (!string.IsNullOrEmpty(chars))
        {
            var c = chars[0];
            if (c is >= (char)0x20 and < (char)0x7F)
            {
                return (KeySymbol)c;
            }
        }

        return null;
    }
}
