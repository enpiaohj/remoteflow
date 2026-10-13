namespace RemoteFlow.Core.Sessions;

/// <summary>远端画面尺寸（像素）。</summary>
public readonly record struct FrameSize(int Width, int Height)
{
    public static FrameSize Empty => new(0, 0);

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// 帧源：由协议层产出远端画面，交给 UI 层按各自的位图类型取走。
/// <para>
/// <b>存在意义：</b>把「协议线程解码出的像素」与「UI 框架的位图类型」解耦。
/// 协议实现只负责维护一块 BGRA32 缓冲区并标记脏数据；UI 层拥有自己的位图
/// （WPF 的 <c>WriteableBitmap</c> 或 Avalonia 的同名类型），按显示帧率来取。
/// 这样 <c>RemoteFlow.Protocol.*</c> 不必引用任何 UI 框架。
/// </para>
/// <para>
/// <b>线程模型：</b><see cref="TryCopyLatestFrame"/> 由 UI 线程调用，
/// 协议线程并发写入缓冲区；实现方负责内部同步。无新帧时必须直接返回 false，
/// 不做任何拷贝，以免空闲时白耗 CPU。
/// </para>
/// <para>
/// VNC 与 macOS 侧的 RDP 都以此为画面输出口（技术方案 §3.2 / §6.5）。
/// </para>
/// </summary>
public interface IFrameSource
{
    /// <summary>当前帧尺寸。线程安全读取。</summary>
    FrameSize FrameSize { get; }

    /// <summary>
    /// 远端画面尺寸变化。UI 需要据此重建位图。
    /// 可能在协议线程上触发，处理方需自行封送到 UI 线程。
    /// </summary>
    event EventHandler<FrameSize>? FrameSizeChanged;

    /// <summary>
    /// 把最新一帧拷贝到目标缓冲区。
    /// <para>
    /// 逐行拷贝并遵循 <paramref name="destinationStride"/>，因此目标位图存在行对齐
    /// 填充（stride &gt; 宽度 × 4）时也不会错位。
    /// </para>
    /// </summary>
    /// <param name="destination">目标像素缓冲区首地址，要求 BGRA32 布局。</param>
    /// <param name="destinationCapacityBytes">目标缓冲区可写字节数，用于越界保护。</param>
    /// <param name="destinationStride">目标缓冲区每行字节数。</param>
    /// <param name="expectedWidth">调用方位图的宽度，与当前帧不一致时放弃本次拷贝。</param>
    /// <param name="expectedHeight">调用方位图的高度，与当前帧不一致时放弃本次拷贝。</param>
    /// <returns>是否真的写入了新画面。无新帧或尺寸不匹配时返回 false。</returns>
    bool TryCopyLatestFrame(
        nint destination,
        long destinationCapacityBytes,
        int destinationStride,
        int expectedWidth,
        int expectedHeight);
}
