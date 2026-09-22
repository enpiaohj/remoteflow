using AppKit;

namespace RemoteFlow.App.Mac;

/// <summary>
/// AppKit <see cref="NSEvent"/> → RDP scancode（PC set 1）。功能 / 导航 / 修饰键查表；
/// 可打印字符走 Unicode 通道（<see cref="RemoteFlow.Protocol.Rdp.Mac.RdpSession.SendUnicode"/>）。
/// </summary>
internal static class AppKitRdpKeyMapper
{
    // macOS 虚拟键码 → (scancode, extended)
    private static readonly Dictionary<ushort, (int Code, bool Ext)> Map = new()
    {
        [0x24] = (0x1C, false), // Return
        [0x4C] = (0x1C, true),  // Keypad Enter
        [0x30] = (0x0F, false), // Tab
        [0x31] = (0x39, false), // Space
        [0x33] = (0x0E, false), // Backspace
        [0x35] = (0x01, false), // Escape
        [0x75] = (0x53, true),  // Forward Delete
        [0x72] = (0x52, true),  // Insert/Help
        [0x73] = (0x47, true),  // Home
        [0x77] = (0x4F, true),  // End
        [0x74] = (0x49, true),  // Page Up
        [0x79] = (0x51, true),  // Page Down
        [0x7B] = (0x4B, true),  // Left
        [0x7C] = (0x4D, true),  // Right
        [0x7D] = (0x50, true),  // Down
        [0x7E] = (0x48, true),  // Up
        [0x39] = (0x3A, false), // Caps Lock
        [0x7A] = (0x3B, false), // F1
        [0x78] = (0x3C, false), // F2
        [0x63] = (0x3D, false), // F3
        [0x76] = (0x3E, false), // F4
        [0x60] = (0x3F, false), // F5
        [0x61] = (0x40, false), // F6
        [0x62] = (0x41, false), // F7
        [0x64] = (0x42, false), // F8
        [0x65] = (0x43, false), // F9
        [0x6D] = (0x44, false), // F10
        [0x67] = (0x57, false), // F11
        [0x6F] = (0x58, false), // F12
    };

    private static readonly Dictionary<ushort, (int Code, bool Ext)> Modifiers = new()
    {
        [0x38] = (0x2A, false), // Left Shift
        [0x3C] = (0x36, false), // Right Shift
        [0x3B] = (0x1D, false), // Left Control
        [0x3E] = (0x1D, true),  // Right Control
        [0x3A] = (0x38, false), // Left Option → Alt
        [0x3D] = (0x38, true),  // Right Option → AltGr
        [0x37] = (0x5B, true),  // Left Command → LWin
        [0x36] = (0x5C, true),  // Right Command → RWin
    };

    public static bool IsModifier(ushort keyCode) => Modifiers.ContainsKey(keyCode);

    public static (int Code, bool Ext)? MapModifier(ushort keyCode)
        => Modifiers.TryGetValue(keyCode, out var v) ? v : null;

    public static (int Code, bool Ext)? MapSpecial(ushort keyCode)
        => Map.TryGetValue(keyCode, out var v) ? v : null;

    public static NSEventModifierMask MaskFor(ushort keyCode) => keyCode switch
    {
        0x38 or 0x3C => NSEventModifierMask.ShiftKeyMask,
        0x3B or 0x3E => NSEventModifierMask.ControlKeyMask,
        0x3A or 0x3D => NSEventModifierMask.AlternateKeyMask,
        0x37 or 0x36 => NSEventModifierMask.CommandKeyMask,
        _ => 0,
    };
}
