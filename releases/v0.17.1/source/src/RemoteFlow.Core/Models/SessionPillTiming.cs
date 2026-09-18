namespace RemoteFlow.Core.Models;

/// <summary>
/// 全屏悬浮工具条（药丸）显示 / 消失延迟的解析。
/// 设置里存的是毫秒 <see cref="int"/>，这里统一夹取到合法区间，并按全屏档位取对应值——
/// 窗口最大化档与完全全屏档允许配不同的延迟来区分交互手感。
/// </summary>
public static class SessionPillTiming
{
    /// <summary>延迟下限。0 表示立即显示 / 收起。</summary>
    public const int MinDelayMs = 0;

    /// <summary>延迟上限，挡住手改 JSON 填进来的异常值。</summary>
    public const int MaxDelayMs = 10_000;

    /// <summary>
    /// 顶沿悬停多久才唤出药丸。
    /// <paramref name="screenFull"/>：true = 完全全屏档，false = 窗口最大化档。
    /// </summary>
    public static TimeSpan GetRevealDelay(this AppSettings settings, bool screenFull)
        => TimeSpan.FromMilliseconds(Clamp(screenFull
            ? settings.PillRevealDelayScreenFullMs
            : settings.PillRevealDelayWindowFullMs));

    /// <summary>鼠标离开药丸 / 停留感应区后多久收起。档位语义同 <see cref="GetRevealDelay"/>。</summary>
    public static TimeSpan GetHideDelay(this AppSettings settings, bool screenFull)
        => TimeSpan.FromMilliseconds(Clamp(screenFull
            ? settings.PillHideDelayScreenFullMs
            : settings.PillHideDelayWindowFullMs));

    private static int Clamp(int ms) => Math.Clamp(ms, MinDelayMs, MaxDelayMs);
}
