namespace RemoteFlow.App.Services;

/// <summary>
/// 玻璃外观的透明度（设置 → 窗口材质 → 透明度，0–100）。
/// <para>
/// 只调三层半透明面：窗口底（亮度薄涂）、工作区面板、卡片纸张。每层在「最不透明」与「最透明」
/// 之间按透明度线性插值，两端以 Glass.*.xaml 中的设计值为中点对称展开，因此默认 50 与设计稿完全一致。
/// 弹出层、悬停 / 选中叠加色与文字色不随透明度变化，保证菜单可读、交互反馈稳定。
/// </para>
/// </summary>
public static class GlassTransparency
{
    /// <summary>默认透明度，对应 Glass.*.xaml 中的设计值。</summary>
    public const int Default = 50;

    /// <summary>随透明度缩放 Alpha 的语义键。</summary>
    public static IReadOnlyList<string> ScaledKeys { get; } = ["Bg.Base", "Bg.Workspace", "Bg.Layer"];

    // (最不透明时的 Alpha, 最透明时的 Alpha)。中点 = 设计值：
    // 浅色 Base 0x8C / Workspace 0x66 / Layer 0xA6；深色 Base 0xCC / Workspace 0x66 / Layer 0x99。
    // 最透明端仍保留薄涂，文字不会直接压在窗口后方的任意内容上。
    private static readonly Dictionary<string, (byte Opaque, byte Clear)> Light = new()
    {
        ["Bg.Base"] = (230, 50),
        ["Bg.Workspace"] = (178, 26),
        ["Bg.Layer"] = (230, 102),
    };

    private static readonly Dictionary<string, (byte Opaque, byte Clear)> Dark = new()
    {
        ["Bg.Base"] = (250, 158),
        ["Bg.Workspace"] = (178, 26),
        ["Bg.Layer"] = (217, 89),
    };

    /// <summary>指定层在给定透明度下的 Alpha；透明度会被夹到 0–100。</summary>
    public static byte Alpha(string key, bool dark, int transparency)
    {
        var (opaque, clear) = (dark ? Dark : Light)[key];
        var t = Math.Clamp(transparency, 0, 100) / 100.0;
        return (byte)Math.Round(opaque + (clear - opaque) * t, MidpointRounding.AwayFromZero);
    }
}
